using System;
using System.Collections.Generic;
using Lidgren.Network;

namespace PGvZOnlineMod.Protocol
{
    /// <summary>
    /// 协议版本：字段布局**或字段语义**不兼容时 +1，握手不一致直接拒连。
    /// v12 = 生成清单带血量上限；v13 = 关卡表 5→20；v14 = 关卡表改取游戏的 gChallengeDefs；
    /// v15 = PauseRequest 载荷加槽位（主机的暂停要能广播给客人）+ 新增 SeedState 位图。
    /// v16 = 天降种子包同步 + 「手里种子包种下去」的输入转发（19 天降种子）。
    /// v17 = 没加消息，但"谁在造僵尸"变了：客户端把墓碑起僵尸/屋顶空降/泳池出水一并锁进总闸，
    ///       传送带关卡的卡改按种子类型转发。老 v16 客户端仍会在本地凭空造僵尸，
    ///       与新端配对必不同步——语义不兼容就得升号。
    /// </summary>
    public static class ProtocolVersion
    {
        public const int Current = 17;
    }

    /// <summary>模组标识哈希：进握手包，两端必须一致（防不同版本逻辑不同步）。</summary>
    public static class ModFingerprint
    {
        public const string Fingerprint = "PGvZOnlineMod/v1";
    }

    public enum PacketType : byte
    {
        HandshakeC2H = 0,
        HandshakeH2C = 1,
        HandshakeResult = 2,
        RoomState = 3,
        StartGame = 4,
        InputPlant = 5,
        InputShovel = 6,
        InputSun = 7,
        InputCursor = 8,
        SpawnBatch = 9,
        Retire = 10,
        Snapshot = 11,
        Chat = 12,
        Goodbye = 13,
        Ping = 14,
        Ready = 15,
        /// <summary>Host 上某株植物刚产出了阳光（载荷=植物 netId），客户端同步触发自己的同株植物</summary>
        SunProduced = 16,
        /// <summary>Host 天降了一枚阳光（载荷=落点x/类型/新倒计时），客户端同位置同刻掉落</summary>
        SkySun = 17,
        /// <summary>
        /// 一方暂停/恢复（载荷=槽位 + bool）。
        /// 槽位必须带上：主机是裁决者，它要把"谁按了暂停"广播给其余人，
        /// 收端才知道该记在哪一格、能不能解除冻结。
        /// </summary>
        PauseRequest = 18,
        /// <summary>Host 放置了钉耙（载荷=格子x,y），客户端同位镜像放置</summary>
        RakePlaced = 19,
        /// <summary>Host 开场预览僵尸（载荷=类型/格x/格y），客户端镜像（视觉，随开场清理）</summary>
        CutsceneZombie = 20,
        /// <summary>加速倍率变化（载荷=分子/分母）</summary>
        Acceleration = 21,
        /// <summary>主机广播：所有玩家就绪，放行选卡门闩（空载荷）</summary>
        AllReady = 22,
        /// <summary>远端光标（载荷=槽位/x/y），主机中继到其余客户端</summary>
        CursorAt = 23,
        /// <summary>聊天文本（载荷=槽位/文本），主机中继</summary>
        ChatAt = 24,
        /// <summary>客人房间准备/取消准备（载荷=bool），槽位由连接推断</summary>
        RoomReadyRequest = 25,
        /// <summary>主机踢人（载荷=原因文本）</summary>
        Kick = 26,
        /// <summary>各槽位选卡就绪位图（主机 → 全员，选卡界面按人显示状态）</summary>
        SeedState = 27,
        /// <summary>Host 天降了一枚可用种子包（载荷=落点x/种子类型/新模式倒计时），客户端同位置掉自己的一份</summary>
        RainSeedPacket = 28,
        /// <summary>客户端把手里捡的种子包 / 传送带上的卡种下去（载荷=槽位/种子类型/变异类型/格坐标）</summary>
        InputPlantCoin = 29,
    }

    // ------------------------------------------------------------ 数据结构（Lawn 无关，离线可测）

    public struct NetPlantSpawn
    {
        public uint NetId;
        public int GridX, GridY;
        public int SeedType;
        public int Hp;
        /// <summary>主机当前的产阳光计数（客户端创建即同步节奏，首轮产出就对齐）</summary>
        public int LaunchCounter;
    }

