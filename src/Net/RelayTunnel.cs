using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PGvZOnlineMod.Core;
using PGvZOnlineMod.Protocol;

namespace PGvZOnlineMod.Net
{
    /// <summary>
    /// 主机侧的一条本地隧道：中继 ↔ 本机游戏服务器。
    ///
    /// 为什么一个客人要一条：主机的 Lidgren 服务器按"对端端点"认连接。三个客人的包若都
    /// 从同一个源端点进来，主机只会认成一个。每条隧道各自绑一个本地随机端口，
    /// 转发时源端口天然不同，主机才能正常分出 1P/2P/3P/4P；
    /// 回包由中继从客人当初拨的 guestPort 发出去，客人的客户端才不会觉得对端变了。
    ///
    /// 登记报文（<c>PGVZREG 房间码 槽位</c>）每秒重发一次，兼作 NAT 保活——
    /// 隧道是纯出站发起的，主机不需要任何公网可入端口或端口映射。
    ///
    /// 非阻塞、只在主线程泵里 <see cref="Tick"/>：不碰游戏状态，只搬数据报。
    /// </summary>
    public sealed class RelayTunnel : IDisposable
    {
        /// <summary>一帧最多搬多少包：宁可下一帧再搬，也不能让绘制卡在这一圈里。</summary>
        private const int MaxPerTick = 32;
        private const int RegIntervalMs = 1000;

        private UdpClient _sock;
        private IPEndPoint _relayHostPort;      // 中继的 hostPort（登记与转发都走这一个口）
        private IPEndPoint _gameEp;             // 127.0.0.1:主机实际绑定的游戏端口
        private int _nextReg;

        public int Slot { get; }
        public int LocalPort { get; private set; }
        public string Error { get; private set; }

        public RelayTunnel(int slot)
        {
            Slot = slot;
        }

        public bool Open(IPEndPoint relayHostPort, IPEndPoint gameEp)
        {
            Close();
            if (relayHostPort == null || gameEp == null)
            {
                Error = "隧道缺少端点";
                return false;
            }
            try
            {
                _sock = new UdpClient(AddressFamily.InterNetwork);
                // 绑 Any 而不是 Loopback：这条隧道既要够得着本机的游戏端口，也要够得着中继的公网地址
                _sock.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                RelayClient.DisableIcmpReset(_sock);   // 同 RelayClient：ICMP 错误会连真包一起吃掉
                LocalPort = ((IPEndPoint)_sock.Client.LocalEndPoint).Port;
                _relayHostPort = relayHostPort;
                _gameEp = gameEp;
                Error = null;
                return true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Close();
                return false;
            }
        }

        /// <summary>每帧调一次：发登记/保活，再把两个方向的包各搬一批。</summary>
        public void Tick(string roomCode)
        {
            if (_sock == null)
            {
                return;
            }
            int now = Environment.TickCount;
            if (now - _nextReg >= 0)
            {
                _nextReg = now + RegIntervalMs;
                SendRaw(Encoding.UTF8.GetBytes(RelayProtocol.BuildTunnelRegister(roomCode, Slot)));
            }

            try
            {
                int n = 0;
                while (n < MaxPerTick && _sock.Available > 0)
                {
                    IPEndPoint from = null;
                    byte[] data = _sock.Receive(ref from);
                    n++;
                    if (data == null || data.Length == 0 || from == null)
                    {
                        continue;
                    }
                    // 按完整端点判来源：中继那边是 hostPort，游戏这边是 BoundPort，
                    // 认错的后果是把客人的包喂回中继（自我放大），所以宁可丢掉也不猜
                    if (from.Port == _relayHostPort.Port && from.Address.Equals(_relayHostPort.Address))
                    {
                        SendTo(data, _gameEp);
                    }
                    else if (from.Port == _gameEp.Port && from.Address.Equals(_gameEp.Address))
                    {
                        SendTo(data, _relayHostPort);
                    }
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                ModEnv.LogOnce("隧道" + Slot + " 转发异常: " + ex.Message);
            }
        }

        private void SendRaw(byte[] data) => SendTo(data, _relayHostPort);

        private void SendTo(byte[] data, IPEndPoint to)
        {
            try
            {
                _sock.Send(data, data.Length, to);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                ModEnv.LogOnce("隧道" + Slot + " 发送失败: " + ex.Message);
            }
        }

        public void Close()
        {
            try { _sock?.Close(); } catch { }
            _sock = null;
            LocalPort = 0;
        }

        public void Dispose() => Close();
    }
}
