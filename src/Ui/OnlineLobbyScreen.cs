using System;
using Lawn;
using Sexy;
using Sexy.TodLib;

namespace PGvZOnlineMod.Ui
{
    /// <summary>
    /// 联机整页 —— 与"在线关卡"页同一家族的页面模式（镜像 Lawn.ChallengeScreen）：
    /// 全屏 Widget + DrawImageBox 圆角背景 + GameButton(IMAGE_SEEDCHOOSER_BUTTON2) 按钮族。
    /// 打开：主菜单 [联机] 按钮按下 → KillGameSelector → mGameScene=Challenge → 挂整页
    /// （镜像 LawnApp.ShowChallengeScreen）；关闭：移除整页 → ShowGameSelector。
    /// </summary>
    public class OnlineLobbyScreen : Widget, ButtonListener
    {
        public const int LobbyButtonId = 200;

        private const int BackId = 100;
        private const int HostId = 110;
        private const int JoinId = 111;
        private const int LevelId = 112;
        private const int StartId = 113;
        private const int DisconnectId = 114;
        private const int SaveNickId = 115;
        private const int ReadyBtnId = 116;
        private const int KickBtnIdBase = 125;
        private const int RoomButtonIdBase = 120;
        private const int RoomButtonCount = 4;

        private static OnlineLobbyScreen _inst;
        private static NewLawnButton _menuButton;
        private static GameSelector _boundSelector;

        private readonly LawnApp _app;
        private NewLawnButton _backButton;
        private NewLawnButton _hostBtn;
        private NewLawnButton _joinBtn;
        private NewLawnButton _levelBtn;
        private NewLawnButton _startBtn;
        private NewLawnButton _disconnectBtn;
        private IpInputWidget _ipEdit;
        private IpInputWidget _portEdit;
        private IpInputWidget _nickEdit;
        private NewLawnButton _saveNickBtn;
        private NewLawnButton _readyBtn;
        private readonly NewLawnButton[] _kickBtns = new NewLawnButton[Sync.Session.MaxPlayers];
        private NewLawnButton[] _roomButtons = new NewLawnButton[4];
        private long _roomListVersion = -1;
        private bool _roomPhase;
        private int _halfDeltaWidth;
        private int _halfDeltaHeight;

        public static bool ScreenOpen => _inst != null;

        // LawnCommon 是 internal，DrawImageBox（九宫格圆角背景）经缓存委托反射调用，
        // 静态构造一次，Draw 每帧只做一次委托调用
        private delegate void DrawImageBoxDelegate(Graphics g, TRect theDest, Image theComponentImage);

        private static readonly DrawImageBoxDelegate s_drawImageBox = CreateDrawImageBox();

        private static DrawImageBoxDelegate CreateDrawImageBox()
        {
            var type = typeof(LawnApp).Assembly.GetType("Lawn.LawnCommon");
            var m = type.GetMethod("DrawImageBox",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null, new[] { typeof(Graphics), typeof(TRect), typeof(Image) }, null);
            if (m == null)
            {
                throw new MissingMethodException("Lawn.LawnCommon", "DrawImageBox");
            }
            return (DrawImageBoxDelegate)Delegate.CreateDelegate(typeof(DrawImageBoxDelegate), m);
        }

        // ------------------------------------------------------------ 打开 / 关闭

        public static void Toggle(LawnApp app)
        {
            if (_inst != null)
            {
                Close(app);
            }
            else
            {
                OpenScreen(app);
            }
        }

        public static void OpenScreen(LawnApp app)
        {
            if (_inst != null || app == null)
            {
                return;
            }
            app.KillGameSelector();
            app.mGameScene = GameScenes.Challenge;
            var screen = new OnlineLobbyScreen(app);
            screen.Resize(0, 0, app.mWidth, app.mHeight);
            app.mWidgetManager.AddWidget(screen);
            app.mWidgetManager.BringToBack(screen);
            app.mWidgetManager.SetFocus(screen);
            _inst = screen;
            Core.ModEnv.Log("打开联机页");
        }

        public static void Close(LawnApp app)
        {
            if (_inst == null || app == null)
            {
                return;
            }
            app.mWidgetManager.RemoveWidget(_inst);
            app.SafeDeleteWidget(_inst);
            _inst = null;
            Core.ModEnv.Log("关闭联机页");
        }

        public static void CloseIfOpen(LawnApp app)
        {
            if (_inst != null)
            {
                Close(app);
            }
        }

        public static void RefreshIfOpen()
        {
            _inst?.RefreshUi();
        }

        // ------------------------------------------------------------ 主菜单入口按钮