    public struct NetZombieSpawn
    {
        public uint NetId;
        public int ZombieType, Row;
        public float X, Y;
        public int Hp;
        /// <summary>
        /// 血量上限：主机按人数加压后与基础值不同，客户端不带上就会错位
        /// 断头/伤害帧/巨人扔小鬼等一切"按血量比例"的判定。
        /// </summary>
        public int MaxHp;
    }

    public struct NetZombieState
    {
        public uint NetId;
        public float X, Y;
        public int Row;
        public int Hp;
    }

    public struct NetPlantState
    {
        public uint NetId;
        public int Hp;
    }

    public struct SnapshotMsg
    {
        public int SunMoney;
        public int CurrentWave;
        public int NumWaves;
        public int SunCountDown;
        public int ZombieCountDown;
        public int ZombieCountDownStart;
        public byte Flags; // bit0 paused, bit1 levelComplete, bit2 zombiesWon

        /// <summary>卡槽种子类型（≤10 槽，-1 无）。</summary>
        public int[] SeedTypes;
        public int[] RefreshCounters;
        public bool[] Refreshing;
        public bool[] Active;

        public List<NetZombieState> Zombies;
        public List<NetPlantState> Plants;

        public const byte FlagPaused = 1;
        public const byte FlagLevelComplete = 2;
        public const byte FlagZombiesWon = 4;
    }

    // ------------------------------------------------------------ 编解码

    /// <summary>
    /// 全部消息的编码/解码。编码侧第一个字节恒为 PacketType；
    /// 解码方法假设调用方已读完类型字节。仅依赖 Lidgren，可在 VerifyHost 离线回归。
    /// </summary>
    public static class Packets
    {
        public const int MaxSeedSlots = 10;

        // ---- 握手

        public static void WriteHandshake(NetOutgoingMessage m, PacketType type, int protoVer, string gameVer, string fingerprint, string nickname)
        {
            m.Write((byte)type);
            m.Write(protoVer);
            m.Write(gameVer ?? "");
            m.Write(fingerprint ?? "");
            m.Write(nickname ?? "");
        }

        public static void ReadHandshake(NetIncomingMessage m, out int protoVer, out string gameVer, out string fingerprint, out string nickname)
        {
            protoVer = m.ReadInt32();
            gameVer = m.ReadString();
            fingerprint = m.ReadString();
            nickname = m.ReadString();
        }

        public static void WriteHandshakeResult(NetOutgoingMessage m, bool ok, string reason, int slot)
        {
            m.Write((byte)PacketType.HandshakeResult);
            m.Write(ok);
            m.Write(reason ?? "");
            m.Write(slot);
        }

        public static void ReadHandshakeResult(NetIncomingMessage m, out bool ok, out string reason, out int slot)
        {
            ok = m.ReadBoolean();
            reason = m.ReadString();
            slot = m.ReadInt32();
        }

        // ---- 房间 / 开局

        public static void WriteRoomState(NetOutgoingMessage m, string[] nicks, int levelIndex, bool[] roomReady)
        {
            m.Write((byte)PacketType.RoomState);
            m.WriteVariableUInt32((uint)(nicks?.Length ?? 0));
            for (int i = 0; i < (nicks?.Length ?? 0); i++)
            {
                m.Write(nicks[i] ?? "");
                m.Write(roomReady != null && i < roomReady.Length && roomReady[i]);
            }
            m.Write(levelIndex);
        }

        public static void ReadRoomState(NetIncomingMessage m, out string[] nicks, out bool[] roomReady, out int levelIndex)
        {
            uint count = m.ReadVariableUInt32();
            nicks = new string[count];
            roomReady = new bool[count];
            for (uint i = 0; i < count; i++)
            {
                nicks[i] = m.ReadString();
                roomReady[i] = m.ReadBoolean();
            }
            levelIndex = m.ReadInt32();
        }

        public static void WriteRoomReadyRequest(NetOutgoingMessage m, bool ready)
        {
            m.Write((byte)PacketType.RoomReadyRequest);
            m.Write(ready);
        }

        public static bool ReadRoomReadyRequest(NetIncomingMessage m)
        {
            return m.ReadBoolean();
        }

