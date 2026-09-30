using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Lawn;
using Lidgren.Network;
using PGvZOnlineMod.Core;
using PGvZOnlineMod.Net;
using PGvZOnlineMod.Protocol;

namespace PGvZOnlineVerify
{
    /// <summary>
    /// 离线验证宿主（不启动游戏）：
    ///  1. NetIdRegistry 语义；
    ///  2. 全部消息编解码往返 —— 走真实 Lidgren 本机回环（比读内部字段更严格）；
    ///  3. 中继控制报文的往返与畸形输入；
    ///  4. 玩家身份与手改配置里的服务器列表。
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
            TestLevels();
            TestHostingSeat();
            TestLoopback();
            TestExtendedPackets();
            TestRelayProtocol();
            TestIdentity();
            TestDetour();
            Console.WriteLine($"== 结果：通过 {_passes} / 失败 {_failures} ==");
            return _failures == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------ 1c. 主机座位占位

        /// <summary>
        /// 主机必须把自己算进 0 号位。历史上漏了这一笔，后果是连锁的：
        /// 选卡门闩在主机侧看不到自己 → 客人一点就绪主机就广播 AllReady，
        /// 客人进战场而主机还停在选卡界面；HUD 人数少一个；主机自己暂停不生效；
        /// 按人数加压数不到主机（永远 1 人 → 加压静默失效）。
        /// </summary>
        private static void TestHostingSeat()
        {
            Console.WriteLine("-- 主机座位与选卡门闩 --");
            PGvZOnlineMod.Sync.Session.StartHosting(null);
            bool occupied = PGvZOnlineMod.Sync.Session.SlotOccupied[0];
            int live = PGvZOnlineMod.Sync.Session.LivePlayerCount();

            // 主机自己没选完卡之前，绝不能算"全员就绪"（漏算 0 号位时这里会假绿）
            bool readyTooEarly = PGvZOnlineMod.Sync.Session.AllReadyNow;
            var deck = new int[10];
            var imit = new int[10];
            for (int i = 0; i < 10; i++)
            {
                deck[i] = -1;
                imit[i] = -1;
            }
            PGvZOnlineMod.Sync.Session.NoteLocalReady(deck, imit);
            bool readyAfter = PGvZOnlineMod.Sync.Session.AllReadyNow;
            PGvZOnlineMod.Sync.Session.CancelOrDisconnect();

            Check("建房后主机占住 0 号位且在场人数为 1", occupied && live == 1,
                "SlotOccupied[0]=" + occupied + " 在场=" + live);
            Check("选卡门闩：主机选完前不算全员就绪、选完后才算",
                !readyTooEarly && readyAfter,
                "选完前=" + readyTooEarly + " 选完后=" + readyAfter);
        }

        // ------------------------------------------------------------ 1b. 关卡表映射

