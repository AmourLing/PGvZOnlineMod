using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Lawn;
using Lidgren.Network;
using PGvZOnlineMod.Core;
using PGvZOnlineMod.Net;
using PGvZOnlineMod.Protocol;
using PGvZOnlineMod.Ui;
using Sexy;

namespace PGvZOnlineMod.Sync
{
    public enum SessionPhase
    {
        Idle,          // 未联机
        HostingLobby,  // 已开房等待加入
        JoiningLobby,  // 正在连接
        InRoom,        // 已连接，房间内
        InGame,        // 对局中
    }

    /// <summary>
    /// 联机会话核心（多人版，最多 MaxPlayers 人）：状态机 + 消息路由 + 快照编排 + 输入转发。
    /// 同步模型：Host 权威 + 客户端影子模拟——
    /// - Host 正常跑逻辑；每 1/SnapshotHz 采集快照（波次/倒计时/实体 hp+位姿）广播，
    ///   新实体（懒分配 netId）以 SpawnBatch 可靠下发，消失实体发 Retire。
    /// - 卡组/阳光完全独立：每名玩家自己选卡、自己付钱、自己收阳光；
    ///   客户端种植/铲除改发请求（带玩家槽位），Host 用该玩家的卡组 + 原生调用执行。
    /// - 客户端抑制本地出怪与植物产出/天降阳光，由 Host 事件广播（防双份与漂移）。
    /// - 任一客户端断线后其槽位释放，其余人继续（不卡死）；主机断开则全员回退单机。
    /// </summary>
    public static class Session
    {
        /// <summary>最大玩家数（1 主机 + 3 客人）。</summary>
        public const int MaxPlayers = NetMgr.MaxClients + 1;

        /// <summary>客户端自己的槽位（握手时由主机分配；主机恒为 0）。</summary>
        public static int MySlot;

        /// <summary>各槽位昵称（0 = 主机）。</summary>
        public static readonly string[] Nicks = new string[MaxPlayers];

        /// <summary>各槽位占用状态（0 = 主机恒占用）。</summary>
        public static readonly bool[] SlotOccupied = new bool[MaxPlayers];

        public static readonly NetMgr Net = new();
        public static SessionPhase Phase = SessionPhase.Idle;

        public static int SelectedLevelIndex;

        // ---- 重入/守卫
        /// <summary>Client 正在应用同步数据（放行同步路径的 AddZombie/AddPlant）。</summary>
        public static bool ApplyingSync;
        /// <summary>Host 正在执行远端输入（钩子不当作本地玩家操作转发）。</summary>
        public static bool ExecutingRemoteInput;
        /// <summary>Client 应用 SpawnBatch 时暂存待登记的 netId。</summary>
        public static uint RegisterNextId;

        // ---- 开局就绪门闩（所有玩家都点完"Let's Rock"才进入战场）
        public static bool AwaitingReady;
        public static bool LevelActuallyStarted;
        private static bool _localReadySent;
        private static Action _pendingStart;
        private static readonly bool[] _readyFlags = new bool[MaxPlayers];

        // ---- 各玩家声明的卡组（Host 执行其种植请求时按槽位取用）
        public static readonly int[][] RemoteDeckType = CreateDecks();
        public static readonly int[][] RemoteDeckImitater = CreateDecks();

        // ---- 暂停同步（任一方暂停 = 全体冻结；按槽位记录意愿）
        private static readonly bool[] _pauseWanted = new bool[MaxPlayers];
        private static bool _remotePauseWanted; // 客户端视角：主机侧的冻结意愿（快照标志）
        public static bool ExecutingPauseSync;

        // ---- 同步状态
        public static readonly NetIdRegistry Registry = new();
        private static readonly List<NetZombieSpawn> _pendingZombieSpawns = new();
        private static readonly List<NetPlantSpawn> _pendingPlantSpawns = new();
        private static readonly HashSet<uint> _announcedIds = new();
        private static readonly List<NetZombieState> _zombieStates = new();
        private static readonly List<NetPlantState> _plantStates = new();
        private static readonly List<NetZombieSpawn> _rxZombieSpawns = new();
        private static readonly List<NetPlantSpawn> _rxPlantSpawns = new();
        private static readonly int[] _seedTypes = new int[Packets.MaxSeedSlots];
        private static readonly int[] _refreshCounters = new int[Packets.MaxSeedSlots];
        private static readonly bool[] _refreshing = new bool[Packets.MaxSeedSlots];
        private static readonly bool[] _active = new bool[Packets.MaxSeedSlots];
        private static readonly List<uint> _objectIds = new();

        /// <summary>当前对局棋盘（Board.Update 钩子按实例身份刷新）。</summary>
        public static Board CurrentBoard;

        // ---- HUD 状态（光标按槽位：0=主机，1..=客人；自己槽位的不绘制）
        public static readonly float[] RemoteCursorX = FillF(-9999f);
        public static readonly float[] RemoteCursorY = FillF(-9999f);
        public static readonly double[] RemoteCursorAge = FillD(999);
        public static string LastChat = "";
        public static double LastChatAge = 999;
        public static string StatusText = "";
        public static bool StatusIsError;

        // ---- 关卡表（GameMode 自带阶段信息，客户端可直接 PreNewGame）
        public static readonly (GameMode Mode, string Label)[] Levels =
        {
            (GameMode.SurvivalNormalStage1, "生存 · 白天前院"),
            (GameMode.SurvivalNormalStage2, "生存 · 黑夜"),
            (GameMode.SurvivalNormalStage3, "生存 · 泳池"),
            (GameMode.SurvivalNormalStage4, "生存 · 浓雾"),
            (GameMode.SurvivalNormalStage5, "生存 · 屋顶"),
        };

        // ---- 局域网房间发现
        public class DiscoveredRoom
        {
            public string Ip;
            public int Port;
            public string HostNick;
            public int LevelIndex;
            public double LastSeen;
            public string DisplayText = "";
        }

        /// <summary>ip → 房间（仅主线程访问）。</summary>
        public static readonly Dictionary<string, DiscoveredRoom> Rooms = new();

        /// <summary>按发现时间倒序的展示列表（主线程维护，UI 只读）。</summary>
        public static readonly List<DiscoveredRoom> RoomList = new();

        public static long RoomListVersion;

        // ---- 房间准备（进入对局前，所有客人都需准备）
        private static readonly bool[] _roomReady = new bool[MaxPlayers];

        // ---- 产出/预览/加速 同步状态
        public static bool SpawningMirrorCutsceneZombie;
        private static readonly HashSet<uint> _productionSynced = new();
        private static readonly Dictionary<uint, int> _pendingSunSync = new();

        // ---- 计时
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static double _now;
        private static double _nextSnapshotAt;
        private static double _nextCursorAt;
        private static double _nextDiscoverAt;
        private static double _nextSweepAt;
        private static IPAddress _localIPv4;
        private static double _localIPv4At = -999;
        private static bool _lastSuppression;
        private static int _clientWaveLogged = -1;
        private static long _lastDiscoveryReqCount;
        private static double _lastDiscoveryReqAt = -1;

        /// <summary>主机侧：最近一次收到对方搜索请求距今的秒数（-1 = 从未收到）。</summary>
        public static double DiscoveryReqAge
        {
            get
            {
                long cnt = Net.DiscoveryRequestsReceived;
                if (cnt != _lastDiscoveryReqCount)
                {
                    return 0; // 刚收到新请求
                }
                return _lastDiscoveryReqAt < 0 ? -1 : _now - _lastDiscoveryReqAt;
            }
        }







        public static bool IsHost => Net.Role == NetRole.Host;
        public static bool InGame => Phase == SessionPhase.InGame;

        /// <summary>联机同步生效中（钩子据此决定抑制/转发）。</summary>
        public static bool SyncActive => InGame && Net.IsConnected;

        /// <summary>Client 端应抑制本地逻辑（出怪/种植执行/产出/天降阳光）。</summary>
        public static bool ClientSuppressionActive => SyncActive && !IsHost;

        /// <summary>就绪门闩生效条件（StartPlaying 前的选卡等待）。</summary>
        public static bool ReadyGateActive => InGame && Net.IsConnected && !LevelActuallyStarted;

        static Session()
        {
            Net.OnConnected += OnPeerConnected;
            Net.OnPeerDisconnected += OnPeerDisconnected;
            Net.OnData += OnData;
            Net.OnRoomDiscovered += OnRoomDiscovered;
            Net.DiscoveryResponder = BuildRoomBeacon;
        }

        private static int[] Filled(int v)
        {
            var a = new int[Packets.MaxSeedSlots];
            for (int i = 0; i < a.Length; i++)
            {
                a[i] = v;
            }
            return a;
        }

        private static int[][] CreateDecks()
        {
            var decks = new int[MaxPlayers][];
            for (int i = 0; i < MaxPlayers; i++)
            {
                decks[i] = Filled(-1);
            }
            return decks;
        }

        private static float[] FillF(float v)
        {
            var a = new float[MaxPlayers];
            for (int i = 0; i < a.Length; i++)
            {
                a[i] = v;
            }
            return a;
        }

