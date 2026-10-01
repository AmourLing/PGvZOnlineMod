using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Lidgren.Network;
using PGvZOnlineMod.Core;
using PGvZOnlineMod.Protocol;

namespace PGvZOnlineMod.Net
{
    public enum NetRole
    {
        None,
        Host,
        Client,
    }

    /// <summary>
    /// Lidgren 传输封装（多人版）。轮询式（Poll 在主线程泵里调），不注册回调线程。
    /// 可靠通道走 ReliableOrdered（事件），快照走 UnreliableSequenced（丢旧不追）。
    /// 主机侧维护 槽位→连接 映射（slot 0 = 主机自己，1..MaxClients = 客人）。
    /// </summary>
    public class NetMgr : IDisposable
    {
        public const int MaxClients = 3;

        private NetPeer _peer;                 // NetServer 或 NetClient
        private readonly NetConnection[] _clientConns = new NetConnection[MaxClients]; // 下标=槽位-1
        private readonly Dictionary<NetConnection, int> _connSlot = new();
        private readonly List<NetIncomingMessage> _drain = new();
        private readonly List<NetConnection> _sendList = new();

        public NetRole Role { get; private set; }
        public Action<NetConnection, string> OnConnected;        // 参数：连接（主机侧可取端点）
        public Action<NetConnection, string> OnPeerDisconnected; // 参数：连接、原因（Session 先取槽位，NetMgr 随后释放）
        public Action<NetIncomingMessage> OnData;

        /// <summary>Host 侧：收到局域网发现请求时调用；返回 null 表示不响应（如已在局内）。</summary>
        public Func<NetOutgoingMessage> DiscoveryResponder;

        /// <summary>Client 侧：收到房间广播（ip, 消息体）。</summary>
        public Action<string, NetIncomingMessage> OnRoomDiscovered;

        public string Error { get; private set; }

        /// <summary>主机实际绑定的端口（StartHost 可能自动 +1 回退，双开同一台机器时用）。</summary>
        public int BoundPort { get; private set; }

        /// <summary>累计收到的局域网发现请求数（主机侧判断"对方到底搜没搜我"）。</summary>
        public long DiscoveryRequestsReceived { get; private set; }

        /// <summary>
        /// 主机侧：Lidgren 真正认到的连接数（不经过槽位分配，直接看传输层）。
        /// 走中继时这是"主机能不能把三个客人当成三个人"的直接证据——
        /// 三个客人的包若被换成同一个源端点，这里只会是 1。
        /// </summary>
        public int ServerConnectionCount => (_peer as NetServer)?.ConnectionsCount ?? 0;

        public bool IsConnected
        {
            get
            {
                if (_peer == null)
                {
                    return false;
                }
                if (Role == NetRole.Host)
                {
                    foreach (var c in _clientConns)
                    {
                        if (c != null && c.Status == NetConnectionStatus.Connected)
                        {
                            return true;
                        }
                    }
                    return false;
                }
                var rc = RemoteConnection;
                return rc != null && rc.Status == NetConnectionStatus.Connected;
            }
        }

        /// <summary>2P 兼容：唯一远端连接（客户端=主机连接；主机=第一个客人）。</summary>
        public NetConnection RemoteConnection
        {
            get
            {
                if (_peer == null)
                {
                    return null;
                }
                if (Role == NetRole.Host)
                {
                    foreach (var c in _clientConns)
                    {
                        if (c != null)
                        {
                            return c;
                        }
                    }
                    return null;
                }
                return _peer.Connections.Count > 0 ? _peer.Connections[0] : null;
            }
        }

        /// <summary>当前往返延迟（毫秒，客户端到主机）；未连接返回 -1。</summary>
        public float RemoteRttMs
        {
            get
            {
                var c = Role == NetRole.Client ? RemoteConnection : null;
                return c != null ? c.AverageRoundtripTime * 1000f : -1f;
            }
        }

