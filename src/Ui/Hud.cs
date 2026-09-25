using System;
using Lawn;
using PGvZOnlineMod.Sync;
using Sexy;

namespace PGvZOnlineMod.Ui
{
    /// <summary>
    /// 局内 HUD：所有其他玩家的光标（昵称牌）+ 连接状态角标 + 聊天/事件浮层。
    /// 在 Board.Draw 后段绘制（棋盘局部坐标系）。每帧执行——不做任何分配，
    /// 动态字符串一律由 Session 在状态变化时提前拼好。
    /// </summary>
    public static class Hud
    {
        private static string _cachedStatus = "";
        private static bool _cachedError;
        private static float _cachedRtt = -1f;
        private static bool _cachedConnected;
        private static int _cachedPlayerCount = -1;

        /// <summary>状态字符串重建（主线程泵低频调用，非绘制路径）。</summary>
        public static void RefreshStatusCache()
        {
            bool connected = Session.Net.IsConnected;
            float rtt = Session.Net.RemoteRttMs;
            int count = 0;
            for (int s = 0; s < Session.MaxPlayers; s++)
            {
                if (Session.SlotOccupied[s])
                {
                    count++;
                }
            }
            if (_cachedStatus == Session.StatusText && _cachedError == Session.StatusIsError
                && _cachedConnected == connected && _cachedPlayerCount == count
                && Math.Abs(_cachedRtt - rtt) < 15f)
            {
                return;
            }
            _cachedStatus = Session.StatusText;
            _cachedError = Session.StatusIsError;
            _cachedConnected = connected;
            _cachedRtt = rtt;
            _cachedPlayerCount = count;
        }

        public static void Draw(Board board, Graphics g)
        {
            if (Session.Phase != SessionPhase.InGame)
            {
                return;
            }

            // 其他玩家的光标（2 秒内有效；跳过自己的槽位）
            for (int s = 0; s < Session.MaxPlayers; s++)
            {
                if (s == Session.MySlot || Session.RemoteCursorAge[s] >= 2.0)
                {
                    continue;
                }
                float cx = Session.RemoteCursorX[s];
                float cy = Session.RemoteCursorY[s];
                if (cx <= -9000f)
                {
                    continue;
                }
                g.SetColor(new SexyColor(120, 220, 255, 200));
                g.FillRect((int)cx - 3, (int)cy - 3, 7, 7);
                g.SetColor(new SexyColor(120, 220, 255, 90));
                g.FillRect((int)cx - 6, (int)cy - 6, 13, 13);
                string nick = Session.Nicks[s];
                if (!string.IsNullOrEmpty(nick))
                {
                    g.SetFont(Resources.FONT_BRIANNETOD16);
                    g.SetColor(new SexyColor(120, 220, 255));
                    g.DrawString(nick, (int)cx + 8, (int)cy - 10);
                }
            }

            // 右上角连接状态
            g.SetFont(Resources.FONT_BRIANNETOD16);
            if (Session.Net.IsConnected)
            {
                g.SetColor(new SexyColor(140, 255, 140, 220));
                string label = "联机中 · " + _cachedPlayerCount + " 人"
                    + (_cachedRtt > 0 ? "  " + (int)_cachedRtt + "ms" : "");
                int w = (int)Resources.FONT_BRIANNETOD16.StringWidth(label);
                g.DrawString(label, board.mWidth - w - 16, 8);
            }
            else
            {
                g.SetColor(new SexyColor(255, 110, 90, 220));
                g.DrawString("连接已断开（单机继续）", board.mWidth - 260, 8);
            }

            // 浮动消息（聊天/提示）
            if (Session.LastChatAge < 6.0 && !string.IsNullOrEmpty(Session.LastChat))
            {
                g.SetFont(Resources.FONT_BRIANNETOD16);
                g.SetColor(new SexyColor(255, 255, 255, 220));
                g.DrawString(Session.LastChat, 16, board.mHeight - 30);
            }
        }
    }
}
