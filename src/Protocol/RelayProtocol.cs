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
        public int GuestPort;
        public int HostPort;
        public int LevelIndex;
        public int Players;
        public int MaxPlayers;
        /// <summary>ROOMS 应答里声明的房间数（以实际解出的条目为准做上限校验用）。</summary>
        public int Count;
        public List<RelayRoomInfo> Rooms;
    }

    /// <summary>
    /// 模组与跨互联网中继之间的控制协议（与游戏数据通道分开，走一条独立的 UDP 文本口）。
    ///
    /// 用 `|` 分字段、`,` 分房间记录，两端一律经 Clean() 过滤，所以名字里不会出现分隔符。
    /// 文本协议是刻意选的：出问题时用 nc/socat 手敲一行就能验，不必为了调试再写一个客户端。
    /// 这份定义由模组与 RelayServer 共用（服务端直接链入本文件），保证两端不会各写一套而写串。
    /// </summary>
    public static class RelayProtocol
    {
        public const int DefaultControlPort = 27270;
        public const char Sep = '|';
        public const char RecSep = ',';

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

        public static string BuildHost(string name, string password)
            => "HOST" + Sep + Clean(name) + Sep + Clean(password);

        /// <summary>主机保活并上报房间当前状态（关卡、人数）。中继靠它判断房间还活着。</summary>
        public static string BuildSeen(string code, int levelIndex, int players, int maxPlayers, string password)
            => "SEEN" + Sep + Clean(code) + Sep + levelIndex.ToString(CultureInfo.InvariantCulture)
               + Sep + N(players) + Sep + N(maxPlayers)
               + Sep + Clean(password);

        public static string BuildJoin(string code, string password)
            => "JOIN" + Sep + Clean(code) + Sep + Clean(password);

        public static string BuildList() => "LIST";

        public static string BuildDrop(string code) => "DROP" + Sep + Clean(code);

        public static string BuildPing() => "PING";

        /// <summary>主机侧隧道的登记报文（同时充当 NAT 保活）。</summary>
        public static string BuildTunnelRegister(string code, int slot)
            => TunnelPrefix + Clean(code) + " " + slot.ToString(CultureInfo.InvariantCulture);

        // ------------------------------------------------------------ 组包（应答）

        public static string BuildRoom(string code, int guestPort, int hostPort)
            => "ROOM" + Sep + Clean(code) + Sep + N(guestPort) + Sep + N(hostPort);

        public static string BuildOk(int guestPort) => "OK" + Sep + N(guestPort);

        public static string BuildPong() => "PONG";

        public static string BuildError(string reason) => "ERR" + Sep + Clean(reason);

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
                    return msg;
                case "LIST":
                    msg.Kind = RelayKind.List;
                    return msg;
                case "DROP":
                    msg.Kind = RelayKind.Drop;
                    msg.Code = Field(p, 1);
                    return msg;
                case "PING":
                    msg.Kind = RelayKind.Ping;
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
                    return msg;
                case "ERR":
                    msg.Kind = RelayKind.Err;
                    msg.Error = Field(p, 1);
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