        public bool StartHost(int requestedPort)
        {
            Shutdown();
            // 同机双开时默认端口可能被另一个实例占用：自动向后找空位（最多 +9）
            for (int attempt = 0; attempt < 10; attempt++)
            {
                int port = requestedPort + attempt;
                try
                {
                    var cfg = new NetPeerConfiguration("PGvZOnline")
                    {
                        Port = port,
                        MaximumConnections = MaxClients,
                        // 掉线判定从默认 25s 缩到 10s（对方强杀进程时另一方更快收到断开）
                        ConnectionTimeout = 10f,
                    };
                    cfg.EnableMessageType(NetIncomingMessageType.DiscoveryRequest);
                    _peer = new NetServer(cfg);
                    _peer.Start();
                    Role = NetRole.Host;
                    ClearClients();
                    Error = null;
                    BoundPort = port;
                    ModEnv.Log("NetServer 已启动，端口 " + port
                        + (attempt > 0 ? "（默认端口被占用，自动回退 +" + attempt + "）" : ""));
                    return true;
                }
                catch (Exception ex)
                {
                    if (attempt < 9)
                    {
                        ModEnv.Log("端口 " + port + " 被占用，尝试下一个…");
                        continue;
                    }
                    Error = ex.Message;
                    ModEnv.Log("NetServer 启动失败: " + ex);
                    Shutdown();
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// 启动一个仅用于局域网搜索的客户端 peer（不连接任何人）。
        /// 已是客户端时直接复用（随后可对同一 peer 调 Connect 加入房间）。
        /// </summary>
        public bool StartDiscoveryPeer()
        {
            if (_peer != null && Role == NetRole.Client)
            {
                return true;
            }
            Shutdown();
            try
            {
                var cfg = new NetPeerConfiguration("PGvZOnline")
                {
                    ConnectionTimeout = 10f,
                };
                cfg.EnableMessageType(NetIncomingMessageType.DiscoveryResponse);
                _peer = new NetClient(cfg);
                _peer.Start();
                Role = NetRole.Client;
                Error = null;
                return true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                ModEnv.Log("搜索 peer 启动失败: " + ex);
                Shutdown();
                return false;
            }
        }

        public bool StartClient(string host, int port)
        {
            if (!StartDiscoveryPeer())
            {
                return false;
            }
            try
            {
                _peer.Connect(host, port);
                ModEnv.Log("NetClient 连接 " + host + ":" + port);
                return true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                ModEnv.Log("NetClient 连接失败: " + ex);
                return false;
            }
        }

        /// <summary>向局域网广播房间搜索请求（端口 = 主机的建房端口）。</summary>
        public void DiscoverRooms(int port)
        {
            _peer?.DiscoverLocalPeers(port);
        }

        /// <summary>向指定地址单播发现请求（扫段用：广播在热点/多网卡下不可靠）。</summary>
        public void SendDiscoveryTo(IPEndPoint ep)
        {
            if (_peer == null || ep == null)
            {
                return;
            }
            try
            {
                _peer.DiscoverKnownPeer(ep);
            }
            catch (Exception ex)
            {
                ModEnv.LogOnce("定向搜索失败（不再重复记）: " + ex.Message);
            }
        }

        /// <summary>主线程泵里调用：收包 + 状态变化分发。</summary>
        public void Poll()
        {
            if (_peer == null)
            {
                return;
            }
            _drain.Clear();
            var peer = _peer;
            while (peer.ReadMessages(_drain) > 0)
            {
                foreach (var im in _drain)
                {
                    if (_peer == null)
                    {
                        return; // 消息处理中触发了 Shutdown
                    }
                    HandleIncoming(im);
                }
                if (_peer == null)
                {
                    return;
                }
                peer.Recycle(_drain);
                _drain.Clear();
            }
        }

        private void HandleIncoming(NetIncomingMessage im)
        {
            switch (im.MessageType)
            {
                case NetIncomingMessageType.StatusChanged:
                {
                    var status = (NetConnectionStatus)im.ReadByte();
                    string reason = im.ReadString();
                    switch (status)
                    {
                        case NetConnectionStatus.Connected:
                            if (Role == NetRole.Host)
                            {
                                ModEnv.Log("玩家接入: " + im.SenderConnection.RemoteEndPoint);
                                if (SlotOf(im.SenderConnection) < 0)
                                {
                                    AssignSlot(im.SenderConnection); // 连接建立即预占槽位（幂等）
                                }
                            }
                            OnConnected?.Invoke(im.SenderConnection, reason);
                            break;
                        case NetConnectionStatus.Disconnected:
                            ModEnv.Log("连接断开: " + reason);
                            OnPeerDisconnected?.Invoke(im.SenderConnection, reason);
                            FreeSlot(im.SenderConnection);
                            break;
                    }
                    break;
                }

                case NetIncomingMessageType.Data:
                    OnData?.Invoke(im);
                    break;

                case NetIncomingMessageType.DiscoveryRequest:
                {
                    DiscoveryRequestsReceived++;
                    var resp = DiscoveryResponder?.Invoke();
                    if (resp != null)
                    {
                        _peer.SendDiscoveryResponse(resp, im.SenderEndPoint);
                    }
                    break;
                }

                case NetIncomingMessageType.DiscoveryResponse:
                    OnRoomDiscovered?.Invoke(im.SenderEndPoint?.Address?.ToString() ?? "", im);
                    break;

                case NetIncomingMessageType.DebugMessage:
                case NetIncomingMessageType.VerboseDebugMessage:
                case NetIncomingMessageType.WarningMessage:
                    ModEnv.Log("Lidgren: " + im.ReadString());
                    break;

                case NetIncomingMessageType.ErrorMessage:
                    ModEnv.Log("Lidgren 错误: " + im.ReadString());
                    break;

                default:
                    break; // 其他类型忽略
            }
        }

        public NetOutgoingMessage CreateMessage()
        {
            return _peer?.CreateMessage();
        }

        // ------------------------------------------------------------ 槽位管理（Host 侧）

        /// <summary>分配最低空闲客人槽位（1..MaxClients）；已有槽位则原样返回（幂等）。</summary>
        public int AssignSlot(NetConnection c)
        {
            if (c != null && _connSlot.TryGetValue(c, out int existing))
            {
                return existing;
            }
            for (int s = 1; s <= MaxClients; s++)
            {
                if (_clientConns[s - 1] == null)
                {
                    _clientConns[s - 1] = c;
                    _connSlot[c] = s;
                    return s;
                }
            }
            return -1;
        }

        public void FreeSlot(NetConnection c)
        {
            if (c != null && _connSlot.Remove(c, out int s))
            {
                _clientConns[s - 1] = null;
            }
        }

        /// <summary>连接 → 槽位（未登记返回 -1）。</summary>
        public int SlotOf(NetConnection c)
        {
            return c != null && _connSlot.TryGetValue(c, out int s) ? s : -1;
        }

        /// <summary>槽位 → 连接（Host 侧；未占用返回 null）。</summary>
        public NetConnection ConnectionOfSlot(int slot)
        {
            return slot >= 1 && slot <= MaxClients ? _clientConns[slot - 1] : null;
        }

        /// <summary>定向可靠发送给某个客人（踢人等定向消息）。</summary>
        public void SendReliableTo(NetOutgoingMessage msg, NetConnection c)
        {
            if (msg == null || c == null || _peer == null)
            {
                return;
            }
            _peer.SendMessage(msg, c, NetDeliveryMethod.ReliableOrdered);
        }

        /// <summary>主机主动断开某个客人的连接（踢人用）。</summary>
        public void DisconnectClient(NetConnection c, string reason)
        {
            c?.Disconnect(reason);
        }

        private void ClearClients()
        {
            for (int i = 0; i < _clientConns.Length; i++)
            {
                _clientConns[i] = null;
            }
            _connSlot.Clear();
        }

        // ------------------------------------------------------------ 发送

        /// <summary>向所有客人广播（可排除某个连接）；主机不给自己发。</summary>
        public void SendReliableToClients(NetOutgoingMessage msg, NetConnection except = null)
        {
            SendToClients(msg, NetDeliveryMethod.ReliableOrdered, 0, except);
        }

        public void SendUnreliableToClients(NetOutgoingMessage msg, NetConnection except = null)
        {
            SendToClients(msg, NetDeliveryMethod.UnreliableSequenced, 1, except);
        }

        private void SendToClients(NetOutgoingMessage msg, NetDeliveryMethod method, int channel, NetConnection except)
        {
            if (msg == null || _peer == null || Role != NetRole.Host)
            {
                return;
            }
            _sendList.Clear();
            foreach (var c in _clientConns)
            {
                if (c != null && c.Status == NetConnectionStatus.Connected && c != except)
                {
                    _sendList.Add(c);
                }
            }
            if (_sendList.Count == 0)
            {
                return;
            }
            _peer.SendMessage(msg, _sendList, method, channel);
        }

        /// <summary>客户端 → 主机（可靠）。</summary>
        public void SendReliableToHost(NetOutgoingMessage msg)
        {
            var c = RemoteConnection;
            if (msg == null || c == null)
            {
                return;
            }
            _peer.SendMessage(msg, c, NetDeliveryMethod.ReliableOrdered);
        }

        /// <summary>客户端 → 主机（不可靠，光标等高频）。</summary>
        public void SendUnreliableToHost(NetOutgoingMessage msg)
        {
            var c = RemoteConnection;
            if (msg == null || c == null)
            {
                return;
            }
            _peer.SendMessage(msg, c, NetDeliveryMethod.UnreliableSequenced, 1);
        }

        public void Shutdown()
        {
            if (_peer != null)
            {
                try
                {
                    // Lidgren gen3 的 Shutdown 与在途可靠包 ACK 存在竞态（DestoreMessage NRE），
                    // 先留一小拍让 ACK 落地再停；断线是低频事件，主线程阻塞可接受
                    Thread.Sleep(150);
                    _peer.Shutdown("mod shutdown");
                }
                catch
                {
                }
                _peer = null;
            }
            ClearClients();
            Role = NetRole.None;
        }

        public void Dispose()
        {
            Shutdown();
        }
    }
}
