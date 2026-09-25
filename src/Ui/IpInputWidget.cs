using System;
using Sexy;

namespace PGvZOnlineMod.Ui
{
    /// <summary>
    /// 自绘单行文本输入（光标在文末，带闪烁，仿 植物娘AI对话 ChatInputWidget 的做法）。
    /// Lawn.dll 的 EditWidget 是 internal，mod 无法访问；也不引入 IME 依赖。
    /// 点击获得焦点（mWantsFocus），Backspace 删字。
    /// AllowAnyChar=false：IP/端口模式，只收 ASCII 数字与 . : 字母；
    /// AllowAnyChar=true：昵称模式，收一切可见字符（含中文，依赖游戏引擎的 KeyChar 通路）。
    /// </summary>
    public class IpInputWidget : Widget
    {
        private string _text = "";
        public int MaxLength = 21;
        public bool AllowAnyChar;

        private int _blinkAcc;
        private bool _showCursor = true;
        private const int BlinkDelay = 30; // 帧

        public IpInputWidget()
        {
            mWantsFocus = true;
            mDoFinger = true;
        }

        public string Text => _text;

        public void SetText(string s)
        {
            _text = s ?? "";
            if (_text.Length > MaxLength)
            {
                _text = _text.Substring(0, MaxLength);
            }
            ResetBlink();
            MarkDirty();
        }

        private void ResetBlink()
        {
            _blinkAcc = 0;
            _showCursor = true;
        }

        public override void Update()
        {
            base.Update();
            _blinkAcc++;
            if (_blinkAcc > BlinkDelay)
            {
                _blinkAcc = 0;
                _showCursor = !_showCursor;
            }
        }

        public override void KeyChar(SexyChar theChar)
        {
            char c = theChar.value_type;
            if (_text.Length >= MaxLength || c < ' ')
            {
                return;
            }
            if (AllowAnyChar || char.IsDigit(c) || c == '.' || c == ':'
                || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            {
                _text += c;
                ResetBlink();
                MarkDirty();
            }
        }

        public override void KeyDown(KeyCode theKey)
        {
            if (theKey == KeyCode.Back)
            {
                if (_text.Length > 0)
                {
                    _text = _text.Substring(0, _text.Length - 1);
                }
                ResetBlink();
                MarkDirty();
            }
        }

        public override void Draw(Graphics g)
        {
            var font = Resources.FONT_BRIANNETOD16;
            g.SetColor(new SexyColor(0, 0, 0, 210));
            g.FillRect(0, 0, mWidth, mHeight);
            g.SetColor(mHasFocus ? new SexyColor(255, 220, 149) : new SexyColor(255, 255, 255, 90));
            g.DrawRect(0, 0, mWidth, mHeight);
            g.SetFont(font);
            g.SetColor(new SexyColor(235, 225, 200));
            int textY = (mHeight - 16) / 2;
            g.DrawString(_text, 6, textY);

            // 闪烁光标（有焦点时）：文末一条 2px 绿条（同 AI对话 ChatInputWidget）
            if (_showCursor && mHasFocus)
            {
                try
                {
                    int cursorX = 6 + (int)font.StringWidth(_text);
                    g.SetColor(new SexyColor(170, 230, 140, 255));
                    g.FillRect(cursorX, 5, 2, mHeight - 10);
                }
                catch
                {
                }
            }
        }
    }
}
