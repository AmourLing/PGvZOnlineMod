using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    ///
    /// 为什么只有 LIST 要令牌：满房时一次 LIST 是 4 字节请求换 336 字节应答（约 84 倍），
    /// 这是这台机器唯一能被拿伪造源地址当反射放大器的地方。令牌只在 PONG 里发给真正的来源，
    /// 伪造者收不到应答就拿不到令牌，LIST 只会回 9 字节的 ERR|token。HOST/JOIN 的应答本来就比
    /// 请求大不到一倍，再卡一道只会给建房与加入这两条要命的路径多塞一次失败面，所以不卡。
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

        /// <summary>按来源端点发的一次性令牌（LIST 的入场券，兼作反射放大的闸门）。</summary>
        private static readonly ConcurrentDictionary<string, Grant> Grants = new();

        /// <summary>按来源地址记的房间密码错误次数（换源端口不该把计数清零）。</summary>
        private static readonly ConcurrentDictionary<string, Fails> PwFails = new();

        private sealed class Grant
        {
            public string Token = "";
            public DateTime Until;
        }

        private sealed class Fails
        {
            public int Count;
            public DateTime Until;
        }

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
                $"容量={_maxRooms}房 空闲回收={_idleSeconds}s 控制协议=v{RelayProtocol.Version} " +
                $"令牌={RelayProtocol.TokenTtlSeconds}s 密码限速={RelayProtocol.MaxPasswdFails}次/{RelayProtocol.PasswdWindowSeconds}s");

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
                    // 字段布局对不上就别往下猜：旧端不带版本号，按 v2 的含义去解它的报文只会静默错乱
                    if (msg.Ctl < RelayProtocol.Version)
                    {
                        Log($"来自 {from.Address}:{from.Port} 的 HOST 不带控制协议版本，拒绝");
                        return RelayProtocol.BuildError(RelayError.OldCtl);
                    }
                    // 同一来源 + 同样的房名与密码才复用：应答丢包时主机必然原样重试，
                    // 每次重试都新建的话几个来回就把整台服务器的房间位占满，
                    // 后来的人只会收到 ERR|full（端到端用例踩到的就是这个）。
                    // 但改了房名或密码是"我要换一个房"，不能拿旧的糊过去——
                    // 只按来源复用会让"给房间加密码"静默失效（本地自测就是这么抓到的）。
                    string wantName = msg.Name.Length > 0 ? msg.Name : "host";
                    string wantPwd = RelayProtocol.Clean(msg.Password);
                    foreach (var exist in Rooms.Values)
                    {
                        if (Same(exist.Owner, from) && exist.RoomName == wantName && exist.Password == wantPwd)
                        {
                            exist.Touch();
                            Log($"复用 {exist.Code} 主机={exist.RoomName} guest={exist.GuestPort} host={exist.HostPort}");
                            return RelayProtocol.BuildRoom(exist.Code, exist.GuestPort, exist.HostPort);
                        }
                    }
                    var room = CreateRoom(msg.Name, msg.Password, from, msg.GameVersion);
                    if (room == null)
                    {
                        Log("容量已满，拒绝建房");
                        return RelayProtocol.BuildError(RelayError.Full);
                    }
                    Log($"建房 {room.Code} 主机={room.RoomName} 协议=v{room.GameVersion} guest={room.GuestPort} " +
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
                    if (msg.Ctl < RelayProtocol.Version)
                    {
                        Log($"来自 {from.Address}:{from.Port} 的 JOIN 不带控制协议版本，拒绝");
                        return RelayProtocol.BuildError(RelayError.OldCtl);
                    }
                    if (!Rooms.TryGetValue(msg.Code, out var room))
                    {
                        return RelayProtocol.BuildError(RelayError.NoReady);
                    }
                    if (room.Locked && room.Password != RelayProtocol.Clean(msg.Password))
                    {
                        if (NotePwFail(from))
                        {
                            // 房间码是 6 位数字、一次请求就能试一个，不限速的话"要密码"只是装饰
                            Log($"{room.Code} 密码错误超过 {RelayProtocol.MaxPasswdFails} 次，暂时不应答 {from.Address}");
                            return RelayProtocol.BuildError(RelayError.Slow);
                        }
                        Log($"{room.Code} 密码不对，拒绝 {from.Address}:{from.Port}");
                        return RelayProtocol.BuildError(RelayError.Passwd);
                    }
                    // 版本比对放在密码之后：先确认来路，再把房主的版本号透出去。
                    // 这一步拦在 Lidgren 握手之前，双方看到的会是"版本差多少"而不是干等超时。
                    if (room.GameVersion != msg.GameVersion)
                    {
                        string reason = msg.GameVersion < room.GameVersion ? RelayError.Old : RelayError.New;
                        Log($"{room.Code} 版本不匹配：客人=v{msg.GameVersion} 房主=v{room.GameVersion}");
                        return RelayProtocol.BuildError(reason, room.GameVersion.ToString());
                    }
                    ClearPwFail(from);
                    room.Touch();
                    return RelayProtocol.BuildOk(room.GuestPort);
                }
                case RelayKind.List:
                {
                    // 令牌对不上就只回这一句：应答比请求还小，伪造源地址从这里得不到放大
                    if (!SpendToken(msg.Token, from))
                    {
                        return RelayProtocol.BuildError(RelayError.Token);
                    }
                    return RelayProtocol.BuildRooms(Snapshot());
                }
                case RelayKind.Drop:
                {
                    if (Rooms.TryGetValue(msg.Code, out var room))
                    {
                        // 只认建房者：房间码是 6 位数字，任何人都能猜到并据此把别人的房关掉
                        if (!Same(room.Owner, from))
                        {
                            Log($"{room.Code} 的 DROP 来自非房主 {from.Address}:{from.Port}，拒绝");
                            return RelayProtocol.BuildError(RelayError.NoPerm);
                        }
                        CloseRoom(room, "主机主动关闭");
                    }
                    return "OK";
                }
                case RelayKind.Ping:
                    return RelayProtocol.BuildPong(IssueToken(from), Rooms.Count, _maxRooms);
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

        private static Room CreateRoom(string hostName, string password, IPEndPoint owner, int gameVersion)
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
                    string.IsNullOrEmpty(hostName) ? "host" : hostName, password, gameVersion)
                {
                    Owner = owner,
                };
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

        internal static bool Same(IPEndPoint a, IPEndPoint b)
            => a != null && b != null && a.Address.Equals(b.Address) && a.Port == b.Port;

        // ------------------------------------------------------------ 令牌与限速

        /// <summary>发一张新令牌并续期。客户端每 5 秒 PING 一次，令牌就是在这条路上一直续着的。</summary>
        private static string IssueToken(IPEndPoint from)
        {
            var g = Grants.GetOrAdd(from.Address.ToString() + ":" + from.Port, _ => new Grant());
            // 用加密随机数而不是房间里那台 LCG：令牌的全部价值就在于伪造源地址的人算不出它
            g.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            g.Until = DateTime.Now.AddSeconds(RelayProtocol.TokenTtlSeconds);
            return g.Token;
        }

        /// <summary>
        /// 验令牌，对上了顺手续期。按完整端点认人：NAT 重新绑定会换源端口，
        /// 那时旧令牌作废——客户端收到 ERR|token 会立刻重发 PING 拿新的，不会卡成红字。
        /// </summary>
        private static bool SpendToken(string token, IPEndPoint from)
        {
            if (token.Length == 0
                || !Grants.TryGetValue(from.Address.ToString() + ":" + from.Port, out var g)
                || g.Token != token || DateTime.Now > g.Until)
            {
                return false;
            }
            g.Until = DateTime.Now.AddSeconds(RelayProtocol.TokenTtlSeconds);
            return true;
        }

        /// <summary>记一次密码错误；返回 true 表示这一下已经越过限速线，不该再应答了。</summary>
        private static bool NotePwFail(IPEndPoint from)
        {
            string key = from.Address.ToString();
            var f = PwFails.GetOrAdd(key, _ => new Fails
            {
                Until = DateTime.Now.AddSeconds(RelayProtocol.PasswdWindowSeconds),
            });
            if (DateTime.Now > f.Until)
            {
                f.Count = 0;
                f.Until = DateTime.Now.AddSeconds(RelayProtocol.PasswdWindowSeconds);
            }
            f.Count++;
            return f.Count > RelayProtocol.MaxPasswdFails;
        }

        private static void ClearPwFail(IPEndPoint from)
            => PwFails.TryRemove(from.Address.ToString(), out _);

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
                // 令牌与限速的表只靠访问时判断过期会一直涨（公网机器上扫描过的地址是无限的）
                foreach (var kv in Grants)
                {
                    if (DateTime.Now > kv.Value.Until)
                    {
                        Grant dropped;
                        Grants.TryRemove(kv.Key, out dropped);
                    }
                }
                foreach (var kv in PwFails)
                {
                    if (DateTime.Now > kv.Value.Until)
                    {
                        Fails dropped;
                        PwFails.TryRemove(kv.Key, out dropped);
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
        /// <summary>房主上报的游戏协议版本：客人 JOIN 时与它比对，不一致就在握手之前拒掉。</summary>
        public readonly int GameVersion;
        /// <summary>建房者的端点：重复 HOST 复用它、DROP 也只认它。</summary>
        public IPEndPoint Owner;
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

        public Room(string code, int guestPort, int hostPort, int index, string roomName, string password,
            int gameVersion)
        {
            Code = code;
            GuestPort = guestPort;
            HostPort = hostPort;
            Index = index;
            RoomName = roomName;
            Password = RelayProtocol.Clean(password);
            GameVersion = gameVersion;
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
                    if (Program.Same(_slots[i].Tunnel, ep))
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
                    if (Program.Same(_slots[i].Guest, ep))
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
