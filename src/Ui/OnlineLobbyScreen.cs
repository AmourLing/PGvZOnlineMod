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
    /// 浏览页排版照参考图：左列"服务器"（局域网 + 玩家自加的中继 + 手动填 IP），
    /// 右列"房间列表"（点一条即加入），面板底部一排 添加服务器 / 创建房间 / 刷新，面板外左下"返回"。
    /// 三个对话框：添加服务器、手动连接、创建房间。
    ///
    /// 排版两条铁律（都是实机截图里踩出来的）：
    /// 1) NewLawnButton 的贴图按**原始尺寸**绘制（ButtonWidget.DrawImage 传源矩形，不拉伸），
    ///    而标签按 mWidth 居中 —— 把按钮 Resize 成 330/470 宽只会让文字飘到图外面。
    ///    所以真按钮一律用贴图原始大小；参考图里那种"整条宽 bar"全改成自绘 + 命中测试。
    /// 2) 横向按 mWidth 居中而不是写死 800：宽屏时 mVirtualWidth 比 800 宽，
    ///    写死会让整页内容偏左、右边空一条。
    /// </summary>
    public class OnlineLobbyScreen : Widget, ButtonListener
    {
        public const int LobbyButtonId = 200;

        // ------------------------------------------------------------ 控件 id（各段区间互不重叠）

        private const int BackId = 100;
        private const int AddServerId = 101;
        private const int CreateRoomId = 102;
        private const int RefreshId = 103;
        private const int LevelId = 104;
        private const int StartId = 105;
        private const int DisconnectId = 106;
        private const int ReadyBtnId = 107;
        private const int DlgOkId = 108;
        private const int DlgCancelId = 109;
        private const int PickLevelBtnId = 110;

        private const int KickBtnIdBase = 125;      // 125..127（最多 3 个客人位）
        private const int LvlPageIdBase = 150;      // 150..155 下拉的分类行
        private const int LvlRowIdBase = 160;       // 160..164 下拉的关卡条目行（每页 PickRows 关）
        private const int LvlPrevId = 170;
        private const int LvlNextId = 171;
        private const int RoomRowIdBase = 180;      // 180..187 房间列表行
        private const int ServerRowIdBase = 190;    // 190..199 服务器行
        private const int ManualRowId = 240;
        private const int PwToggleId = 241;
        private const int DropCloseId = 244;      // 选关面板的[完成]
        private const int DelServerId = 243;
        private const int CodeRowId = 242;
        private const int RelayRoomIdBase = 250;  // 250..257 中继列表里的房间行
        internal const int TabCount = 6;            // 全部 + 五个页签（离线回归会拿它当分类数的上限校验）

        private const int RoomRowMax = 8;           // 一屏放得下的房间行数
        private const int ServerRowMax = 10;        // 局域网 + 8 台中继 + 手动一行

        private enum HitKind { Info, Seat, RoomRow, Kick, Tab, LevelRow, Pager, ServerRow, Small, CheckBox }

        /// <summary>自绘条目：几何 + 文案 + 点下去等价于按哪个按钮 id（Id 为负＝纯展示）。</summary>
        private struct Hit
        {
            public HitKind Kind;
            public int X, Y, W, H, Id;
            public string Left, Main, Right;
            public bool Selected;
        }

        /// <summary>左列一行：局域网 / 某台中继 / 手动填 IP。</summary>
        private struct ServerRow
        {
            public bool IsLan;
            public bool IsManual;
            public string Label;
            public string Sub;
            public Core.ServerEntry Entry;
        }

        private enum Dialog { None, AddServer, ManualJoin, CreateRoom, JoinRoom }

        private static OnlineLobbyScreen _inst;
        private static NewLawnButton _menuButton;
        private static GameSelector _boundSelector;

        private readonly LawnApp _app;
        private NewLawnButton _backButton;
        private NewLawnButton _addServerBtn;
        private NewLawnButton _delServerBtn;
        private NewLawnButton _createBtn;
        private NewLawnButton _refreshBtn;
        private NewLawnButton _levelBtn;
        private NewLawnButton _startBtn;
        private NewLawnButton _readyBtn;
        private NewLawnButton _disconnectBtn;
        private NewLawnButton _dlgOk;
        private NewLawnButton _dlgCancel;
        private NewLawnButton _pickLevelBtn;
        private IpInputWidget _nameEdit;
        private IpInputWidget _addrEdit;
        private IpInputWidget _portEdit;
        private IpInputWidget _pwdEdit;

        private Dialog _dialog = Dialog.None;
        private int _sel;                       // 左列选中行：0=局域网，1..=中继，最后一条=手动
        private bool _pwOn;
        private int _pendingLevel = -1;         // 创建房间对话框里选定、尚未开房的关卡
        private string _joinCode = "";          // 从列表点进来的房间号（要密码时先记着）
        private bool _dropFromDialog;           // 选关下拉是从对话框里点开的

        private bool _dropOpen;
        private int _dropPage;
        private int _dropPages = 1;
        private int _dropFilter; // 0 = 全部
        private List<string> _dropFilters;
        private List<int> _dropIndices = new List<int>();

        private readonly List<Hit> _hits = new List<Hit>();
        private readonly List<ServerRow> _serverRows = new List<ServerRow>();
        private long _roomListVersion = -1;
        private long _relayRoomsVersion = -1;
        private bool _roomPhase;
        private int _halfDeltaWidth;
        private int _halfDeltaHeight;
        private int _lastW = -1;
        private int _lastH = -1;

        // 浏览页几何（分辨率变了重算，见 ComputeLayout）
        private int _x0 = 20;
        private int _cw = 760;
        private int _panelTop = 64;
        private int _panelBottom = 526;
        private int _leftW = 200;
        private int _rightX = 234;
        private int _rightW = 546;
        private LobbyGeom _geom;

        /// <summary>
        /// 浏览页的全部尺寸。**单独抽出来是为了离线门能直接把布局算一遍**——
        /// 重叠这类事在测试里看不见，实机来回报了两次（副文字压行、说明压房间行），
        /// 抽出来之后任何一处改动只要让矩形相交，回归就红，不必再靠眼睛。
        /// 数值只有这一份：用例不许另抄一遍字面量。
        /// </summary>
        internal struct LobbyGeom
        {
            public int W, H, X0, Cw, PanelTop, PanelBottom, LeftW, RightX, RightW, BtnW, BtnH;

            // 行距/行高/一屏行数：只有这里一份
            public const int ServerPitch = 33;
            public const int ServerRowH = 25;
            public const int MaxServerRows = 10;      // 局域网 + 8 台中继 + 手动一行
            public const int RoomPitch = 44;
            public const int RoomRowH = 38;
            public const int MaxRoomRows = 7;         // 右列一屏最多几行（含"用房间号加入"）
            public const int HeaderY = 42;
            public const int PanelTitleY = PanelTopConst + 8;
            private const int PanelTopConst = 64;

            public static LobbyGeom Compute(int w, int h, int btnW, int btnH)
            {
                var g = new LobbyGeom { W = w, H = h, BtnW = btnW, BtnH = btnH };
                g.Cw = Math.Min(w - 32, 760);
                g.X0 = (w - g.Cw) / 2;
                if (g.X0 < 8)
                {
                    g.X0 = 8;
                }
                g.PanelTop = PanelTopConst;
                g.PanelBottom = h - 74;
                if (g.PanelBottom < g.PanelTop + 240)
                {
                    g.PanelBottom = g.PanelTop + 240;
                }
                g.LeftW = 200;
                g.RightX = g.X0 + g.LeftW + 14;
                g.RightW = g.X0 + g.Cw - g.RightX;
                if (g.RightW < 220)
                {
                    g.RightW = Math.Max(160, w - g.RightX - 8);
                }
                return g;
            }

            public int ListTop => PanelTop + 30;
            public int ButtonRow => PanelBottom - BtnH - 8;
            public int AddButton => PanelBottom - BtnH - 6;
            public int DelButton => AddButton - BtnH - 4;
            public int Notice => ButtonRow - 66;

            /// <summary>能塞下几行服务器：窗口矮就少画几行，而不是叠到按钮上。</summary>
            public int ServerRowsFit => Fit(DelButton - 4, ServerPitch, ServerRowH, MaxServerRows, 3);

            /// <summary>右列能塞下几行房间（中继态还要给"用房间号加入"留一行）。</summary>
            public int RoomRowsFit => Fit(Notice - 4, RoomPitch, RoomRowH, MaxRoomRows, 2);

            /// <summary>
            /// 装得下几行：最后一行的**下沿**（不是顶边）必须不超过 bottom，
            /// 所以是 (bottom - 首行顶 - 行高)/行距 + 1；只按行距除会多算一行（640x480 那档就是这么差 1px）。
            /// </summary>
            private int Fit(int bottom, int pitch, int rowH, int cap, int min)
            {
                int n = (bottom - ListTop - rowH) / pitch + 1;
                if (n > cap)
                {
                    n = cap;
                }
                return n < min ? min : n;
            }

            /// <summary>左列实际画出来的最后一行的下沿。</summary>
            public int ServerRowsBottom => ListTop + (ServerRowsFit - 1) * ServerPitch + ServerRowH;
            /// <summary>右列实际画出来的最后一行（含"用房间号加入"那行）的下沿。</summary>
            public int RelayRowsBottom => ListTop + (RoomRowsFit - 1) * RoomPitch + RoomRowH;
            public int NoticeBottom => Notice + 36 + 12;
            public int NickY => H - 26;

            // ---- 选关面板：整块内容区都给它（挤成一坨、翻页文字叠在一起都是以前的事）
            public const int PickRows = 5;
            public const int PickRowH = 30;
            public const int PickRowPitch = 34;
            public const int PickTabH = 26;
            public const int PickCtlH = 26;

            public int PickX => X0;
            public int PickY => PanelTop;
            public int PickW => Cw;
            public int PickH => PanelBottom - PanelTop;
            public int PickTabsY => PickY + 12;
            public int PickRowsY => PickY + 48;
            public int PickRowsBottom => PickRowsY + (PickRows - 1) * PickRowPitch + PickRowH;
            public int PickCtlY => PanelBottom - PickCtlH - 10;

            /// <summary>分类页签均分宽度（页签个数由界面传进来）。</summary>
            public int PickTabWidth(int tabs) => (PickW - 16 - (tabs - 1) * 6) / Math.Max(1, tabs);
        }

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
            screen.Resize(0, 0, app.mScreenScales.mVirtualWidth, app.mScreenScales.mVirtualHeight);
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

            _backButton = MakeButton(BackId, "[BACK_TO_MENU]");
            _addServerBtn = MakeButton(AddServerId, "添加服务器");
            _delServerBtn = MakeButton(DelServerId, "删除服务器");
            _delServerBtn.mVisible = false;
            _createBtn = MakeButton(CreateRoomId, "创建房间");
            _refreshBtn = MakeButton(RefreshId, "刷新");

            // 对话框控件：一次建好，靠 mVisible 切，避免每帧往 WidgetManager 里塞
            _nameEdit = new IpInputWidget { MaxLength = 24, AllowAnyChar = true };
            _addrEdit = new IpInputWidget { MaxLength = 40 };
            _portEdit = new IpInputWidget { MaxLength = 5 };
            _pwdEdit = new IpInputWidget { MaxLength = 24, AllowAnyChar = true };
            AddWidget(_nameEdit);
            AddWidget(_addrEdit);
            AddWidget(_portEdit);
            AddWidget(_pwdEdit);
            _dlgOk = MakeButton(DlgOkId, "添加");
            _dlgCancel = MakeButton(DlgCancelId, "取消");
            _pickLevelBtn = MakeButton(PickLevelBtnId, "选择关卡");

            // 房间内：真按钮一律贴图原始尺寸，宽 bar 全部自绘
            _levelBtn = MakeButton(LevelId, "选关");
            _startBtn = MakeButton(StartId, "开始游戏");
            _readyBtn = MakeButton(ReadyBtnId, "准备");
            _disconnectBtn = MakeButton(DisconnectId, "离开房间");
            _disconnectBtn.mVisible = false;

            ComputeLayout();
            ApplyLayout();
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

        // ------------------------------------------------------------ 几何

        private void ComputeLayout()
        {
            _geom = LobbyGeom.Compute(mWidth, mHeight, BtnW, BtnH);
            _x0 = _geom.X0;
            _cw = _geom.Cw;
            _panelTop = _geom.PanelTop;
            _panelBottom = _geom.PanelBottom;
            _leftW = _geom.LeftW;
            _rightX = _geom.RightX;
            _rightW = _geom.RightW;
        }

        private int ButtonRowY => _geom.ButtonRow;
        private int AddButtonY => _geom.AddButton;
        private int ListTopY => _geom.ListTop;
        private int NoticeY => _geom.Notice;
        private int DelButtonY => _geom.DelButton;
        private int ServerRowsBottom => _geom.ServerRowsBottom;
        private int RelayRowsBottom => _geom.RelayRowsBottom;

        // 选关下拉是整页级的覆盖层，几何跟着布局走（写死 158 起会在宽屏上压到左列）

        /// <summary>把真按钮摆到布局上；分辨率变了（宽屏切换）要重算一遍。</summary>
        private void ApplyLayout()
        {
            int bw = BtnW, bh = BtnH;
            _backButton.Resize(_x0, _panelBottom + 10, bw, bh);
            // 左列两个按钮叠着放：删除在上、添加在下（× 画在服务器行上太容易误触）
            _addServerBtn.Resize(_x0 + (_leftW - bw) / 2, AddButtonY, bw, bh);
            _delServerBtn.Resize(_x0 + (_leftW - bw) / 2, DelButtonY, bw, bh);
            _createBtn.Resize(_rightX + _rightW / 2 - bw - 5, ButtonRowY, bw, bh);
            _refreshBtn.Resize(_rightX + _rightW / 2 + 5, ButtonRowY, bw, bh);

            // 房间内那一排沿用旧坐标（已实机验过），只跟着横向居中走
            int rowX = _x0 + 120;
            _levelBtn.Resize(rowX, 232, bw, bh);
            _startBtn.Resize(rowX, 280, bw, bh);
            _readyBtn.Resize(rowX, 280, bw, bh);
            _disconnectBtn.Resize(rowX + bw + 10, 280, bw, bh);
        }

        private void ComputeDialogRect(out int dx, out int dy, out int dw, out int dh)
        {
            dw = Math.Min(500, mWidth - 40);
            int rows = _dialog switch
            {
                Dialog.AddServer => 3,
                Dialog.ManualJoin => 2,
                Dialog.CreateRoom => 2,
                Dialog.JoinRoom => 2,
                _ => 1,
            };
            dh = 70 + rows * 74 + BtnH + 30;
            if (dh > mHeight - 40)
            {
                dh = mHeight - 40;
            }
            dx = (mWidth - dw) / 2;
            dy = (mHeight - dh) / 2;
        }

        private void LayoutDialog()
        {
            if (_dialog == Dialog.None)
            {
                return;
            }
            ComputeDialogRect(out int dx, out int dy, out int dw, out int dh);
            int bw = BtnW, bh = BtnH;
            _dlgOk.Resize(dx + dw / 2 - bw - 8, dy + dh - bh - 18, bw, bh);
            _dlgCancel.Resize(dx + dw / 2 + 8, dy + dh - bh - 18, bw, bh);
            _pickLevelBtn.Resize(dx + dw - bw - 24, dy + 58, bw, bh);

            int fieldX = dx + 24, fieldW = dw - 48;
            switch (_dialog)
            {
                case Dialog.AddServer:
                    _nameEdit.Resize(fieldX, dy + 62, fieldW, 32);
                    _addrEdit.Resize(fieldX, dy + 136, fieldW, 32);
                    _portEdit.Resize(fieldX, dy + 210, fieldW, 32);
                    break;
                case Dialog.ManualJoin:
                    _addrEdit.Resize(fieldX, dy + 62, fieldW, 32);
                    _portEdit.Resize(fieldX, dy + 136, fieldW, 32);
                    break;
                case Dialog.JoinRoom:
                    _addrEdit.Resize(fieldX, dy + 62, fieldW, 32);   // 这里当"房间号"用
                    _pwdEdit.Resize(fieldX, dy + 136, fieldW, 32);
                    break;
                case Dialog.CreateRoom:
                    _pwdEdit.Resize(fieldX, dy + 164, fieldW, 32);
                    break;
            }
        }

        // ------------------------------------------------------------ 左列服务器

        private void RebuildServerRows()
        {
            _serverRows.Clear();
            _serverRows.Add(new ServerRow { IsLan = true, Label = "局域网", Sub = "同网段/热点，自动搜房" });
            foreach (var s in Core.ModEnv.GetConfig().Servers)
            {
                if (_serverRows.Count >= ServerRowMax - 1)
                {
                    break;
                }
                _serverRows.Add(new ServerRow { Entry = s, Label = s.Name, Sub = s.Host + ":" + s.Port });
            }
            _serverRows.Add(new ServerRow { IsManual = true, Label = "手动填 IP…", Sub = "虚拟网 / 内外穿透地址" });
            if (_sel >= _serverRows.Count)
            {
                _sel = 0;
            }
        }

        private ServerRow SelectedRow =>
            _sel >= 0 && _sel < _serverRows.Count ? _serverRows[_sel] : default;

        private bool SelectedIsLan => _sel == 0;
        private bool SelectedIsManual => _sel > 0 && _sel >= _serverRows.Count - 1;

        /// <summary>选中的是玩家自加的中继（右列这时显示 LIST 回来的房间）。</summary>
        private bool SelectedIsRelay => !SelectedIsLan && !SelectedIsManual && SelectedRow.Entry != null;
        private Core.ServerEntry SelectedEntry => SelectedRow.Entry;

        /// <summary>换选左列：中继要开控制通道，切回局域网/手动则把中继收掉。</summary>
        private void SelectServerRow(int i)
        {
            _sel = i;
            RebuildServerRows();
            if (i < 0 || i >= _serverRows.Count)
            {
                return;
            }
            var entry = _serverRows[i].Entry;
            Sync.Session.RelaySelect(entry);
        }

        // ------------------------------------------------------------ 状态刷新（同时重建自绘条目）

        private void RefreshUi()
        {
            bool room = Sync.Session.Phase != Sync.SessionPhase.Idle;
            bool host = Sync.Session.IsHost;
            bool connected = Sync.Session.Net.IsConnected;
            _roomPhase = room;
            _hits.Clear();

            if (room && _dialog != Dialog.None)
            {
                // 已经进房/在连了，浏览页的对话框没有意义（建房与手动连接都已落地）
                CloseDialog();
                return;
            }

            _backButton.mVisible = true;
            // 选关面板是整页覆盖层：开着的时候底下这些按钮不该露出来（实机反馈第 4 条）
            bool pick = _dropOpen;
            _backButton.mVisible = !pick;
            _addServerBtn.mVisible = !room && !pick;
            _createBtn.mVisible = !room && !SelectedIsManual && !pick;
            _refreshBtn.mVisible = !room && !pick;
            _delServerBtn.mVisible = !room && SelectedIsRelay && !pick;
            // 离开房间只在房间内出现：这里漏设过一次，浏览页中间就杵着一个"离开房间"
            _disconnectBtn.mVisible = room && !pick;

            bool canPick = room && host && connected;
            if (!canPick && !_dropFromDialog)
            {
                _dropOpen = false; // 否则[选关]按钮隐掉了，展开的下拉关不上
                // 但"创建房间"对话框里点开的下拉不属于这条：那时还没建房（room=false），
                // 一并关掉会让对话框直接消失，表现就是"点[选择关卡]没反应"
            }
            _levelBtn.mVisible = canPick && !pick;
            _startBtn.mVisible = room && host && !pick;
            _readyBtn.mVisible = room && !host && !pick;
            if (_readyBtn.mVisible)
            {
                _readyBtn.mLabel = Sync.Session.AmRoomReady ? "取消准备" : "准备";
            }

            bool dlg = _dialog != Dialog.None;
            _dlgOk.mVisible = dlg;
            _dlgCancel.mVisible = dlg;
            _dlgOk.mLabel = DialogOkLabel;
            _pickLevelBtn.mVisible = _dialog == Dialog.CreateRoom;
            _nameEdit.mVisible = _dialog == Dialog.AddServer;
            _addrEdit.mVisible = _dialog == Dialog.AddServer || _dialog == Dialog.ManualJoin
                || _dialog == Dialog.JoinRoom;
            _addrEdit.MaxLength = _dialog == Dialog.JoinRoom ? 6 : 40;
            _portEdit.mVisible = _dialog == Dialog.AddServer || _dialog == Dialog.ManualJoin;
            _pwdEdit.mVisible = (_dialog == Dialog.CreateRoom && _pwOn) || _dialog == Dialog.JoinRoom;

            if (dlg)
            {
                LayoutDialog();
                BuildDialogHits();
            }
            else if (_dropOpen)
            {
                BuildDropdownHits();
            }
            else if (!room)
            {
                RebuildServerRows();
                BuildBrowseHits();
            }
            else
            {
                BuildRoomHits(host, connected);
            }
            MarkDirty();
        }

        /// <summary>浏览页：左列服务器行 + 右列房间行。</summary>
        private void BuildBrowseHits()
        {
            for (int i = 0; i < _serverRows.Count && i < _geom.ServerRowsFit; i++)
            {
                var r = _serverRows[i];
                _hits.Add(new Hit
                {
                    Kind = HitKind.ServerRow,
                    X = _x0 + 10,
                    Y = ListTopY + i * LobbyGeom.ServerPitch,
                    W = _leftW - 20,
                    H = LobbyGeom.ServerRowH,
                    Id = r.IsManual ? ManualRowId : ServerRowIdBase + i,
                    Main = Clip(r.Label, 11),
                    Right = r.Sub,
                    Selected = i == _sel,
                });
            }

            if (SelectedIsManual)
            {
                return; // 手动那行点了直接弹框，右列没有列表
            }
            if (SelectedIsRelay)
            {
                var rooms = Sync.Session.RelayRooms;
                // 一屏最多 6 个房 + 1 行"用房间号加入"，再往下是说明与按钮排：
                // 行数不封顶的话说明会压到房间行上（实机截图第二次反馈的就是这个）
                // 留一行给"用房间号加入"：窗口矮时少列几个房，也不许叠上去
                int shown = Math.Min(rooms.Count, System.Math.Max(1, _geom.RoomRowsFit - 1));
                for (int i = 0; i < shown; i++)
                {
                    var r = rooms[i];
                    string lvl = r.LevelIndex >= 0
                        ? Sync.Session.Levels[Sync.Session.ClampLevelIndex(r.LevelIndex)].FullLabel
                        : "还没选关";
                    _hits.Add(new Hit
                    {
                        Kind = HitKind.RoomRow,
                        X = _rightX + 10,
                        Y = ListTopY + i * LobbyGeom.RoomPitch,
                        W = _rightW - 20,
                        H = LobbyGeom.RoomRowH,
                        Id = RelayRoomIdBase + i,
                        Main = Clip("房间：" + r.RoomName + "的房间   关卡：" + lvl + "   "
                                   + r.Players + "/" + r.MaxPlayers + (r.Locked ? "   #密码" : ""), 28),
                        Right = "房号 " + r.Code,
                    });
                }
                // 房间号是这套方案的主入口：列表可能因为别人刚建房还没刷出来，填号总能进
                _hits.Add(new Hit
                {
                    Kind = HitKind.RoomRow,
                    X = _rightX + 10,
                    Y = ListTopY + shown * LobbyGeom.RoomPitch,
                    W = _rightW - 20,
                    H = 38,
                    Id = CodeRowId,
                    Main = "用房间号加入…",
                });
                return;
            }
            int n = Sync.Session.RoomList.Count;
            for (int i = 0; i < _geom.RoomRowsFit && i < n; i++)
            {
                var room = Sync.Session.RoomList[i];
                _hits.Add(new Hit
                {
                    Kind = HitKind.RoomRow,
                    X = _rightX + 10,
                    Y = ListTopY + i * LobbyGeom.RoomPitch,
                    W = _rightW - 20,
                    H = LobbyGeom.RoomRowH,
                    Id = RoomRowIdBase + i,
                    Main = Clip(room.DisplayText, 28),
                    Right = room.Ip + ":" + room.Port,
                });
            }
        }

        private void BuildDialogHits()
        {
            if (_dialog != Dialog.CreateRoom)
            {
                return;
            }
            ComputeDialogRect(out int dx, out int dy, out _, out _);
            // 参考图里有"使用密码"。局域网建房这套没有密码机制（同一网段搜到就能进），
            // 所以先按图把它画出来但点不动；走中继建房那一批接上才真的生效。
            _hits.Add(new Hit
            {
                Kind = HitKind.CheckBox,
                X = dx + 24,
                Y = dy + 128,
                W = 260,
                H = 26,
                Id = SelectedIsLan ? -1 : PwToggleId,
                Main = SelectedIsLan ? "使用密码（走中继建房时可用）" : "使用密码",
                Selected = _pwOn,
            });
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
                    X = _x0 + 120,
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
                        X = _x0 + 516,
                        Y = 58 + ps * 40,
                        W = 76,
                        H = 24,
                        Id = KickBtnIdBase + ps - 1,
                        Main = "踢出",
                    });
                }
            }

            if (Sync.Session.RelayHosting)
            {
                // 房间号是这套流程里唯一要口头传达的东西，画在座位下面一眼能看到
                _hits.Add(new Hit
                {
                    Kind = HitKind.Info, X = _x0 + 120, Y = 220, W = 480, H = 18, Id = -1,
                    Main = "房间号 " + Sync.Session.RelayRoomCode + "（经中继 "
                           + Sync.Session.RelayServerName + "）—— 朋友在联机页选同一台中继、填这个号就能进",
                });
            }
            else if (!host && Sync.Session.RelayServerName.Length > 0)
            {
                _hits.Add(new Hit
                {
                    Kind = HitKind.Info, X = _x0 + 120, Y = 220, W = 480, H = 18, Id = -1,
                    Main = "经中继 " + Sync.Session.RelayServerName + " 连入",
                });
            }

            if (host && connected)
            {
                _hits.Add(new Hit
                {
                    Kind = HitKind.Info, X = _x0 + 132 + BtnW, Y = 244, W = 460, H = 20, Id = -1,
                    Main = "当前：" + level.FullLabel,
                });
            }
            else if (!host)
            {
                _hits.Add(new Hit
                {
                    Kind = HitKind.Info, X = _x0 + 120, Y = 244, W = 480, H = 20, Id = -1,
                    Main = "关卡：" + level.FullLabel + "（由主机选择）",
                });
            }
            _hits.Add(new Hit
            {
                Kind = HitKind.Info, X = _x0 + 120, Y = 328, W = 480, H = 16, Id = -1,
                Main = Sync.Session.LevelRuleHint(level.Mode),
            });
            if (host && connected
                && (Sync.Session.DiscoveryReqAge < 0 || Sync.Session.DiscoveryReqAge > 15))
            {
                _hits.Add(new Hit
                {
                    Kind = HitKind.Info, X = _x0 + 120, Y = 346, W = 480, H = 16, Id = -1,
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
            int sel = Sync.Session.ClampLevelIndex(PendingOrCurrent());
            for (int f = 1; f < _dropFilters.Count; f++)
            {
                int pos = Sync.Session.LevelIndicesOfPage(_dropFilters[f]).IndexOf(sel);
                if (pos >= 0)
                {
                    _dropFilter = f;
                    _dropPage = pos / LobbyGeom.PickRows;
                    return;
                }
            }
            _dropFilter = 0;
            int inAll = Sync.Session.LevelIndicesOfPage(_dropFilters[0]).IndexOf(sel);
            _dropPage = inAll >= 0 ? inAll / LobbyGeom.PickRows : 0;
        }

        private int PendingOrCurrent()
            => _pendingLevel >= 0 ? _pendingLevel : Sync.Session.SelectedLevelIndex;

        /// <summary>选关下拉：分类 tab + 关卡条目 + 翻页。全部自绘，不依赖按钮控件。</summary>
        private void BuildDropdownHits()
        {
            _dropFilters ??= Sync.Session.LevelPageFilters();
            if (_dropFilter >= _dropFilters.Count)
            {
                _dropFilter = 0;
            }
            _dropIndices = Sync.Session.LevelIndicesOfPage(_dropFilters[_dropFilter]);
            _dropPages = Math.Max(1, (_dropIndices.Count + LobbyGeom.PickRows - 1) / LobbyGeom.PickRows);
            if (_dropPage >= _dropPages)
            {
                _dropPage = 0;
            }
            int selected = Sync.Session.ClampLevelIndex(PendingOrCurrent());

            int tabs = Math.Min(_dropFilters.Count, TabCount);
            int tabW = Math.Max(40, _geom.PickTabWidth(tabs));
            for (int i = 0; i < tabs; i++)
            {
                _hits.Add(new Hit
                {
                    Kind = HitKind.Tab,
                    X = _geom.PickX + 8 + i * (tabW + 6),
                    Y = _geom.PickTabsY,
                    W = tabW,
                    H = LobbyGeom.PickTabH,
                    Id = LvlPageIdBase + i,
                    Main = _dropFilters[i],
                    Selected = i == _dropFilter,
                });
            }
            for (int i = 0; i < LobbyGeom.PickRows; i++)
            {
                int k = _dropPage * LobbyGeom.PickRows + i;
                if (k >= _dropIndices.Count)
                {
                    break;
                }
                int li = _dropIndices[k];
                _hits.Add(new Hit
                {
                    Kind = HitKind.LevelRow,
                    X = _geom.PickX + 8,
                    Y = _geom.PickRowsY + i * LobbyGeom.PickRowPitch,
                    W = _geom.PickW - 16,
                    H = LobbyGeom.PickRowH,
                    Id = LvlRowIdBase + i,
                    Main = Clip(Sync.Session.Levels[li].Name, 26),
                    Selected = li == selected,
                });
            }
            int ctlY = _geom.PickCtlY;
            if (_dropPages > 1)
            {
                _hits.Add(new Hit
                {
                    Kind = HitKind.Pager, X = _geom.PickX + 8, Y = ctlY, W = 92, H = LobbyGeom.PickCtlH,
                    Id = LvlPrevId, Main = "< 上一页",
                });
                _hits.Add(new Hit
                {
                    Kind = HitKind.Pager, X = _geom.PickX + 106, Y = ctlY, W = 92, H = LobbyGeom.PickCtlH,
                    Id = LvlNextId, Main = "下一页 >",
                });
            }
            // 没有退出按钮的话，点开就只能"必须选一个"或者退回主菜单（实机反馈的第 3 条）
            _hits.Add(new Hit
            {
                Kind = HitKind.Pager,
                X = _geom.PickX + _geom.PickW - 100,
                Y = ctlY,
                W = 92,
                H = LobbyGeom.PickCtlH,
                Id = DropCloseId,
                Main = "完成",
            });
        }

        // ------------------------------------------------------------ 对话框开关

        private void OpenDialog(Dialog d)
        {
            _dialog = d;
            _dropOpen = false;
            _dropFromDialog = false;
            var cfg = Core.ModEnv.GetConfig();
            switch (d)
            {
                case Dialog.AddServer:
                    _nameEdit.SetText("");
                    _addrEdit.SetText("");
                    _portEdit.SetText(Protocol.RelayProtocol.DefaultControlPort.ToString());
                    Focus(_nameEdit);
                    break;
                case Dialog.ManualJoin:
                    _addrEdit.SetText(cfg.LastIp ?? "127.0.0.1");
                    _portEdit.SetText(cfg.LastPort.ToString());
                    Focus(_addrEdit);
                    break;
                case Dialog.CreateRoom:
                    _pendingLevel = Sync.Session.ClampLevelIndex(Sync.Session.SelectedLevelIndex);
                    _pwdEdit.SetText("");
                    _pwOn = false;
                    break;
                case Dialog.JoinRoom:
                    _addrEdit.SetText(_joinCode ?? "");
                    _pwdEdit.SetText("");
                    Focus(_addrEdit);
                    break;
            }
            RefreshUi();
        }

        private void CloseDialog()
        {
            _dialog = Dialog.None;
            _dropFromDialog = false;
            RefreshUi();
        }

        private void Focus(Widget w)
        {
            try
            {
                _app?.mWidgetManager?.SetFocus(w);
            }
            catch
            {
            }
        }

        /// <summary>对话框标题与真按钮文字（同一个按钮在三个对话框里干三件事）。</summary>
        private string DialogTitle => _dialog switch
        {
            Dialog.AddServer => "添加服务器",
            Dialog.ManualJoin => "手动连接",
            Dialog.CreateRoom => "创建房间",
            Dialog.JoinRoom => "用房间号加入",
            _ => "",
        };

        private string DialogOkLabel => _dialog switch
        {
            Dialog.AddServer => "添加",
            Dialog.ManualJoin => "连接",
            Dialog.CreateRoom => "创建",
            Dialog.JoinRoom => "加入",
            _ => "确定",
        };

        private void ConfirmDialog()
        {
            switch (_dialog)
            {
                case Dialog.AddServer:
                    AddServerFromDialog();
                    break;
                case Dialog.ManualJoin:
                    JoinFromDialog();
                    break;
                case Dialog.CreateRoom:
                    CreateRoomFromDialog();
                    break;
                case Dialog.JoinRoom:
                    JoinRoomFromDialog();
                    break;
            }
        }

        private void JoinRoomFromDialog()
        {
            if (!SelectedIsRelay)
            {
                CloseDialog();
                return;
            }
            string code = _addrEdit.Text.Trim();
            _joinCode = "";
            CloseDialog();
            Sync.Session.JoinViaRelay(SelectedEntry, code, _pwdEdit.Text.Trim());
        }

        private void AddServerFromDialog()
        {
            string host = _addrEdit.Text.Trim();
            if (host.Length == 0)
            {
                Sync.Session.SetStatus("服务器地址不能为空", true);
                return;
            }
            var cfg = Core.ModEnv.GetConfig();
            var entry = new Core.ServerEntry
            {
                Name = _nameEdit.Text.Trim(),
                Host = host,
                Port = ParsePortField() ?? Protocol.RelayProtocol.DefaultControlPort,
            };
            if (entry.Name.Length == 0)
            {
                entry.Name = entry.Host;
            }
            cfg.Servers.Add(entry);
            Core.ModEnv.SaveConfig();
            RebuildServerRows();
            _sel = Math.Max(1, Math.Min(cfg.Servers.Count, _serverRows.Count - 2));
            CloseDialog();
            Sync.Session.SetStatus("已添加中继：" + entry.Name, false);
            Core.ModEnv.Log("联机页添加服务器 " + entry.Host + ":" + entry.Port);
        }

        private int? ParsePortField()
        {
            if (int.TryParse(_portEdit.Text.Trim(), out int port) && port >= 1024 && port <= 65535)
            {
                return port;
            }
            return null;
        }

        private void JoinFromDialog()
        {
            string ip = _addrEdit.Text.Trim();
            if (ip.Length == 0)
            {
                Sync.Session.SetStatus("地址不能为空", true);
                return;
            }
            var cfg = Core.ModEnv.GetConfig();
            cfg.LastIp = ip;
            int? port = ParsePortField();
            if (port.HasValue)
            {
                cfg.LastPort = port.Value;
            }
            Core.ModEnv.SaveConfig();
            CloseDialog();
            Sync.Session.StartJoining(_app, ip, port ?? cfg.LastPort);
        }

        private void CreateRoomFromDialog()
        {
            int picked = PendingOrCurrent();
            string pwd = _pwOn ? _pwdEdit.Text.Trim() : "";
            bool viaRelay = SelectedIsRelay;
            var entry = SelectedEntry;
            CloseDialog();
            if (viaRelay)
            {
                // 走中继时关卡由 StartHostingViaRelay 之后的 HostSetLevel 落地，
                // 这里不能提前设——还没建房，Session 会直接 return
                Sync.Session.StartHostingViaRelay(_app, entry, pwd);
                Sync.Session.HostSetLevel(picked);
                _pendingLevel = -1;
                return;
            }
            Sync.Session.StartHosting(_app);
            _pendingLevel = -1;
            // 建房之前 HostSetLevel 会因"还不是主机"直接返回，所以顺序必须是先建房再落地关卡
            Sync.Session.HostSetLevel(picked);
        }

        // ------------------------------------------------------------ 帧更新（镜像 ChallengeScreen.UpdateScreen）

        /// <summary>
        /// 每帧把自身 Resize 到虚拟尺寸（含宽屏 letterbox）并重算外扩量——
        /// 背景 Box 用 -halfDelta 起笔铺满整个窗口，消除内容区外的黑边。
        /// 尺寸变了要重排真按钮，否则宽屏切换后按钮还停在旧坐标。
        /// </summary>
        public override void Update()
        {
            base.Update();
            Resize(mX, mY, _app.mScreenScales.mVirtualWidth, _app.mScreenScales.mVirtualHeight);
            _halfDeltaWidth = (mWidth - Constants.BOARD_WIDTH) / 2;
            _halfDeltaHeight = (mHeight - Constants.BOARD_HEIGHT) / 2;
            if (mWidth != _lastW || mHeight != _lastH)
            {
                _lastW = mWidth;
                _lastH = mHeight;
                ComputeLayout();
                ApplyLayout();
                RefreshUi();
            }
            if (_roomListVersion != Sync.Session.RoomListVersion
                || _relayRoomsVersion != Sync.Session.RelayRoomsVersion)
            {
                _roomListVersion = Sync.Session.RoomListVersion;
                _relayRoomsVersion = Sync.Session.RelayRoomsVersion;
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

                Text(g, "多人联机", mWidth / 2, 20, Resources.FONT_DWARVENTODCRAFT15,
                    new SexyColor(220, 220, 220), DrawStringJustification.Center);

                if (_dropOpen)
                {
                    DrawDropdown(g);
                }
                else if (!_roomPhase)
                {
                    DrawBrowse(g);
                }

                DrawHits(g);

                if (_dialog != Dialog.None)
                {
                    DrawDialog(g);
                }

                string status = Sync.Session.StatusText;
                if (!string.IsNullOrEmpty(status))
                {
                    Text(g, Clip(status, 46), mWidth / 2, _panelBottom + 16, Resources.FONT_BRIANNETOD16,
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

        private void DrawDropdown(Graphics g)
        {
            // 下拉面板底：盖住它下面的内容（经典下拉行为），条目随后由 DrawHits 画在上面
            g.SetColor(new SexyColor(24, 28, 20, 250));
            g.FillRect(_geom.PickX, _geom.PickY, _geom.PickW, _geom.PickH);
            g.SetColor(new SexyColor(170, 220, 150, 220));
            g.DrawRect(_geom.PickX, _geom.PickY, _geom.PickW, _geom.PickH);
            Text(g, "选关 · 第 " + (_dropPage + 1) + "/" + _dropPages + " 页 · 共 "
                + _dropIndices.Count + " 关", _geom.PickX + 210, _geom.PickCtlY + 7,
                Resources.FONT_BRIANNETOD12, new SexyColor(180, 190, 180));
        }

        private void DrawBrowse(Graphics g)
        {
            Panel(g, _x0, _panelTop, _leftW, _panelBottom - _panelTop);
            Panel(g, _rightX, _panelTop, _rightW, _panelBottom - _panelTop);
            Text(g, "服务器", _x0 + _leftW / 2, LobbyGeom.PanelTitleY, Resources.FONT_DWARVENTODCRAFT15,
                new SexyColor(255, 244, 200), DrawStringJustification.Center);
            Text(g, "房间列表", _rightX + _rightW / 2, LobbyGeom.PanelTitleY, Resources.FONT_DWARVENTODCRAFT15,
                new SexyColor(255, 244, 200), DrawStringJustification.Center);

            int rooms = Sync.Session.RoomList.Count;
            Text(g, SelectedIsLan ? "发现 " + rooms + " 个房间" : "选择左边的服务器",
                mWidth / 2, LobbyGeom.HeaderY, Resources.FONT_BRIANNETOD16, new SexyColor(235, 235, 225),
                DrawStringJustification.Center);

            if (SelectedIsLan)
            {
                if (rooms == 0)
                {
                    Text(g, "正在搜索…（对方点[创建房间]后几秒内出现）",
                        _rightX + _rightW / 2, ListTopY + 26, Resources.FONT_BRIANNETOD12,
                        new SexyColor(255, 240, 210), DrawStringJustification.Center);
                }
            }
            else if (SelectedIsManual)
            {
                Text(g, "点上面那一行填对方地址：虚拟局域网（ZeroTier/Tailscale）、",
                    _rightX + 14, ListTopY + 10, Resources.FONT_BRIANNETOD12, new SexyColor(255, 240, 210));
                Text(g, "内外穿透的地址、热点里的局域网 IP 都走这条，不经过任何服务器。",
                    _rightX + 14, ListTopY + 28, Resources.FONT_BRIANNETOD12, new SexyColor(255, 240, 210));
            }
            else
            {
                // 中继那几行说明统一压到面板底部：右列上面是房间行 + "用房间号加入…"行，
                // 说明写在行中间就会叠上去（实机截图第二次反馈的就是这个）
                var r = SelectedRow;
                Text(g, "已选中继：" + r.Label + "（" + r.Sub + "）",
                    _rightX + 14, NoticeY, Resources.FONT_BRIANNETOD12, new SexyColor(255, 244, 200));
                int rn = Sync.Session.RelayRooms.Count;
                Text(g, rn == 0
                    ? "这台服务器上暂时没有等待中的房间；[刷新] 再问一次。"
                    : "发现 " + rn + " 个房间（点一行加入，要密码的会问你密码）。",
                    _rightX + 14, NoticeY + 18, Resources.FONT_BRIANNETOD12, new SexyColor(255, 235, 200));
                Text(g, "这条不依赖同一网段：双方各自出网到这台服务器即可。",
                    _rightX + 14, NoticeY + 36, Resources.FONT_BRIANNETOD12, new SexyColor(255, 235, 200));
            }

            Text(g, "你的昵称：" + Sync.Session.LocalNick() + "（取游戏存档里的名字）",
                mWidth / 2, _geom.NickY, Resources.FONT_BRIANNETOD12,
                new SexyColor(170, 180, 170), DrawStringJustification.Center);
        }

        private static void Panel(Graphics g, int x, int y, int w, int h)
        {
            g.SetColor(new SexyColor(140, 96, 58, 235));
            g.FillRect(x, y, w, h);
            g.SetColor(new SexyColor(74, 48, 28, 235));
            g.DrawRect(x, y, w, h);
        }

        private void DrawDialog(Graphics g)
        {
            ComputeDialogRect(out int dx, out int dy, out int dw, out int dh);
            g.SetColor(new SexyColor(0, 0, 0, 150));
            g.FillRect(0, 0, mWidth, mHeight);
            Panel(g, dx, dy, dw, dh);
            g.SetColor(new SexyColor(255, 190, 110, 220));
            g.DrawRect(dx - 1, dy - 1, dw + 2, dh + 2);
            Text(g, DialogTitle, dx + dw / 2, dy + 12, Resources.FONT_DWARVENTODCRAFT15,
                new SexyColor(255, 244, 170), DrawStringJustification.Center);

            var label = new SexyColor(255, 232, 170);
            switch (_dialog)
            {
                case Dialog.AddServer:
                    Text(g, "服务器名称", dx + 24, dy + 42, Resources.FONT_BRIANNETOD12, label);
                    Text(g, "服务器地址（IP 或域名）", dx + 24, dy + 116, Resources.FONT_BRIANNETOD12, label);
                    Text(g, "端口（中继控制口）", dx + 24, dy + 190, Resources.FONT_BRIANNETOD12, label);
                    break;
                case Dialog.ManualJoin:
                    Text(g, "对方地址（IP / 虚拟网 / 穿透地址）", dx + 24, dy + 42,
                        Resources.FONT_BRIANNETOD12, label);
                    Text(g, "端口（主机建房端口，双方要一致）", dx + 24, dy + 116,
                        Resources.FONT_BRIANNETOD12, label);
                    break;
                case Dialog.JoinRoom:
                    Text(g, "房间号（主机那台显示的 6 位数字）", dx + 24, dy + 42,
                        Resources.FONT_BRIANNETOD12, label);
                    Text(g, "房间密码（没设密码就留空）", dx + 24, dy + 116,
                        Resources.FONT_BRIANNETOD12, label);
                    break;
                case Dialog.CreateRoom:
                    Text(g, "关卡", dx + 24, dy + 42, Resources.FONT_BRIANNETOD12, label);
                    int li = Sync.Session.ClampLevelIndex(PendingOrCurrent());
                    Text(g, Clip(Sync.Session.Levels[li].FullLabel, 16), dx + 24, dy + 66,
                        Resources.FONT_BRIANNETOD16, new SexyColor(240, 240, 235));
                    if (SelectedIsLan)
                    {
                        Text(g, "局域网建房不需要密码：同一网段里搜到房间的人就能进来", dx + 24, dy + 160,
                            Resources.FONT_BRIANNETOD12, new SexyColor(255, 235, 200));
                    }
                    break;
            }
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

                    case HitKind.ServerRow:
                        DrawServerRow(g, h, font16, font12);
                        break;

                    case HitKind.RoomRow:
                        Bar(g, h, new SexyColor(48, 34, 26, 235), new SexyColor(24, 16, 12, 220));
                        g.SetFont(font16);
                        g.SetColor(new SexyColor(238, 232, 220));
                        g.DrawString(h.Main, h.X + 10, h.Y + 11);
                        g.SetFont(font12);
                        g.SetColor(new SexyColor(215, 195, 165));
                        g.DrawString(h.Right, h.X + h.W - 8 - (int)font12.StringWidth(h.Right), h.Y + 13);
                        break;

                    case HitKind.Seat:
                        DrawSeat(g, h, font16, font12);
                        break;

                    case HitKind.Kick:
                        Centered(g, h, font12, new SexyColor(74, 44, 40, 220),
                            new SexyColor(255, 150, 130, 170), new SexyColor(255, 200, 180));
                        break;

                    case HitKind.Small:
                        Centered(g, h, font16, new SexyColor(96, 44, 40, 230),
                            new SexyColor(255, 170, 150, 200), new SexyColor(255, 230, 220));
                        break;

                    case HitKind.CheckBox:
                        DrawCheckBox(g, h, font16);
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

        /// <summary>
        /// 服务器行：参考图里是石头色按钮，这里自绘成同一种观感（贴图不能拉伸）。
        /// 副文字（地址/说明）不画在行下面——行距只有 34px，压到下一行上（实机截图踩过）。
        /// </summary>
        private static void DrawServerRow(Graphics g, Hit h, Font font16, Font font12)
        {
            Bar(g, h,
                h.Selected ? new SexyColor(176, 180, 190, 240) : new SexyColor(122, 126, 136, 230),
                h.Selected ? new SexyColor(255, 240, 170, 230) : new SexyColor(58, 60, 66, 220));
            g.SetFont(font16);
            g.SetColor(new SexyColor(28, 96, 34));
            g.DrawString(h.Main, h.X + (h.W - (int)font16.StringWidth(h.Main)) / 2, h.Y + 5);
        }

        private static void DrawCheckBox(Graphics g, Hit h, Font font16)
        {
            bool enabled = h.Id >= 0;
            g.SetColor(new SexyColor(40, 34, 28, 235));
            g.FillRect(h.X, h.Y + 2, 22, 22);
            g.SetColor(enabled ? new SexyColor(255, 200, 120, 230) : new SexyColor(150, 130, 105, 200));
            g.DrawRect(h.X, h.Y + 2, 22, 22);
            if (h.Selected)
            {
                g.SetFont(font16);
                g.SetColor(new SexyColor(170, 235, 150));
                g.DrawString("✓", h.X + 3, h.Y + 5);
            }
            g.SetFont(font16);
            g.SetColor(enabled ? new SexyColor(240, 236, 226) : new SexyColor(178, 172, 162));
            g.DrawString(h.Main, h.X + 32, h.Y + 5);
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
                if (_dialog != Dialog.None || _dropOpen)
                {
                    return; // 模态：对话框/下拉开着时点外面不翻页也不关页，Esc 才收
                }
                // 点空白处把焦点还给整页：软键盘弹着的时候没有别的出口
                if (_app.mWidgetManager.mFocusWidget is IpInputWidget)
                {
                    _app.mWidgetManager.SetFocus(this);
                }
            }
            catch (Exception ex)
            {
                Core.ModEnv.LogOnce("联机页点击处理异常（重复不再记）: " + ex.Message);
            }
            base.MouseDown(x, y, theBtnNum, theClickCount);
        }

        public override void KeyDown(KeyCode theKey)
        {
            if (theKey == KeyCode.Escape)
            {
                if (_dropOpen)
                {
                    ClosePicker();
                    return;
                }
                if (_dialog != Dialog.None)
                {
                    CloseDialog();
                }
                return;
            }
            base.KeyDown(theKey);
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
                case AddServerId:
                    OpenDialog(Dialog.AddServer);
                    return;
                case CreateRoomId:
                    OpenDialog(Dialog.CreateRoom);
                    return;
                case RefreshId:
                    // 局域网是"催一次重扫"，中继是"再问一次 LIST"
                    if (SelectedIsRelay)
                    {
                        Sync.Session.RelayRefresh();
                    }
                    else
                    {
                        Sync.Session.RediscoverNow();
                    }
                    break;
                case DlgOkId:
                    ConfirmDialog();
                    return;
                case DlgCancelId:
                    CloseDialog();
                    return;
                case PickLevelBtnId:
                    // 下拉是整页级的覆盖层，对话框先让位；选完关回来时再恢复
                    _dropFromDialog = true;
                    JumpToSelectedLevel();
                    _dropOpen = true;
                    _dialog = Dialog.None;
                    break;
                case LevelId:
                    if (!_dropOpen)
                    {
                        JumpToSelectedLevel();
                    }
                    _dropOpen = !_dropOpen;
                    break;
                case DropCloseId:
                    ClosePicker();
                    return;
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

        /// <summary>收掉选关面板：从"创建房间"对话框点开的，收完要回到那个对话框。</summary>
        private void ClosePicker()
        {
            bool backToCreate = _dropFromDialog;
            _dropOpen = false;
            _dropFromDialog = false;
            if (backToCreate)
            {
                _dialog = Dialog.CreateRoom;
            }
            RefreshUi();
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
            if (theId >= LvlRowIdBase && theId < LvlRowIdBase + LobbyGeom.PickRows)
            {
                int k = _dropPage * LobbyGeom.PickRows + (theId - LvlRowIdBase);
                if (k >= _dropIndices.Count)
                {
                    return;
                }
                int picked = _dropIndices[k];
                if (_dropFromDialog)
                {
                    // 对话框里选关：只记下，等[创建]再落地（还没建房，HostSetLevel 会直接返回）
                    _pendingLevel = picked;
                    _dropFromDialog = false;
                    _dropOpen = false;
                    _dialog = Dialog.CreateRoom;
                    return;
                }
                Sync.Session.HostSetLevel(picked);
                _dropOpen = false;
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
            if (theId >= RoomRowIdBase && theId < RoomRowIdBase + RoomRowMax)
            {
                int idx = theId - RoomRowIdBase;
                if (idx < Sync.Session.RoomList.Count)
                {
                    Sync.Session.JoinFromDiscovery(Sync.Session.RoomList[idx].Ip);
                }
                return;
            }
            if (theId == ManualRowId)
            {
                OpenDialog(Dialog.ManualJoin);
                return;
            }
            if (theId == PwToggleId)
            {
                _pwOn = !_pwOn;
                return;
            }
            if (theId == DelServerId)
            {
                RemoveServer(_sel);
                return;
            }
            if (theId == CodeRowId)
            {
                OpenDialog(Dialog.JoinRoom);
                return;
            }
            if (theId >= RelayRoomIdBase && theId < RelayRoomIdBase + RoomRowMax)
            {
                int i = theId - RelayRoomIdBase;
                var rooms = Sync.Session.RelayRooms;
                if (i < rooms.Count)
                {
                    var r = rooms[i];
                    if (r.Locked)
                    {
                        _joinCode = r.Code;   // 要密码的先问一句
                        OpenDialog(Dialog.JoinRoom);
                    }
                    else
                    {
                        Sync.Session.JoinViaRelay(SelectedEntry, r.Code, "");
                    }
                }
                return;
            }
            if (theId >= ServerRowIdBase && theId < ServerRowIdBase + ServerRowMax)
            {
                SelectServerRow(theId - ServerRowIdBase);
            }
        }

        private void RemoveServer(int rowIndex)
        {
            if (rowIndex <= 0 || rowIndex >= _serverRows.Count)
            {
                return;
            }
            var entry = _serverRows[rowIndex].Entry;
            if (entry == null)
            {
                return;
            }
            var cfg = Core.ModEnv.GetConfig();
            cfg.Servers.Remove(entry);
            Core.ModEnv.SaveConfig();
            _sel = 0;
            Sync.Session.SetStatus("已移除中继：" + entry.Name, false);
            Core.ModEnv.Log("联机页移除服务器 " + entry.Host + ":" + entry.Port);
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
            RemoveWidget(_addServerBtn);
            RemoveWidget(_delServerBtn);
            RemoveWidget(_createBtn);
            RemoveWidget(_refreshBtn);
            RemoveWidget(_levelBtn);
            RemoveWidget(_startBtn);
            RemoveWidget(_disconnectBtn);
            RemoveWidget(_readyBtn);
            RemoveWidget(_dlgOk);
            RemoveWidget(_dlgCancel);
            RemoveWidget(_pickLevelBtn);
            RemoveWidget(_nameEdit);
            RemoveWidget(_addrEdit);
            RemoveWidget(_portEdit);
            RemoveWidget(_pwdEdit);
            _hits.Clear();
            base.RemovedFromManager(manager);
        }
    }
}