        /// <summary>主泵调用：主菜单出现时挂 [联机] 按钮（"在线关卡"下方，同列同款）。</summary>
        public static void EnsureMenuButton(LawnApp app)
        {
            var selector = app?.mGameSelector;
            if (selector == null)
            {
                _menuButton = null;
                _boundSelector = null;
                return;
            }
            if (ReferenceEquals(_boundSelector, selector) && _menuButton != null)
            {
                // 跟随"在线关卡"按钮：其下 80px，同列节奏（用户存档 20 / 作者主页 100 / 在线关卡 180 / 联机 260）
                var anchor = selector.mOnlineLevelsButton;
                if (anchor != null)
                {
                    int wx = anchor.mX;
                    int wy = anchor.mY + 80;
                    if (_menuButton.mX != wx || _menuButton.mY != wy)
                    {
                        _menuButton.Resize(wx, wy, _menuButton.mWidth, _menuButton.mHeight);
                    }
                }
                return;
            }

            var btn = MainMenuButton.MakeNewButton(LobbyButtonId, selector, "联机", null,
                AtlasResources.IMAGE_MENUSTYLEFREE_ONLINE_LEVELS, 7, -5);
            var a = selector.mOnlineLevelsButton;
            btn.Resize(a?.mX ?? 40, (a?.mY ?? 180) + 80, btn.mWidth, btn.mHeight);
            selector.AddWidget(btn);
            _menuButton = btn;
            _boundSelector = selector;
            Core.ModEnv.Log("主菜单 [联机] 按钮已挂载（在线关卡下方）");
        }

        // ------------------------------------------------------------ 整页构造

        private OnlineLobbyScreen(LawnApp app)
        {
            _app = app;
            mClip = false;

            _backButton = MakeButton(BackId, "[BACK_TO_MENU]");
            _backButton.Resize(18, Constants.BackBufferSize.X - 40, 130, 40);

            // 昵称（与标题同区，独立一行）+ 立即保存按钮
            _nickEdit = new IpInputWidget { AllowAnyChar = true, MaxLength = 12 };
            _nickEdit.Resize(280, 54, 200, 34);
            _nickEdit.SetText(Core.ModEnv.GetConfig().Nickname ?? "玩家");
            AddWidget(_nickEdit);

            _saveNickBtn = MakeButton(SaveNickId, "保存昵称");
            _saveNickBtn.Resize(495, 50, 110, 40);

            _readyBtn = MakeButton(ReadyBtnId, "准备");
            _readyBtn.Resize(165, 250, 380, 40);
            _readyBtn.mVisible = false;
            for (int i = 0; i < Sync.Session.MaxPlayers; i++)
            {
                _kickBtns[i] = MakeButton(KickBtnIdBase + i, "踢出");
                _kickBtns[i].Resize(665, 104 + i * 44, 80, 32);
                _kickBtns[i].mVisible = false;
            }

            // 局域网房间列表（主路径）：4 个固定槽位按钮，按发现结果填充
            for (int i = 0; i < _roomButtons.Length; i++)
            {
                _roomButtons[i] = MakeButton(RoomButtonIdBase + i, "");
                _roomButtons[i].Resize(165, 138 + i * 48, 470, 40);
                _roomButtons[i].mVisible = false;
            }

            _levelBtn = MakeButton(LevelId, "关卡: ?");
            _levelBtn.Resize(165, 240, 380, 40);
            _startBtn = MakeButton(StartId, "开始游戏");
            _startBtn.Resize(575, 240, 150, 40);
            _disconnectBtn = MakeButton(DisconnectId, "断开连接");
            _disconnectBtn.Resize(340, 310, 160, 40);

            // 手动加入：第一行 IP+端口，第二行两个按钮
            _ipEdit = new IpInputWidget();
            _ipEdit.Resize(165, 370, 240, 34);
            _ipEdit.SetText(Core.ModEnv.GetConfig().LastIp ?? "127.0.0.1");
            AddWidget(_ipEdit);

            _portEdit = new IpInputWidget { MaxLength = 5 };
            _portEdit.Resize(425, 370, 100, 34);
            _portEdit.SetText(Core.ModEnv.GetConfig().LastPort.ToString());
            AddWidget(_portEdit);

            _hostBtn = MakeButton(HostId, "建立房间");
            _hostBtn.Resize(165, 412, 180, 40);
            _joinBtn = MakeButton(JoinId, "加入房间");
            _joinBtn.Resize(365, 412, 180, 40);

            RefreshUi();
        }

