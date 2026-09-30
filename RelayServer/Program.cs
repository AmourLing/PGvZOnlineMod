using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PGvZOnlineMod.Protocol;

namespace PGvZRelay
{
    /// <summary>
    /// 跨互联网中继：只搬 UDP 数据报，不解析模组的游戏内协议，所以模组的 30 种消息与握手校验一行都不用改。
    ///
    /// 一个房间占两个端口：
    /// · guestPort —— 所有客人拨这一个口。客人靠"源端点不同"区分，按到达顺序占 0..2 号槽位。
    /// · hostPort  —— 主机侧三条本地隧道的出入口；隧道用 "PGVZREG 房间码 槽位号" 登记自己负责哪一槽，
    ///                并靠这条报文当 NAT 保活。
    ///
    /// 为什么要三条隧道：主机那侧跑的是 Lidgren 服务器，它按"对端端点"认连接。三个客人的包若
    /// 都从同一个源端点进来，主机只会认成一个。隧道把每个客人换成各自不同的本地源端口交给游戏进程，
    /// 主机才能正常区分 1P/2P/3P/4P。回包一律从客人当初拨的 guestPort 发出去，
    /// 客人的 Lidgren 客户端才不会觉得对端变了。
    ///
    /// 控制协议的定义不在这里——它链入模组的 src/Protocol/RelayProtocol.cs，两端共用一份，避免各写一套写串。
    /// </summary>
    internal static class Program
    {
        internal const int MaxSlots = 3;          // 1 主机 + 3 客人，与模组 Session.MaxPlayers 一致
        internal const int MaxPacket = 2048;      // Lidgren 单包不会到这个量级，超了按滥用丢掉

        private static int _controlPort = RelayProtocol.DefaultControlPort;
        private static int _basePort = 27200;
        private static int _maxRooms = 15;
        private static int _idleSeconds = 60;

        private static readonly ConcurrentDictionary<string, Room> Rooms = new();
        private static readonly ConcurrentBag<int> FreeIndex = new();
        private static readonly object Gate = new();
        private static int _rng = (Environment.TickCount ^ DateTime.Now.Second) & 0x7FFFFFFF;

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            for (int i = 0; i + 1 < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--control": _controlPort = Parse(args[i + 1], _controlPort); i++; break;
                    case "--base": _basePort = Parse(args[i + 1], _basePort); i++; break;
                    case "--rooms": _maxRooms = Parse(args[i + 1], _maxRooms); i++; break;
                    case "--idle": _idleSeconds = Parse(args[i + 1], _idleSeconds); i++; break;
                }
            }
            for (int k = 0; k < _maxRooms; k++)
            {
                FreeIndex.Add(k);
            }

            // 控制端口在主线程先绑：占着端口的一旦是别的实例，这里当场退出并说清原因，
            // 不能让后台线程抛未处理异常把进程静默干掉（那会让调用方误以为连上了刚起的服务）
            UdpClient control;
            try
            {
                control = new UdpClient(_controlPort);
            }
            catch (Exception ex)
            {
                Log($"控制端口 {_controlPort} 绑定失败（端口已被占用？）: {ex.Message}");
                return 1;
            }

            Log($"中继启动 控制={_controlPort} 端口段={_basePort}~{_basePort + _maxRooms * 2 - 1} " +
                $"容量={_maxRooms}房 空闲回收={_idleSeconds}s");

            new Thread(() => ControlLoop(control)) { IsBackground = true, Name = "control" }.Start();
            new Thread(ReapLoop) { IsBackground = true, Name = "reap" }.Start();

