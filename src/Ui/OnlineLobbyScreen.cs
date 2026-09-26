using System;
using System.Collections.Generic;
using Lawn;
using Sexy;
using Sexy.TodLib;

namespace PGvZOnlineMod.Ui
{
    /// <summary>
    /// 联机整页 —— 与"在线关卡"页同一家族的页面模式（镜像 Lawn.ChallengeScreen）：
    /// 全屏 Widget + DrawImageBox 圆角背景 + GameButton(IMAGE_SEEDCHOOSER_BUTTON2) 按钮族。
    /// 打开：主菜单 [联机] 按钮按下 → KillGameSelector → mGameScene=Challenge → 挂整页；
    /// 关闭：移除整页 → ShowGameSelector。
    ///
    /// 排版两条铁律（都是实机截图里踩出来的）：
    /// 1) NewLawnButton 的贴图按**原始尺寸**绘制（ButtonWidget.DrawImage 传源矩形，不拉伸），
    ///    而标签按 mWidth 居中 —— 把按钮 Resize 成 330/470 宽只会让文字飘到图外面。
    ///    所以真按钮一律用贴图原始大小（游戏自己也是这样，见 AwardScreen 用 image.mWidth）。
    /// 2) 需要"整条宽 bar"的地方（房间列表 / 座位行 / 踢出 / 选关下拉）不用按钮控件，
    ///    改为自绘 + 在本控件 MouseDown 里做命中测试。
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
        private const int LvlPageIdBase = 150; // 下拉的分类行
        private const int LvlRowIdBase = 160;  // 下拉的关卡条目行
        private const int LvlPrevId = 170;
        private const int LvlNextId = 171;
        internal const int TabCount = 6;       // 全部 + 五个页签（离线回归会拿它当分类数的上限校验）
        private const int RowCount = 4;        // 下拉每页显示几关

        private enum HitKind { Info, Seat, RoomRow, Kick, Tab, LevelRow, Pager }

        /// <summary>自绘条目：几何 + 文案 + 点下去等价于按哪个按钮 id（Id 为负＝纯展示）。</summary>
        private struct Hit
        {
            public HitKind Kind;
            public int X, Y, W, H, Id;
            public string Left, Main, Right;
            public bool Selected;
        }

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
        private NewLawnButton _saveNickBtn;
        private NewLawnButton _readyBtn;
        private IpInputWidget _ipEdit;
        private IpInputWidget _portEdit;
        private IpInputWidget _nickEdit;

        private bool _dropOpen;
        private int _dropPage;
        private int _dropPages = 1;
        private int _dropFilter; // 0 = 全部
        private List<string> _dropFilters;
        private List<int> _dropIndices = new List<int>();

        private readonly List<Hit> _hits = new List<Hit>();
        private long _roomListVersion = -1;
        private bool _roomPhase;
        private int _halfDeltaWidth;
        private int _halfDeltaHeight;

        public static bool ScreenOpen => _inst != null;

        /// <summary>按钮贴图原始尺寸：游戏自己也是按这个大小摆的，不要拉伸。</summary>
        private static int BtnW
        {
            get
            {
                var img = AtlasResources.IMAGE_SEEDCHOOSER_BUTTON2;
                return img != null && img.mWidth > 40 ? img.mWidth : 130;
            }
        }

        private static int BtnH
        {
            get
            {
                var img = AtlasResources.IMAGE_SEEDCHOOSER_BUTTON2;
                return img != null && img.mHeight > 20 ? img.mHeight : 40;
            }
        }

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
            mWantsFocus = true;

            int bw = BtnW, bh = BtnH;

            _backButton = MakeButton(BackId, "[BACK_TO_MENU]");
            _backButton.Resize(18, Constants.BackBufferSize.X - 40, bw, bh);

            // 大厅：昵称行
            _nickEdit = new IpInputWidget { AllowAnyChar = true, MaxLength = 12 };
            _nickEdit.Resize(268, 50, 200, 34);
            _nickEdit.SetText(Core.ModEnv.GetConfig().Nickname ?? "玩家");
            AddWidget(_nickEdit);
            _saveNickBtn = MakeButton(SaveNickId, "保存昵称");
            _saveNickBtn.Resize(486, 46, bw, bh);