        public static void WriteKick(NetOutgoingMessage m, string reason)
        {
            m.Write((byte)PacketType.Kick);
            m.Write(reason ?? "");
        }

        public static string ReadKick(NetIncomingMessage m)
        {
            return m.ReadString();
        }

        public static void WriteStartGame(NetOutgoingMessage m, int gameMode, int seed)
        {
            m.Write((byte)PacketType.StartGame);
            m.Write(gameMode);
            m.Write(seed);
        }

        public static void ReadStartGame(NetIncomingMessage m, out int gameMode, out int seed)
        {
            gameMode = m.ReadInt32();
            seed = m.ReadInt32();
        }

        // ---- 开局就绪（携带本地卡组 10 槽：类型 + 仿制者），双方各自声明自己的卡

        public static void WriteReady(NetOutgoingMessage m, int[] deckTypes, int[] deckImitaters)
        {
            m.Write((byte)PacketType.Ready);
            int slots = deckTypes?.Length ?? 0;
            m.WriteVariableUInt32((uint)slots);
            for (int i = 0; i < slots; i++)
            {
                m.Write(deckTypes[i]);
                m.Write(deckImitaters[i]);
            }
        }

        public static void ReadReady(NetIncomingMessage m, out int[] deckTypes, out int[] deckImitaters)
        {
            int slots = (int)m.ReadVariableUInt32();
            deckTypes = new int[slots];
            deckImitaters = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                deckTypes[i] = m.ReadInt32();
                deckImitaters[i] = m.ReadInt32();
            }
        }

        // ---- 天降阳光事件

        public static void WriteSkySun(NetOutgoingMessage m, float x, int coinType, int newCountdown)
        {
            m.Write((byte)PacketType.SkySun);
            m.Write(x);
            m.Write(coinType);
            m.Write(newCountdown);
        }

        public static void ReadSkySun(NetIncomingMessage m, out float x, out int coinType, out int newCountdown)
        {
            x = m.ReadFloat();
            coinType = m.ReadInt32();
            newCountdown = m.ReadInt32();
        }

        // ---- 天降可用种子包（19 天降种子 / 131 僵尸博士2 的种子雨）

        public static void WriteRainSeedPacket(NetOutgoingMessage m, float x, int seedType, int nextDropCounter)
        {
            m.Write((byte)PacketType.RainSeedPacket);
            m.Write(x);
            m.Write(seedType);
            m.Write(nextDropCounter);
        }

        public static void ReadRainSeedPacket(NetIncomingMessage m, out float x, out int seedType, out int nextDropCounter)
        {
            x = m.ReadFloat();
            seedType = m.ReadInt32();
            nextDropCounter = m.ReadInt32();
        }

        // ---- 开场预览僵尸 / 加速倍率

        public static void WriteCutsceneZombie(NetOutgoingMessage m, int zombieType, int gridX, int gridY)
        {
            m.Write((byte)PacketType.CutsceneZombie);
            m.Write(zombieType);
            m.Write(gridX);
            m.Write(gridY);
        }

        public static void ReadCutsceneZombie(NetIncomingMessage m, out int zombieType, out int gridX, out int gridY)
        {
            zombieType = m.ReadInt32();
            gridX = m.ReadInt32();
            gridY = m.ReadInt32();
        }

        public static void WriteAcceleration(NetOutgoingMessage m, int numerator, int denominator)
        {
            m.Write((byte)PacketType.Acceleration);
            m.Write(numerator);
            m.Write(denominator);
        }

        public static void ReadAcceleration(NetIncomingMessage m, out int numerator, out int denominator)
        {
            numerator = m.ReadInt32();
            denominator = m.ReadInt32();
        }

        // ---- 全员就绪 / 光标中继 / 聊天中继

        public static void WriteAllReady(NetOutgoingMessage m)
        {
            m.Write((byte)PacketType.AllReady);
        }

        public static void WriteCursorAt(NetOutgoingMessage m, int playerSlot, float x, float y)
        {
            m.Write((byte)PacketType.CursorAt);
            m.Write((byte)playerSlot);
            m.Write(x);
            m.Write(y);
        }

