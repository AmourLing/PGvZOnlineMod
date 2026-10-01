using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PGvZOnlineMod.Protocol
{
    /// <summary>中继回包里描述的一个房间。</summary>
    public struct RelayRoomInfo
    {
        public string Code;
        public string RoomName;
        public int LevelIndex;
        public int Players;
        public int MaxPlayers;
        public bool Locked;
    }

    /// <summary>ERR 的原因码；模组端按这个翻成中文，不要再猜。</summary>
    public static class RelayError
    {
        public const string Passwd = "passwd";
        public const string NoReady = "noready";
        public const string Full = "full";
        public const string NoPerm = "noperm";
        /// <summary>控制协议过旧：这台中继已按新字段布局升级，旧模组连不上。</summary>
        public const string OldCtl = "oldctl";
        /// <summary>房主的游戏协议比本端新（Detail 带房主的版本号）。</summary>
        public const string Old = "old";
        /// <summary>房主的游戏协议比本端旧（Detail 带房主的版本号）。</summary>
        public const string New = "new";
        /// <summary>没带有效令牌：LIST 的应答比请求大几十倍，不发令牌就不给列表。</summary>
        public const string Token = "token";
        /// <summary>密码错得太频繁，暂时不再应答。</summary>
        public const string Slow = "slow";
    }

    public enum RelayKind
    {
        Unknown = 0,
        // 客户端 → 服务端
        Host,
        Seen,
        Join,
        List,
        Drop,
        Ping,
        // 服务端 → 客户端
        Room,
        Ok,
        Rooms,
        Pong,
        Err,
    }

    /// <summary>一条中继控制报文解析后的结果；不适用的字段保持默认值。</summary>
    public struct RelayMessage
    {
        public RelayKind Kind;
        public string Code;
        public string Name;
        public string Password;
        public string Error;
        /// <summary>ERR 附带的信息（目前只有 old/new 带的那一对游戏协议版本号）。</summary>
        public string Detail;
        public int GuestPort;
        public int HostPort;
        public int LevelIndex;
        public int Players;
        public int MaxPlayers;
        /// <summary>报文尾部声明的控制协议版本；旧端不带，解出来是 0。</summary>
        public int Ctl;
        /// <summary>HOST/JOIN 声明的游戏协议版本（0 = 没声明）。</summary>
        public int GameVersion;
        /// <summary>LIST 带上来、PONG 发下去的那个一次性令牌。</summary>
        public string Token;
        /// <summary>ROOMS 应答里声明的房间数 / PONG 里声明的当前房间数。</summary>
        public int Count;
        /// <summary>PONG 里声明的房间容量。</summary>
        public int Capacity;
        public List<RelayRoomInfo> Rooms;
    }

    /// <summary>
    /// 模组与跨互联网中继之间的控制协议（与游戏数据通道分开，走一条独立的 UDP 文本口）。
    ///
    /// 用 `|` 分字段、`,` 分房间记录，两端一律经 Clean() 过滤，所以名字里不会出现分隔符。
    /// 文本协议是刻意选的：出问题时用 nc/socat 手敲一行就能验，不必为了调试再写一个客户端。
    /// 这份定义由模组与 RelayServer 共用（服务端直接链入本文件），保证两端不会各写一套而写串。
    ///
    /// v2 加了尾部字段：控制协议版本、游戏协议版本、LIST 用的一次性令牌。
    /// **新字段一律追加在尾部**——旧服务端解析 HOST 取的是第 1、2 字段，版本号放前面的话
    /// 它会把版本数字当成房名，静默建出一个名叫 "2" 的房间，比直接拒绝坏得多。
    /// </summary>
    public static class RelayProtocol
    {
        public const int DefaultControlPort = 27270;
        public const char Sep = '|';
        public const char RecSep = ',';

        /// <summary>
        /// 控制协议的字段布局版本。服务端见到不带它的 HOST/LIST 一律拒绝，
        /// 因为字段含义不同的两端互相猜是没结果的。
        /// </summary>
        public const int Version = 2;

        /// <summary>LIST 令牌的有效期（秒）：客户端每 5 秒 PING 一次续期，2 秒 LIST 一次用掉。</summary>
        public const int TokenTtlSeconds = 90;

        /// <summary>同一来源允许连续输错房间密码的次数，超过就暂时不再应答。</summary>
        public const int MaxPasswdFails = 5;

        /// <summary>密码错误的计数窗口（秒）。</summary>
        public const int PasswdWindowSeconds = 30;

        /// <summary>主机侧隧道的登记/保活报文前缀（空格分隔，不带自由文本，所以不走 Sep）。</summary>
        public const string TunnelPrefix = "PGVZREG ";

        /// <summary>去掉分隔符、控制字符与空白——它们会破坏按分隔符切行的解析。</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (ch == Sep || ch == RecSep || ch == ' ' || char.IsControl(ch))
                {
                    continue;
                }
                sb.Append(ch);
                if (sb.Length >= 24)
                {
                    break;
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------ 组包（请求）

        /// <summary>
        /// 建房。尾部的 gameVersion 是**本端的游戏协议版本**（调用方从 ProtocolVersion.Current 传进来）：
        /// 中继自己不写死这个数，所以游戏协议升到 v19 时只重发模组，服务端不用动。
        /// </summary>
        public static string BuildHost(string name, string password, int gameVersion)
            => "HOST" + Sep + Clean(name) + Sep + Clean(password) + Sep + N(Version) + Sep + N(gameVersion);

        /// <summary>主机保活并上报房间当前状态（关卡、人数）。中继靠它判断房间还活着。</summary>
        public static string BuildSeen(string code, int levelIndex, int players, int maxPlayers, string password)
            => "SEEN" + Sep + Clean(code) + Sep + levelIndex.ToString(CultureInfo.InvariantCulture)
               + Sep + N(players) + Sep + N(maxPlayers)
               + Sep + Clean(password);

        /// <summary>加入。gameVersion 与 HOST 同一个来源，服务端拿它跟房主上报的那一份比对。</summary>
        public static string BuildJoin(string code, string password, int gameVersion)
            => "JOIN" + Sep + Clean(code) + Sep + Clean(password) + Sep + N(Version) + Sep + N(gameVersion);

        /// <summary>要房间列表。token 来自上一次 PONG；不带或过期都会被回 ERR|token。</summary>
        public static string BuildList(string token)
            => "LIST" + Sep + (token ?? "");

        public static string BuildDrop(string code) => "DROP" + Sep + Clean(code);

        public static string BuildPing() => "PING" + Sep + N(Version);

        /// <summary>主机侧隧道的登记报文（同时充当 NAT 保活）。</summary>
        public static string BuildTunnelRegister(string code, int slot)
            => TunnelPrefix + Clean(code) + " " + slot.ToString(CultureInfo.InvariantCulture);

        // ------------------------------------------------------------ 组包（应答）

        public static string BuildRoom(string code, int guestPort, int hostPort)
            => "ROOM" + Sep + Clean(code) + Sep + N(guestPort) + Sep + N(hostPort);

        public static string BuildOk(int guestPort) => "OK" + Sep + N(guestPort);

        /// <summary>
        /// PING 的应答：顺带发令牌，并把"这台机现在几个房、能装几个"透给客户端
        /// ——左列那行 "32ms · 3/15 房" 就是这一句的账。
        /// </summary>
        public static string BuildPong(string token, int rooms, int capacity)
            => "PONG" + Sep + N(Version) + Sep + (token ?? "") + Sep + N(rooms) + Sep + N(capacity);

        public static string BuildError(string reason, string detail = "")
            => detail.Length > 0
                ? "ERR" + Sep + Clean(reason) + Sep + Clean(detail)
                : "ERR" + Sep + Clean(reason);

        public static string BuildRooms(IList<RelayRoomInfo> rooms)
        {
            var sb = new StringBuilder();
            sb.Append("ROOMS").Append(Sep).Append(N(rooms?.Count ?? 0));
            if (rooms != null)
            {
                foreach (var r in rooms)
                {
                    sb.Append(Sep).Append(Clean(r.Code)).Append(RecSep)
                      .Append(Clean(r.RoomName)).Append(RecSep)
                      .Append(N(r.LevelIndex)).Append(RecSep)
                      .Append(N(r.Players)).Append(RecSep)
                      .Append(N(r.MaxPlayers)).Append(RecSep)
                      .Append(r.Locked ? 1 : 0);
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------ 解析

        private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);

        private static int Num(string s, int fallback = 0)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        /// <summary>解析一行控制报文。任何畸形输入都只得到 Unknown/Err，不抛异常。</summary>
        public static RelayMessage Parse(string line)
        {
            var msg = new RelayMessage { Kind = RelayKind.Unknown };
            if (string.IsNullOrEmpty(line))
            {
                return msg;
            }
            var p = line.Split(Sep);
            switch (p[0].Trim().ToUpperInvariant())
            {
                case "HOST":
                    msg.Kind = RelayKind.Host;
                    msg.Name = Field(p, 1);
                    msg.Password = Field(p, 2);
                    msg.Ctl = Num(Field(p, 3));
                    msg.GameVersion = Num(Field(p, 4));
                    return msg;
                case "SEEN":
                    if (p.Length < 5)
                    {
                        return msg;
                    }
                    msg.Kind = RelayKind.Seen;
                    msg.Code = Field(p, 1);
                    msg.LevelIndex = Num(p[2], -1);
                    msg.Players = Num(p[3]);
                    msg.MaxPlayers = Num(p[4], 1);
                    msg.Password = Field(p, 5);
                    return msg;
                case "JOIN":
                    if (p.Length < 2)
                    {
                        return msg;
                    }
                    msg.Kind = RelayKind.Join;
                    msg.Code = Field(p, 1);
                    msg.Password = Field(p, 2);
                    msg.Ctl = Num(Field(p, 3));
                    msg.GameVersion = Num(Field(p, 4));
                    return msg;
                case "LIST":
                    msg.Kind = RelayKind.List;
                    msg.Token = Field(p, 1);
                    return msg;
                case "DROP":
                    msg.Kind = RelayKind.Drop;
                    msg.Code = Field(p, 1);
                    return msg;
                case "PING":
                    msg.Kind = RelayKind.Ping;
                    msg.Ctl = Num(Field(p, 1));
                    return msg;
                case "ROOM":
                    if (p.Length < 4)
                    {
                        return msg;
                    }
                    msg.Kind = RelayKind.Room;
                    msg.Code = Field(p, 1);
                    msg.GuestPort = Num(p[2], -1);
                    msg.HostPort = Num(p[3], -1);
                    return msg;
                case "OK":
                    msg.Kind = RelayKind.Ok;
                    msg.GuestPort = Num(Field(p, 1), -1);
                    return msg;
                case "PONG":
                    msg.Kind = RelayKind.Pong;
                    msg.Ctl = Num(Field(p, 1));
                    msg.Token = Field(p, 2);
                    msg.Count = Num(Field(p, 3));
                    msg.Capacity = Num(Field(p, 4));
                    return msg;
                case "ERR":
                    msg.Kind = RelayKind.Err;
                    msg.Error = Field(p, 1);
                    msg.Detail = Field(p, 2);
                    return msg;
                case "ROOMS":
                {
                    msg.Kind = RelayKind.Rooms;
                    msg.Rooms = new List<RelayRoomInfo>();
                    int count = p.Length > 1 ? Num(p[1]) : 0;
                    for (int i = 2; i < p.Length; i++)
                    {
                        var f = p[i].Split(RecSep);
                        if (f.Length < 6)
                        {
                            continue;
                        }
                        msg.Rooms.Add(new RelayRoomInfo
                        {
                            Code = f[0],
                            RoomName = f[1],
                            LevelIndex = Num(f[2], -1),
                            Players = Num(f[3]),
                            MaxPlayers = Num(f[4]),
                            Locked = f[5] == "1",
                        });
                    }
                    msg.Count = count < 0 ? 0 : count;
                    return msg;
                }
                default:
                    return msg;
            }
        }

        private static string Field(string[] p, int i) => i < p.Length ? p[i].Trim() : "";
    }
}