            // 大厅：手动加入
            _ipEdit = new IpInputWidget();
            _ipEdit.Resize(160, 300, 240, 34);
            _ipEdit.SetText(Core.ModEnv.GetConfig().LastIp ?? "127.0.0.1");
            AddWidget(_ipEdit);
            _portEdit = new IpInputWidget { MaxLength = 5 };
            _portEdit.Resize(420, 300, 90, 34);
            _portEdit.SetText(Core.ModEnv.GetConfig().LastPort.ToString());
            AddWidget(_portEdit);
            _hostBtn = MakeButton(HostId, "建立房间");
            _hostBtn.Resize(160, 346, bw, bh);
            _joinBtn = MakeButton(JoinId, "加入房间");
            _joinBtn.Resize(170 + bw, 346, bw, bh);

            // 房间内：真按钮一律贴图原始尺寸，宽 bar 全部自绘
            _levelBtn = MakeButton(LevelId, "选关");
            _levelBtn.Resize(160, 232, bw, bh);
            _levelBtn.mVisible = false;
            _startBtn = MakeButton(StartId, "开始游戏");
            _startBtn.Resize(160, 280, bw, bh);
            _startBtn.mVisible = false;
            _readyBtn = MakeButton(ReadyBtnId, "准备");
            _readyBtn.Resize(160, 280, bw, bh);
            _readyBtn.mVisible = false;
            _disconnectBtn = MakeButton(DisconnectId, "离开房间");
            _disconnectBtn.Resize(170 + bw, 280, bw, bh);
            _disconnectBtn.mVisible = false;

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

        // ------------------------------------------------------------ 状态刷新（同时重建自绘条目）

        private void RefreshUi()
        {
            bool room = Sync.Session.Phase != Sync.SessionPhase.Idle;
            bool host = Sync.Session.IsHost;
            bool connected = Sync.Session.Net.IsConnected;
            _roomPhase = room;
            _hits.Clear();

            _hostBtn.mVisible = !room;
            _joinBtn.mVisible = !room;
            _ipEdit.mVisible = !room;
            _portEdit.mVisible = !room;
            _nickEdit.mVisible = !room;
            _saveNickBtn.mVisible = !room;
            _disconnectBtn.mVisible = room;

            bool canPick = room && host && connected;
            if (!canPick)
            {
                _dropOpen = false; // 否则[选关]按钮隐掉了，展开的下拉关不上
            }
            _levelBtn.mVisible = canPick;
            _startBtn.mVisible = room && host;
            _readyBtn.mVisible = room && !host;
            if (_readyBtn.mVisible)
            {
                _readyBtn.mLabel = Sync.Session.AmRoomReady ? "取消准备" : "准备";
            }

            if (!room)
            {
                BuildLobbyHits();
            }
            else
            {
                BuildRoomHits(host, connected);
            }
            MarkDirty();
        }

        /// <summary>局域网房间条目：自绘整条 bar，点一条即加入。</summary>
        private void BuildLobbyHits()
        {
            for (int i = 0; i < RoomButtonCount && i < Sync.Session.RoomList.Count; i++)
            {
                _hits.Add(new Hit
                {
                    Kind = HitKind.RoomRow,
                    X = 160,
                    Y = 112 + i * 38,
                    W = 480,
                    H = 34,
                    Id = RoomButtonIdBase + i,
                    Main = Clip(Sync.Session.RoomList[i].DisplayText, 34),
                });
            }
        }

