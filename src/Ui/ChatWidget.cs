using System;
using Lawn;
using PGvZOnlineMod.Core;
using PGvZOnlineMod.Sync;
using Sexy;

namespace PGvZOnlineMod.Ui
{
    /// <summary>
    /// 局内聊天：右上角常驻"聊天"小按钮（触屏可点），对局中按 Enter 也能开。
    ///
    /// 为什么这样接键盘：这游戏只有**一条**键盘通路——WidgetManager 把 KeyChar/KeyDown
    /// 只发给 mFocusWidget，而棋盘自己就是那个焦点控件（Board.KeyChar 吃 0-9 选卡、
    /// 空格暂停、Tab 加速、q~n 炮击；Board.KeyDown 吃 ESC 菜单）。所以打开聊天框时
    /// 只要 SetFocus(本控件) 就能完整屏蔽这些热键，不需要再给 Board 加拦键钩子。
    /// 代价是关闭时**必须**把焦点还给 Board，否则游戏热键永久失灵。
    ///
    /// 继承 IMECompatibleWidget 并登记 mIMEHotWidget，Android 点输入区才会弹软键盘；
    /// 文本仍然走同一条 KeyChar 通路（Windows/Android 一致）。实测坑：软键盘组词中的
    /// 退格会以 KeyChar code=8 透传而不是 KeyCode.Back，两种都要处理。
    ///
    /// 打开时控件尺寸铺满整屏（顺带吞掉所有点击，避免边打字边往草坪上种东西）；
    /// 关闭时缩回按钮大小，绝不挡棋盘。
    /// </summary>
    public class ChatWidget : IMECompatibleWidget
    {
        private const int MaxChars = 42;
        private const int BtnW = 76;
        private const int BtnH = 24;

        /// <summary>快捷短语：不方便打字（尤其触屏）时一键发出。</summary>
        private static readonly string[] QuickPhrases =
        {
            "救命！我这行顶不住",
            "来人补一下左路",
            "阳光不够了",
            "稳住，能赢",
        };

        private static ChatWidget _inst;
        private readonly LawnApp _app;

        private string _text = "";
        private bool _open;
        private int _blinkAcc;
        private bool _showCursor;

        private ChatWidget(LawnApp app)
        {
            _app = app;
            mWantsFocus = true;
            mDoFinger = true;
        }

        private int ScreenW => _app != null ? _app.mWidth : 800;

        private int ScreenH => _app != null ? _app.mHeight : 600;

        // ============================================================ 挂载 / 摘除（主线程泵调用）

        /// <summary>随对局生命周期装卸控件；每帧调用，内部有幂等守卫。</summary>
        public static void Sync(LawnApp app)
        {
            try
            {
                // 只在"局内 + 仍然连着"时存在：断线回退单机后按钮自动消失，
                // 打字框若开着也会一并关闭并把键盘焦点还给棋盘。
                bool wantIt = app != null && Session.InGame && Session.Net.IsConnected;
                if (wantIt && _inst == null)
                {
                    _inst = new ChatWidget(app);
                    app.mWidgetManager.AddWidget(_inst);
                    _inst.ResizeToButton();
                    app.mWidgetManager.BringToFront(_inst);
                }
                else if (!wantIt && _inst != null)
                {
                    var old = _inst;
                    _inst = null;
                    if (old._open)
                    {
                        old.GiveFocusBack(); // 必须先还焦点，再把自己从控件树摘掉
                    }
                    if (app != null)
                    {
                        app.mWidgetManager.RemoveWidget(old);
                        app.SafeDeleteWidget(old);
                    }
                }
            }
            catch (Exception ex)
            {
                ModEnv.LogOnce("聊天控件装卸异常（重复不再记）: " + ex.Message);
            }
        }

        // ============================================================ 开关

        private void ResizeToButton()
        {
            Resize(ScreenW - BtnW - 12, 34, BtnW, BtnH);
        }

        /// <summary>Enter 键入口（由 Board.KeyDown 钩子调用）。返回 true 表示这个键已被消费。</summary>
        public static bool TryToggleByKeyboard()
        {
            if (_inst == null || !Session.Net.IsConnected)
            {
                return false;
            }
            _inst.Toggle();
            return true;
        }