        private NewLawnButton MakeButton(int id, string label)
        {
            var b = GameButton.MakeNewButton(id, this, label, null,
                AtlasResources.IMAGE_SEEDCHOOSER_BUTTON2, AtlasResources.IMAGE_SEEDCHOOSER_BUTTON2_GLOW, AtlasResources.IMAGE_SEEDCHOOSER_BUTTON2_GLOW);
            b.mTextDownOffsetX = 1;
            b.mTextDownOffsetY = 1;
            b.mColors[0] = new SexyColor(42, 42, 90);
            b.mColors[1] = new SexyColor(42, 42, 90);
            AddWidget(b);
            return b;
        }

        private void RefreshUi()
        {
            bool room = Sync.Session.Phase != Sync.SessionPhase.Idle;
            _roomPhase = room;
            _hostBtn.mVisible = !room;
            _joinBtn.mVisible = !room;
            _ipEdit.mVisible = !room;
            _portEdit.mVisible = !room;
            _nickEdit.mVisible = !room;
            _saveNickBtn.mVisible = !room;
            _levelBtn.mVisible = room && Sync.Session.IsHost;
            _startBtn.mVisible = room && Sync.Session.IsHost;
            _readyBtn.mVisible = room && !Sync.Session.IsHost;
            _disconnectBtn.mVisible = room;
            for (int i = 0; i < Sync.Session.MaxPlayers; i++)
            {
                int guestSlot = i + 1;
                _kickBtns[i].mVisible = room && Sync.Session.IsHost && guestSlot < Sync.Session.MaxPlayers
                    && Sync.Session.SlotOccupied[guestSlot];
            }
            if (room && !Sync.Session.IsHost)
            {
                _readyBtn.mLabel = Sync.Session.AmRoomReady ? "取消准备" : "准备";
            }
            RefreshRoomRows();
            if (room)
            {
                bool host = Sync.Session.IsHost;
                bool connected = Sync.Session.Net.IsConnected;
                var level = Sync.Session.Levels[Math.Clamp(Sync.Session.SelectedLevelIndex, 0, Sync.Session.Levels.Length - 1)];
                _levelBtn.mLabel = "关卡: " + level.Label;
                _startBtn.mLabel = host ? "开始游戏" : "等待主机开始…";
                _levelBtn.mDisabled = !host || !connected;
                _startBtn.mDisabled = !host || !connected;
            }
            MarkDirty();
        }

        // ------------------------------------------------------------ 帧更新（镜像 ChallengeScreen.UpdateScreen）

        /// <summary>
        /// 每帧把自身 Resize 到虚拟尺寸（含宽屏 letterbox）并重算外扩量——
        /// 背景 Box 用 -halfDelta 起笔铺满整个窗口，消除内容区外的黑边。
        /// </summary>
        public override void Update()
        {
            base.Update();
            Resize(mX, mY, _app.mScreenScales.mVirtualWidth, _app.mScreenScales.mVirtualHeight);
            _halfDeltaWidth = (mWidth - Constants.BOARD_WIDTH) / 2;
            _halfDeltaHeight = (mHeight - Constants.BOARD_HEIGHT) / 2;
            if (_roomListVersion != Sync.Session.RoomListVersion)
            {
                _roomListVersion = Sync.Session.RoomListVersion;
                RefreshRoomRows();
            }
        }

        /// <summary>把发现结果填进 4 个固定槽位按钮（真按钮，点击即加入）。</summary>
        private void RefreshRoomRows()
        {
            for (int i = 0; i < RoomButtonCount; i++)
            {
                var room = i < Sync.Session.RoomList.Count ? Sync.Session.RoomList[i] : null;
                _roomButtons[i].mVisible = !_roomPhase && room != null;
                if (room != null)
                {
                    _roomButtons[i].mLabel = room.DisplayText;
                }
            }
        }

        // ------------------------------------------------------------ 绘制