        /// <summary>
        /// 联机关卡表现在直接取游戏的 ChallengeScreen.gChallengeDefs。
        /// 断言只针对**语义**（生存族齐备、排除项确实被排除、排序稳定、分页不重不漏），
        /// 期望值取自游戏自己的 GameMode 成员，不重抄模组的算式。
        /// </summary>
        private static void TestLevels()
        {
            Console.WriteLine("-- 关卡表 --");
            var levels = PGvZOnlineMod.Sync.Session.Levels;
            Console.WriteLine("  共 " + levels.Length + " 关可选 / 分类 "
                + string.Join("、", PGvZOnlineMod.Sync.Session.LevelPageFilters()));

            bool basics = levels != null && levels.Length >= 18;
            var seenModes = new HashSet<GameMode>();
            for (int i = 0; basics && i < levels.Length; i++)
            {
                var lv = levels[i];
                if (string.IsNullOrEmpty(lv.Name) || string.IsNullOrEmpty(lv.PageLabel))
                {
                    basics = false;
                }
                if (!seenModes.Add(lv.Mode))
                {
                    basics = false; // 同一 GameMode 不该出现两次
                }
            }
            Check("关卡表：非空、有名、GameMode 不重复", basics, "共 " + (levels?.Length ?? 0) + " 项");

            bool survivalOk = true;
            var families = new[]
            {
                GameMode.SurvivalNormalStage1, GameMode.SurvivalHardStage1,
                GameMode.SurvivalEndlessStage1, GameMode.SurvivalHellStage1,
            };
            foreach (var baseMode in families)
            {
                for (int s = 0; s < 5; s++)
                {
                    var want = baseMode + s;
                    bool found = false;
                    foreach (var lv in levels)
                    {
                        if (lv.Mode == want && lv.PageLabel == "生存")
                        {
                            found = true;
                        }
                    }
                    if (!found)
                    {
                        survivalOk = false;
                        Console.WriteLine("  缺生存条目 " + want + "=" + (int)want);
                    }
                }
            }
            // 核心不变量（排除名单式开放）：游戏自己的 gChallengeDefs 是全集，模组只点名排除，
            // 所以这里要锁三件事——开出来的每条都不在名单里、表里每条都被这两种状态之一覆盖、
            // 以及几条"必须不在"的正面钉法（名单实现写错时这条会独立报警）。
            var present = new HashSet<GameMode>();
            var openModes = new HashSet<GameMode>();
            bool onlyUnblocked = true;
            foreach (var lv in levels)
            {
                present.Add(lv.Mode);
                openModes.Add(lv.Mode);
                if (PGvZOnlineMod.Sync.Session.IsBlockedMode(lv.Mode))
                {
                    onlyUnblocked = false;
                    Console.WriteLine("  排除名单里却开放了: " + lv.Mode);
                }
            }
            bool covered = true;
            int blockedInTable = 0;
            foreach (var def in ChallengeScreen.gChallengeDefs)
            {
                if (def == null || openModes.Contains(def.mChallengeMode))
                {
                    continue;
                }
                if (!PGvZOnlineMod.Sync.Session.IsBlockedMode(def.mChallengeMode))
                {
                    covered = false;
                    Console.WriteLine("  既没开放也不在名单里（判据缺失）: "
                        + (int)def.mChallengeMode + " " + def.mChallengeMode);
                }
                else
                {
                    blockedInTable++;
                }
            }
            var mustBeAbsent = new[]
            {
                GameMode.ChallengeSlotMachine, GameMode.ChallengeBeghouled,
                GameMode.ChallengeWhackAZombie, GameMode.ScaryPotter1,
                GameMode.PuzzleIZombie1, GameMode.ChallengeZenGarden,
                GameMode.ChallengeIce, GameMode.ChallengePortalCombat,
                GameMode.ChallengeStageRandom, // 149 关卡随机数按机器取种，两端可能不在同一张场地上
            };
            bool absentOk = true;
            foreach (var bad in mustBeAbsent)
            {
                if (present.Contains(bad))
                {
                    absentOk = false;
                    Console.WriteLine("  不该开放却出现: " + bad);
                }
            }
            // 正面钉几条"必须在表里"：漏判一条排除理由、或筛表把整族吃掉时，上面几条都不会红。
            // 31/32/148 里 148 融合是被排除的（手套），拿来当反例；开的是 21/31/32/44/123。
            var mustBePresent = new[]
            {
                GameMode.ChallengeRainingSeeds,   // 19 靠 InputPlantCoin
                GameMode.ChallengeInvisighoul,    // 21 只有僵尸侧行为
                GameMode.ChallengeWarAndPeas2,    // 31
                GameMode.ChallengeWallnutBowling2, // 32
                GameMode.ChallengeGraveDanger,    // 44 靠墓碑出怪闸
                GameMode.ExtraChallengeStart,     // 123 额外页第一关
                GameMode.ImitaterRandom,          // 132 手套转发通了才敢放
                GameMode.ChallengeFusion,         // 148 同上
                GameMode.RogueConveyorbelt,       // 129 同上（带子卡另走 InputPlantCoin）
            };
            bool presentOk = true;
            foreach (var want in mustBePresent)
            {
                if (!present.Contains(want))
                {
                    presentOk = false;
                    Console.WriteLine("  应该开放却不在表里: " + want);
                }
            }
            Console.WriteLine("  全表 " + ChallengeScreen.gChallengeDefs.Length + " 条 / 开放 "
                + levels.Length + " 关 / 点名排除 " + blockedInTable + " 条");
            Check("关卡表：开放集合与排除名单互斥", onlyUnblocked);
            Check("关卡表：全表每条要么开放、要么被点名排除（无判据盲区）", covered);
            Check("关卡表：老虎机/宝石迷阵/砸罐子/我是僵尸/敲僵尸/禅境/冰面/传送门均未开放", absentOk);
            Check("关卡表：九条正面钉法都在表里（种子包闸 / 墓碑闸 / 手套转发的前提）", presentOk);
            Check("关卡表：生存四族各 5 场景共 20 项齐备", survivalOk);

            // 排序：页签 → GameMode 数值。曾经按游戏的行/列排，结果大泳池那几条
            // （行列是负数）被顶到列表最前面，这里锁住正确顺序。
            bool ordered = true;
            for (int i = 1; i < levels.Length; i++)
            {
                var a = levels[i - 1];
                var b = levels[i];
                if (a.PageOrder > b.PageOrder
                    || (a.PageOrder == b.PageOrder && (int)a.Mode > (int)b.Mode))
                {
                    ordered = false;
                }
            }
            Check("关卡表：按页签→GameMode 稳定排序", ordered);

            // 同一页签里显示名不能撞车：游戏的生存串只到"白天/黑夜/泳池"这一层，
            // 四个难度会重名，所以模组必须补族名前缀。断言只看"唯一性"，
            // 不依赖翻译结果（离线时游戏字符串表是空的，会返回 <Missing KEY>）。
            bool namesUnique = true;
            var perPage = new Dictionary<string, HashSet<string>>();
            foreach (var lv in levels)
            {
                if (!perPage.TryGetValue(lv.PageLabel, out var set))
                {
                    perPage[lv.PageLabel] = set = new HashSet<string>();
                }
                if (!set.Add(lv.Name))
                {
                    namesUnique = false;
                    Console.WriteLine("  同页签重名: " + lv.PageLabel + " / " + lv.Name);
                }
            }
            Check("关卡表：同页签内显示名唯一（生存族已带难度前缀）", namesUnique);

            var filters = PGvZOnlineMod.Sync.Session.LevelPageFilters();
            var union = new HashSet<int>();
            bool partitionOk = filters != null && filters.Count >= 1 && filters[0] == "全部";
            for (int f = 1; partitionOk && f < filters.Count; f++)
            {
                foreach (int idx in PGvZOnlineMod.Sync.Session.LevelIndicesOfPage(filters[f]))
                {
                    if (!union.Add(idx))
                    {
                        partitionOk = false; // 分类之间不该重叠
                    }
                }
            }
            partitionOk &= union.Count == levels.Length;
            partitionOk &= PGvZOnlineMod.Sync.Session.LevelIndicesOfPage("全部").Count == levels.Length;
            partitionOk &= PGvZOnlineMod.Sync.Session.ClampLevelIndex(-5) == 0
                && PGvZOnlineMod.Sync.Session.ClampLevelIndex(99999) == levels.Length - 1;
            // 分类比页签控件还多的话，多出来的那几类只能从"全部"里翻——开放表一大就会撞上，
            // 上限取自 UI 自己的常量，不在测试里重抄一遍数字。
            partitionOk &= filters.Count <= PGvZOnlineMod.Ui.OnlineLobbyScreen.TabCount;
            Check("关卡表：分类分页不重不漏且下标越界兜底", partitionOk,
                "分类 " + (filters?.Count ?? 0) + " 个 / 覆盖 " + union.Count + "/" + levels.Length);
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
                    // MaxHp 与 Hp 故意取不同值：加压后的僵尸血量≠上限，同值会漏掉字段写串
                    new NetZombieSpawn { NetId = 7, ZombieType = 3, Row = 4, X = 800.5f, Y = 90f, Hp = 270, MaxHp = 553 },
                    new NetZombieSpawn { NetId = 9, ZombieType = 0, Row = 0, X = 10f, Y = 10f, Hp = 1, MaxHp = 1200 },
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
                            && zs[0].MaxHp == 553 && zs[1].MaxHp == 1200
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
        /// <summary>
        /// 后加消息的往返覆盖。老环测只覆盖到 Kick 之前那批，
        /// PauseRequest（现在带槽位）、SeedState（位图）、RakePlaced 这些都没人验过——
        /// MaxHp 那次的教训就是"没覆盖的字段"等于"会写串的字段"。
        /// </summary>
        private static void TestExtendedPackets()
        {
            Console.WriteLine("-- 扩展消息 --");
            const int port = 27233;
            using var host = new NetMgr();
            using var client = new NetMgr();
            var connected = new ManualResetEventSlim(false);
            var arrived = new ManualResetEventSlim(false);
            int got = 0;
            bool sunProd = false, skySun = false, pause = false, rake = false, cutscene = false,
                accel = false, allReady = false, cursorAt = false, chatAt = false, roomReady = false,
                kick = false, seed = false, rain = false, plantCoin = false,
                movePlant = false, plantMoved = false;

            client.OnConnected += (c, r) => connected.Set();
            client.OnData += im =>
            {
                var t = (PacketType)im.ReadByte();
                switch (t)
                {
                    case PacketType.SunProduced:
                        Packets.ReadSunProduced(im, out uint nid, out int cnt);
                        sunProd = nid == 777 && cnt == 4321;
                        break;
                    case PacketType.SkySun:
                        Packets.ReadSkySun(im, out float sx, out int coin, out int cd);
                        skySun = sx > 299.9f && sx < 300.1f && coin == 1 && cd == 555;
                        break;
                    case PacketType.PauseRequest:
                        Packets.ReadPauseRequest(im, out int pslot, out bool pz);
                        pause = pslot == 2 && pz;
                        break;
                    case PacketType.RakePlaced:
                        Packets.ReadRakePlaced(im, out int rx, out int ry);
                        rake = rx == 3 && ry == 5;
                        break;
                    case PacketType.CutsceneZombie:
                        Packets.ReadCutsceneZombie(im, out int zt, out int gx, out int gy);
                        cutscene = zt == 12 && gx == 8 && gy == 1;
                        break;
                    case PacketType.Acceleration:
                        Packets.ReadAcceleration(im, out int num, out int den);
                        accel = num == 3 && den == 2;
                        break;
                    case PacketType.AllReady:
                        allReady = true;
                        break;
                    case PacketType.CursorAt:
                        Packets.ReadCursorAt(im, out int cslot, out float cx, out float cy);
                        cursorAt = cslot == 3 && cx == 11.5f && cy == 22.25f;
                        break;
                    case PacketType.ChatAt:
                        Packets.ReadChatAt(im, out int chslot, out string txt);
                        chatAt = chslot == 1 && txt == "救命！我这行顶不住";
                        break;
                    case PacketType.RoomReadyRequest:
                        roomReady = Packets.ReadRoomReadyRequest(im);
                        break;
                    case PacketType.Kick:
                        kick = Packets.ReadKick(im) == "你被主机踢出了房间";
                        break;
                    case PacketType.SeedState:
                        seed = Packets.ReadSeedState(im) == 3;
                        break;
                    case PacketType.RainSeedPacket:
                        Packets.ReadRainSeedPacket(im, out float rx2, out int rseed, out int rnext);
                        rain = rx2 > 313.2f && rx2 < 313.3f && rseed == 53 && rnext == 4444;
                        break;
                    case PacketType.InputMovePlant:
                        Packets.ReadInputMovePlant(im, out int mslot, out uint mnid, out int mx, out int my, out int mclick);
                        movePlant = mslot == 2 && mnid == 90210u && mx == -30 && my == 512 && mclick == -1;
                        break;
                    case PacketType.PlantMoved:
                        Packets.ReadPlantMoved(im, out uint pmid, out int mvx, out int mvy);
                        plantMoved = pmid == 90210u && mvx == 7 && mvy == 4;
                        break;
                    case PacketType.InputPlantCoin:
                        Packets.ReadInputPlantCoin(im, out int poslot, out int pseed, out int pimit, out int pgx, out int pgy);
                        plantCoin = poslot == 2 && pseed == 12 && pimit == 53 && pgx == 3 && pgy == 4;
                        break;
                }
                if (++got >= 16)
                {
                    arrived.Set();
                }
            };

            // Lidgren 的事件只在 Poll() 里派发，不轮询就永远连不上
            var sw = Stopwatch.StartNew();
            bool link = host.StartHost(port) && client.StartClient("127.0.0.1", port);
            while (link && !connected.IsSet && sw.Elapsed.TotalSeconds < 5)
            {
                host.Poll();
                client.Poll();
                Thread.Sleep(1);
            }
            Check("扩展：建房与连接", link && connected.IsSet);

            void Send(Action<NetOutgoingMessage> write)
            {
                var m = host.CreateMessage();
                if (m == null)
                {
                    return;
                }
                write(m);
                host.SendReliableToClients(m);
            }
            if (link && connected.IsSet)
            {
                Send(m => Packets.WriteSunProduced(m, 777, 4321));
                Send(m => Packets.WriteSkySun(m, 300f, 1, 555));
                Send(m => Packets.WritePauseRequest(m, 2, true));
                Send(m => Packets.WriteRakePlaced(m, 3, 5));
                Send(m => Packets.WriteCutsceneZombie(m, 12, 8, 1));
                Send(m => Packets.WriteAcceleration(m, 3, 2));
                Send(m => Packets.WriteAllReady(m));
                Send(m => Packets.WriteCursorAt(m, 3, 11.5f, 22.25f));
                Send(m => Packets.WriteChatAt(m, 1, "救命！我这行顶不住"));
                Send(m => Packets.WriteRoomReadyRequest(m, true));
                Send(m => Packets.WriteKick(m, "你被主机踢出了房间"));
                Send(m => Packets.WriteSeedState(m, 3));
                Send(m => Packets.WriteRainSeedPacket(m, 313.25f, 53, 4444));
                Send(m => Packets.WriteInputPlantCoin(m, 2, 12, 53, 3, 4));
                Send(m => Packets.WriteInputMovePlant(m, 2, 90210u, -30, 512, -1));
                Send(m => Packets.WritePlantMoved(m, 90210u, 7, 4));
                while (!arrived.IsSet && sw.Elapsed.TotalSeconds < 5)
                {
                    host.Poll();
                    client.Poll();
                    Thread.Sleep(1);
                }
            }

            Check("扩展：产出/天降阳光/暂停(含槽位)/钉耙 往返", sunProd && skySun && pause && rake,
                "sun=" + sunProd + " sky=" + skySun + " pause=" + pause + " rake=" + rake);
            Check("扩展：预览僵尸/加速/全员就绪/光标中继 往返", cutscene && accel && allReady && cursorAt,
                "cut=" + cutscene + " accel=" + accel + " allReady=" + allReady + " cursor=" + cursorAt);
            Check("扩展：聊天中继(中文)/房间准备/踢人/选卡位图 往返", chatAt && roomReady && kick && seed,
                "chat=" + chatAt + " ready=" + roomReady + " kick=" + kick + " seed=" + seed);
            Check("扩展：天降种子包 / 种子包种植请求 往返", rain && plantCoin,
                "rain=" + rain + " plantCoin=" + plantCoin);
            // 挪植物的请求带棋盘像素与 clickCount（放不进/丢垃圾桶靠它区分），netId 走无符号：
            // 写成有符号或写窄，这条会直接红。
            Check("扩展：手套挪植请求 / PlantMoved 广播 往返", movePlant && plantMoved,
                "move=" + movePlant + " moved=" + plantMoved);
            host.Shutdown();
            client.Shutdown();
        }

        // ------------------------------------------------------------ 4. 中继控制协议

        /// <summary>
        /// 中继那份文本控制协议。模组与 RelayServer 链入的是同一个源文件，所以这里测的不是
        /// "我的组包和我的解包对不对得上"，而是两件真会咬人的事：
        ///   · 玩家手输的自由文本（房间名、密码）过 Clean 后不会溢出成多余字段——否则一个
        ///     "我的房|JOIN|123456" 就能当两条报文用；
        ///   · 畸形报文只会得到 Unknown/回落值，不抛异常——服务端收的是公网来的任意字节。
        /// </summary>
        private static void TestRelayProtocol()
        {
            Console.WriteLine("-- 中继控制协议 --");

            var host = RelayProtocol.Parse(RelayProtocol.BuildHost("测试主机☆", "p|w|d"));
            Check("HOST 往返（房间名含中文）",
                host.Kind == RelayKind.Host && host.Name == "测试主机☆", "kind=" + host.Kind + " name=" + host.Name);
            Check("密码里的分隔符被 Clean 吃掉，不会多出一个字段",
                host.Password == "pwd", "pwd=" + host.Password);

            var seen = RelayProtocol.Parse(RelayProtocol.BuildSeen("123456", 7, 2, 4, "ab"));
            Check("SEEN 往返（关卡/人数/上限/密码）",
                seen.Kind == RelayKind.Seen && seen.Code == "123456" && seen.LevelIndex == 7
                && seen.Players == 2 && seen.MaxPlayers == 4 && seen.Password == "ab",
                "lvl=" + seen.LevelIndex + " p=" + seen.Players + "/" + seen.MaxPlayers);

            // 主机还没选关时上报 -1；Num 的回落写成 0 就会显示成"第 0 关"
            var seenUnknown = RelayProtocol.Parse(RelayProtocol.BuildSeen("123456", -1, 1, 4, ""));
            Check("SEEN 的未知关卡原样保持 -1", seenUnknown.LevelIndex == -1, "lvl=" + seenUnknown.LevelIndex);

            var room = RelayProtocol.Parse(RelayProtocol.BuildRoom("654905", 27201, 27200));
            Check("ROOM 往返（客人口/主机口）",
                room.Kind == RelayKind.Room && room.Code == "654905" && room.GuestPort == 27201 && room.HostPort == 27200,
                "g=" + room.GuestPort + " h=" + room.HostPort);

            var join = RelayProtocol.Parse(RelayProtocol.BuildJoin("000000", ""));
            Check("JOIN 空密码", join.Kind == RelayKind.Join && join.Code == "000000" && join.Password == "");
            Check("OK 往返客人口",
                RelayProtocol.Parse(RelayProtocol.BuildOk(27201)) is var okp
                && okp.Kind == RelayKind.Ok && okp.GuestPort == 27201);
            Check("LIST/DROP/PING/PONG/ERR 往返",
                RelayProtocol.Parse(RelayProtocol.BuildList()).Kind == RelayKind.List
                && RelayProtocol.Parse(RelayProtocol.BuildDrop("123456")).Code == "123456"
                && RelayProtocol.Parse(RelayProtocol.BuildPing()).Kind == RelayKind.Ping
                && RelayProtocol.Parse(RelayProtocol.BuildPong()).Kind == RelayKind.Pong
                && RelayProtocol.Parse(RelayProtocol.BuildError("passwd")).Error == "passwd");

            var rooms = new List<RelayRoomInfo>
            {
                new() { Code = "111111", RoomName = "小明的房间", LevelIndex = 7, Players = 2, MaxPlayers = 4, Locked = false },
                new() { Code = "222222", RoomName = "加密房", LevelIndex = -1, Players = 1, MaxPlayers = 4, Locked = true },
                new() { Code = "333333", RoomName = "满员房", LevelIndex = 61, Players = 4, MaxPlayers = 4, Locked = false },
            };
            var list = RelayProtocol.Parse(RelayProtocol.BuildRooms(rooms));
            Check("ROOMS 三房往返（含要密码/未知关卡）",
                list.Kind == RelayKind.Rooms && list.Count == 3 && list.Rooms.Count == 3
                && list.Rooms[0].RoomName == "小明的房间" && list.Rooms[0].LevelIndex == 7
                && list.Rooms[1].Locked && list.Rooms[1].LevelIndex == -1
                && list.Rooms[2].Code == "333333" && list.Rooms[2].Players == 4 && !list.Rooms[2].Locked,
                "n=" + list.Rooms?.Count);
            var noRoom = RelayProtocol.Parse(RelayProtocol.BuildRooms(new List<RelayRoomInfo>()));
            Check("ROOMS 空表解出 0 房", noRoom.Kind == RelayKind.Rooms && noRoom.Count == 0 && noRoom.Rooms.Count == 0);

            Check("隧道登记报文格式（空格分隔，不走 Sep）",
                RelayProtocol.BuildTunnelRegister("654905", 2) == "PGVZREG 654905 2",
                RelayProtocol.BuildTunnelRegister("654905", 2));

            Check("Clean 去掉分隔符、逗号、空白与控制字符",
                RelayProtocol.Clean("a|b,c d\r\n\t中") == "abcd中", "got=" + RelayProtocol.Clean("a|b,c d\r\n\t中"));
            Check("Clean 截到 24 字符（左列一行画得下）",
                RelayProtocol.Clean(new string('x', 100)).Length == 24,
                "len=" + RelayProtocol.Clean(new string('x', 100)).Length);
            Check("Clean 处理 null 与空串", RelayProtocol.Clean(null) == "" && RelayProtocol.Clean("") == "");

            // 调试时是用 nc/socat 手敲的，大小写不保证
            Check("动词大小写宽容",
                RelayProtocol.Parse("host|a|").Kind == RelayKind.Host
                && RelayProtocol.Parse("join|x|").Kind == RelayKind.Join
                && RelayProtocol.Parse("LiSt").Kind == RelayKind.List);

            // 只该拿到 Unknown 的畸形输入
            string[] unknown =
            {
                null, "", "GARBAGE", "|", "|||", "ROOM|1", "ROOM", "SEEN|123", "SEEN", "JOIN",
                "PGVZREG 1 2", new string('摸', 5000), new string('|', 300), "\n\0\r", " rooms|1",
            };
            string badDetail = "";
            foreach (var raw in unknown)
            {
                try
                {
                    var m = RelayProtocol.Parse(raw);
                    // " rooms|1" 带前导空格：Trim 后仍认得 Rooms，不算畸形
                    if (m.Kind != RelayKind.Unknown && raw != " rooms|1")
                    {
                        badDetail += " [" + Short(raw) + "->" + m.Kind + "]";
                    }
                }
                catch (Exception ex)
                {
                    badDetail += " [" + Short(raw) + " 抛 " + ex.GetType().Name + "]";
                }
            }
            Check("畸形报文只得到 Unknown 且不抛异常", badDetail == "", badDetail);

            // 认得动词但字段是垃圾：必须落到"不可能的端口/关卡值"，0 会被当成真端口
            var noPort = RelayProtocol.Parse("ROOM|123456|abc|");
            Check("端口字段非数字时回落 -1（不是 0）",
                noPort.Kind == RelayKind.Room && noPort.GuestPort == -1 && noPort.HostPort == -1,
                "g=" + noPort.GuestPort + " h=" + noPort.HostPort);
            Check("OK 无端口字段回落 -1",
                RelayProtocol.Parse("OK|xx").GuestPort == -1 && RelayProtocol.Parse("OK").GuestPort == -1);
            var junkSeen = RelayProtocol.Parse("SEEN|123|a|b|c|d");
            Check("SEEN 数字字段垃圾时逐项回落",
                junkSeen.LevelIndex == -1 && junkSeen.Players == 0 && junkSeen.MaxPlayers == 1
                && junkSeen.Password == "d",
                "lvl=" + junkSeen.LevelIndex + " p=" + junkSeen.Players + " max=" + junkSeen.MaxPlayers);
            var bare = RelayProtocol.Parse("HOST");
            Check("缺字段的动词得到空串而非崩溃",
                bare.Kind == RelayKind.Host && bare.Name == "" && bare.Password == "");
            // 声明的房间数与实际条数不符（截包/伪造）：以条数为准，UI 不能按 Count 建控件；
            // 记录段数不够时必须整条丢掉——留着会在取 f[2..5] 时越界，把这条断言变成"崩掉"而不是"报红"。
            string roomsJunk = "";
            int roomsJunkN = -1;
            try
            {
                var shortList = RelayProtocol.Parse("ROOMS|5|1,2");
                roomsJunkN = shortList.Kind == RelayKind.Rooms ? shortList.Rooms.Count : -2;
            }
            catch (Exception ex)
            {
                roomsJunk = ex.GetType().Name;
            }
            Check("ROOMS 记录不足 6 段时整条丢弃且不抛", roomsJunk.Length == 0 && roomsJunkN == 0,
                "抛=" + roomsJunk + " n=" + roomsJunkN);
            Check("ROOMS 多余字段不影响已解出的记录",
                RelayProtocol.Parse("ROOMS|1|a,b,-1,1,4,x,y,z").Rooms.Count == 1);
        }

        private static string Short(string s)
            => s == null ? "<null>" : (s.Length > 10 ? s.Substring(0, 10) + "…" : s);

        // ------------------------------------------------------------ 5. 身份与手改配置

        /// <summary>
        /// 显示名改从游戏存档取（mPlayerInfo.mName），联机配置里不再有 Nickname；
        /// 服务器列表来自玩家手改的 JSON，必须在进 UI 之前被洗干净。
        /// 本会话没跑过游戏循环，_app 为空，走的就是回落分支。
        /// </summary>
        private static void TestIdentity()
        {
            Console.WriteLine("-- 身份与服务器列表 --");
            Check("取不到存档名时回落为 玩家（不抛 NullReference）",
                PGvZOnlineMod.Sync.Session.LocalNick() == "玩家",
                "got=" + PGvZOnlineMod.Sync.Session.LocalNick());

            string path = Path.Combine(ModEnv.DataDir, "联机配置.json");
            byte[] backup = File.Exists(path) ? File.ReadAllBytes(path) : null;
            try
            {
                Directory.CreateDirectory(ModEnv.DataDir);
                File.WriteAllText(path, @"
{
  ""Nickname"": ""旧字段，已从配置里删掉"",
  ""HostPort"": 99999,
  ""SnapshotHz"": 999,
  ""CursorHz"": 1,
  ""ZombieHpPerExtraPlayer"": 99,
  ""Servers"": [
    { ""Name"": ""自家中继"", ""Host"": ""47.116.78.238"", ""Port"": 27270 },
    { ""Name"": """", ""Host"": ""  relay.example.com  "", ""Port"": 27271 },
    { ""Name"": ""空白地址"", ""Host"": ""   "", ""Port"": 27272 },
    null,
    { ""Name"": ""零端口"", ""Host"": ""10.0.0.9"", ""Port"": 0 },
    { ""Name"": ""越界端口"", ""Host"": ""10.0.0.10"", ""Port"": 70000 },
    { ""Host"": ""10.0.0.11"", ""Port"": 27277 },
    { ""Name"": ""第七"", ""Host"": ""10.0.0.12"", ""Port"": 27278 },
    { ""Name"": ""第八"", ""Host"": ""10.0.0.13"", ""Port"": 27279 },
    { ""Name"": ""第九"", ""Host"": ""10.0.0.14"", ""Port"": 27280 },
    { ""Name"": ""第十"", ""Host"": ""10.0.0.15"", ""Port"": 27281 },
    { ""Name"": ""第十一"", ""Host"": ""10.0.0.16"", ""Port"": 27282 }
  ]
}");
                Check("能重置配置缓存（_config 改名要同步这里）", ResetCachedConfig());
                var cfg = ModEnv.GetConfig();

                Check("服务器列表截到 8 台", cfg.Servers.Count == 8, "n=" + cfg.Servers.Count);
                Check("空地址与 null 条目被丢掉（截到 8 台之前先清）",
                    cfg.Servers.All(s => s != null && s.Host.Length > 0)
                    && cfg.Servers.All(s => s.Host != "10.0.0.15" && s.Host != "10.0.0.16")
                    && cfg.Servers[7].Host == "10.0.0.14",
                    string.Join(",", cfg.Servers.Select(s => s.Host)));
                Check("地址首尾空白被去掉（否则 Dns 解析直接失败）",
                    cfg.Servers[1].Host == "relay.example.com", "host=" + cfg.Servers[1].Host);
                Check("没名字的服务器用地址当显示名",
                    cfg.Servers[1].Name == "relay.example.com" && cfg.Servers[4].Name == "10.0.0.11",
                    "name=" + cfg.Servers[1].Name + "/" + cfg.Servers[4].Name);
                Check("有名字的服务器不被覆盖",
                    cfg.Servers[0].Name == "自家中继" && cfg.Servers[0].Host == "47.116.78.238",
                    "name=" + cfg.Servers[0].Name);
                Check("端口 0 与越界端口回落控制口",
                    cfg.Servers[2].Port == RelayProtocol.DefaultControlPort
                    && cfg.Servers[3].Port == RelayProtocol.DefaultControlPort,
                    "p=" + cfg.Servers[2].Port + "/" + cfg.Servers[3].Port);
                Check("合法端口原样保留",
                    cfg.Servers[1].Port == 27271 && cfg.Servers[5].Port == 27278,
                    "p=" + cfg.Servers[1].Port + "/" + cfg.Servers[5].Port);
                Check("旧 Nickname 字段被忽略，其余越界值照样回落",
                    cfg.HostPort == 27150 && cfg.SnapshotHz == 20 && cfg.CursorHz == 10
                    && Math.Abs(cfg.ZombieHpPerExtraPlayer - 0.35f) < 1e-6,
                    "port=" + cfg.HostPort + " hz=" + cfg.SnapshotHz + "/" + cfg.CursorHz
                    + " hp=" + cfg.ZombieHpPerExtraPlayer);
            }
            finally
            {
                // 成对恢复：这个宿主下次运行（以及本次后面的用例）看到的必须还是原文件
                try
                {
                    if (backup != null)
                    {
                        File.WriteAllBytes(path, backup);
                    }
                    else if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                }
                ResetCachedConfig();
            }

            byte[] after = File.Exists(path) ? File.ReadAllBytes(path) : null;
            Check("配置用例已把 联机配置.json 恢复原样",
                (backup == null && after == null) || (backup != null && after != null && backup.SequenceEqual(after)),
                "exists=" + File.Exists(path));
            // 恢复后再读一次：拿到的应该是原配置而不是我塞进去的那份脏列表
            var restored = ModEnv.GetConfig();
            Check("恢复后重新读取不到测试用的服务器列表",
                restored.Servers.All(s => s.Host != "10.0.0.14"),
                "n=" + restored.Servers.Count);
            if (backup == null)
            {
                // 本来没有配置文件时，上面那次读取会顺手落一份默认配置（模组首次运行也一样会落）；
                // 这里再删掉，保证两次运行看到的是同一个起点。
                try { File.Delete(path); } catch { }
                ResetCachedConfig();
            }
        }

        private static bool ResetCachedConfig()
        {
            var field = typeof(ModEnv).GetField("_config", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
            {
                return false;
            }
            field.SetValue(null, null);
            return true;
        }

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
