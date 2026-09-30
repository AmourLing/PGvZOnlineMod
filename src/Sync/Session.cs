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
        private static bool _allReadySent;
        private static int _seedStateSent;
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

        // ---- 联机关卡表：直接取游戏自己的关卡定义 ChallengeScreen.gChallengeDefs
        //      （GameMode + 页签 + 名称），与主菜单"生存/挑战"页看到的是同一张表——
        //      不自造"难度×场景"矩阵，游戏里有哪个 GameMode 就能选哪个。
        //      握手已强制两端游戏版本一致，所以两端枚举出的顺序必然相同，levelIndex 可直接用。

        public class OnlineLevel
        {
            public GameMode Mode;
            public string Name;       // 已翻成中文的显示名
            public string PageLabel;  // 分类：生存 / 挑战 / 挑战2 / 解谜 / 额外
            public int PageOrder;
            public int Row, Col;
            public string FullLabel => PageLabel + " · " + Name;
        }

        private static OnlineLevel[] _levels;

        /// <summary>联机可选关卡（按页签→行→列排序）。首次访问时从游戏表构建。</summary>
        public static OnlineLevel[] Levels => _levels ??= BuildLevels();

        private static readonly (ChallengePage Page, string Label)[] PageNames =
        {
            (ChallengePage.Survival, "生存"),
            (ChallengePage.Challenge, "挑战"),
            (ChallengePage.Challenge2, "挑战2"),
            (ChallengePage.Puzzle, "解谜"),
            (ChallengePage.Extra, "额外"),
        };

        private static string PageLabelOf(ChallengePage page)
        {
            for (int i = 0; i < PageNames.Length; i++)
            {
                if (PageNames[i].Page == page)
                {
                    return PageNames[i].Label;
                }
            }
            return page.ToString();
        }

        private static int PageOrderOf(ChallengePage page)
        {
            for (int i = 0; i < PageNames.Length; i++)
            {
                if (PageNames[i].Page == page)
                {
                    return i;
                }
            }
            return int.MaxValue;
        }

        /// <summary>
        /// 开放判据（三条都要过）。表的全集是游戏自己的 gChallengeDefs，我们只点名排除，
        /// 每排除一条都得说得出它撞了下面哪一条（回归反过来验"表里每条要么开放、要么在名单里"）。
        /// ① 出怪可控：通用波次 SpawnZombieWave、模式专用 Challenge.UpdateZombieSpawning、
        ///    墓碑/屋顶空降/泳池出水 Board.SpawnZombiesFromGraves——三条造僵尸的入口客户端都已钩住，
        ///    僵尸只能由主机的 SpawnBatch 创建。
        /// ② 输入只有：卡槽种植、捡来的种子包 / 传送带卡种植、铲除、手套挪植物（转发手势 +
        ///    PlantMoved 广播）。锤子/开罐/拖拽换牌没有转发。
        /// ③ 过关判定必须是共享状态：阳光与卡组各人独立，所以"本地阳光攒够就通关"的不能开。
        /// 还有一类是"逐帧改世界状态、而那个状态本身不在同步字段里"（冰面按行融化、传送门随机搬位、
        /// 速度关重入整局更新），得先补批量同步才救得回来，同样先排除。
        /// 冒险模式另说：它是 (GameMode, 关卡号) 二元组，光给 GameMode 起不了关。
        /// </summary>
        private static bool IsOnlinePlayable(ChallengeDefinition def)
        {
            return def != null && !IsBlockedMode(def.mChallengeMode);
        }

        /// <summary>排除名单（public 是给离线回归当权威源用，不要在别处调）。</summary>
        public static bool IsBlockedMode(GameMode m)
        {
            switch (m)
            {
            // ② 输入没转发：29 锤子敲僵尸、48 点松鼠；
            //    20/23 宝石迷阵两关另有一层——它的棋盘就是 mBoard.mPlants 本身，
            //    每格宝石都是植物，与"植物层由主机权威广播"正面冲突。
            case GameMode.ChallengeWhackAZombie:
            case GameMode.ChallengeSquirrel:
            case GameMode.ChallengeBeghouled:
            case GameMode.ChallengeBeghouledTwist:
            // ③ 18 老虎机的通关判定是"本地阳光攒满 2000"，阳光各人独立 → 每人各自时刻通关。
            case GameMode.ChallengeSlotMachine:
            // 关卡随机数按机器取种：149 随机挑战两端可能落在完全不同的场地上（背景/行配置都不一样），
            // 场地不同就已经不是同一关了。
            case GameMode.ChallengeStageRandom:
            // 逐帧改世界状态、而该状态不同步：41 冰面按行融化（mIceTimer[]）、
            // 25 传送门位置本地随机搬、28 速度关在 Challenge.Update 里重入 Board.UpdateGame、
            // 30 最后一战有自己的阶段状态机 + 中途重选卡。
            case GameMode.ChallengeIce:
            case GameMode.ChallengePortalCombat:
            case GameMode.ChallengeSpeed:
            case GameMode.ChallengeLastStand:
            // 不是"操控植物守家"，或根本不是关卡：42 禅境花园、49 智慧树、122 僵尸水族馆、
            // 70 推销页、71 开场。
            case GameMode.ChallengeZenGarden:
            case GameMode.TreeOfWisdom:
            case GameMode.ChallengeZombiquarium:
            case GameMode.Upsell:
            case GameMode.Intro:
                return true;
            }
            // 50..59 砸罐子：开罐是独立输入，罐子里是随机的（僵尸/种子/阳光）必须先做主机裁决；
            // 60..69 我是僵尸：玩家操控的是僵尸，本模组的种植/铲子入口在那边根本不存在。
            return (m >= GameMode.ScaryPotter1 && m <= GameMode.ScaryPotterEndless)
                || (m >= GameMode.PuzzleIZombie1 && m <= GameMode.PuzzleIZombieEndless);
        }

        private static OnlineLevel[] BuildLevels()
        {
            var list = new List<OnlineLevel>();
            try
            {
                foreach (var def in ChallengeScreen.gChallengeDefs)
                {
                    if (!IsOnlinePlayable(def))
                    {
                        continue;
                    }
                    string name = TranslateGameString(def.mChallengeName);
                    string family = SurvivalFamilyOf(def.mChallengeMode);
                    list.Add(new OnlineLevel
                    {
                        Mode = def.mChallengeMode,
                        Name = family.Length > 0 && !name.Contains(family) ? family + " · " + name : name,
                        PageLabel = PageLabelOf(def.mPage),
                        PageOrder = PageOrderOf(def.mPage),
                        Row = def.mRow,
                        Col = def.mCol,
                    });
                }
            }
            catch (Exception ex)
            {
                // 离线验证台（VerifyHost）里没有游戏运行时，拿不到这张表；
                // 退回生存 18 关，保证关卡语义仍可被断言。
                ModEnv.Log("读取游戏关卡表失败，退回生存关: " + ex.Message);
                list.Clear();
            }
            if (list.Count == 0)
            {
                foreach (var m in FallbackSurvivalModes())
                {
                    list.Add(m);
                }
            }
            // 排序按"页签 → GameMode 数值"：游戏表里的行/列是给九宫格摆位用的，
            // 大泳池那几条是负数行列，按行列排会把它们顶到最前面（实测就是这毛病）。
            list.Sort((a, b) =>
            {
                int c = a.PageOrder.CompareTo(b.PageOrder);
                if (c != 0)
                {
                    return c;
                }
                return ((int)a.Mode).CompareTo((int)b.Mode);
            });
            return list.ToArray();
        }

        private static IEnumerable<OnlineLevel> FallbackSurvivalModes()
        {
            string[] scenes = { "白天前院", "黑夜", "泳池", "浓雾", "屋顶" };
            var families = new (GameMode Base, string Family)[]
            {
                (GameMode.SurvivalNormalStage1, "普通"),
                (GameMode.SurvivalHardStage1, "困难"),
                (GameMode.SurvivalEndlessStage1, "无尽"),
                (GameMode.SurvivalHellStage1, "地狱"),
            };
            foreach (var f in families)
            {
                for (int s = 0; s < scenes.Length; s++)
                {
                    yield return new OnlineLevel
                    {
                        Mode = f.Base + s,
                        Name = f.Family + " · " + scenes[s],
                        PageLabel = "生存",
                        PageOrder = 0,
                        Row = Array.IndexOf(families, f),
                        Col = s,
                    };
                }
            }
        }

        // TodStringFile 是 internal，翻译只能反射（与联机页反射 LawnCommon.DrawImageBox 同一家族的做法）
        private delegate string TranslateDelegate(string key);

        private static readonly TranslateDelegate s_translate = ResolveTranslate();

        private static TranslateDelegate ResolveTranslate()
        {
            try
            {
                var type = typeof(LawnApp).Assembly.GetType("Sexy.TodLib.TodStringFile");
                var m = type?.GetMethod("TodStringTranslate",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null);
                return m == null ? null : (TranslateDelegate)Delegate.CreateDelegate(typeof(TranslateDelegate), m);
            }
            catch
            {
                return null;
            }
        }

        private static string TranslateGameString(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "未命名";
            }
            string trimmed = key.Trim('[', ']');
            try
            {
                if (s_translate == null)
                {
                    return trimmed;
                }
                var v = s_translate(key);
                // 游戏找不到该串时会返回 "<Missing XXX>"，别把它甩给玩家看
                if (string.IsNullOrEmpty(v) || v.Contains("<Missing"))
                {
                    return trimmed;
                }
                return v;
            }
            catch
            {
                return trimmed;
            }
        }

        /// <summary>按 GameMode 给一句结束条件说明（无尽没有通关判定，UI 要提前讲清）。</summary>
        public static string LevelRuleHint(GameMode mode)
        {
            if (mode >= GameMode.SurvivalEndlessStage1 && mode <= GameMode.SurvivalEndlessStage5)
            {
                return "生存无尽：没有通关判定，打到阵亡或主动退出为止";
            }
            if (mode >= GameMode.SurvivalHardStage1 && mode <= GameMode.SurvivalHardStage5)
            {
                return "生存困难：每旗 20 波、共 10 旗";
            }
            if (mode >= GameMode.SurvivalHellStage1 && mode <= GameMode.SurvivalHellStage5)
            {
                return "生存地狱：5 个阶段，僵尸种类与掉落另算";
            }
            if (mode >= GameMode.SurvivalNormalStage1 && mode <= GameMode.SurvivalNormalStage5)
            {
                return "生存普通：每旗 10 波、共 10 旗";
            }
            if (mode == GameMode.ChallengeRainingSeeds)
            {
                return "天降种子：照常用卡组守关，天上不断掉免费种子包，各人接各人的那份";
            }
            return "目标与单人打这一关一致：出怪全由主机驱动，打完全部波次即整局胜利";
        }

        /// <summary>
        /// 生存族前缀：游戏自己的串只到"白天/黑夜/泳池"这一层，
        /// 难度是靠页面上的行位置区分的，列表里必须自己补上才不会四个"白天"撞名。
        /// 区间取自 GameMode 枚举定义（普通1-5 / 困难6-10 / 无尽11-15 / 地狱136-140）。
        /// </summary>
        public static string SurvivalFamilyOf(GameMode m)
        {
            if (m >= GameMode.SurvivalNormalStage1 && m <= GameMode.SurvivalNormalStage5) return "普通";
            if (m >= GameMode.SurvivalHardStage1 && m <= GameMode.SurvivalHardStage5) return "困难";
            if (m >= GameMode.SurvivalEndlessStage1 && m <= GameMode.SurvivalEndlessStage5) return "无尽";
            if (m >= GameMode.SurvivalHellStage1 && m <= GameMode.SurvivalHellStage5) return "地狱";
            return "";
        }

        public static int ClampLevelIndex(int index)
        {
            return Math.Clamp(index, 0, Levels.Length - 1);
        }

        /// <summary>表里实际出现的分类名（按排序顺序），下拉的分类行用它。</summary>
        public static List<string> LevelPageFilters()
        {
            var outList = new List<string> { "全部" };
            foreach (var lv in Levels)
            {
                if (!outList.Contains(lv.PageLabel))
                {
                    outList.Add(lv.PageLabel);
                }
            }
            return outList;
        }

        /// <summary>某分类下的关卡下标（pageLabel 为"全部"或空＝不过滤）。</summary>
        public static List<int> LevelIndicesOfPage(string pageLabel)
        {
            var idx = new List<int>();
            for (int i = 0; i < Levels.Length; i++)
            {
                if (string.IsNullOrEmpty(pageLabel) || pageLabel == "全部" || Levels[i].PageLabel == pageLabel)
                {
                    idx.Add(i);
                }
            }
            return idx;
        }

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
            Nicks[0] = LocalNick();
            // 主机自己的座位必须算占用：漏了它会导致
            // ① 主机点完选卡就以为"全员就绪"（AllPlayersReady 看不见主机自己），
            //    客人被 AllReady 放进战场而主机还停在选卡界面；
            // ② HUD 人数少算一个；③ 主机自己暂停意愿被忽略；④ 按人数加压数不到主机。
            SlotOccupied[0] = true;
            _readyFlags[0] = false;
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
            SlotOccupied[0] = false; // 主机位由 RoomState 置回；避免"先建房后加入"时残留
            Nicks[MySlot] = LocalNick();
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

        /// <summary>
        /// 主机改选关（难度 + 场景两维直选，比循环点按快得多）；
        /// 立即广播 RoomState，客人那边只是同步显示，不能改。
        /// </summary>
        /// <summary>主机改选关（下拉里点某一项）；立即广播 RoomState，客人只同步显示、不能改。</summary>
        public static void HostSetLevel(int levelIndex)
        {
            if (!IsHost || InGame)
            {
                return;
            }
            int idx = ClampLevelIndex(levelIndex);
            if (SelectedLevelIndex == idx)
            {
                return;
            }
            SelectedLevelIndex = idx;
            ModEnv.Log("主机改选关: " + Levels[idx].FullLabel + " (GameMode=" + (int)Levels[idx].Mode + ")");
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
            var mode = Levels[ClampLevelIndex(SelectedLevelIndex)].Mode;
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
            // 带数值：GameMode 存在同值别名（ChallengeStart == SurvivalNormalStage1 == 1），
            // 只打枚举名会把"生存·白天前院"显示成"ChallengeStart"
            ModEnv.Log("开局: " + mode + " (GameMode=" + (int)mode + ")");
            app.PreNewGame(mode, false);
        }

        // ============================================================ 开局就绪门闩

        private static void ResetReadyGate()
        {
            AwaitingReady = false;
            LevelActuallyStarted = false;
            _localReadySent = false;
            _allReadySent = false;
            _seedStateSent = 0;
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
                _readyFlags[IsHost ? 0 : MySlot] = true; // 本方就绪要自己记账（主机自己不会收到自己的包）
                SendReady(deckTypes, deckImitaters);
            }
            TryReleaseAllReady(); // 主机也可能是最后一个选完的，这条路同样要广播 AllReady
            BroadcastSeedState();
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
                BroadcastSeedState(); // 还没齐：先把"谁选完了"推给所有客人，界面上能看到进度
                return;
            }
            BroadcastSeedState();
            TryReleaseAllReady();
        }

        /// <summary>
        /// 主机侧统一放行：集齐在场所有人的就绪后放自己的门闩并广播 AllReady。
        /// 必须同时挂在"收到客人 Ready"和"主机自己点完选卡"两条路径上——
        /// 只挂在收包路径上，会出现"客人先选完、主机最后选完"时没人广播，
        /// 客人永远卡在选卡界面（2026-09-26 实机就是这个现象）。
        /// </summary>
        private static void TryReleaseAllReady()
        {
            if (!IsHost || _allReadySent || !AllPlayersReady())
            {
                return;
            }
            _allReadySent = true;
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

        /// <summary>某槽位的选卡是否已完成（含自己）——选卡界面按人显示状态用。</summary>
        public static bool SeedReadyOf(int slot)
        {
            return slot >= 0 && slot < MaxPlayers && _readyFlags[slot];
        }

        /// <summary>
        /// 主机把全员选卡状态广播出去（客人自己算不出别人的进度）。
        /// 位图没变化就不发；_seedStateSent 随 ResetReadyGate 复位。
        /// </summary>
        private static void BroadcastSeedState()
        {
            if (!IsHost || !Net.IsConnected)
            {
                return;
            }
            int mask = 0;
            for (int s = 0; s < MaxPlayers; s++)
            {
                if (_readyFlags[s])
                {
                    mask |= 1 << s;
                }
            }
            if (mask == _seedStateSent)
            {
                return;
            }
            _seedStateSent = mask;
            var m = Net.CreateMessage();
            if (m == null)
            {
                return;
            }
            Packets.WriteSeedState(m, mask);
            Net.SendReliableToClients(m);
        }

        /// <summary>Client：合并主机发来的选卡状态位图（自己那一位由本地记账，不覆盖）。</summary>
        private static void ApplySeedState(int mask)
        {
            for (int s = 0; s < MaxPlayers; s++)
            {
                if (s == MySlot)
                {
                    continue;
                }
                _readyFlags[s] = (mask & (1 << s)) != 0;
            }
            ModEnv.Log("收到选卡状态：位图 " + Convert.ToString(mask, 2).PadLeft(MaxPlayers, '0'));
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

        private static LawnApp _app;

        /// <summary>
        /// 本端显示名：直接用游戏自己的玩家名（存档里那个，主菜单"欢迎 XXX"同一来源），
        /// 联机页不再单独设一份昵称——两处各存一份迟早对不上。名字为空时回落到"玩家"。
        /// </summary>
        public static string LocalNick()
        {
            try
            {
                string name = _app?.mPlayerInfo?.mName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
            catch
            {
            }
            return "玩家";
        }

        public static void Pump(LawnApp app)
        {
            _app = app;
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
            Ui.ChatWidget.Sync(app);
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
            var req = Net.CreateMessage();
            if (req != null)
            {
                Packets.WritePauseRequest(req, slot, paused);
                if (IsHost)
                {
                    // 主机是裁决者：自己的暂停意愿也必须发出去，
                    // 否则客人永远看不到"有人按了暂停"（实机就是这样不同步的）
                    Net.SendReliableToClients(req);
                }
                else
                {
                    Net.SendReliableToHost(req);
                }
            }
            ApplyPauseSync(board, "有玩家未恢复，游戏保持暂停");
        }

        /// <summary>Host：收到某槽位客户端的暂停/恢复请求 → 本地裁决后转发给其余客人。</summary>
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
            // 三人群里客人之间看不到彼此的暂停：由主机把这一格的意愿原样转给其余客人
            var relay = Net.CreateMessage();
            if (relay != null)
            {
                Packets.WritePauseRequest(relay, slot, paused);
                Net.SendReliableToClients(relay);
            }
        }

        /// <summary>Client：收到某槽位的暂停/恢复（主机裁决后转发过来的）。</summary>
        private static void OnPauseStateFromHost(int slot, bool paused)
        {
            var board = CurrentBoard;
            if (board == null || !InGame || slot < 0 || slot >= MaxPlayers || slot == MySlot)
            {
                return;
            }
            if (_pauseWanted[slot] != paused)
            {
                _pauseWanted[slot] = paused;
                ModEnv.Log("槽位 " + slot + (paused ? " 暂停" : " 恢复"));
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

        // ============================================================ 天降可用种子包同步

        /// <summary>
        /// Host：模式刚掉下一枚可用种子包（19 天降种子 / 131 僵尸博士2 的种子雨）
        /// → 广播落点/类型/下一次掉落的倒计时。客户端抑制自己的随机雨，按这里的事件
        /// 掉"自己那一份"——种子包和阳光一样是各收各的资源，只有"种下去"要主机裁决。
        /// </summary>
        public static void OnHostRainSeedPacket(Coin coin, int nextDropCounter)
        {
            if (!IsHost || !SyncActive || coin == null)
            {
                return;
            }
            try
            {
                var m = Net.CreateMessage();
                if (m == null)
                {
                    return;
                }
                Packets.WriteRainSeedPacket(m, coin.mPosX, (int)coin.mUsableSeedType, nextDropCounter);
                Net.SendReliableToClients(m);
                ModEnv.Log("[天降种子] x=" + (int)coin.mPosX + " 类型=" + coin.mUsableSeedType
                    + " 下次掉落=" + nextDropCounter);
            }
            catch (Exception ex)
            {
                ModEnv.Log("天降种子事件发送异常: " + ex.Message);
            }
        }

        /// <summary>Client：同位置掉一枚同类型的种子包，并把掉落排期对齐主机。</summary>
        private static void OnRemoteRainSeedPacket(float x, int seedType, int nextDropCounter)
        {
            var board = CurrentBoard;
            if (board == null || !SyncActive || IsHost)
            {
                return;
            }
            try
            {
                board.AddCoin((int)x, 60, CoinType.UsableSeedPacket, CoinMotion.FromSkySlow)
                    .mUsableSeedType = (SeedType)seedType;
                if (board.mChallenge != null)
                {
                    board.mChallenge.mChallengeStateCounter = nextDropCounter;
                }
            }
            catch (Exception ex)
            {
                ModEnv.Log("同步天降种子异常: " + ex.Message);
            }
        }

        // ============================================================ 植物挪格同步（手套）

        /// <summary>
        /// 植物换了格子——快照只认"多出来"和"消失"，位置变了看不出来，所以必须单独广播。
        /// 走原生 `Challenge.MovePlant`，顺手把莲叶/花盆这些挂在旧格的网格物一起带过去。
        /// </summary>
        public static void OnHostPlantMoved(Plant plant, int gridX, int gridY)
        {
            if (!IsHost || !SyncActive || plant == null)
            {
                return;
            }
            try
            {
                if (!Registry.TryGetId(plant, out uint id))
                {
                    return; // 这株植物还没进过同步（开局前的预览植物之类），不用广播
                }
                var m = Net.CreateMessage();
                if (m == null)
                {
                    return;
                }
                Packets.WritePlantMoved(m, id, gridX, gridY);
                Net.SendReliableToClients(m);
                ModEnv.Log("[挪植物] 主机广播 netId=" + id + " → " + gridX + "," + gridY);
            }
            catch (Exception ex)
            {
                ModEnv.Log("挪植物广播异常: " + ex.Message);
            }
        }

        /// <summary>
        /// Client：让本地那株同 netId 的植物走原生 MovePlant 落到新格（莲叶/花盆一起带过去）。
        /// 不会再弹回主机——`OnHostPlantMoved` 只对主机发言，所以这里不需要额外标志。
        /// </summary>
        public static void OnRemotePlantMoved(uint plantNetId, int gridX, int gridY)
        {
            var board = CurrentBoard;
            if (board?.mChallenge == null || !SyncActive || IsHost || plantNetId == 0)
            {
                return;
            }
            try
            {
                if (!Registry.TryGetObject(plantNetId, out var obj) || obj is not Plant plant
                    || plant.mDead || gridX < 0 || gridY < 0 || gridY >= Constants.MAX_GRIDSIZEY)
                {
                    return;
                }
                board.mChallenge.MovePlant(plant, gridX, gridY);
            }
            catch (Exception ex)
            {
                ModEnv.Log("同步挪植物异常: " + ex.Message);
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
                    Packets.WriteRakePlaced(m, gridX, gridY);
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

        // ============================================================ 按人数加压（主机权威）

        /// <summary>在场玩家数（含主机）；槽位占用即计入。</summary>
        public static int LivePlayerCount()
        {
            int n = 0;
            for (int s = 0; s < MaxPlayers; s++)
            {
                if (SlotOccupied[s])
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>
        /// 主机生成僵尸时按在场人数放大体血，上限同乘——断头/断臂/伤害帧/巨人扔小鬼
        /// 都是按比例判定的，只放大当前值会让这些阈值错位。
        /// 护具（头盔/盾牌/飞行）不放大：它们的耐久本就不进协议，各端各自模拟，
        /// 只给主机加会让两端差得更远。
        /// 只作用于主机：客户端的镜像僵尸由 SpawnBatch 的 Hp/MaxHp 落值，不会二次放大。
        /// </summary>
        public static void BoostHostZombie(Zombie z, int fromWave)
        {
            if (z == null || !IsHost || !SyncActive || fromWave == GameConstants.ZOMBIE_WAVE_CUTSCENE)
            {
                return;
            }
            float step = ModEnv.GetConfig().ZombieHpPerExtraPlayer;
            int players = LivePlayerCount();
            if (step <= 0f || players < 2)
            {
                return;
            }
            float mult = 1f + (players - 1) * step;
            z.mBodyHealth = (int)(z.mBodyHealth * mult);
            z.mBodyMaxHealth = (int)(z.mBodyMaxHealth * mult);
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
                        MaxHp = z.mBodyMaxHealth,
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
                            // 上限也要落：主机加压后按比例判定的断头/伤害帧/扔小鬼才对得上
                            if (s.MaxHp > 0)
                            {
                                z.mBodyMaxHealth = s.MaxHp;
                            }
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
                    var m = Net.CreateMessage();
                    if (m != null)
                    {
                        Packets.WriteHandshake(m, PacketType.HandshakeC2H, ProtocolVersion.Current,
                            LawnApp.AppVersionNumber, ModFingerprint.Fingerprint, LocalNick());
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

                case PacketType.InputPlantCoin:
                {
                    Packets.ReadInputPlantCoin(im, out _, out int seedType, out int imitater, out int gx, out int gy);
                    var board = CurrentBoard;
                    if (IsHost && board != null)
                    {
                        MainThreadQueue.Post(() => InputExecutor.ExecutePlantCoin(board, seedType, imitater, gx, gy));
                    }
                    break;
                }

                case PacketType.InputMovePlant:
                {
                    Packets.ReadInputMovePlant(im, out _, out uint plantId, out int mx, out int my, out int click);
                    var board = CurrentBoard;
                    if (IsHost && board != null)
                    {
                        MainThreadQueue.Post(() => InputExecutor.ExecuteMovePlant(board, plantId, mx, my, click));
                    }
                    break;
                }

                case PacketType.PlantMoved:
                {
                    Packets.ReadPlantMoved(im, out uint plantId, out int gx, out int gy);
                    if (!IsHost)
                    {
                        OnRemotePlantMoved(plantId, gx, gy);
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

                case PacketType.RainSeedPacket:
                {
                    Packets.ReadRainSeedPacket(im, out float rx, out int seedType, out int nextCounter);
                    if (!IsHost)
                    {
                        OnRemoteRainSeedPacket(rx, seedType, nextCounter);
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
                    Packets.ReadPauseRequest(im, out int pSlot, out bool paused);
                    if (IsHost)
                    {
                        // 主机侧槽位以连接为准（不采信客人自报的 slot）
                        OnRemotePauseRequest(Net.SlotOf(im.SenderConnection), paused);
                    }
                    else if (pSlot >= 0 && pSlot < MaxPlayers && pSlot != MySlot)
                    {
                        OnPauseStateFromHost(pSlot, paused);
                    }
                    break;
                }

                case PacketType.RakePlaced:
                {
                    Packets.ReadRakePlaced(im, out int rgx, out int rgy);
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

                case PacketType.SeedState:
                {
                    int mask = Packets.ReadSeedState(im);
                    if (!IsHost)
                    {
                        ApplySeedState(mask);
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

        /// <summary>客户端把"手里的可用种子包"种下去：种子包归自己，落点由主机裁决。</summary>
        public static void SendPlantCoinRequest(int playerSlot, int seedType, int imitaterType, int gridX, int gridY)
        {
            var m = Net.CreateMessage();
            if (m == null)
            {
                return;
            }
            Packets.WriteInputPlantCoin(m, playerSlot, seedType, imitaterType, gridX, gridY);
            Net.SendReliableToHost(m);
        }

        /// <summary>客户端把手套里的植物放下：手势原样交主机重放，落点结论由主机给。</summary>
        public static void SendMovePlantRequest(int playerSlot, uint plantNetId, int x, int y, int clickCount)
        {
            var m = Net.CreateMessage();
            if (m == null)
            {
                return;
            }
            Packets.WriteInputMovePlant(m, playerSlot, plantNetId, x, y, clickCount);
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
                var level = Levels[ClampLevelIndex(room.LevelIndex)];
                // 带上 IP：自动搜房失败时，玩家照着这行手填就行
                room.DisplayText = room.HostNick + " 的房间 — " + level.FullLabel + "  [" + room.Ip + "]";
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