        public override void Draw(Graphics g)
        {
            g.SetLinearBlend(true);
            s_drawImageBox(g, new TRect(-_halfDeltaWidth, -_halfDeltaHeight, mWidth, mHeight),
                AtlasResources.IMAGE_ALMANAC_ROUNDED_OUTLINE);

            TodCommon.TodDrawString(g, "植物娘联机", 500, 22, Resources.FONT_DWARVENTODCRAFT15,
                new SexyColor(220, 220, 220), DrawStringJustification.Center);

            if (!_roomPhase)
            {
                // 昵称行（输入框 280,54,200,34，标签与其左对齐同高）
                TodCommon.TodDrawString(g, "你的昵称:", 165, 62, Resources.FONT_BRIANNETOD16,
                    new SexyColor(220, 220, 220), DrawStringJustification.Left);

                // 区块一：局域网房间（房间是真按钮，这里只画区块标题与空态）
                TodCommon.TodDrawString(g, "局域网房间（自动搜索，点按钮加入）：", 165, 106, Resources.FONT_BRIANNETOD16,
                    new SexyColor(220, 220, 220), DrawStringJustification.Left);
                if (Sync.Session.RoomList.Count == 0)
                {
                    TodCommon.TodDrawString(g, "正在搜索…（对方点[建立房间]后几秒内出现）",
                        400, 154, Resources.FONT_BRIANNETOD12, new SexyColor(150, 150, 160), DrawStringJustification.Center);
                }

                // 区块二：手动加入（标题独占一行，输入框/端口/按钮在下一行，互不重叠）
                TodCommon.TodDrawString(g, "手动加入（填对方 IP 和端口）：", 165, 342, Resources.FONT_BRIANNETOD16,
                    new SexyColor(220, 220, 220), DrawStringJustification.Left);
                TodCommon.TodDrawString(g, "/", 413, 380, Resources.FONT_BRIANNETOD12,
                    new SexyColor(150, 150, 160), DrawStringJustification.Center);

                // 帮助文字（小字号，两行防溢出）
                TodCommon.TodDrawString(g, "搜不到房间？Windows 防火墙允许『专用+公用』（热点属公用）",
                    500, 492, Resources.FONT_BRIANNETOD12, new SexyColor(150, 150, 160), DrawStringJustification.Center);
                TodCommon.TodDrawString(g, "跨互联网请双方用虚拟局域网工具后填虚拟网 IP",
                    500, 512, Resources.FONT_BRIANNETOD12, new SexyColor(150, 150, 160), DrawStringJustification.Center);
            }
            else
            {
                bool connected = Sync.Session.Net.IsConnected;
                // 主机等待期间：15 秒没收到任何局域网搜索请求 → 大概率是防火墙挡了
                if (Sync.Session.IsHost && connected && (Sync.Session.DiscoveryReqAge < 0 || Sync.Session.DiscoveryReqAge > 15))
                {
                    TodCommon.TodDrawString(g, "对方一直搜不到本机？Windows 防火墙允许『专用网络+公用网络』，或把热点网络设为专用",
                        500, 205, Resources.FONT_BRIANNETOD16, new SexyColor(255, 200, 120), DrawStringJustification.Center);
                }
                // 玩家列表（4 行，44px 行距，含准备状态）
                int rowY = 60;
                for (int ps = 0; ps < Sync.Session.MaxPlayers; ps++)
                {
                    bool occupied = ps == 0 || Sync.Session.SlotOccupied[ps];
                    string tag = ps == 0 ? "主机" : "客人" + ps;
                    string nick = Sync.Session.Nicks[ps];
                    string who = tag + ": " + (occupied && !string.IsNullOrEmpty(nick) ? nick : "（等待加入）");
                    string state = "";
                    if (occupied && ps != 0)
                    {
                        state = Sync.Session.RoomReadyOf(ps) ? "  ✓已准备" : "  （未准备）";
                    }
                    g.SetColor(new SexyColor(90, 90, 90, 120));
                    g.FillRect(160, rowY - 4, 480, 34);
                    TodCommon.TodDrawString(g, who + state, 172, rowY + 6, Resources.FONT_BRIANNETOD16,
                        new SexyColor(240, 240, 240), DrawStringJustification.Left);
                    rowY += 44;
                }
                if (!connected)
                {
                    TodCommon.TodDrawString(g, "未连接", 500, 240, Resources.FONT_BRIANNETOD16,
                        new SexyColor(255, 120, 100), DrawStringJustification.Center);
                }
            }

            string status = Sync.Session.StatusText;
            if (!string.IsNullOrEmpty(status))
            {
                TodCommon.TodDrawString(g, status, 500, 462, Resources.FONT_BRIANNETOD16,
                    Sync.Session.StatusIsError ? new SexyColor(255, 120, 100) : new SexyColor(160, 255, 160),
                    DrawStringJustification.Center);
            }
        }

        // ------------------------------------------------------------ 输入保存

        /// <summary>把昵称/IP/端口写回配置（点建立/加入时落盘）。</summary>
        private void SaveIdentity()
        {
            var cfg = Core.ModEnv.GetConfig();
            string nick = _nickEdit.Text.Trim();
            if (!string.IsNullOrEmpty(nick))
            {
                cfg.Nickname = nick;
            }
            cfg.LastIp = _ipEdit.Text;
            if (int.TryParse(_portEdit.Text.Trim(), out int port) && port >= 1024 && port <= 65535)
            {
                cfg.LastPort = port;
            }
            Core.ModEnv.SaveConfig();
        }