        public static void ReadCursorAt(NetIncomingMessage m, out int playerSlot, out float x, out float y)
        {
            playerSlot = m.ReadByte();
            x = m.ReadFloat();
            y = m.ReadFloat();
        }

        public static void WriteChatAt(NetOutgoingMessage m, int playerSlot, string text)
        {
            m.Write((byte)PacketType.ChatAt);
            m.Write((byte)playerSlot);
            m.Write(text ?? "");
        }

        public static void ReadChatAt(NetIncomingMessage m, out int playerSlot, out string text)
        {
            playerSlot = m.ReadByte();
            text = m.ReadString();
        }

        // ---- 暂停同步

        public static void WritePauseRequest(NetOutgoingMessage m, int playerSlot, bool paused)
        {
            m.Write((byte)PacketType.PauseRequest);
            m.Write((byte)playerSlot);
            m.Write(paused);
        }

        public static void ReadPauseRequest(NetIncomingMessage m, out int playerSlot, out bool paused)
        {
            playerSlot = m.ReadByte();
            paused = m.ReadBoolean();
        }

        /// <summary>选卡就绪位图：bit s = 槽位 s 已完成选卡。</summary>
        public static void WriteSeedState(NetOutgoingMessage m, int readyMask)
        {
            m.Write((byte)PacketType.SeedState);
            m.Write((byte)readyMask);
        }

        public static int ReadSeedState(NetIncomingMessage m)
        {
            return m.ReadByte();
        }

        public static void WriteRakePlaced(NetOutgoingMessage m, int gridX, int gridY)
        {
            m.Write((byte)PacketType.RakePlaced);
            m.Write(gridX);
            m.Write(gridY);
        }

        public static void ReadRakePlaced(NetIncomingMessage m, out int gridX, out int gridY)
        {
            gridX = m.ReadInt32();
            gridY = m.ReadInt32();
        }

        // ---- 植物产出事件

        public static void WriteSunProduced(NetOutgoingMessage m, uint netId, int newCounter)
        {
            m.Write((byte)PacketType.SunProduced);
            m.WriteVariableUInt32(netId);
            m.Write(newCounter);
        }

        public static void ReadSunProduced(NetIncomingMessage m, out uint netId, out int newCounter)
        {
            netId = m.ReadVariableUInt32();
            newCounter = m.ReadInt32();
        }

        // ---- 局域网房间广播（DiscoveryResponse 的载荷）

        public static void WriteRoomBeacon(NetOutgoingMessage m, string hostNick, int levelIndex)
        {
            m.Write(hostNick ?? "");
            m.Write(levelIndex);
        }

        public static void ReadRoomBeacon(NetIncomingMessage m, out string hostNick, out int levelIndex)
        {
            hostNick = m.ReadString();
            levelIndex = m.ReadInt32();
        }

        // ---- 输入

        public static void WriteInputPlant(NetOutgoingMessage m, int playerSlot, int cardSlot, int gridX, int gridY)
        {
            m.Write((byte)PacketType.InputPlant);
            m.Write(playerSlot);
            m.Write(cardSlot);
            m.Write(gridX);
            m.Write(gridY);
        }

        public static void ReadInputPlant(NetIncomingMessage m, out int playerSlot, out int cardSlot, out int gridX, out int gridY)
        {
            playerSlot = m.ReadInt32();
            cardSlot = m.ReadInt32();
            gridX = m.ReadInt32();
            gridY = m.ReadInt32();
        }

        /// <summary>
        /// 手里种子包（CursorType.PlantFromUsableCoin）的种植请求。
        /// 与 InputPlant 的区别：这里没有卡槽，携带的是种子类型本身——种子包不属于任何人的卡组，
        /// 它是天上掉下来的公共事件，落地后归各自所有，只有"种下去"这个动作要主机裁决。
        /// 传送带关的卡也走这条：带子上的卡同样"第几格"没有身份含义，两端的带子各走各的。
        /// </summary>
        public static void WriteInputPlantCoin(NetOutgoingMessage m, int playerSlot, int seedType, int imitaterType, int gridX, int gridY)
        {
            m.Write((byte)PacketType.InputPlantCoin);
            m.Write(playerSlot);
            m.Write(seedType);
            m.Write(imitaterType);
            m.Write(gridX);
            m.Write(gridY);
        }

