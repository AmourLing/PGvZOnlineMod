using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PGvZOnlineMod.Core;
using PGvZOnlineMod.Protocol;

namespace PGvZOnlineMod.Net
{
    /// <summary>
    /// 中继控制通道的客户端（一条独立的 UDP 文本口，与游戏数据的 Lidgren 口分开）。
    ///
    /// 全程非阻塞、只在主线程泵里 Tick：收包靠 <see cref="Poll"/>，
    /// 不另起线程——这个类的结果要直接写进 Session 的房间表，跨线程碰游戏状态是另一套纪律。
    /// 域名在这里解析一次（点开服务器那一下阻塞几十毫秒可以接受，每帧解析不行）。
    /// </summary>
    public sealed class RelayClient : IDisposable
    {
        private UdpClient _sock;
        private IPEndPoint _server;

        public string Host { get; private set; }
        public int Port { get; private set; }
        public string Error { get; private set; }

        /// <summary>拿到过任何一次服务端应答（PONG/ROOM/ROOMS/OK）就算通。</summary>
        public bool Alive { get; private set; }

        /// <summary>最后一次收到应答的时刻（Environment.TickCount 毫秒），用来判"这服务器还活着吗"。</summary>
        public int LastReplyMs { get; private set; } = -100000;

        /// <summary>解析后的服务端点；主机侧隧道要拿它的地址去配中继的 hostPort。</summary>
        public IPEndPoint Server => _server;

        public bool Open(string host, int port)
        {
            Close();
            Host = host ?? "";
            Port = port;
            try
            {
                var addrs = Dns.GetHostAddresses(Host);
                IPAddress ip = null;
                foreach (var a in addrs)
                {
                    if (a.AddressFamily == AddressFamily.InterNetwork)
                    {
                        ip = a;
                        break;
                    }
                }
                if (ip == null)
                {
                    Error = "解析不到 " + Host + " 的 IPv4 地址";
                    return false;
                }
                _server = new IPEndPoint(ip, Port);
                _sock = new UdpClient(AddressFamily.InterNetwork);
                _sock.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                DisableIcmpReset(_sock);
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

        /// <summary>
        /// 关掉 Windows 把 ICMP "端口不可达" 变成接收异常的行为。
        /// 不关的后果不是报错而是**丢包**：对端 momentarily 没起来时，
        /// 下一次 Receive 会抛 WSAECONNRESET，把同一时刻真正到达的应答一起吃掉——
        /// 主机那边表现为"HOST 发了没回"，于是重试，每次重试又真的建一个新房，
        /// 服务器被自己人占满（这条是端到端用例踩出来的）。Linux/Android 不支持这个
        /// IOControl，忽略即可。
        /// </summary>
        internal static void DisableIcmpReset(UdpClient sock)
        {
            const uint SIO_UDP_CONNRESET = 0x9800000C;
            try
            {
                sock.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch
            {
            }
        }

        public void Send(string message)
        {
            if (_sock == null || _server == null || string.IsNullOrEmpty(message))
            {
                return;
            }
            try
            {
                byte[] b = Encoding.UTF8.GetBytes(message);
                _sock.Send(b, b.Length, _server);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                ModEnv.LogOnce("中继控制包发送失败: " + ex.Message);
            }
        }

        /// <summary>把当前排队的应答都解出来（不阻塞）。返回本次收到的条数。</summary>
        public int Poll(List<RelayMessage> into, int max = 16)
        {
            if (_sock == null || into == null)
            {
                return 0;
            }
            int n = 0;
            try
            {
                while (n < max && _sock.Available > 0)
                {
                    IPEndPoint from = null;
                    byte[] raw = _sock.Receive(ref from);
                    if (raw == null || raw.Length == 0)
                    {
                        continue;
                    }
                    var msg = RelayProtocol.Parse(Encoding.UTF8.GetString(raw));
                    if (msg.Kind == RelayKind.Unknown)
                    {
                        continue; // 不是我们的报文（端口被人占了之类），忽略
                    }
                    Alive = true;
                    LastReplyMs = Environment.TickCount;
                    into.Add(msg);
                    n++;
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                ModEnv.LogOnce("中继控制包接收异常: " + ex.Message);
            }
            return n;
        }

        public void Close()
        {
            try { _sock?.Close(); } catch { }
            _sock = null;
            _server = null;
            Alive = false;
        }

        public void Dispose() => Close();
    }
}