            var quit = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit.Set(); };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => quit.Set();
            quit.Wait();
            control.Close();
            Log("退出");
            return 0;
        }

        private static int Parse(string s, int fallback)
            => int.TryParse(s, out int v) && v > 0 ? v : fallback;

        // ------------------------------------------------------------ 控制通道

        private static void ControlLoop(UdpClient c)
        {
            IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                try
                {
                    byte[] raw = c.Receive(ref from);
                    if (raw == null || raw.Length == 0 || raw.Length > 256)
                    {
                        continue;
                    }
                    var msg = RelayProtocol.Parse(Encoding.UTF8.GetString(raw));
                    string reply = Handle(msg, from);
                    if (reply != null)
                    {
                        byte[] outBytes = Encoding.UTF8.GetBytes(reply);
                        c.Send(outBytes, outBytes.Length, from);
                    }
                }
                catch (Exception ex)
                {
                    Log("控制通道异常: " + ex.Message);
                }
            }
        }

        private static string Handle(RelayMessage msg, IPEndPoint from)
        {
            switch (msg.Kind)
            {
                case RelayKind.Host:
                {
                    var room = CreateRoom(msg.Name, msg.Password);
                    if (room == null)
                    {
                        Log("容量已满，拒绝建房");
                        return RelayProtocol.BuildError("full");
                    }
                    Log($"建房 {room.Code} 主机={room.RoomName} guest={room.GuestPort} " +
                        $"host={room.HostPort}（当前 {Rooms.Count} 房）");
                    return RelayProtocol.BuildRoom(room.Code, room.GuestPort, room.HostPort);
                }
                case RelayKind.Seen:
                {
                    // 主机保活 + 上报房间状态；房间不存在就让它自然超时，不额外应答
                    if (Rooms.TryGetValue(msg.Code, out var room))
                    {
                        room.ReportSeen(msg.LevelIndex, msg.Players, msg.MaxPlayers);
                    }
                    return null;
                }
                case RelayKind.Join:
                {
                    if (!Rooms.TryGetValue(msg.Code, out var room))
                    {
                        return RelayProtocol.BuildError("noready");
                    }
                    if (room.Locked && room.Password != RelayProtocol.Clean(msg.Password))
                    {
                        Log($"{room.Code} 密码不对，拒绝 {from.Address}:{from.Port}");
                        return RelayProtocol.BuildError("passwd");
                    }
                    room.Touch();
                    return RelayProtocol.BuildOk(room.GuestPort);
                }
                case RelayKind.List:
                    return RelayProtocol.BuildRooms(Snapshot());
                case RelayKind.Drop:
                {
                    if (Rooms.TryGetValue(msg.Code, out var room))
                    {
                        CloseRoom(room, "主机主动关闭");
                    }
                    return "OK";
                }
                case RelayKind.Ping:
                    return RelayProtocol.BuildPong();
                default:
                    return null;
            }
        }

        private static List<RelayRoomInfo> Snapshot()
        {
            var list = new List<RelayRoomInfo>();
            foreach (var room in Rooms.Values)
            {
                list.Add(room.Info());
            }
            return list;
        }

        private static Room CreateRoom(string hostName, string password)
        {
            lock (Gate)
            {
                if (!FreeIndex.TryTake(out int k))
                {
                    return null;
                }
                string code;
                do
                {
                    _rng = (_rng * 1103515245 + 12345) & 0x7FFFFFFF;
                    code = (_rng % 1000000).ToString("D6");
                }
                while (Rooms.ContainsKey(code));

                var room = new Room(code, _basePort + k * 2 + 1, _basePort + k * 2, k,
                    string.IsNullOrEmpty(hostName) ? "host" : hostName, password);
                if (!room.Open())
                {
                    FreeIndex.Add(k);
                    return null;
                }
                Rooms[code] = room;
                return room;
            }
        }

        private static void CloseRoom(Room room, string why)
        {
            lock (Gate)
            {
                if (!Rooms.TryRemove(room.Code, out _))
                {
                    return;
                }
                FreeIndex.Add(room.Index);
            }
            room.Close();
            Log($"回收 {room.Code}（{why}）");
        }

        private static void ReapLoop()
        {
            while (true)
            {
                Thread.Sleep(5000);
                foreach (var room in Rooms.Values)
                {
                    if (room.IdleSeconds > _idleSeconds)
                    {
                        CloseRoom(room, $"空闲 {room.IdleSeconds}s");
                    }
                }
            }
        }

        internal static void Log(string message)
            => Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
    }

    /// <summary>一个房间：两个端口 + 三个槽位（槽位 = 一个客人 ↔ 主机侧一条隧道）。</summary>
    internal sealed class Room
    {
        public string Code { get; }
        public int GuestPort { get; }
        public int HostPort { get; }
        public int Index { get; }
        public string RoomName { get; }
        public string Password { get; }
        public bool Locked => Password.Length > 0;
        public volatile bool Alive = true;

        private readonly UdpClient _hostSock = new();
        private readonly UdpClient _guestSock = new();
        private readonly Slot[] _slots = new Slot[Program.MaxSlots];
        private readonly object _sync = new();
        private DateTime _last = DateTime.Now;
        private int _levelIndex = -1;
        private int _players = 1;
        private int _maxPlayers = Program.MaxSlots + 1;

        public Room(string code, int guestPort, int hostPort, int index, string roomName, string password)
        {
            Code = code;
            GuestPort = guestPort;
            HostPort = hostPort;
            Index = index;
            RoomName = roomName;
            Password = RelayProtocol.Clean(password);
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i] = new Slot();
            }
        }

        public bool Open()
        {
            try
            {
                _hostSock.Client.Bind(new IPEndPoint(IPAddress.Any, HostPort));
                _guestSock.Client.Bind(new IPEndPoint(IPAddress.Any, GuestPort));
            }
            catch (Exception ex)
            {
                Program.Log($"开端口失败 {Code}: {ex.Message}");
                Close();
                return false;
            }
            new Thread(HostLoop) { IsBackground = true, Name = "room-" + Code + "-h" }.Start();
            new Thread(GuestLoop) { IsBackground = true, Name = "room-" + Code + "-g" }.Start();
            return true;
        }

        public void Close()
        {
            Alive = false;
            try { _hostSock.Close(); } catch { }
            try { _guestSock.Close(); } catch { }
        }

        public void Touch()
        {
            lock (_sync)
            {
                _last = DateTime.Now;
            }
        }

        public void ReportSeen(int levelIndex, int players, int maxPlayers)
        {
            lock (_sync)
            {
                _levelIndex = levelIndex;
                _players = players;
                _maxPlayers = maxPlayers;
                _last = DateTime.Now;
            }
        }

        public RelayRoomInfo Info()
        {
            lock (_sync)
            {
                return new RelayRoomInfo
                {
                    Code = Code,
                    RoomName = RoomName,
                    LevelIndex = _levelIndex,
                    Players = _players,
                    MaxPlayers = _maxPlayers,
                    Locked = Locked,
                };
            }
        }

        public int IdleSeconds
        {
            get
            {
                lock (_sync)
                {
                    return (int)(DateTime.Now - _last).TotalSeconds;
                }
            }
        }

        // 主机侧：隧道登记报文（兼 NAT 保活），或隧道转过来的"发给客人"的数据
        private void HostLoop()
        {
            IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (Alive)
            {
                byte[] data;
                IPEndPoint src;
                try
                {
                    data = _hostSock.Receive(ref from);
                    src = new IPEndPoint(from.Address, from.Port);
                }
                catch (Exception) when (!Alive)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Program.Log($"[{Code}] 主机侧收包异常: {ex.Message}");
                    continue;
                }
                if (data == null || data.Length == 0 || data.Length > Program.MaxPacket)
                {
                    continue;
                }
                Touch();

                if (data.Length < 64 && StartsWithReg(data))
                {
                    RegisterTunnel(Encoding.UTF8.GetString(data, 0, data.Length), src);
                    continue;
                }
                var slot = SlotByTunnel(src);
                if (slot != null)
                {
                    Send(_guestSock, data, slot.Guest);   // 回客人：从客人拨过的那个口发出去
                }
            }
        }

        // 客人侧：认出是哪个槽位，转给主机对应的那条隧道
        private void GuestLoop()
        {
            IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (Alive)
            {
                byte[] data;
                IPEndPoint src;
                try
                {
                    data = _guestSock.Receive(ref from);
                    src = new IPEndPoint(from.Address, from.Port);
                }
                catch (Exception) when (!Alive)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Program.Log($"[{Code}] 客人侧收包异常: {ex.Message}");
                    continue;
                }
                if (data == null || data.Length == 0 || data.Length > Program.MaxPacket)
                {
                    continue;
                }
                Touch();

                var slot = SlotByGuest(src);
                if (slot == null)
                {
                    continue; // 房间满：客人自己会收到超时，主机侧也会显示掉线
                }
                if (!slot.TunnelReady)
                {
                    slot.Pending = data;  // 隧道还没登记：攒最新一个包，登记时补发
                    continue;
                }
                Send(_hostSock, data, slot.Tunnel);
            }
        }

        private static bool StartsWithReg(byte[] data)
        {
            var prefix = Encoding.UTF8.GetBytes(RelayProtocol.TunnelPrefix);
            if (data.Length < prefix.Length)
            {
                return false;
            }
            for (int i = 0; i < prefix.Length; i++)
            {
                if (data[i] != prefix[i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>"PGVZREG 房间码 槽位号"：登记这条隧道负责哪个槽位，并补发登记前攒下的包。</summary>
        private void RegisterTunnel(string line, IPEndPoint src)
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3 || p[1] != Code || !int.TryParse(p[2], out int i)
                || i < 0 || i >= Program.MaxSlots)
            {
                return;
            }
            byte[] pending;
            bool first;
            lock (_sync)
            {
                var slot = _slots[i];
                slot.Tunnel = src;
                slot.TunnelReady = true;
                pending = slot.Pending;
                slot.Pending = null;
                first = !slot.Announced;
                slot.Announced = true;
            }
            if (first)
            {
                Program.Log($"[{Code}] 槽位{i} 隧道登记 {src.Address}:{src.Port}");
            }
            if (pending != null)
            {
                Send(_hostSock, pending, src);
            }
        }

        private Slot SlotByTunnel(IPEndPoint ep)
        {
            lock (_sync)
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (Same(_slots[i].Tunnel, ep))
                    {
                        return _slots[i];
                    }
                }
            }
            return null;
        }

        private Slot SlotByGuest(IPEndPoint ep)
        {
            lock (_sync)
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (Same(_slots[i].Guest, ep))
                    {
                        return _slots[i];
                    }
                }
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].Guest == null)
                    {
                        _slots[i].Guest = ep;
                        Program.Log($"[{Code}] 槽位{i} 客人接入 {ep.Address}:{ep.Port}");
                        return _slots[i];
                    }
                }
            }
            return null;
        }

        private static bool Same(IPEndPoint a, IPEndPoint b)
            => a != null && b != null && a.Address.Equals(b.Address) && a.Port == b.Port;

        private static void Send(UdpClient sock, byte[] data, IPEndPoint to)
        {
            if (to == null)
            {
                return;
            }
            try
            {
                sock.Send(data, data.Length, to);
            }
            catch (Exception ex)
            {
                Program.Log("转发失败: " + ex.Message);
            }
        }

        private sealed class Slot
        {
            public IPEndPoint Guest;
            public IPEndPoint Tunnel;
            public bool TunnelReady;
            public bool Announced;
            public byte[] Pending;
        }
    }
}