        public static void ReadInputPlantCoin(NetIncomingMessage m, out int playerSlot, out int seedType, out int imitaterType, out int gridX, out int gridY)
        {
            playerSlot = m.ReadInt32();
            seedType = m.ReadInt32();
            imitaterType = m.ReadInt32();
            gridX = m.ReadInt32();
            gridY = m.ReadInt32();
        }

        public static void WriteInputShovel(NetOutgoingMessage m, int playerSlot, int gridX, int gridY)
        {
            m.Write((byte)PacketType.InputShovel);
            m.Write(playerSlot);
            m.Write(gridX);
            m.Write(gridY);
        }

        public static void ReadInputShovel(NetIncomingMessage m, out int playerSlot, out int gridX, out int gridY)
        {
            playerSlot = m.ReadInt32();
            gridX = m.ReadInt32();
            gridY = m.ReadInt32();
        }

        public static void WriteInputSun(NetOutgoingMessage m, float x, float y)
        {
            m.Write((byte)PacketType.InputSun);
            m.Write(x);
            m.Write(y);
        }

        public static void ReadInputSun(NetIncomingMessage m, out float x, out float y)
        {
            x = m.ReadFloat();
            y = m.ReadFloat();
        }

        public static void WriteInputCursor(NetOutgoingMessage m, float x, float y)
        {
            m.Write((byte)PacketType.InputCursor);
            m.Write(x);
            m.Write(y);
        }

        public static void ReadInputCursor(NetIncomingMessage m, out float x, out float y)
        {
            x = m.ReadFloat();
            y = m.ReadFloat();
        }

        // ---- 实体事件

        public static void WriteSpawnBatch(NetOutgoingMessage m, List<NetZombieSpawn> zombies, List<NetPlantSpawn> plants)
        {
            m.Write((byte)PacketType.SpawnBatch);
            m.WriteVariableUInt32((uint)(zombies?.Count ?? 0));
            if (zombies != null)
            {
                foreach (var z in zombies)
                {
                    m.WriteVariableUInt32(z.NetId);
                    m.Write(z.ZombieType);
                    m.Write(z.Row);
                    m.Write(z.X);
                    m.Write(z.Y);
                    m.Write(z.Hp);
                    m.Write(z.MaxHp);
                }
            }
            m.WriteVariableUInt32((uint)(plants?.Count ?? 0));
            if (plants != null)
            {
                foreach (var p in plants)
                {
                    m.WriteVariableUInt32(p.NetId);
                    m.Write(p.GridX);
                    m.Write(p.GridY);
                    m.Write(p.SeedType);
                    m.Write(p.Hp);
                    m.Write(p.LaunchCounter);
                }
            }
        }

        public static void ReadSpawnBatch(NetIncomingMessage m, List<NetZombieSpawn> zombies, List<NetPlantSpawn> plants)
        {
            zombies.Clear();
            plants.Clear();
            uint zc = m.ReadVariableUInt32();
            for (uint i = 0; i < zc; i++)
            {
                zombies.Add(new NetZombieSpawn
                {
                    NetId = m.ReadVariableUInt32(),
                    ZombieType = m.ReadInt32(),
                    Row = m.ReadInt32(),
                    X = m.ReadFloat(),
                    Y = m.ReadFloat(),
                    Hp = m.ReadInt32(),
                    MaxHp = m.ReadInt32(),
                });
            }
            uint pc = m.ReadVariableUInt32();
            for (uint i = 0; i < pc; i++)
            {
                plants.Add(new NetPlantSpawn
                {
                    NetId = m.ReadVariableUInt32(),
                    GridX = m.ReadInt32(),
                    GridY = m.ReadInt32(),
                    SeedType = m.ReadInt32(),
                    Hp = m.ReadInt32(),
                    LaunchCounter = m.ReadInt32(),
                });
            }
        }

        /// <summary>kind: 0=僵尸 1=植物</summary>
        public static void WriteRetire(NetOutgoingMessage m, uint netId, byte kind)
        {
            m.Write((byte)PacketType.Retire);
            m.WriteVariableUInt32(netId);
            m.Write(kind);
        }

