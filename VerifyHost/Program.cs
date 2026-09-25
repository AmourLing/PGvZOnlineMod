using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using PGvZOnlineMod.Core;
using PGvZOnlineMod.Net;
using PGvZOnlineMod.Protocol;

namespace PGvZOnlineVerify
{
    /// <summary>
    /// 离线验证宿主（不启动游戏）：
    ///  1. NetIdRegistry 语义；
    ///  2. 全部消息编解码往返 —— 走真实 Lidgren 本机回环（比读内部字段更严格）。
    /// 全部通过 exit code = 0，任一失败非 0，可直接当回归门。
    /// </summary>
    internal static class Program
    {
        private static int _failures;
        private static int _passes;

        private static void Check(string name, bool cond, string detail = "")
        {
            if (cond)
            {
                Interlocked.Increment(ref _passes);
                Console.WriteLine("[通过] " + name);
            }
            else
            {
                Interlocked.Increment(ref _failures);
                Console.WriteLine("[失败] " + name + (detail.Length > 0 ? "  —— " + detail : ""));
            }
        }

        private static int Main()
        {
            Console.WriteLine("== PGvZOnlineMod 离线验证 ==");
            TestRegistry();
            TestLoopback();
            TestDetour();
            Console.WriteLine($"== 结果：通过 {_passes} / 失败 {_failures} ==");
            return _failures == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------ 1. 注册表

        private class Obj
        {
        }

        private static void TestRegistry()
        {
            var reg = new NetIdRegistry();
            var a = new Obj();
            var b = new Obj();
            uint ia = reg.Assign(a);
            uint ia2 = reg.Assign(a);
            reg.Register(999, b);
            Check("netId 分配幂等", ia == ia2 && ia != 0);
            Check("netId 反查对象", reg.TryGetObject(ia, out var ra) && ReferenceEquals(ra, a));
            Check("Register 登记生效", reg.TryGetId(b, out uint ib) && ib == 999 && reg.TryGetObject(999, out var rb) && ReferenceEquals(rb, b));
            reg.Remove(999);
            Check("Remove 解除双向", !reg.TryGetObject(999, out _) && !reg.TryGetId(b, out _));
            reg.Clear();
            Check("Clear 清空", !reg.TryGetObject(ia, out _));
        }

        // ------------------------------------------------------------ 2. 回环全链路

        // client 连上后发握手 C2H；host 收到后回显 H2C 并把其余 12 种包全部下发；client 逐包解码断言。
        private static void TestLoopback()
        {
            const int port = 27199;
            using var host = new NetMgr();
            using var client = new NetMgr();

            var done = new ManualResetEventSlim(false);
            var connected = new ManualResetEventSlim(false);
            var sw = new Stopwatch();

            string c2hGameVer = null, c2hFp = null, c2hNick = null;
            int c2hProto = -1;
            bool h2cOk, hsResultOk, roomOk, startOk, plantOk, shovelOk, sunOk, cursorOk, spawnOk, retireOk, snapOk, chatOk, goodbyeOk, pingOk;
            h2cOk = hsResultOk = roomOk = startOk = plantOk = shovelOk = sunOk = cursorOk = spawnOk = retireOk = snapOk = chatOk = goodbyeOk = pingOk = false;

            client.OnConnected += (conn, r) =>
            {
                connected.Set();
                var m = client.CreateMessage();
                Packets.WriteHandshake(m, PacketType.HandshakeC2H, ProtocolVersion.Current, "PGvZ 1.3.1", ModFingerprint.Fingerprint, "测试玩家");
                client.SendReliableToHost(m);
            };

            host.OnData += im =>
            {
                var t = (PacketType)im.ReadByte();
                if (t != PacketType.HandshakeC2H)
                {
                    return;
                }
                Packets.ReadHandshake(im, out c2hProto, out c2hGameVer, out c2hFp, out c2hNick);

                var m0 = host.CreateMessage();
                Packets.WriteHandshake(m0, PacketType.HandshakeH2C, ProtocolVersion.Current, c2hGameVer, c2hFp, "主机");
                host.SendReliableToClients(m0);

                var m1 = host.CreateMessage();
                Packets.WriteHandshakeResult(m1, false, "版本不一致", 0);
                host.SendReliableToClients(m1);

                var m2 = host.CreateMessage();
                Packets.WriteRoomState(m2, new[] { "host", "guest", "", "" }, 3, new[] { false, true, false, false });
                host.SendReliableToClients(m2);

                var m3 = host.CreateMessage();
                Packets.WriteStartGame(m3, 7, 12345);
                host.SendReliableToClients(m3);

                var m4 = host.CreateMessage();
                Packets.WriteInputPlant(m4, 1, 2, 5, 3);
                host.SendReliableToClients(m4);

                var m5 = host.CreateMessage();
                Packets.WriteInputShovel(m5, 1, 8, 1);
                host.SendReliableToClients(m5);

                var m6 = host.CreateMessage();
                Packets.WriteInputSun(m6, 12.5f, 300.25f);
                host.SendReliableToClients(m6);

                var m7 = host.CreateMessage();
                Packets.WriteInputCursor(m7, 1.5f, 2.5f);
                host.SendUnreliableToClients(m7);

                var zs = new List<NetZombieSpawn>
                {
                    new NetZombieSpawn { NetId = 7, ZombieType = 3, Row = 4, X = 800.5f, Y = 90f, Hp = 270 },
                    new NetZombieSpawn { NetId = 9, ZombieType = 0, Row = 0, X = 10f, Y = 10f, Hp = 1 },
                };
                var ps = new List<NetPlantSpawn>
                {
                    new NetPlantSpawn { NetId = 11, GridX = 1, GridY = 2, SeedType = 40, Hp = 300 },
                };
                var m8 = host.CreateMessage();
                Packets.WriteSpawnBatch(m8, zs, ps);
                host.SendReliableToClients(m8);

                var m9 = host.CreateMessage();
                Packets.WriteRetire(m9, 55, 1);
                host.SendReliableToClients(m9);

                var m10 = host.CreateMessage();
                var snap = new SnapshotMsg
                {
                    SunMoney = 500,
                    CurrentWave = 3,
                    NumWaves = 20,
                    SunCountDown = 123,
                    Flags = SnapshotMsg.FlagPaused,
                    SeedTypes = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 40 },
                    RefreshCounters = new[] { 0, 10, 0, 0, 0, 0, 0, 0, 0, 0 },
                    Refreshing = new[] { false, true, false, false, false, false, false, false, false, false },
                    Active = new[] { true, true, true, true, true, true, true, true, true, true },
                    Zombies = new List<NetZombieState>
                    {
                        new NetZombieState { NetId = 1, X = 123.5f, Y = 45.25f, Row = 2, Hp = 100 },
                    },
                    Plants = new List<NetPlantState>
                    {
                        new NetPlantState { NetId = 2, Hp = 300 },
                        new NetPlantState { NetId = 3, Hp = 4000 },
                    },
                };
                Packets.WriteSnapshot(m10, in snap);
                host.SendUnreliableToClients(m10);

                var m11 = host.CreateMessage();
                Packets.WriteChat(m11, "你好，植物娘！");
                host.SendReliableToClients(m11);

                var m12 = host.CreateMessage();
                Packets.WriteGoodbye(m12, "exit");
                host.SendReliableToClients(m12);

                var m13 = host.CreateMessage();
                Packets.WritePing(m13, 4242);
                host.SendReliableToClients(m13);
            };

            client.OnData += im =>
            {
                var t = (PacketType)im.ReadByte();
                switch (t)
                {
                    case PacketType.HandshakeH2C:
                        Packets.ReadHandshake(im, out int v, out string gv, out string fp, out string nick);
                        h2cOk = v == ProtocolVersion.Current && gv == "PGvZ 1.3.1" && fp == ModFingerprint.Fingerprint && nick == "主机";
                        break;

                    case PacketType.HandshakeResult:
                        Packets.ReadHandshakeResult(im, out bool ok, out string reason, out int rSlot);
                        hsResultOk = !ok && reason == "版本不一致" && rSlot == 0;
                        break;

                    case PacketType.RoomState:
                    {
                        Packets.ReadRoomState(im, out string[] rn, out bool[] rd, out int lv);
                        roomOk = rn.Length == 4 && rn[0] == "host" && rn[1] == "guest" && rd[1] && lv == 3;
                        break;
                    }

                    case PacketType.StartGame:
                        Packets.ReadStartGame(im, out int mode, out int seed);
                        startOk = mode == 7 && seed == 12345;
                        break;

                    case PacketType.InputPlant:
                        Packets.ReadInputPlant(im, out int pSlot, out int cSlot, out int gx, out int gy);
                        plantOk = pSlot == 1 && cSlot == 2 && gx == 5 && gy == 3;
                        break;

                    case PacketType.InputShovel:
                        Packets.ReadInputShovel(im, out int pSlot2, out int gx2, out int gy2);
                        shovelOk = pSlot2 == 1 && gx2 == 8 && gy2 == 1;
                        break;

                    case PacketType.InputSun:
                        Packets.ReadInputSun(im, out float x, out float y);
                        sunOk = x == 12.5f && y == 300.25f;
                        break;

                    case PacketType.InputCursor:
                        Packets.ReadInputCursor(im, out float cx, out float cy);
                        cursorOk = cx == 1.5f && cy == 2.5f;
                        break;

                    case PacketType.SpawnBatch:
                    {
                        var zs = new List<NetZombieSpawn>();
                        var ps = new List<NetPlantSpawn>();
                        Packets.ReadSpawnBatch(im, zs, ps);
                        spawnOk = zs.Count == 2 && ps.Count == 1
                            && zs[0].NetId == 7 && zs[0].ZombieType == 3 && zs[0].Row == 4 && zs[0].X == 800.5f && zs[0].Hp == 270
                            && zs[1].NetId == 9 && zs[1].Hp == 1
                            && ps[0].NetId == 11 && ps[0].SeedType == 40;
                        break;
                    }

                    case PacketType.Retire:
                        Packets.ReadRetire(im, out uint id, out byte kind);
                        retireOk = id == 55 && kind == 1;
                        break;

                    case PacketType.Snapshot:
                    {
                        var s = Packets.ReadSnapshot(im);
                        snapOk = s.SunMoney == 500 && s.CurrentWave == 3 && s.NumWaves == 20 && s.SunCountDown == 123
                            && s.Flags == SnapshotMsg.FlagPaused
                            && s.SeedTypes.Length == 10 && s.SeedTypes[9] == 40 && s.Refreshing[1]
                            && s.Zombies.Count == 1 && s.Zombies[0].NetId == 1 && s.Zombies[0].X == 123.5f && s.Zombies[0].Row == 2
                            && s.Plants.Count == 2 && s.Plants[1].Hp == 4000;
                        break;
                    }

                    case PacketType.Chat:
                        chatOk = Packets.ReadChat(im) == "你好，植物娘！";
                        break;

                    case PacketType.Goodbye:
                        goodbyeOk = Packets.ReadGoodbye(im) == "exit";
                        break;

                    case PacketType.Ping:
                        pingOk = Packets.ReadPing(im) == 4242;
                        done.Set();
                        break;
                }
            };

            Check("建房", host.StartHost(port));
            Check("连接", client.StartClient("127.0.0.1", port));

            sw.Restart();
            while (sw.Elapsed.TotalSeconds < 10 && !done.IsSet)
            {
                host.Poll();
                client.Poll();
                Thread.Sleep(2);
            }

            // 等可靠通道 ACK 落地再关（缓解 Lidgren shutdown 竞态）
            sw.Restart();
            while (sw.Elapsed.TotalMilliseconds < 500)
            {
                host.Poll();
                client.Poll();
                Thread.Sleep(5);
            }

            host.Shutdown();
            client.Shutdown();

            Check("回环：连接建立", connected.IsSet);
            Check("回环：握手 C2H 上行解码", c2hProto == ProtocolVersion.Current && c2hGameVer == "PGvZ 1.3.1" && c2hFp == ModFingerprint.Fingerprint && c2hNick == "测试玩家",
                "proto=" + c2hProto + " ver=" + c2hGameVer + " fp=" + c2hFp + " nick=" + c2hNick);
            Check("回环：握手回显下行（H2C）", h2cOk);
            Check("回环：握手结果往返", hsResultOk);
            Check("回环：房间状态往返", roomOk);
            Check("回环：开局往返", startOk);
            Check("回环：种植输入往返", plantOk);
            Check("回环：铲除输入往返", shovelOk);
            Check("回环：收阳光输入往返", sunOk);
            Check("回环：光标往返（不可靠）", cursorOk);
            Check("回环：生成批次往返", spawnOk);
            Check("回环：退场往返", retireOk);
            Check("回环：快照往返（不可靠）", snapOk);
            Check("回环：聊天往返（中文）", chatOk);
            Check("回环：告别往返", goodbyeOk);
            Check("回环：心跳往返", pingOk);
        }