        private void BuildRoomHits(bool host, bool connected)
        {
            if (_dropOpen)
            {
                // 展开时只留下拉：座位条与它 y 区间重叠，两张一起画就又糊成一团了
                BuildDropdownHits();
                return;
            }

            int selected = Sync.Session.ClampLevelIndex(Sync.Session.SelectedLevelIndex);
            var level = Sync.Session.Levels[selected];

            for (int ps = 0; ps < Sync.Session.MaxPlayers; ps++)
            {
                bool occupied = ps == 0 || Sync.Session.SlotOccupied[ps];
                string nick = occupied
                    ? (!string.IsNullOrEmpty(Sync.Session.Nicks[ps]) ? Sync.Session.Nicks[ps] : "（未命名）")
                    : "（等待加入…）";
                if (ps == Sync.Session.MySlot)
                {
                    nick += "（你）";
                }
                string right = !occupied ? ""
                    : ps == 0 ? "房主"
                    : Sync.Session.RoomReadyOf(ps) ? "✓ 已准备" : "未准备";
                _hits.Add(new Hit
                {
                    Kind = HitKind.Seat,
                    X = 160,
                    Y = 52 + ps * 40,
                    W = 390,
                    H = 36,
                    Id = -1,
                    Left = ps == 0 ? "主机位" : "客人位 " + (ps + 1),
                    Main = Clip(nick, 14),
                    Right = right,
                    Selected = ps == Sync.Session.MySlot,
                });
                if (host && occupied && ps != 0)
                {
                    _hits.Add(new Hit
                    {
                        Kind = HitKind.Kick,
                        X = 556,
                        Y = 58 + ps * 40,
                        W = 76,
                        H = 24,
                        Id = KickBtnIdBase + ps - 1,
                        Main = "踢出",
                    });
                }
            }

            if (host && connected)
            {
                _hits.Add(new Hit
                {
                    Kind = HitKind.Info, X = 172 + BtnW, Y = 244, W = 460, H = 20, Id = -1,
                    Main = "当前：" + level.FullLabel,
                });
            }
            else if (!host)
            {
                _hits.Add(new Hit
                {
                    Kind = HitKind.Info, X = 160, Y = 244, W = 480, H = 20, Id = -1,
                    Main = "关卡：" + level.FullLabel + "（由主机选择）",
                });
            }
            _hits.Add(new Hit
            {
                Kind = HitKind.Info, X = 160, Y = 328, W = 480, H = 16, Id = -1,
                Main = Sync.Session.LevelRuleHint(level.Mode),
            });
            if (host && connected
                && (Sync.Session.DiscoveryReqAge < 0 || Sync.Session.DiscoveryReqAge > 15))
            {
                _hits.Add(new Hit
                {
                    Kind = HitKind.Info, X = 160, Y = 346, W = 480, H = 16, Id = -1,
                    Main = "对方一直搜不到本机？防火墙请允许『专用+公用』，或把热点网络设为专用",
                });
            }
        }

        /// <summary>
        /// 展开下拉时先定位到"当前选定关卡"所在的分类与页：表已经有 60 来关，
        /// 每次都从第 1 页开始翻，换个关要点七八下。
        /// </summary>
        private void JumpToSelectedLevel()
        {
            _dropFilters ??= Sync.Session.LevelPageFilters();
            int sel = Sync.Session.ClampLevelIndex(Sync.Session.SelectedLevelIndex);
            for (int f = 1; f < _dropFilters.Count; f++)
            {
                int pos = Sync.Session.LevelIndicesOfPage(_dropFilters[f]).IndexOf(sel);
                if (pos >= 0)
                {
                    _dropFilter = f;
                    _dropPage = pos / RowCount;
                    return;
                }
            }
            _dropFilter = 0;
            int inAll = Sync.Session.LevelIndicesOfPage(_dropFilters[0]).IndexOf(sel);
            _dropPage = inAll >= 0 ? inAll / RowCount : 0;
        }