        public static void ReadRetire(NetIncomingMessage m, out uint netId, out byte kind)
        {
            netId = m.ReadVariableUInt32();
            kind = m.ReadByte();
        }

        // ---- 快照

        public static void WriteSnapshot(NetOutgoingMessage m, in SnapshotMsg s)
        {
            m.Write((byte)PacketType.Snapshot);
            m.Write(s.SunMoney);
            m.Write(s.CurrentWave);
            m.Write(s.NumWaves);
            m.Write(s.SunCountDown);
            m.Write(s.ZombieCountDown);
            m.Write(s.ZombieCountDownStart);
            m.Write(s.Flags);

            int slots = s.SeedTypes?.Length ?? 0;
            m.WriteVariableUInt32((uint)slots);
            for (int i = 0; i < slots; i++)
            {
                m.Write(s.SeedTypes[i]);
                m.Write(s.RefreshCounters[i]);
                m.Write(s.Refreshing[i]);
                m.Write(s.Active[i]);
            }

            m.WriteVariableUInt32((uint)(s.Zombies?.Count ?? 0));
            if (s.Zombies != null)
            {
                foreach (var z in s.Zombies)
                {
                    m.WriteVariableUInt32(z.NetId);
                    m.Write(z.X);
                    m.Write(z.Y);
                    m.Write(z.Row);
                    m.Write(z.Hp);
                }
            }

            m.WriteVariableUInt32((uint)(s.Plants?.Count ?? 0));
            if (s.Plants != null)
            {
                foreach (var p in s.Plants)
                {
                    m.WriteVariableUInt32(p.NetId);
                    m.Write(p.Hp);
                }
            }
        }

        public static SnapshotMsg ReadSnapshot(NetIncomingMessage m)
        {
            var s = new SnapshotMsg
            {
                SunMoney = m.ReadInt32(),
                CurrentWave = m.ReadInt32(),
                NumWaves = m.ReadInt32(),
                SunCountDown = m.ReadInt32(),
                ZombieCountDown = m.ReadInt32(),
                ZombieCountDownStart = m.ReadInt32(),
                Flags = m.ReadByte(),
            };

            int slots = (int)m.ReadVariableUInt32();
            s.SeedTypes = new int[slots];
            s.RefreshCounters = new int[slots];
            s.Refreshing = new bool[slots];
            s.Active = new bool[slots];
            for (int i = 0; i < slots; i++)
            {
                s.SeedTypes[i] = m.ReadInt32();
                s.RefreshCounters[i] = m.ReadInt32();
                s.Refreshing[i] = m.ReadBoolean();
                s.Active[i] = m.ReadBoolean();
            }

            int zc = (int)m.ReadVariableUInt32();
            s.Zombies = new List<NetZombieState>(zc);
            for (int i = 0; i < zc; i++)
            {
                s.Zombies.Add(new NetZombieState
                {
                    NetId = m.ReadVariableUInt32(),
                    X = m.ReadFloat(),
                    Y = m.ReadFloat(),
                    Row = m.ReadInt32(),
                    Hp = m.ReadInt32(),
                });
            }

            int pc = (int)m.ReadVariableUInt32();
            s.Plants = new List<NetPlantState>(pc);
            for (int i = 0; i < pc; i++)
            {
                s.Plants.Add(new NetPlantState
                {
                    NetId = m.ReadVariableUInt32(),
                    Hp = m.ReadInt32(),
                });
            }
            return s;
        }

        // ---- 其它

        public static void WriteChat(NetOutgoingMessage m, string text)
        {
            m.Write((byte)PacketType.Chat);
            m.Write(text ?? "");
        }

        public static string ReadChat(NetIncomingMessage m)
        {
            return m.ReadString();
        }

        public static void WriteGoodbye(NetOutgoingMessage m, string reason)
        {
            m.Write((byte)PacketType.Goodbye);
            m.Write(reason ?? "");
        }

        public static string ReadGoodbye(NetIncomingMessage m)
        {
            return m.ReadString();
        }

        public static void WritePing(NetOutgoingMessage m, int timestampMs)
        {
            m.Write((byte)PacketType.Ping);
            m.Write(timestampMs);
        }

        public static int ReadPing(NetIncomingMessage m)
        {
            return m.ReadInt32();
        }
    }
}