        public void Toggle()
        {
            if (_open)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        private void Open()
        {
            try
            {
                _open = true;
                Resize(0, 0, ScreenW, ScreenH);
                if (mWidgetManager != null)
                {
                    mWidgetManager.SetFocus(this);
                }
                MarkDirty();
            }
            catch (Exception ex)
            {
                ModEnv.LogOnce("打开聊天框失败（重复不再记）: " + ex.Message);
            }
        }

        private void Close()
        {
            _open = false;
            _text = "";
            ResizeToButton();
            GiveFocusBack();
            MarkDirty();
        }

        /// <summary>焦点交还棋盘——漏掉这一步会让数字键/空格/ESC 全部失灵。</summary>
        private void GiveFocusBack()
        {
            try
            {
                var board = _app != null ? _app.mBoard : null;
                if (mWidgetManager != null && board != null)
                {
                    mWidgetManager.SetFocus(board);
                }
            }
            catch (Exception ex)
            {
                ModEnv.LogOnce("归还键盘焦点失败（重复不再记）: " + ex.Message);
            }
        }

        private void Send(string text)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Session.SendChat(text.Trim());
                }
            }
            catch (Exception ex)
            {
                ModEnv.LogOnce("发送聊天失败（重复不再记）: " + ex.Message);
            }
        }

        // ============================================================ 焦点与 IME

        public override bool WantsFocus() => true;

        /// <summary>
        /// IME/软键盘热区：游戏自己的 WidgetManager.HandleGlobalIME 在点击该区域时
        /// 会 StartTextComposition 并把控件顶起避让软键盘——所以本模组不去调
        /// mIMEHandler（它在 MonoGame.IMEHelper.Common 里，不值得为它加程序集引用），
        /// 只登记 mIMEHotWidget 就够了。文本上屏始终走 KeyChar（联机页昵称框已验证）。
        /// </summary>
        public override TRect mIMEHotArea =>
            _open ? new TRect(_panelX + 16, _panelY + 40, _panelW - 32, 30)
                  : new TRect(mX, mY, mWidth, mHeight);

        public override void GotFocus()
        {
            try
            {
                base.GotFocus();
            }
            catch
            {
                mHasFocus = true;
            }
            _showCursor = true;
            try
            {
                if (mWidgetManager != null)
                {
                    mWidgetManager.mIMEHotWidget = this;
                }
            }
            catch (Exception ex)
            {
                ModEnv.LogOnce("登记输入法热区失败（重复不再记）: " + ex.Message);
            }
        }

        public override void LostFocus()
        {
            try
            {
                base.LostFocus();
            }
            catch
            {
                mHasFocus = false;
            }
            _showCursor = false;
            try
            {
                if (mWidgetManager != null && ReferenceEquals(mWidgetManager.mIMEHotWidget, this))
                {
                    mWidgetManager.mIMEHotWidget = null;
                }
            }
            catch
            {
            }
        }

        // ============================================================ 输入

        public override void Update()
        {
            base.Update();
            if (!_open)
            {
                return;
            }
            if (++_blinkAcc > 30)
            {
                _blinkAcc = 0;
                _showCursor = !_showCursor;
            }
        }

        public override void KeyDown(KeyCode theKey)
        {
            try
            {
                if (!_open)
                {
                    return;
                }
                if (theKey == KeyCode.Return)
                {
                    Send(_text);
                    Close();
                }
                else if (theKey == KeyCode.Escape)
                {
                    Close();
                }
                else if (theKey == KeyCode.Back)
                {
                    Backspace();
                }
                else
                {
                    base.KeyDown(theKey);
                }
            }
            catch (Exception ex)
            {
                ModEnv.LogOnce("聊天按键处理异常（重复不再记）: " + ex.Message);
            }
        }

        public override void KeyChar(SexyChar theChar)
        {
            try
            {
                if (!_open)
                {
                    return;
                }
                char c = theChar.value_type;
                int code = c;
                // 软键盘退格以 code=8 透传（见类注释）
                if (code == 8 || code == 127)
                {
                    Backspace();
                    return;
                }
                if (code < 32 || _text.Length >= MaxChars)
                {
                    return;
                }
                _text += c;
                _blinkAcc = 0;
                _showCursor = true;
                MarkDirty();
            }
            catch (Exception ex)
            {
                ModEnv.LogOnce("聊天字符处理异常（重复不再记）: " + ex.Message);
            }
        }

        private void Backspace()
        {
            if (_text.Length > 0)
            {
                _text = _text.Substring(0, _text.Length - 1);
            }
            _blinkAcc = 0;
            _showCursor = true;
            MarkDirty();
        }

        public override void MouseDown(int x, int y, int theBtnNum, int theClickCount)
        {
            try
            {
                if (!_open)
                {
                    Open(); // 关闭时控件只有按钮大小，点到这里就是想聊天
                    return;
                }
                foreach (var r in PhraseRects())
                {
                    if (x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H)
                    {
                        Send(r.Text);
                        return;
                    }
                }
                if (x < _panelX || x >= _panelX + _panelW || y < _panelY || y >= _panelY + _panelH)
                {
                    Close(); // 点面板外＝取消
                }
            }
            catch (Exception ex)
            {
                ModEnv.LogOnce("聊天点击处理异常（重复不再记）: " + ex.Message);
            }
        }

        /// <summary>手柄 Back / 安卓返回键：开着就关掉并吞掉，否则会一路退到主菜单。</summary>
        public override bool BackButtonPress()
        {
            if (_open)
            {
                Close();
                return true;
            }
            return false;
        }

        // ============================================================ 绘制

        private struct HitRect
        {
            public int X, Y, W, H;
            public string Text;
        }

        private int _panelX => (ScreenW - 600) / 2;

        private int _panelW => 600;

        private int _panelH => 196;

        private int _panelY => (ScreenH - _panelH) / 2;

        private HitRect[] PhraseRects()
        {
            var rects = new HitRect[QuickPhrases.Length];
            int px = _panelX + 16;
            int py = _panelY + 96;
            for (int i = 0; i < QuickPhrases.Length; i++)
            {
                rects[i] = new HitRect
                {
                    X = px + (i % 2) * 292,
                    Y = py + (i / 2) * 38,
                    W = 276,
                    H = 30,
                    Text = QuickPhrases[i],
                };
            }
            return rects;
        }

        public override void Draw(Graphics g)
        {
            try
            {
                var font = Resources.FONT_BRIANNETOD16;
                g.SetFont(font);
                if (!_open)
                {
                    DrawButton(g, font);
                    return;
                }
                DrawPanel(g, font);
            }
            catch (Exception ex)
            {
                ModEnv.LogOnce("聊天界面绘制异常（重复不再记）: " + ex.Message);
            }
        }

        private void DrawButton(Graphics g, Font font)
        {
            g.SetColor(new SexyColor(20, 24, 16, 190));
            g.FillRect(0, 0, mWidth, mHeight);
            g.SetColor(new SexyColor(150, 210, 255, 170));
            g.DrawRect(0, 0, mWidth, mHeight);
            const string label = "聊天";
            g.SetColor(new SexyColor(225, 235, 215));
            g.DrawString(label, (mWidth - font.StringWidth(label)) / 2, (mHeight - 16) / 2);
        }

        private void DrawPanel(Graphics g, Font font)
        {
            int px = _panelX, py = _panelY, pw = _panelW, ph = _panelH;
            g.SetColor(new SexyColor(0, 0, 0, 150));
            g.FillRect(0, 0, mWidth, mHeight); // 整屏遮罩：打字期间不吃棋盘点击
            g.SetColor(new SexyColor(28, 34, 22, 245));
            g.FillRect(px, py, pw, ph);
            g.SetColor(new SexyColor(170, 220, 150, 220));
            g.DrawRect(px, py, pw, ph);

            g.SetColor(new SexyColor(255, 235, 150));
            g.DrawString("联机聊天", px + 16, py + 10);
            g.SetColor(new SexyColor(190, 200, 180, 210));
            string tip = Session.LivePlayerCount() + " 人在场 · Enter 发送 · Esc 取消";
            g.DrawString(tip, px + pw - font.StringWidth(tip) - 16, py + 10);

            // 输入行
            int ix = px + 16, iy = py + 40, iw = pw - 32, ih = 30;
            g.SetColor(new SexyColor(12, 14, 10, 230));
            g.FillRect(ix, iy, iw, ih);
            g.SetColor(mHasFocus ? new SexyColor(255, 220, 149) : new SexyColor(160, 160, 160, 160));
            g.DrawRect(ix, iy, iw, ih);
            g.SetColor(new SexyColor(235, 225, 200));
            if (_text.Length == 0)
            {
                g.SetColor(new SexyColor(160, 170, 150, 170));
                g.DrawString("说点什么…（触屏可点下面的快捷短语）", ix + 8, iy + 7);
            }
            else
            {
                g.SetColor(new SexyColor(235, 225, 200));
                g.DrawString(_text, ix + 8, iy + 7);
            }
            if (_showCursor && mHasFocus)
            {
                g.SetColor(new SexyColor(170, 230, 140, 255));
                g.FillRect(ix + 8 + (int)font.StringWidth(_text), iy + 6, 2, ih - 12);
            }

            // 快捷短语
            foreach (var r in PhraseRects())
            {
                g.SetColor(new SexyColor(46, 56, 36, 235));
                g.FillRect(r.X, r.Y, r.W, r.H);
                g.SetColor(new SexyColor(150, 210, 255, 150));
                g.DrawRect(r.X, r.Y, r.W, r.H);
                g.SetColor(new SexyColor(225, 235, 215));
                g.DrawString(r.Text, r.X + 10, r.Y + 7);
            }

            // 最近一条聊天（让对方知道你发出去了）
            if (!string.IsNullOrEmpty(Session.LastChat) && Session.LastChatAge < 8.0)
            {
                g.SetColor(new SexyColor(200, 210, 190, 220));
                g.DrawString(Session.LastChat, px + 16, py + ph - 22);
            }
        }
    }
}