        // ------------------------------------------------------------ 3. detour 真实绑定

        /// <summary>
        /// 用与 GameHooks 完全相同的安装路径（HookInstaller.M 反射取方法 +
        /// HookEndpointManager.Add）钩一个静态纯函数 Plant.IsUpgrade，
        /// 真实触发 detour 并断言触发次数——验证钩子机制本身离线可用。
        /// （Plant.IsUpgrade 是静态无副作用方法，可离线安全调用。）
        /// </summary>
        private static void TestDetour()
        {
            try
            {
                // 先调用 20 次确保 JIT 编译，再挂钩子（pgvz-script-authoring 实测流程）
                for (int i = 0; i < 20; i++)
                {
                    _ = Lawn.Plant.IsUpgrade(Lawn.SeedType.Peashooter);
                }

                var target = PGvZOnlineMod.Hooks.HookInstaller.M(
                    typeof(Lawn.Plant), "IsUpgrade", new[] { typeof(Lawn.SeedType) });
                Check("detour：反射取到目标方法", target != null);

                int fired = 0;
                bool? captured = null;
                var hook = (Func<Func<Lawn.SeedType, bool>, Lawn.SeedType, bool>)((orig, seedType) =>
                {
                    fired++;
                    bool r = orig(seedType);
                    captured = r;
                    return r;
                });
                MonoMod.RuntimeDetour.HookGen.HookEndpointManager.Add(target, hook);

                // 走反射调用：直调小方法可能被 JIT 内联绕过 detour（调用点内联不经过方法体），
                // Invoke 每次都经方法入口，必然踩到 trampoline。
                bool viaInvoke = (bool)target.Invoke(null, new object[] { Lawn.SeedType.Peashooter });
                Check("detour：钩子被真实触发", fired == 1, "fired=" + fired);

                MonoMod.RuntimeDetour.HookGen.HookEndpointManager.Remove(target, hook);
                _ = target.Invoke(null, new object[] { Lawn.SeedType.Peashooter });
                Check("detour：卸载后不再触发", fired == 1 && captured != null && captured.Value == viaInvoke,
                    "fired=" + fired);
            }
            catch (Exception ex)
            {
                Check("detour：安装流程", false, ex.ToString());
            }
        }
    }
}