        private static double[] FillD(double v)
        {
            var a = new double[MaxPlayers];
            for (int i = 0; i < a.Length; i++)
            {
                a[i] = v;
            }
            return a;
        }

        // ============================================================ 房间操作（UI 调用）

        public static void StartHosting(LawnApp app)
        {
            var cfg = ModEnv.GetConfig();
            if (!Net.StartHost(cfg.HostPort))
            {
                SetStatus("开房失败: " + Net.Error, true);
                return;
            }
            Nicks[0] = cfg.Nickname;
            for (int i = 1; i < MaxPlayers; i++)
            {
                Nicks[i] = "";
                SlotOccupied[i] = false;
                _readyFlags[i] = false;
                _pauseWanted[i] = false;
            }
            MySlot = 0;
            SelectedLevelIndex = 0;
            Phase = SessionPhase.HostingLobby;
            SetStatus("房间已建立（端口 " + Net.BoundPort + "），等待玩家加入…", false);
            ModEnv.Log("进入 HostingLobby，昵称=" + Nicks[0]);
        }

        public static void StartJoining(LawnApp app, string ip, int port)
        {
            var cfg = ModEnv.GetConfig();
            if (string.IsNullOrWhiteSpace(ip))
            {
                SetStatus("请输入主机 IP", true);
                return;
            }
            if (port < 1024 || port > 65535)
            {
                port = cfg.HostPort;
            }
            if (!Net.StartClient(ip.Trim(), port))
            {
                SetStatus("连接失败: " + Net.Error, true);
                return;
            }
            Nicks[0] = "";
            Nicks[MySlot] = cfg.Nickname;
            for (int i = 1; i < MaxPlayers; i++)
            {
                if (i != MySlot)
                {
                    Nicks[i] = "";
                    SlotOccupied[i] = false;
                    _readyFlags[i] = false;
                    _pauseWanted[i] = false;
                }
            }
            Phase = SessionPhase.JoiningLobby;
            SetStatus("正在连接 " + ip.Trim() + ":" + port + " …", false);
            ModEnv.Log("开始连接 " + ip.Trim() + ":" + port);
        }

        public static void CancelOrDisconnect()
        {
            if (Phase == SessionPhase.InGame)
            {
                // 对局中断开：保留本地游戏，回退单机逻辑
                Net.Shutdown();
                SetStatus("已与对方断开，本局继续（单机）", true);
                ModEnv.Log("对局中断线，回退单机");
                return;
            }
            if (Net.IsConnected)
            {
                // 先告别：主机收到后留在房间继续等待，不会被踢出
                SendGoodbye(Phase == SessionPhase.InRoom ? "有玩家退出了房间，继续等待新玩家" : "对方取消了连接");
            }
            Net.Shutdown();
            Phase = SessionPhase.Idle;
            Registry.Clear();
            _announcedIds.Clear();
            SetStatus("", false);
        }

        public static void CycleLevel()
        {
            if (!IsHost)
            {
                return;
            }
            SelectedLevelIndex = (SelectedLevelIndex + 1) % Levels.Length;
            BroadcastRoomState();
        }

        public static void HostStartGame(LawnApp app)
        {
            if (!IsHost || !Net.IsConnected || app == null)
            {
                return;
            }
            // 所有客人都必须准备
            for (int s = 1; s < MaxPlayers; s++)
            {
                if (SlotOccupied[s] && !_roomReady[s])
                {
                    SetStatus("还有玩家未准备，无法开始", true);
                    return;
                }
            }
            var mode = Levels[Math.Clamp(SelectedLevelIndex, 0, Levels.Length - 1)].Mode;
            int seed = Environment.TickCount & 0x7fffffff;
            var m = Net.CreateMessage();
            if (m != null)
            {
                Packets.WriteStartGame(m, (int)mode, seed);
                Net.SendReliableToClients(m);
            }
            BeginGame(app, mode);
        }

        private static void BeginGame(LawnApp app, GameMode mode)
        {
            Phase = SessionPhase.InGame;
            Registry.Clear();
            _announcedIds.Clear();
            _pendingZombieSpawns.Clear();
            _pendingPlantSpawns.Clear();
            CurrentBoard = null;
            _nextSnapshotAt = 0;
            ResetReadyGate();
            OnlineLobbyScreen.CloseIfOpen(app);
            SetStatus("", false);
            ModEnv.Log("开局: " + mode);
            app.PreNewGame(mode, false);
        }

        // ============================================================ 开局就绪门闩

        private static void ResetReadyGate()
        {
            AwaitingReady = false;
            LevelActuallyStarted = false;
            _localReadySent = false;
            _pendingStart = null;
            for (int s = 0; s < MaxPlayers; s++)
            {
                _readyFlags[s] = false;
                _pauseWanted[s] = false;
                for (int i = 0; i < RemoteDeckType[s].Length; i++)
                {
                    RemoteDeckType[s][i] = -1;
                    RemoteDeckImitater[s][i] = -1;
                }
            }
            _remotePauseWanted = false;
            _productionSynced.Clear();
            _pendingSunSync.Clear();
            for (int s = 0; s < MaxPlayers; s++)
            {
                _roomReady[s] = false;
            }
        }

        /// <summary>当前是否全员就绪（选卡界面提示用）。</summary>
        public static bool AllReadyNow => AllPlayersReady();