        /// <summary>选关下拉：分类 tab + 关卡条目 + 翻页。全部自绘，不依赖按钮控件。</summary>
        private void BuildDropdownHits()
        {
            _dropFilters ??= Sync.Session.LevelPageFilters();
            if (_dropFilter >= _dropFilters.Count)
            {
                _dropFilter = 0;
            }
            _dropIndices = Sync.Session.LevelIndicesOfPage(_dropFilters[_dropFilter]);
            _dropPages = Math.Max(1, (_dropIndices.Count + RowCount - 1) / RowCount);
            if (_dropPage >= _dropPages)
            {
                _dropPage = 0;
            }
            int selected = Sync.Session.ClampLevelIndex(Sync.Session.SelectedLevelIndex);

            for (int i = 0; i < _dropFilters.Count && i < TabCount; i++)
            {
                _hits.Add(new Hit
                {
                    Kind = HitKind.Tab,
                    X = 166 + i * 78,
                    Y = 56,
                    W = 74,
                    H = 22,
                    Id = LvlPageIdBase + i,
                    Main = _dropFilters[i],
                    Selected = i == _dropFilter,
                });
            }
            for (int i = 0; i < RowCount; i++)
            {
                int k = _dropPage * RowCount + i;
                if (k >= _dropIndices.Count)
                {
                    break;
                }
                int li = _dropIndices[k];
                _hits.Add(new Hit
                {
                    Kind = HitKind.LevelRow,
                    X = 166,
                    Y = 84 + i * 28,
                    W = 468,
                    H = 26,
                    Id = LvlRowIdBase + i,
                    Main = Clip(Sync.Session.Levels[li].Name, 26),
                    Selected = li == selected,
                });
            }
            if (_dropPages > 1)
            {
                _hits.Add(new Hit
                {
                    Kind = HitKind.Pager, X = 166, Y = 200, W = 86, H = 22,
                    Id = LvlPrevId, Main = "< 上一页",
                });
                _hits.Add(new Hit
                {
                    Kind = HitKind.Pager, X = 258, Y = 200, W = 86, H = 22,
                    Id = LvlNextId, Main = "下一页 >",
                });
            }
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
                RefreshUi();
            }
        }

        // ------------------------------------------------------------ 绘制

        public override void Draw(Graphics g)
        {
            try
            {
                g.SetLinearBlend(true);
                s_drawImageBox(g, new TRect(-_halfDeltaWidth, -_halfDeltaHeight, mWidth, mHeight),
                    AtlasResources.IMAGE_ALMANAC_ROUNDED_OUTLINE);

                Text(g, "植物娘联机", 400, 22, Resources.FONT_DWARVENTODCRAFT15,
                    new SexyColor(220, 220, 220), DrawStringJustification.Center);

                if (!_roomPhase)
                {
                    DrawLobby(g);
                }
                else if (_dropOpen)
                {
                    // 下拉面板底：盖住座位区（经典下拉行为），条目随后由 DrawHits 画在上面
                    g.SetColor(new SexyColor(24, 28, 20, 246));
                    g.FillRect(158, 50, 484, 178);
                    g.SetColor(new SexyColor(170, 220, 150, 220));
                    g.DrawRect(158, 50, 484, 178);
                    Text(g, "第 " + (_dropPage + 1) + "/" + _dropPages + " 页 · 共 " + _dropIndices.Count + " 关",
                        352, 206, Resources.FONT_BRIANNETOD12, new SexyColor(180, 190, 180));
                    Text(g, "点一条即选定", 626, 206, Resources.FONT_BRIANNETOD12,
                        new SexyColor(150, 160, 150), DrawStringJustification.Right);
                }

                DrawHits(g);

                string status = Sync.Session.StatusText;
                if (!string.IsNullOrEmpty(status))
                {
                    Text(g, status, 400, 462, Resources.FONT_BRIANNETOD16,
                        Sync.Session.StatusIsError ? new SexyColor(255, 120, 100) : new SexyColor(160, 255, 160),
                        DrawStringJustification.Center);
                }
                Text(g, "协议 v" + Protocol.ProtocolVersion.Current, mWidth - 12, mHeight - 22,
                    Resources.FONT_BRIANNETOD12, new SexyColor(120, 130, 120, 200), DrawStringJustification.Right);
            }
            catch (Exception ex)
            {
                Core.ModEnv.LogOnce("联机页绘制异常（重复不再记）: " + ex.Message);
            }
        }

        private void DrawLobby(Graphics g)
        {
            Text(g, "你的昵称:", 160, 58, Resources.FONT_BRIANNETOD16, new SexyColor(220, 220, 220));
            Text(g, "局域网房间（点一条加入）", 160, 100, Resources.FONT_BRIANNETOD16, new SexyColor(220, 220, 220));
            if (Sync.Session.RoomList.Count == 0)
            {
                Text(g, "正在搜索…（对方点[建立房间]后几秒内出现）", 400, 130,
                    Resources.FONT_BRIANNETOD12, new SexyColor(150, 150, 160), DrawStringJustification.Center);
            }
            Text(g, "手动加入（填对方 IP 和端口）", 160, 284, Resources.FONT_BRIANNETOD16, new SexyColor(220, 220, 220));
            Text(g, "搜不到房间？Windows 防火墙允许『专用+公用』（手机热点属公用）", 400, 404,
                Resources.FONT_BRIANNETOD12, new SexyColor(150, 150, 160), DrawStringJustification.Center);
            Text(g, "跨互联网：双方先连同一个虚拟局域网（ZeroTier/Tailscale），再填虚拟网 IP", 400, 420,
                Resources.FONT_BRIANNETOD12, new SexyColor(150, 150, 160), DrawStringJustification.Center);
        }

        private static void Text(Graphics g, string s, int x, int y, Font font, SexyColor color,
            DrawStringJustification just = DrawStringJustification.Left)
        {
            TodCommon.TodDrawString(g, s, x, y, font, color, just);
        }

        private void DrawHits(Graphics g)
        {
            var font16 = Resources.FONT_BRIANNETOD16;
            var font12 = Resources.FONT_BRIANNETOD12;
            foreach (var h in _hits)
            {
                switch (h.Kind)
                {
                    case HitKind.Info:
                        g.SetFont(font12);
                        g.SetColor(new SexyColor(170, 180, 170));
                        g.DrawString(h.Main, h.X, h.Y);
                        break;

                    case HitKind.Seat:
                        DrawSeat(g, h, font16, font12);
                        break;

                    case HitKind.RoomRow:
                        Bar(g, h, new SexyColor(52, 58, 46, 215), new SexyColor(150, 214, 255, 190));
                        g.SetFont(font16);
                        g.SetColor(new SexyColor(235, 235, 225));
                        g.DrawString(h.Main, h.X + 10, h.Y + 9);
                        break;

                    case HitKind.Kick:
                        Centered(g, h, font12, new SexyColor(74, 44, 40, 220),
                            new SexyColor(255, 150, 130, 170), new SexyColor(255, 200, 180));
                        break;

                    case HitKind.Tab:
                        Centered(g, h, font12,
                            h.Selected ? new SexyColor(96, 112, 74, 235) : new SexyColor(44, 50, 40, 215),
                            h.Selected ? new SexyColor(215, 248, 180, 235) : new SexyColor(130, 145, 125, 170),
                            h.Selected ? new SexyColor(255, 244, 180) : new SexyColor(205, 210, 200));
                        break;

                    case HitKind.LevelRow:
                        Bar(g, h, h.Selected ? new SexyColor(64, 84, 52, 235) : new SexyColor(38, 42, 34, 215),
                            h.Selected ? new SexyColor(215, 248, 180, 235) : new SexyColor(120, 132, 116, 160));
                        g.SetFont(font16);
                        g.SetColor(h.Selected ? new SexyColor(170, 235, 150) : new SexyColor(150, 160, 150));
                        g.DrawString(h.Selected ? "*" : "-", h.X + 8, h.Y + 5);
                        g.SetColor(new SexyColor(232, 234, 226));
                        g.DrawString(h.Main, h.X + 26, h.Y + 5);
                        break;

                    case HitKind.Pager:
                        Centered(g, h, font12, new SexyColor(44, 50, 40, 215),
                            new SexyColor(150, 214, 255, 170), new SexyColor(225, 232, 220));
                        break;
                }
            }
        }

        private static void Bar(Graphics g, Hit h, SexyColor fill, SexyColor edge)
        {
            g.SetColor(fill);
            g.FillRect(h.X, h.Y, h.W, h.H);
            g.SetColor(edge);
            g.DrawRect(h.X, h.Y, h.W, h.H);
        }

        private static void Centered(Graphics g, Hit h, Font font, SexyColor fill, SexyColor edge, SexyColor text)
        {
            Bar(g, h, fill, edge);
            g.SetFont(font);
            g.SetColor(text);
            g.DrawString(h.Main, h.X + (h.W - font.StringWidth(h.Main)) / 2, h.Y + (h.H - 12) / 2 + 1);
        }

        private void DrawSeat(Graphics g, Hit h, Font font16, Font font12)
        {
            string seat = h.Left ?? "";
            Bar(g, h, new SexyColor(90, 96, 84, 150),
                h.Selected ? new SexyColor(255, 235, 150, 200) : new SexyColor(120, 130, 115, 120));
            g.SetFont(font12);
            g.SetColor(seat == "主机位" ? new SexyColor(255, 224, 130) : new SexyColor(150, 214, 255));
            g.DrawString(seat, h.X + 10, h.Y + 12);
            g.SetFont(font16);
            g.SetColor(new SexyColor(240, 240, 240));
            g.DrawString(h.Main, h.X + 10 + (int)font12.StringWidth(seat) + 14, h.Y + 9);
            if (string.IsNullOrEmpty(h.Right))
            {
                return;
            }
            g.SetColor(h.Right == "房主" ? new SexyColor(255, 224, 130)
                : h.Right.Contains("已准备") ? new SexyColor(160, 255, 160)
                : new SexyColor(255, 190, 120));
            g.DrawString(h.Right, h.X + h.W - 12 - (int)font16.StringWidth(h.Right), h.Y + 9);
        }

        // ------------------------------------------------------------ 自绘条目的点击命中

        public override void MouseDown(int x, int y, int theBtnNum, int theClickCount)
        {
            try
            {
                for (int i = _hits.Count - 1; i >= 0; i--)
                {
                    var h = _hits[i];
                    if (h.Id < 0)
                    {
                        continue; // 纯展示，不拦点击
                    }
                    if (x >= h.X && x < h.X + h.W && y >= h.Y && y < h.Y + h.H)
                    {
                        try
                        {
                            _app.PlaySample(Resources.SOUND_BUTTONCLICK);
                        }
                        catch
                        {
                        }
                        ButtonDepress(h.Id);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Core.ModEnv.LogOnce("联机页点击处理异常（重复不再记）: " + ex.Message);
            }
            base.MouseDown(x, y, theBtnNum, theClickCount);
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

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max)
            {
                return s ?? "";
            }
            return s.Substring(0, max) + "…";
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
                    return;
                case HostId:
                    SaveIdentity();
                    Sync.Session.StartHosting(_app);
                    break;
                case JoinId:
                    SaveIdentity();
                    Sync.Session.StartJoining(_app, _ipEdit.Text, ParsePort());
                    break;
                case SaveNickId:
                    SaveNick();
                    break;
                case LevelId:
                    if (!_dropOpen)
                    {
                        JumpToSelectedLevel();
                    }
                    _dropOpen = !_dropOpen;
                    break;
                case StartId:
                    Sync.Session.HostStartGame(_app);
                    break;
                case DisconnectId:
                    _dropOpen = false;
                    Sync.Session.CancelOrDisconnect();
                    break;
                case ReadyBtnId:
                    Sync.Session.SetRoomReady(!Sync.Session.AmRoomReady);
                    break;
                case LvlPrevId:
                    _dropPage = (_dropPage + _dropPages - 1) % _dropPages;
                    break;
                case LvlNextId:
                    _dropPage = (_dropPage + 1) % _dropPages;
                    break;
                default:
                    HandleOtherButton(theId);
                    break;
            }
            RefreshUi();
        }

        private void SaveNick()
        {
            var cfg = Core.ModEnv.GetConfig();
            string nick = _nickEdit.Text.Trim();
            if (string.IsNullOrEmpty(nick))
            {
                Sync.Session.SetStatus("昵称不能为空", true);
                return;
            }
            cfg.Nickname = nick;
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
        }

        private void HandleOtherButton(int theId)
        {
            if (theId >= LvlPageIdBase && theId < LvlPageIdBase + TabCount)
            {
                int f = theId - LvlPageIdBase;
                if (_dropFilters != null && f < _dropFilters.Count)
                {
                    _dropFilter = f;
                    _dropPage = 0;
                }
                return;
            }
            if (theId >= LvlRowIdBase && theId < LvlRowIdBase + RowCount)
            {
                int k = _dropPage * RowCount + (theId - LvlRowIdBase);
                if (k < _dropIndices.Count)
                {
                    Sync.Session.HostSetLevel(_dropIndices[k]);
                    _dropOpen = false;
                }
                return;
            }
            if (theId >= KickBtnIdBase && theId < KickBtnIdBase + Sync.Session.MaxPlayers)
            {
                int kickSlot = theId - KickBtnIdBase + 1;
                if (kickSlot < Sync.Session.MaxPlayers && Sync.Session.SlotOccupied[kickSlot])
                {
                    Sync.Session.KickGuest(kickSlot);
                }
                return;
            }
            if (theId >= RoomButtonIdBase && theId < RoomButtonIdBase + RoomButtonCount)
            {
                int idx = theId - RoomButtonIdBase;
                if (idx < Sync.Session.RoomList.Count)
                {
                    Sync.Session.JoinFromDiscovery(Sync.Session.RoomList[idx].Ip);
                }
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
            _hits.Clear();
            base.RemovedFromManager(manager);
        }
    }
}