        private int ParsePort()
        {
            if (int.TryParse(_portEdit.Text.Trim(), out int port) && port >= 1024 && port <= 65535)
            {
                return port;
            }
            return Core.ModEnv.GetConfig().HostPort;
        }

        // ------------------------------------------------------------ ButtonListener

        public void ButtonDepress(int theId)
        {
            switch (theId)
            {
                case BackId:
                    Sync.Session.CancelOrDisconnect();
                    Close(_app);
                    _app.mGameScene = GameScenes.Menu;
                    _app.ShowGameSelector();
                    break;
                case HostId:
                    SaveIdentity();
                    Sync.Session.StartHosting(_app);
                    RefreshUi();
                    break;
                case SaveNickId:
                {
                    var cfgN = Core.ModEnv.GetConfig();
                    string nick = _nickEdit.Text.Trim();
                    if (string.IsNullOrEmpty(nick))
                    {
                        Sync.Session.SetStatus("昵称不能为空", true);
                        break;
                    }
                    cfgN.Nickname = nick;
                    Core.ModEnv.SaveConfig();
                    if (Sync.Session.Phase == Sync.SessionPhase.HostingLobby
                        || Sync.Session.Phase == Sync.SessionPhase.InRoom)
                    {
                        if (Sync.Session.IsHost)
                        {
                            Sync.Session.Nicks[0] = nick;
                            Sync.Session.BroadcastRoomState(); // 房间信息里同步新昵称
                        }
                        else
                        {
                            Sync.Session.Nicks[Sync.Session.MySlot] = nick;
                        }
                    }
                    Sync.Session.SetStatus("昵称已保存并生效：" + nick, false);
                    break;
                }
                case JoinId:
                    SaveIdentity();
                    Sync.Session.StartJoining(_app, _ipEdit.Text, ParsePort());
                    RefreshUi();
                    break;
                case LevelId:
                    Sync.Session.CycleLevel();
                    break;
                case StartId:
                    Sync.Session.HostStartGame(_app);
                    break;
                case DisconnectId:
                    Sync.Session.CancelOrDisconnect();
                    RefreshUi();
                    break;
                case ReadyBtnId:
                    Sync.Session.SetRoomReady(!Sync.Session.AmRoomReady);
                    RefreshUi();
                    break;
                default:
                    if (theId >= KickBtnIdBase && theId < KickBtnIdBase + 3)
                    {
                        int kickSlot = theId - KickBtnIdBase + 1;
                        if (kickSlot < Sync.Session.MaxPlayers && Sync.Session.SlotOccupied[kickSlot])
                        {
                            Sync.Session.KickGuest(kickSlot);
                        }
                    }
                    else if (theId >= RoomButtonIdBase && theId < RoomButtonIdBase + RoomButtonCount)
                    {
                        int idx = theId - RoomButtonIdBase;
                        if (idx < Sync.Session.RoomList.Count)
                        {
                            Sync.Session.JoinFromDiscovery(Sync.Session.RoomList[idx].Ip);
                        }
                    }
                    break;
            }
        }

        public void ButtonPress(int theId)
        {
            try
            {
                _app.PlaySample(Resources.SOUND_BUTTONCLICK);
            }
            catch
            {
            }
        }

        public void ButtonPress(int theId, int theClickCount)
        {
        }

        public void ButtonDownTick(int theId)
        {
        }

        public void ButtonMouseEnter(int theId)
        {
        }

        public void ButtonMouseLeave(int theId)
        {
        }

        public void ButtonMouseMove(int theId, int theX, int theY)
        {
        }

        // ------------------------------------------------------------ 生命周期

        public override void RemovedFromManager(WidgetManager manager)
        {
            RemoveWidget(_backButton);
            RemoveWidget(_hostBtn);
            RemoveWidget(_joinBtn);
            RemoveWidget(_levelBtn);
            RemoveWidget(_startBtn);
            RemoveWidget(_disconnectBtn);
            RemoveWidget(_ipEdit);
            RemoveWidget(_portEdit);
            RemoveWidget(_nickEdit);
            RemoveWidget(_saveNickBtn);
            RemoveWidget(_readyBtn);
            for (int i = 0; i < _kickBtns.Length; i++)
            {
                if (_kickBtns[i] != null)
                {
                    RemoveWidget(_kickBtns[i]);
                }
            }
            base.RemovedFromManager(manager);
        }
    }
}