        /// <summary>所有已占用槽位是否都已就绪。</summary>
        private static bool AllPlayersReady()
        {
            for (int s = 0; s < MaxPlayers; s++)
            {
                if (SlotOccupied[s] && !_readyFlags[s])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>选卡完成钩子拦截时调用：发送本方就绪+卡组（幂等），返回全员是否已就绪。</summary>
        public static bool NoteLocalReady(int[] deckTypes, int[] deckImitaters)
        {
            if (!_localReadySent)
            {
                _localReadySent = true;
                SendReady(deckTypes, deckImitaters);
            }
            return AllPlayersReady();
        }

        /// <summary>还有玩家未完成选卡：把真正的进关动作挂起，等全员就绪广播再执行。</summary>
        public static void HoldStart(Action startOnce)
        {
            _pendingStart = startOnce;
            AwaitingReady = true;
        }

        /// <summary>本路径真正执行了进关（门闩关闭，防重复触发二次开局）。</summary>
        public static void MarkStartExecuted()
        {
            LevelActuallyStarted = true;
            AwaitingReady = false;
            _pendingStart = null;
        }

        /// <summary>Host：收到某槽位的 Ready 包（带其卡组）。全员就绪 → 放行并广播 AllReady。</summary>
        private static void OnRemoteReady(int slot, int[] deckTypes, int[] deckImitaters)
        {
            if (slot < 0 || slot >= MaxPlayers)
            {
                return;
            }
            _readyFlags[slot] = true;
            if (deckTypes != null)
            {
                for (int i = 0; i < RemoteDeckType[slot].Length && i < deckTypes.Length; i++)
                {
                    RemoteDeckType[slot][i] = deckTypes[i];
                    RemoteDeckImitater[slot][i] = deckImitaters[i];
                }
            }
            if (!AllPlayersReady())
            {
                return;
            }
            // 全员就绪：主机放行自己的门闩，并广播 AllReady 让各客户端放行
            LevelActuallyStarted = true;
            SetStatus("全员就绪，进入关卡", false);
            if (AwaitingReady && _pendingStart != null)
            {
                var start = _pendingStart;
                _pendingStart = null;
                AwaitingReady = false;
                MainThreadQueue.Post(start);
            }
            var m = Net.CreateMessage();
            if (m != null)
            {
                Packets.WriteAllReady(m);
                Net.SendReliableToClients(m);
            }
        }

        private static void SendReady(int[] deckTypes, int[] deckImitaters)
        {
            var m = Net.CreateMessage();
            if (m == null)
            {
                return;
            }
            Packets.WriteReady(m, deckTypes, deckImitaters);
            Net.SendReliableToHost(m);
        }

        // ============================================================ 主线程泵（LawnApp.UpdateFrames 钩子）

        public static void Pump(LawnApp app)
        {
            _now = _clock.Elapsed.TotalSeconds;
            MainThreadQueue.Pump();
            Net.Poll();

            if (app == null)
            {
                return;
            }

            // 本方退出对局（选卡中途退/局内退回主菜单/结算回菜单）→ 先通知对方再复位
            if (Phase == SessionPhase.InGame && app.mGameScene == GameScenes.Menu)
            {
                if (Net.IsConnected)
                {
                    SendGoodbye("有玩家已退出本局，其余人继续（单机）");
                    Net.Shutdown();
                }
                ResetReadyGate();
                Phase = SessionPhase.Idle;
                Registry.Clear();
                _announcedIds.Clear();
                ModEnv.Log("退出对局，会话复位");
            }

            long reqCount = Net.DiscoveryRequestsReceived;
            if (reqCount != _lastDiscoveryReqCount)
            {
                _lastDiscoveryReqCount = reqCount;
                _lastDiscoveryReqAt = _now;
            }

            OnlineLobbyScreen.EnsureMenuButton(app);
            Hud.RefreshStatusCache();

            if (SyncActive && CurrentBoard != null)
            {
                var wm = app.mWidgetManager;
                if (wm != null && _now >= _nextCursorAt)
                {
                    _nextCursorAt = _now + 1.0 / Math.Max(2, ModEnv.GetConfig().CursorHz);
                    int mx = wm.mLastMouseX - CurrentBoard.mX;
                    int my = wm.mLastMouseY - CurrentBoard.mY;
                    SendCursor(IsHost ? 0 : MySlot, mx, my);
                }
            }

            TickDiscovery();
        }

        // ============================================================ 暂停同步（任一方暂停=全体冻结）

        /// <summary>Board.Pause 钩子调用：本方暂停/恢复（游戏原生暂停菜单/按钮都走这里）。</summary>
        public static void OnLocalPauseChanged(Board board, bool paused)
        {
            if (!InGame)
            {
                return;
            }
            int slot = IsHost ? 0 : MySlot;
            if (_pauseWanted[slot] != paused)
            {
                _pauseWanted[slot] = paused;
                ModEnv.Log("本方" + (paused ? "暂停" : "恢复"));
            }
            if (!IsHost)
            {
                var m = Net.CreateMessage();
                if (m != null)
                {
                    Packets.WritePauseRequest(m, paused);
                    Net.SendReliableToHost(m);
                }
            }
            ApplyPauseSync(board, "有玩家未恢复，游戏保持暂停");
        }

        /// <summary>Host：收到某槽位客户端的暂停/恢复请求。</summary>
        private static void OnRemotePauseRequest(int slot, bool paused)
        {
            var board = CurrentBoard;
            if (board == null || !InGame || slot < 0 || slot >= MaxPlayers)
            {
                return;
            }
            if (_pauseWanted[slot] != paused)
            {
                _pauseWanted[slot] = paused;
                ModEnv.Log("槽位 " + slot + (paused ? " 暂停" : " 恢复"));
                if (paused)
                {
                    SetStatus("玩家 [" + (Nicks[slot].Length > 0 ? Nicks[slot] : "P" + (slot + 1)) + "] 已暂停", false);
                }
            }
            ApplyPauseSync(board, "有玩家未恢复，游戏保持暂停");
        }

        /// <summary>按"任一方想暂停就全体冻结"重置棋盘暂停态（幂等，重入由 ExecutingPauseSync 防护）。</summary>
        private static void ApplyPauseSync(Board board, string waitingHint)
        {
            if (board == null || ExecutingPauseSync || !InGame)
            {
                return;
            }
            bool target = false;
            for (int s = 0; s < MaxPlayers; s++)
            {
                if (SlotOccupied[s] && _pauseWanted[s])
                {
                    target = true;
                    break;
                }
            }
            if (board.mPaused == target)
            {
                return;
            }
            try
            {
                ExecutingPauseSync = true;
                board.Pause(target);
                if (target)
                {
                    SetStatus(waitingHint, false);
                }
            }
            catch (Exception ex)
            {
                ModEnv.Log("同步暂停异常: " + ex.Message);
            }
            finally
            {
                ExecutingPauseSync = false;
            }
        }

        // ============================================================ 植物产阳光同步

        /// <summary>Host：某株植物刚产出（mLaunchCounter 被重置）→ 把新计数广播给所有客户端。</summary>
        public static void OnHostPlantProduced(Plant plant, int newCounter)
        {
            if (!IsHost || !SyncActive || plant == null)
            {
                return;
            }
            try
            {
                if (!Registry.TryGetId(plant, out uint id))
                {
                    id = Registry.Assign(plant);
                    _objectIds.Add(id);
                    _announcedIds.Add(id);
                    _pendingPlantSpawns.Add(new NetPlantSpawn
                    {
                        NetId = id,
                        GridX = plant.mPlantCol,
                        GridY = plant.mRow,
                        SeedType = (int)plant.mSeedType,
                        Hp = plant.mPlantHealth,
                        LaunchCounter = plant.mLaunchCounter,
                    });
                }
                var m = Net.CreateMessage();
                if (m != null)
                {
                    Packets.WriteSunProduced(m, id, newCounter);
                    Net.SendReliableToClients(m);
                    ModEnv.Log("[产出] 主机检测到产出 netId=" + id + " 新计数=" + newCounter
                        + " 类型=" + plant.mSeedType);
                }
            }
            catch (Exception ex)
            {
                ModEnv.Log("产出事件发送异常: " + ex.Message);
            }
        }

        /// <summary>同步过计数器的客户端植物（这些植物允许自然倒数到 0 并产出）。</summary>
        public static bool IsPlantProductionSynced(Plant plant)
        {
            return Registry.TryGetId(plant, out uint id) && _productionSynced.Contains(id);
        }

        /// <summary>Client：把同株植物的计数节奏对齐主机（只缩短不延长，绝不取消临产产出）。</summary>
        private static void ApplySunSync(Plant plant, uint netId, int newCounter)
        {
            try
            {
                // 半程延迟补偿：让客户端的产出时刻对齐主机（Lidgren tick = 10ms）。
                // 取 min(客户端剩余, 主机新值)：只把节奏"拉早"到主机时刻，
                // 绝不延长——否则会在临产前一帧覆盖掉客户端即将到期的产出（丢阳光）。
                int delayTicks = (int)Math.Clamp(Net.RemoteRttMs / 2f / 10f, 0f, newCounter);
                int target = Math.Max(0, newCounter - delayTicks);
                plant.mLaunchCounter = Math.Min(plant.mLaunchCounter, target);
                _productionSynced.Add(netId);
                ModEnv.Log("[产出] 客户端同步节奏 netId=" + netId + " 计数=" + plant.mLaunchCounter
                    + " (主机新值" + newCounter + " 补偿" + delayTicks + "tick) 类型=" + plant.mSeedType);
            }
            catch (Exception ex)
            {
                ModEnv.Log("同步产出节奏异常: " + ex.Message);
            }
        }

        /// <summary>Client：对方的植物产出了 → 自己的同株植物也立即产出（阳光归自己）。</summary>
        private static void OnRemoteSunProduced(uint netId, int newCounter)
        {
            var board = CurrentBoard;
            if (board == null || !SyncActive)
            {
                return;
            }
            if (!Registry.TryGetObject(netId, out var obj) || obj is not Plant plant || plant.mDead)
            {
                // 植物的生成事件还没应用——先暂存，应用生成时补上
                _pendingSunSync[netId] = newCounter;
                return;
            }
            ApplySunSync(plant, netId, newCounter);
        }

        // ============================================================ 天降阳光同步

        /// <summary>Host：检测到天降阳光 → 把落点/类型/新倒计时广播给所有客户端。</summary>
        public static void OnHostSkySunSpawned(Board board)
        {
            if (!IsHost || !SyncActive || board == null)
            {
                return;
            }
            try
            {
                for (int i = board.mCoins.Count - 1; i >= 0; i--)
                {
                    var coin = board.mCoins[i];
                    if (coin != null && !coin.mDead && coin.mCoinMotion == CoinMotion.FromSky)
                    {
                        var m = Net.CreateMessage();
                        if (m != null)
                        {
                            Packets.WriteSkySun(m, coin.mPosX, (int)coin.mType, board.mSunCountDown);
                            Net.SendReliableToClients(m);
                            ModEnv.Log("[天降阳光] x=" + (int)coin.mPosX + " 类型=" + coin.mType
                                + " 下次倒计时=" + board.mSunCountDown);
                        }
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                ModEnv.Log("天降阳光事件发送异常: " + ex.Message);
            }
        }

        /// <summary>Client：同位置、同类型、同节奏掉落（阳光归自己）。</summary>
        private static void OnRemoteSkySun(float x, int coinType, int newCountdown)
        {
            var board = CurrentBoard;
            if (board == null || !SyncActive)
            {
                return;
            }
            try
            {
                board.AddCoin((int)x, 60, (CoinType)coinType, CoinMotion.FromSky);
                board.mSunCountDown = newCountdown;
            }
            catch (Exception ex)
            {
                ModEnv.Log("同步天降阳光异常: " + ex.Message);
            }
        }

        // ============================================================ 开场预览僵尸同步

        /// <summary>Host：开场预览僵尸生成 → 广播（类型/格坐标，客户端镜像，随开场清理）。</summary>
        public static void OnHostCutsceneZombie(int zombieType, int gridX, int gridY)
        {
            if (!IsHost || !SyncActive)
            {
                return;
            }
            try
            {
                var m = Net.CreateMessage();
                if (m != null)
                {
                    Packets.WriteCutsceneZombie(m, zombieType, gridX, gridY);
                    Net.SendReliableToClients(m);
                }
            }
            catch (Exception ex)
            {
                ModEnv.Log("预览僵尸事件发送异常: " + ex.Message);
            }
        }

        /// <summary>Client：镜像主机开场预览僵尸（mFromWave=CUTSCENE，随自己端的开场清理移除）。</summary>
        private static void OnRemoteCutsceneZombie(int zombieType, int gridX, int gridY)
        {
            var board = CurrentBoard;
            if (board == null || !SyncActive || board.mCutScene == null)
            {
                return;
            }
            try
            {
                SpawningMirrorCutsceneZombie = true;
                board.mCutScene.PlaceAZombie((ZombieType)zombieType, gridX, gridY);
            }
            catch (Exception ex)
            {
                ModEnv.Log("镜像预览僵尸异常: " + ex.Message);
            }
            finally
            {
                SpawningMirrorCutsceneZombie = false;
            }
        }

        // ============================================================ 加速倍率同步

        /// <summary>MouseUpInternal 后调用：分子变化 = 本方点了加速/减速 → 广播新数值。</summary>
        public static void DetectAccelerationChange(Board board, ref int lastKnown)
        {
            if (!SyncActive || board == null)
            {
                return;
            }
            if (lastKnown == -1)
            {
                lastKnown = board.mAccelerationNumerator; // 进局首帧只记录基线
                return;
            }
            if (board.mAccelerationNumerator != lastKnown)
            {
                lastKnown = board.mAccelerationNumerator;
                OnAccelerationChanged(board);
            }
        }

        /// <summary>任一侧加速倍率变化 → 广播（数值直接落对方字段，双方一致）。</summary>
        public static void OnAccelerationChanged(Board board)
        {
            if (!SyncActive || board == null)
            {
                return;
            }
            try
            {
                var m = Net.CreateMessage();
                if (m != null)
                {
                    Packets.WriteAcceleration(m, board.mAccelerationNumerator, board.mAccelerationDenominator);
                    Net.SendReliableToClients(m);
                }
            }
            catch (Exception ex)
            {
                ModEnv.Log("加速事件发送异常: " + ex.Message);
            }
        }

        private static void OnRemoteAcceleration(int numerator, int denominator)
        {
            var board = CurrentBoard;
            if (board == null)
            {
                return;
            }
            try
            {
                board.mAccelerationNumerator = numerator;
                board.mAccelerationDenominator = denominator;
                board.mAccelerationFrameIndex = 0;
                ModEnv.Log("[加速] 同步为 " + numerator + "/" + denominator);
            }
            catch (Exception ex)
            {
                ModEnv.Log("加速同步异常: " + ex.Message);
            }
        }

        // ============================================================ 钉耙同步

        /// <summary>Host：钉耙放置完成（位置含加权随机行）→ 广播给所有客户端。</summary>
        public static void OnHostRakePlaced(Board board, int gridX, int gridY)
        {
            if (!IsHost || !SyncActive)
            {
                return;
            }
            try
            {
                var m = Net.CreateMessage();
                if (m != null)
                {
                    m.Write((byte)PacketType.RakePlaced);
                    m.Write(gridX);
                    m.Write(gridY);
                    Net.SendReliableToClients(m);
                    ModEnv.Log("[钉耙] 主机放置 (" + gridX + "," + gridY + ")");
                }
            }
            catch (Exception ex)
            {
                ModEnv.Log("钉耙事件发送异常: " + ex.Message);
            }
        }

        /// <summary>Client：同位置镜像放置钉耙（消耗的是主机库存）。</summary>
        private static void OnRemoteRakePlaced(int gridX, int gridY)
        {
            var board = CurrentBoard;
            if (board == null || !SyncActive)
            {
                return;
            }
            try
            {
                var item = GridItem.GetNewGridItem();
                item.mGridItemType = GridItemType.Rake;
                item.mGridX = gridX;
                item.mGridY = gridY;
                item.mPosX = board.GridToPixelX(gridX, gridY);
                item.mPosY = board.GridToPixelY(gridX, gridY);
                item.mRenderOrder = Board.MakeRenderOrder(RenderLayer.GraveStone, item.mGridY, 9);
                board.mGridItems.Add(item);
                var reanim = board.CreateRakeReanim(item.mPosX, item.mPosY, 0);
                item.mGridItemReanimID = board.mApp.ReanimationGetID(reanim);
                item.mGridItemState = GridItemState.RakeAttracting;
                ModEnv.Log("[钉耙] 客户端镜像放置 (" + gridX + "," + gridY + ")");
            }
            catch (Exception ex)
            {
                ModEnv.Log("镜像放置钉耙异常: " + ex.Message);
            }
        }

        // ============================================================ Board 钩子

        public static void OnBoardUpdate(Board board)
        {
            _now = _clock.Elapsed.TotalSeconds;
            if (CurrentBoard == null || !ReferenceEquals(CurrentBoard, board))
            {
                CurrentBoard = board;
                ModEnv.Log("绑定棋盘实例");
            }
            if (!InGame)
            {
                return;
            }
            if (IsHost)
            {
                HostTick(board);
            }
            else
            {
                ClientTick(board);
            }
        }

        private static void HostTick(Board board)
        {
            // 帧级轻扫：本地死亡 → 解除注册（远端由 Retire 通知）
            SweepDeadObjects();

            double interval = 1.0 / Math.Max(5, ModEnv.GetConfig().SnapshotHz);
            if (_now < _nextSnapshotAt)
            {
                return;
            }
            _nextSnapshotAt = _now + interval;

            CaptureAndSend(board);
        }

        private static void ClientTick(Board board)
        {
            bool suppression = ClientSuppressionActive;
            if (suppression != _lastSuppression)
            {
                _lastSuppression = suppression;
                ModEnv.Log("客户端出怪抑制 " + (suppression ? "开启" : "关闭"));
            }
            if (suppression && board.mCurrentWave != _clientWaveLogged)
            {
                _clientWaveLogged = board.mCurrentWave;
                ModEnv.Log("[波次] 客户端 wave=" + board.mCurrentWave + "/" + board.mNumWaves);
            }

            // 快照把 hp 置 0 不会触发本地伤害链，这里按注册表补一刀"自然死亡"
            foreach (uint id in _objectIds)
            {
                if (!Registry.TryGetObject(id, out var obj) || obj == null)
                {
                    continue;
                }
                switch (obj)
                {
                    case Zombie z when !z.mDead && z.mBodyHealth <= 0:
                        try
                        {
                            z.DieNoLoot(false);
                        }
                        catch
                        {
                        }
                        break;
                    case Plant p when !p.mDead && p.mPlantHealth <= 0:
                        try
                        {
                            p.Die();
                        }
                        catch
                        {
                        }
                        break;
                }
            }
            SweepDeadObjects();
        }

        private static void SweepDeadObjects()
        {
            // id → object 侧走一遍：死亡即解除注册（数量 = 当前实体数，几十级，开销可忽略）
            for (int i = _objectIds.Count - 1; i >= 0; i--)
            {
                uint id = _objectIds[i];
                if (!Registry.TryGetObject(id, out var obj) || obj == null)
                {
                    _objectIds.RemoveAt(i);
                    continue;
                }
                bool dead = obj switch
                {
                    Zombie z => z.mDead,
                    Plant p => p.mDead,
                    _ => true,
                };
                if (dead)
                {
                    Registry.Remove(id);
                    _objectIds.RemoveAt(i);
                }
            }
        }

        // ============================================================ Host：快照采集

        private static void CaptureAndSend(Board board)
        {
            var snap = new SnapshotMsg
            {
                // 阳光完全独立：客户端不再应用快照里的这个字段（占位发本方值）
                SunMoney = board.mSunMoney,
                CurrentWave = board.mCurrentWave,
                NumWaves = board.mNumWaves,
                SunCountDown = board.mSunCountDown,
                // 波次倒计时同步：客户端的波次推进完全由主机节奏驱动
                ZombieCountDown = board.mZombieCountDown,
                ZombieCountDownStart = board.mZombieCountDownStart,
                Flags = (byte)((board.mPaused ? SnapshotMsg.FlagPaused : 0)
                             | (board.mLevelComplete ? SnapshotMsg.FlagLevelComplete : 0)
                             | (board.mApp != null && board.mApp.mGameScene == GameScenes.ZombiesWon ? SnapshotMsg.FlagZombiesWon : 0)),
                SeedTypes = _seedTypes,
                RefreshCounters = _refreshCounters,
                Refreshing = _refreshing,
                Active = _active,
                Zombies = _zombieStates,
                Plants = _plantStates,
            };

            // 卡槽（占位发送，客户端独立卡组不应用）
            var bank = board.mSeedBank;
            int slots = Math.Min(Packets.MaxSeedSlots, bank?.mNumPackets ?? 0);
            for (int i = 0; i < Packets.MaxSeedSlots; i++)
            {
                if (i < slots && bank.mSeedPackets[i] != null)
                {
                    var packet = bank.mSeedPackets[i];
                    _seedTypes[i] = (int)packet.mPacketType;
                    _refreshCounters[i] = packet.mRefreshCounter;
                    _refreshing[i] = packet.mRefreshing;
                    _active[i] = packet.mActive;
                }
                else
                {
                    _seedTypes[i] = -1;
                    _refreshCounters[i] = 0;
                    _refreshing[i] = false;
                    _active[i] = false;
                }
            }

            // 僵尸：懒分配 + 新实体入待宣布列表（开场预览僵尸不同步）
            _zombieStates.Clear();
            foreach (var z in board.mZombies)
            {
                if (z == null || z.mDead || z.mFromWave == GameConstants.ZOMBIE_WAVE_CUTSCENE)
                {
                    continue; // 开场预览僵尸不同步：各端有自己的，同步会造出双份+幽灵
                }
                if (!Registry.TryGetId(z, out uint id))
                {
                    id = Registry.Assign(z);
                    _objectIds.Add(id);
                    _announcedIds.Add(id); // 登记为"已宣布"，消失时才能发 Retire
                    _pendingZombieSpawns.Add(new NetZombieSpawn
                    {
                        NetId = id,
                        ZombieType = (int)z.mZombieType,
                        Row = z.mRow,
                        X = z.mPosX,
                        Y = z.mPosY,
                        Hp = z.mBodyHealth,
                    });
                }
                _zombieStates.Add(new NetZombieState { NetId = id, X = z.mPosX, Y = z.mPosY, Row = z.mRow, Hp = z.mBodyHealth });
            }

            // 植物：同上
            _plantStates.Clear();
            foreach (var p in board.mPlants)
            {
                if (p == null || p.mDead)
                {
                    continue;
                }
                if (!Registry.TryGetId(p, out uint id))
                {
                    id = Registry.Assign(p);
                    _objectIds.Add(id);
                    _announcedIds.Add(id);
                    _pendingPlantSpawns.Add(new NetPlantSpawn
                    {
                        NetId = id,
                        GridX = p.mPlantCol,
                        GridY = p.mRow,
                        SeedType = (int)p.mSeedType,
                        Hp = p.mPlantHealth,
                        LaunchCounter = p.mLaunchCounter,
                    });
                }
                _plantStates.Add(new NetPlantState { NetId = id, Hp = p.mPlantHealth });
            }

            // Retire：宣布过、但本帧已不在场（或已死）的实体
            SendRetires(board);

            // 发送
            if (_pendingZombieSpawns.Count > 0 || _pendingPlantSpawns.Count > 0)
            {
                var spawnMsg = Net.CreateMessage();
                if (spawnMsg != null)
                {
                    Packets.WriteSpawnBatch(spawnMsg, _pendingZombieSpawns, _pendingPlantSpawns);
                    Net.SendReliableToClients(spawnMsg);
                    ModEnv.Log("[生成] 主机发送: 僵尸x" + _pendingZombieSpawns.Count + " 植物x" + _pendingPlantSpawns.Count
                        + " (wave=" + board.mCurrentWave + ")");
                }
                _pendingZombieSpawns.Clear();
                _pendingPlantSpawns.Clear();
            }

            var msg = Net.CreateMessage();
            if (msg != null)
            {
                Packets.WriteSnapshot(msg, snap);
                Net.SendUnreliableToClients(msg);
            }
        }

        private static void SendRetires(Board board)
        {
            if (_announcedIds.Count == 0)
            {
                return;
            }

            // 逐实体单独成包（客户端按"每包一条 Retire"解析）
            foreach (uint id in _announcedIds)
            {
                bool stillAlive = false;
                byte kind = 0;
                if (Registry.TryGetObject(id, out var obj) && obj != null)
                {
                    switch (obj)
                    {
                        case Zombie z:
                            kind = 0;
                            stillAlive = !z.mDead && board.mZombies.Contains(z);
                            break;
                        case Plant p:
                            kind = 1;
                            stillAlive = !p.mDead && board.mPlants.Contains(p);
                            break;
                    }
                }
                if (stillAlive)
                {
                    continue;
                }
                var retireMsg = Net.CreateMessage();
                if (retireMsg != null)
                {
                    Packets.WriteRetire(retireMsg, id, kind);
                    Net.SendReliableToClients(retireMsg);
                }
            }

            // 清掉已退场 id（含对象丢失的）
            _announcedIds.RemoveWhere(id =>
            {
                bool alive = Registry.TryGetObject(id, out var obj) && obj != null && obj switch
                {
                    Zombie z => !z.mDead && board.mZombies.Contains(z),
                    Plant p => !p.mDead && board.mPlants.Contains(p),
                    _ => false,
                };
                return !alive;
            });
        }

        // ============================================================ Client：快照应用

        private static void ApplySnapshot(Board board, in SnapshotMsg s)
        {
            // 阳光独立：客户端的 mSunMoney 是自己的账，快照不覆盖
            board.mCurrentWave = s.CurrentWave;
            if (board.mCurrentWave != _clientWaveLogged)
            {
                _clientWaveLogged = board.mCurrentWave;
                ModEnv.Log("[波次] 客户端快照同步 wave=" + board.mCurrentWave + "/" + s.NumWaves);
            }
            board.mNumWaves = s.NumWaves;
            board.mSunCountDown = s.SunCountDown;
            // 波次倒计时同步：客户端的波次推进完全由主机节奏驱动
            board.mZombieCountDown = s.ZombieCountDown;
            board.mZombieCountDownStart = s.ZombieCountDownStart;
            bool hostPaused = (s.Flags & SnapshotMsg.FlagPaused) != 0;
            if (hostPaused != _remotePauseWanted)
            {
                _remotePauseWanted = hostPaused;
                ApplyPauseSync(board, "有玩家未恢复，游戏保持暂停");
            }

            // 卡组独立：不再用主机的卡槽覆盖客户端（客户端自己的卡/冷却自己管）

            if (s.Zombies != null)
            {
                foreach (var zs in s.Zombies)
                {
                    if (!Registry.TryGetObject(zs.NetId, out var obj) || obj is not Zombie z || z.mDead)
                    {
                        continue; // spawn 事件未到，跳过
                    }
                    // 位姿插值校正 + hp 硬覆盖
                    z.mPosX += (zs.X - z.mPosX) * 0.45f;
                    z.mPosY += (zs.Y - z.mPosY) * 0.45f;
                    if (z.mRow != zs.Row)
                    {
                        z.mRow = zs.Row;
                        z.mPosX = zs.X;
                        z.mPosY = zs.Y;
                    }
                    z.mBodyHealth = zs.Hp;
                }
            }

            if (s.Plants != null)
            {
                foreach (var ps in s.Plants)
                {
                    if (!Registry.TryGetObject(ps.NetId, out var obj) || obj is not Plant p || p.mDead)
                    {
                        continue;
                    }
                    p.mPlantHealth = ps.Hp;
                }
            }
        }

        private static void ApplySpawnBatch(Board board, List<NetZombieSpawn> zombies, List<NetPlantSpawn> plants)
        {
            ApplyingSync = true;
            int okZombies = 0, okPlants = 0;
            try
            {
                // 逐实体容错：一个失败不拖累同批其余实体
                foreach (var s in zombies)
                {
                    try
                    {
                        if (s.Row < 0 || s.Row >= Constants.MAX_GRIDSIZEY)
                        {
                            continue;
                        }
                        RegisterNextId = s.NetId;
                        var z = board.AddZombieInRow((ZombieType)s.ZombieType, s.Row, 0);
                        RegisterNextId = 0;
                        if (z != null)
                        {
                            z.mPosX = s.X;
                            z.mPosY = s.Y;
                            z.mBodyHealth = s.Hp;
                            Registry.Register(s.NetId, z);
                            _objectIds.Add(s.NetId);
                            okZombies++;
                        }
                    }
                    catch (Exception ex)
                    {
                        RegisterNextId = 0;
                        ModEnv.Log("应用僵尸 " + s.NetId + " (type=" + s.ZombieType + " row=" + s.Row + ") 异常: " + ex.Message);
                    }
                }
                foreach (var s in plants)
                {
                    try
                    {
                        if (s.GridX < 0 || s.GridY < 0)
                        {
                            continue;
                        }
                        RegisterNextId = s.NetId;
                        var p = board.AddPlant(s.GridX, s.GridY, (SeedType)s.SeedType, SeedType.None);
                        RegisterNextId = 0;
                        if (p != null)
                        {
                            p.mPlantHealth = s.Hp;
                            // 产出节奏创建即同步（首轮就对齐），后续每轮由 SunProduced 重同步
                            p.mLaunchCounter = Math.Max(0, s.LaunchCounter);
                            Registry.Register(s.NetId, p);
                            _objectIds.Add(s.NetId);
                            _productionSynced.Add(s.NetId);
                            okPlants++;
                            if (_pendingSunSync.Remove(s.NetId, out int counter))
                            {
                                ApplySunSync(p, s.NetId, counter);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        RegisterNextId = 0;
                        ModEnv.Log("应用植物 " + s.NetId + " (type=" + s.SeedType + " 格=" + s.GridX + "," + s.GridY + ") 异常: " + ex.Message);
                    }
                }
            }
            finally
            {
                ApplyingSync = false;
                RegisterNextId = 0;
            }
            if (okZombies > 0 || okPlants > 0)
            {
                ModEnv.Log("[生成] 应用: 僵尸x" + okZombies + " 植物x" + okPlants);
            }
        }

        private static void ApplyRetire(uint netId, byte kind)
        {
            if (!Registry.TryGetObject(netId, out var obj) || obj == null)
            {
                return;
            }
            try
            {
                switch (obj)
                {
                    case Zombie z when !z.mDead:
                        z.DieNoLoot(false);
                        break;
                    case Plant p when !p.mDead:
                        p.Die();
                        break;
                }
            }
            catch (Exception ex)
            {
                ModEnv.Log("应用退场事件异常: " + ex.Message);
            }
            Registry.Remove(netId);
            _objectIds.Remove(netId);
            _announcedIds.Remove(netId);
        }

        // ============================================================ 网络事件

        private static void OnPeerConnected(NetConnection conn, string reason)
        {
            MainThreadQueue.Post(() =>
            {
                if (IsHost)
                {
                    // 等客户端握手包再分配槽位并发结果
                }
                else
                {
                    var cfg = ModEnv.GetConfig();
                    var m = Net.CreateMessage();
                    if (m != null)
                    {
                        Packets.WriteHandshake(m, PacketType.HandshakeC2H, ProtocolVersion.Current,
                            LawnApp.AppVersionNumber, ModFingerprint.Fingerprint, cfg.Nickname);
                        Net.SendReliableToHost(m);
                    }
                }
            });
        }

        private static void OnPeerDisconnected(NetConnection conn, string reason)
        {
            MainThreadQueue.Post(() =>
            {
                int goneSlot = Net.SlotOf(conn); // -1 = 未知（客户端视角的"主机断开"等）

                if (Phase == SessionPhase.InGame)
                {
                    // 选卡等待中被对方退出/掉线 → 单机直接进关，不再卡门闩
                    if (AwaitingReady && _pendingStart != null)
                    {
                        AwaitingReady = false;
                        LevelActuallyStarted = true;
                        var start = _pendingStart;
                        _pendingStart = null;
                        start?.Invoke();
                    }
                    // 对方掉线时清除其暂停意愿并解冻——否则"任一方暂停就冻结"会永久卡住
                    if (goneSlot >= 0 && _pauseWanted[goneSlot])
                    {
                        _pauseWanted[goneSlot] = false;
                        ApplyPauseSync(CurrentBoard, "");
                    }
                    // 对局继续（单机回退），HUD 显示断开
                    SetStatus("连接断开，本局继续（单机）", true);
                    OnlineLobbyScreen.RefreshIfOpen();
                }
                else if (IsHost && (Phase == SessionPhase.InRoom || Phase == SessionPhase.HostingLobby))
                {
                    // 客人走了：主机留在房间继续等待（不退出、不重开），释放其槽位
                    if (goneSlot > 0)
                    {
                        Nicks[goneSlot] = "";
                        SlotOccupied[goneSlot] = false;
                        _readyFlags[goneSlot] = false;
                        _pauseWanted[goneSlot] = false;
                    }
                    Registry.Clear();
                    _announcedIds.Clear();
                    SetStatus("有玩家已离开，继续等待新玩家加入", false);
                    OnlineLobbyScreen.RefreshIfOpen();
                }
                else
                {
                    Phase = SessionPhase.Idle;
                    Registry.Clear();
                    _announcedIds.Clear();
                    SetStatus("连接断开: " + reason, true);
                    OnlineLobbyScreen.RefreshIfOpen();
                }
            });
        }

        // ============================================================ 消息路由（主线程）

        private static void OnData(NetIncomingMessage im)
        {
            if (im == null || im.LengthBytes < 1)
            {
                return;
            }
            var type = (PacketType)im.ReadByte();
            try
            {
                HandlePacket(type, im);
            }
            catch (Exception ex)
            {
                ModEnv.Log("处理消息 " + type + " 异常: " + ex);
            }
        }

        private static void HandlePacket(PacketType type, NetIncomingMessage im)
        {
            switch (type)
            {
                case PacketType.HandshakeC2H:
                {
                    // Host 侧：验证 + 分配槽位
                    Packets.ReadHandshake(im, out int proto, out string gameVer, out string fp, out string nick);
                    string reject = ValidateHandshake(proto, gameVer, fp);
                    int slot = reject == null ? Net.AssignSlot(im.SenderConnection) : -1;
                    if (slot < 0 && reject == null)
                    {
                        reject = "房间已满";
                    }
                    if (slot >= 0)
                    {
                        Nicks[slot] = nick;
                        SlotOccupied[slot] = true;
                        _roomReady[slot] = false;
                    }
                    var m = Net.CreateMessage();
                    if (m != null)
                    {
                        Packets.WriteHandshakeResult(m, reject == null, reject ?? "", slot);
                        Net.SendReliableToClients(m, reject != null ? im.SenderConnection : null);
                    }
                    if (reject != null)
                    {
                        ModEnv.Log("拒绝连接: " + reject);
                        im.SenderConnection?.Disconnect("房间已满或版本不一致");
                        break;
                    }
                    Phase = SessionPhase.InRoom;
                    BroadcastRoomState();
                    SetStatus("玩家 [" + nick + "] 已加入（座位 " + (slot + 1) + "）", false);
                    ModEnv.Log("握手通过，进房间，槽位=" + slot);
                    break;
                }

                case PacketType.HandshakeResult:
                {
                    Packets.ReadHandshakeResult(im, out bool ok, out string reason, out int slot);
                    if (ok)
                    {
                        MySlot = Math.Clamp(slot, 0, MaxPlayers - 1);
                        SlotOccupied[MySlot] = true;
                        Phase = SessionPhase.InRoom;
                        SetStatus("已连接主机（座位 " + (MySlot + 1) + "）", false);
                        ModEnv.Log("握手通过，进房间，槽位=" + MySlot);
                    }
                    else
                    {
                        SetStatus("被拒绝: " + reason, true);
                        Net.Shutdown();
                        Phase = SessionPhase.Idle;
                    }
                    break;
                }

                case PacketType.RoomState:
                {
                    Packets.ReadRoomState(im, out string[] nicks, out bool[] ready, out int levelIndex);
                    for (int i = 0; i < MaxPlayers && i < nicks.Length; i++)
                    {
                        Nicks[i] = nicks[i];
                        SlotOccupied[i] = Nicks[i].Length > 0 || i == 0;
                        _roomReady[i] = i > 0 && ready[i];
                    }
                    SelectedLevelIndex = levelIndex;
                    OnlineLobbyScreen.RefreshIfOpen();
                    break;
                }

                case PacketType.RoomReadyRequest:
                {
                    bool ready = Packets.ReadRoomReadyRequest(im);
                    if (IsHost)
                    {
                        OnRoomReadyFromClient(Net.SlotOf(im.SenderConnection), ready);
                    }
                    break;
                }

                case PacketType.Kick:
                {
                    string reason = Packets.ReadKick(im);
                    if (!IsHost)
                    {
                        SetStatus(reason, true);
                        LastChat = reason;
                        LastChatAge = 0;
                        Phase = SessionPhase.Idle;
                        Registry.Clear();
                        _announcedIds.Clear();
                        Net.Shutdown();
                        OnlineLobbyScreen.RefreshIfOpen();
                        ModEnv.Log("被主机踢出: " + reason);
                    }
                    break;
                }

                case PacketType.StartGame:
                {
                    Packets.ReadStartGame(im, out int mode, out _);
                    var app = GlobalStaticVars.gLawnApp;
                    if (app != null)
                    {
                        MainThreadQueue.Post(() => BeginGame(app, (GameMode)mode));
                    }
                    break;
                }

                case PacketType.InputPlant:
                {
                    Packets.ReadInputPlant(im, out int playerSlot, out int cardSlot, out int gx, out int gy);
                    var board = CurrentBoard;
                    if (IsHost && board != null)
                    {
                        int reqSlot = Net.SlotOf(im.SenderConnection);
                        MainThreadQueue.Post(() => InputExecutor.ExecutePlant(board, reqSlot, cardSlot, gx, gy));
                    }
                    break;
                }

                case PacketType.InputShovel:
                {
                    Packets.ReadInputShovel(im, out _, out int gx, out int gy);
                    var board = CurrentBoard;
                    if (IsHost && board != null)
                    {
                        MainThreadQueue.Post(() => InputExecutor.ExecuteShovel(board, gx, gy));
                    }
                    break;
                }

                case PacketType.SunProduced:
                {
                    uint plantId = im.ReadVariableUInt32();
                    int newCounter = im.ReadInt32();
                    if (!IsHost)
                    {
                        OnRemoteSunProduced(plantId, newCounter);
                    }
                    break;
                }

                case PacketType.SkySun:
                {
                    Packets.ReadSkySun(im, out float sx, out int cType, out int cd);
                    if (!IsHost)
                    {
                        OnRemoteSkySun(sx, cType, cd);
                    }
                    break;
                }

                case PacketType.CutsceneZombie:
                {
                    Packets.ReadCutsceneZombie(im, out int zType, out int cgx, out int cgy);
                    if (!IsHost)
                    {
                        OnRemoteCutsceneZombie(zType, cgx, cgy);
                    }
                    break;
                }

                case PacketType.Acceleration:
                {
                    Packets.ReadAcceleration(im, out int anum, out int aden);
                    if (!IsHost)
                    {
                        OnRemoteAcceleration(anum, aden);
                    }
                    break;
                }

                case PacketType.PauseRequest:
                {
                    bool paused = Packets.ReadPauseRequest(im);
                    if (IsHost)
                    {
                        OnRemotePauseRequest(Net.SlotOf(im.SenderConnection), paused);
                    }
                    break;
                }

                case PacketType.RakePlaced:
                {
                    int rgx = im.ReadInt32();
                    int rgy = im.ReadInt32();
                    if (!IsHost)
                    {
                        OnRemoteRakePlaced(rgx, rgy);
                    }
                    break;
                }

                case PacketType.Ready:
                {
                    Packets.ReadReady(im, out int[] dTypes, out int[] dImits);
                    if (IsHost)
                    {
                        OnRemoteReady(Net.SlotOf(im.SenderConnection), dTypes, dImits);
                    }
                    break;
                }

                case PacketType.AllReady:
                {
                    // 客户端：全员就绪 → 放行自己的选卡门闩
                    if (!IsHost && AwaitingReady && _pendingStart != null)
                    {
                        var start = _pendingStart;
                        _pendingStart = null;
                        AwaitingReady = false;
                        LevelActuallyStarted = true;
                        SetStatus("全员就绪，进入关卡", false);
                        MainThreadQueue.Post(start);
                    }
                    break;
                }

                case PacketType.CursorAt:
                {
                    // 客户端：主机中继的其他玩家光标
                    int pSlot = im.ReadByte();
                    float cx = im.ReadFloat();
                    float cy = im.ReadFloat();
                    if (!IsHost && pSlot >= 0 && pSlot < MaxPlayers && pSlot != MySlot)
                    {
                        RemoteCursorX[pSlot] = cx;
                        RemoteCursorY[pSlot] = cy;
                        RemoteCursorAge[pSlot] = 0;
                    }
                    break;
                }

                case PacketType.ChatAt:
                {
                    int pSlot = im.ReadByte();
                    string text = im.ReadString();
                    if (!IsHost && pSlot >= 0 && pSlot < MaxPlayers)
                    {
                        LastChat = (Nicks[pSlot].Length > 0 ? Nicks[pSlot] : "P" + (pSlot + 1)) + ": " + text;
                        LastChatAge = 0;
                    }
                    break;
                }

                case PacketType.SpawnBatch:
                {
                    var board = CurrentBoard;
                    if (!IsHost && board != null && SyncActive)
                    {
                        Packets.ReadSpawnBatch(im, _rxZombieSpawns, _rxPlantSpawns);
                        ApplySpawnBatch(board, _rxZombieSpawns, _rxPlantSpawns);
                    }
                    break;
                }

                case PacketType.Retire:
                {
                    Packets.ReadRetire(im, out uint netId, out byte kind);
                    if (!IsHost)
                    {
                        ApplyRetire(netId, kind);
                    }
                    break;
                }

                case PacketType.Snapshot:
                {
                    var board = CurrentBoard;
                    if (!IsHost && board != null && SyncActive)
                    {
                        var s = Packets.ReadSnapshot(im);
                        ApplySnapshot(board, in s);
                    }
                    break;
                }

                case PacketType.Chat:
                {
                    // 主机收到客户端聊天 → 显示并中继给其他人
                    string text = Packets.ReadChat(im);
                    int pSlot = Net.SlotOf(im.SenderConnection);
                    if (IsHost && pSlot > 0)
                    {
                        LastChat = (Nicks[pSlot].Length > 0 ? Nicks[pSlot] : "P" + (pSlot + 1)) + ": " + text;
                        LastChatAge = 0;
                        var relay = Net.CreateMessage();
                        if (relay != null)
                        {
                            Packets.WriteChatAt(relay, pSlot, text);
                            Net.SendReliableToClients(relay, im.SenderConnection);
                        }
                    }
                    break;
                }

                case PacketType.InputCursor:
                {
                    // 主机收到客户端光标 → 记录并中继给其他人
                    float x = im.ReadFloat();
                    float y = im.ReadFloat();
                    if (IsHost)
                    {
                        int pSlot = Net.SlotOf(im.SenderConnection);
                        if (pSlot > 0)
                        {
                            RemoteCursorX[pSlot] = x;
                            RemoteCursorY[pSlot] = y;
                            RemoteCursorAge[pSlot] = 0;
                            var relay = Net.CreateMessage();
                            if (relay != null)
                            {
                                Packets.WriteCursorAt(relay, pSlot, x, y);
                                Net.SendUnreliableToClients(relay, im.SenderConnection);
                            }
                        }
                    }
                    break;
                }

                case PacketType.Goodbye:
                {
                    string reason = Packets.ReadGoodbye(im);
                    ModEnv.Log("对方退出: " + reason);
                    // 对方主动退出：底部浮出提示
                    LastChat = reason;
                    LastChatAge = 0;
                    if (Phase == SessionPhase.InGame)
                    {
                        SetStatus(reason, true);
                        Net.Shutdown();
                    }
                    else if (IsHost && (Phase == SessionPhase.InRoom || Phase == SessionPhase.HostingLobby))
                    {
                        // 客人退出房间：主机留守，恢复等待状态与信标广播
                        Phase = SessionPhase.HostingLobby;
                        SetStatus(reason, false);
                        OnlineLobbyScreen.RefreshIfOpen();
                    }
                    else
                    {
                        SetStatus(reason, true);
                        Phase = SessionPhase.Idle;
                        Registry.Clear();
                        _announcedIds.Clear();
                        OnlineLobbyScreen.RefreshIfOpen();
                    }
                    break;
                }

                case PacketType.Ping:
                default:
                    break;
            }
        }

        private static string ValidateHandshake(int proto, string gameVer, string fingerprint)
        {
            if (proto != ProtocolVersion.Current)
            {
                return "协议版本不一致 (本端 " + ProtocolVersion.Current + " / 对端 " + proto + ")";
            }
            if (fingerprint != ModFingerprint.Fingerprint)
            {
                return "联机模组版本不一致";
            }
            if (gameVer != LawnApp.AppVersionNumber)
            {
                return "游戏版本不一致 (本端 " + LawnApp.AppVersionNumber + " / 对端 " + gameVer + ")";
            }
            return null;
        }

        // ============================================================ 广播/发送

        public static void BroadcastRoomState()
        {
            var m = Net.CreateMessage();
            if (m == null)
            {
                return;
            }
            Packets.WriteRoomState(m, Nicks, SelectedLevelIndex, _roomReady);
            Net.SendReliableToClients(m);
            OnlineLobbyScreen.RefreshIfOpen();
        }

        public static void SendPlantRequest(int playerSlot, int cardSlot, int gridX, int gridY)
        {
            var m = Net.CreateMessage();
            if (m == null)
            {
                return;
            }
            Packets.WriteInputPlant(m, playerSlot, cardSlot, gridX, gridY);
            Net.SendReliableToHost(m);
        }

        public static void SendShovelRequest(int playerSlot, int gridX, int gridY)
        {
            var m = Net.CreateMessage();
            if (m == null)
            {
                return;
            }
            Packets.WriteInputShovel(m, playerSlot, gridX, gridY);
            Net.SendReliableToHost(m);
        }

        public static void SendCursor(int playerSlot, float x, float y)
        {
            var m = Net.CreateMessage();
            if (m == null)
            {
                return;
            }
            if (IsHost)
            {
                Packets.WriteCursorAt(m, playerSlot, x, y);
                Net.SendUnreliableToClients(m);
            }
            else
            {
                Packets.WriteInputCursor(m, x, y);
                Net.SendUnreliableToHost(m);
            }
        }

        /// <summary>优雅退出通知（回主菜单/断开前调用）。</summary>
        public static void SendGoodbye(string reason)
        {
            if (!Net.IsConnected)
            {
                return;
            }
            var m = Net.CreateMessage();
            if (m == null)
            {
                return;
            }
            if (IsHost)
            {
                Packets.WriteGoodbye(m, reason);
                Net.SendReliableToClients(m);
            }
            else
            {
                Packets.WriteGoodbye(m, reason);
                Net.SendReliableToHost(m);
            }
        }

        public static void SendChat(string text)
        {
            if (string.IsNullOrEmpty(text) || !Net.IsConnected)
            {
                return;
            }
            var m = Net.CreateMessage();
            if (m == null)
            {
                return;
            }
            Packets.WriteChat(m, text);
            if (IsHost)
            {
                LastChat = Nicks[0] + ": " + text;
                LastChatAge = 0;
                var relay = Net.CreateMessage();
                if (relay != null)
                {
                    Packets.WriteChatAt(relay, 0, text);
                    Net.SendReliableToClients(relay);
                }
            }
            else
            {
                Net.SendReliableToHost(m);
            }
        }

        // ============================================================ 房间准备 / 踢人

        /// <summary>客人：设置自己的房间准备状态（主机权威同步给所有人）。</summary>
        public static void SetRoomReady(bool ready)
        {
            if (!IsHost)
            {
                var m = Net.CreateMessage();
                if (m != null)
                {
                    Packets.WriteRoomReadyRequest(m, ready);
                    Net.SendReliableToHost(m);
                }
            }
        }

        /// <summary>Host：收到客人的准备/取消准备。</summary>
        private static void OnRoomReadyFromClient(int slot, bool ready)
        {
            if (slot <= 0 || slot >= MaxPlayers
                || (Phase != SessionPhase.InRoom && Phase != SessionPhase.HostingLobby))
            {
                return;
            }
            if (_roomReady[slot] != ready)
            {
                _roomReady[slot] = ready;
                SetStatus("玩家 [" + (Nicks[slot].Length > 0 ? Nicks[slot] : "P" + (slot + 1)) + "] " + (ready ? "已准备" : "取消了准备"), false);
                ModEnv.Log("槽位 " + slot + (ready ? " 准备就绪" : " 取消准备"));
                BroadcastRoomState();
            }
        }

        /// <summary>某槽位的房间准备状态（主机恒视为已准备）。</summary>
        public static bool RoomReadyOf(int slot)
        {
            return slot == 0 || (slot >= 0 && slot < MaxPlayers && _roomReady[slot]);
        }

        /// <summary>本机（客户端视角）的房间准备状态。</summary>
        public static bool AmRoomReady => RoomReadyOf(IsHost ? 0 : MySlot);

        /// <summary>主机是否可以开局（至少一名客人且全部准备）。</summary>
        public static bool CanStartGame()
        {
            if (!IsHost)
            {
                return false;
            }
            bool hasGuest = false;
            for (int s = 1; s < MaxPlayers; s++)
            {
                if (SlotOccupied[s])
                {
                    if (!_roomReady[s])
                    {
                        return false;
                    }
                    hasGuest = true;
                }
            }
            return hasGuest;
        }

        /// <summary>主机踢出指定槽位的客人（发送 Kick 包，客人自行断开）。</summary>
        public static void KickGuest(int slot)
        {
            if (!IsHost || slot <= 0 || slot >= MaxPlayers || !SlotOccupied[slot])
            {
                return;
            }
            var conn = Net.ConnectionOfSlot(slot);
            if (conn == null)
            {
                return;
            }
            try
            {
                var kick = Net.CreateMessage();
                if (kick != null)
                {
                    Packets.WriteKick(kick, "你被主机踢出了房间");
                    Net.SendReliableTo(kick, conn);
                }
                SetStatus("已踢出玩家 [" + (Nicks[slot].Length > 0 ? Nicks[slot] : "P" + (slot + 1)) + "]", false);
                ModEnv.Log("踢出槽位 " + slot);
            }
            catch (Exception ex)
            {
                ModEnv.Log("踢人异常: " + ex.Message);
            }
        }

        // ============================================================ 局域网房间发现

        /// <summary>联机页"未连接"阶段每秒广播一次搜索；1s 一并清理过期房间。</summary>
        private static void TickDiscovery()
        {
            if (Phase != SessionPhase.Idle || !Ui.OnlineLobbyScreen.ScreenOpen || Net.IsConnected)
            {
                return;
            }
            if (_now < _nextDiscoverAt)
            {
                return;
            }
            _nextDiscoverAt = _now + 1.0;

            bool removed = false;
            var expired = new List<string>();
            foreach (var kv in Rooms)
            {
                if (_now - kv.Value.LastSeen > 5.0)
                {
                    expired.Add(kv.Key);
                }
            }
            foreach (var key in expired)
            {
                Rooms.Remove(key);
                removed = true;
            }
            if (removed)
            {
                RebuildRoomList();
            }

            if (Net.StartDiscoveryPeer())
            {
                int port = ModEnv.GetConfig().HostPort;
                Net.DiscoverRooms(port);   // 广播（部分网络可达）
                TickSweep(port);           // 单播扫段（热点/广播被隔离/多网卡时唯一可靠路径）
            }
        }

        /// <summary>扫段要探测的端口：默认端口 + 27150/27151（覆盖同机双开的自动回退端口）。</summary>
        private static IEnumerable<int> SweepPorts(int basePort)
        {
            yield return basePort;
            if (basePort != 27150)
            {
                yield return 27150;
            }
            if (basePort != 27151)
            {
                yield return 27151;
            }
        }

        private static void TickSweep(int port)
        {
            if (_now < _nextSweepAt)
            {
                return;
            }
            _nextSweepAt = _now + 5.0; // /24 全段每 5 秒扫一遍（254 个小 UDP 包 × 端口数）

            var local = GetLocalIPv4();
            if (local == null)
            {
                return;
            }
            byte[] baseBytes = local.GetAddressBytes();
            int selfLast = baseBytes[3];
            foreach (int sweepPort in SweepPorts(port))
            {
                for (int i = 1; i <= 254; i++)
                {
                    if (i == selfLast)
                    {
                        continue; // 跳过自己
                    }
                    baseBytes[3] = (byte)i;
                    Net.SendDiscoveryTo(new IPEndPoint(new IPAddress((byte[])baseBytes.Clone()), sweepPort));
                }
            }
            baseBytes[3] = (byte)selfLast;
        }

        /// <summary>
        /// 取本机出网 IPv4：UDP socket connect 探测（走真实路由表，避开多网卡/虚拟网卡
        /// 选错接口的问题；Android 的 NetworkInterface 枚举也可能为空）。不发任何数据。
        /// </summary>
        private static IPAddress GetLocalIPv4()
        {
            if (_now - _localIPv4At < 5.0)
            {
                return _localIPv4;
            }
            _localIPv4At = _now;
            try
            {
                using (var s = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp))
                {
                    s.Connect("8.8.8.8", 53);
                    _localIPv4 = (s.LocalEndPoint as IPEndPoint)?.Address;
                }
            }
            catch
            {
                _localIPv4 = null;
            }
            return _localIPv4;
        }

        /// <summary>Host 侧：响应局域网发现请求（仅"等待加入"阶段的房间对外可见）。</summary>
        private static NetOutgoingMessage BuildRoomBeacon()
        {
            if (Phase != SessionPhase.HostingLobby)
            {
                return null;
            }
            var m = Net.CreateMessage();
            if (m == null)
            {
                return null;
            }
            Packets.WriteRoomBeacon(m, Nicks[0], SelectedLevelIndex);
            return m;
        }

        /// <summary>Client 侧：收到房间广播（Poll 在主线程，直接改表）。</summary>
        private static void OnRoomDiscovered(string ip, NetIncomingMessage im)
        {
            if (string.IsNullOrEmpty(ip) || Phase != SessionPhase.Idle)
            {
                return;
            }
            try
            {
                Packets.ReadRoomBeacon(im, out string hostNick, out int levelIndex);
                if (!Rooms.TryGetValue(ip, out var room))
                {
                    room = new DiscoveredRoom { Ip = ip };
                    Rooms[ip] = room;
                }
                room.Port = im.SenderEndPoint?.Port ?? ModEnv.GetConfig().HostPort;
                room.HostNick = hostNick;
                room.LevelIndex = levelIndex;
                room.LastSeen = _now;
                RebuildRoomList();
            }
            catch (Exception ex)
            {
                ModEnv.Log("解析房间广播异常: " + ex.Message);
            }
        }

        private static void RebuildRoomList()
        {
            RoomList.Clear();
            foreach (var room in Rooms.Values)
            {
                var level = Levels[Math.Clamp(room.LevelIndex, 0, Levels.Length - 1)];
                room.DisplayText = room.HostNick + " 的房间 — " + level.Label;
                RoomList.Add(room);
            }
            RoomList.Sort((a, b) => b.LastSeen.CompareTo(a.LastSeen));
            RoomListVersion++;
        }

        /// <summary>UI 点击搜到的房间 → 直接加入。</summary>
        public static void JoinFromDiscovery(string ip)
        {
            var app = GlobalStaticVars.gLawnApp;
            if (app == null || string.IsNullOrEmpty(ip))
            {
                return;
            }
            var cfg = ModEnv.GetConfig();
            cfg.LastIp = ip;
            if (Rooms.TryGetValue(ip, out var room) && room.Port >= 1024)
            {
                cfg.LastPort = room.Port; // 用房间实际响应的端口，而不是上次手填的
            }
            ModEnv.SaveConfig();
            SetStatus("正在加入 " + ip + ":" + cfg.LastPort + " …", false);
            StartJoining(app, ip, cfg.LastPort);
            OnlineLobbyScreen.RefreshIfOpen();
        }

        // ============================================================ 工具

        public static void SetStatus(string text, bool isError)
        {
            StatusText = text ?? "";
            StatusIsError = isError;
        }

        /// <summary>Board.Update 后每帧推进 HUD 计时（不能在 Draw 里做）。</summary>
        public static void TickHud()
        {
            for (int s = 0; s < MaxPlayers; s++)
            {
                if (RemoteCursorAge[s] < 999)
                {
                    RemoteCursorAge[s] += 1.0 / 60.0;
                }
            }
            if (LastChatAge < 999)
            {
                LastChatAge += 1.0 / 60.0;
            }
        }
    }
}
