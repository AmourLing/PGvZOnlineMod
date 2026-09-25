using System;
using Lawn;
using MonoMod.RuntimeDetour.HookGen;
using PGvZOnlineMod.Core;
using PGvZOnlineMod.Sync;
using PGvZOnlineMod.Ui;
using Sexy;
using Sexy.TodLib;

namespace PGvZOnlineMod.Hooks
{
    /// <summary>
    /// 全部游戏钩子。改写型钩子每条分支都 return；观察者钩子先 orig 后自己的逻辑，
    /// 且自身逻辑整段 try/catch，绝不因 mod 异常冻结游戏。
    /// </summary>
    public static class GameHooks
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            // 主泵：任何场景每帧必走
            HookEndpointManager.Add(HookInstaller.M(typeof(LawnApp), "UpdateFrames", Type.EmptyTypes),
                (Action<Action<LawnApp>, LawnApp>)LawnAppUpdateFramesHook);

            // 开局门闩：双方都点完选卡（CloseSeedChooser 末尾）才放行进战场——
            // 拦下时选卡界面保持原样，双方就绪后同时进入开场动画
            HookEndpointManager.Add(HookInstaller.M(typeof(CutScene), "EndSeedChooser", Type.EmptyTypes),
                (Action<Action<CutScene>, CutScene>)CutSceneEndSeedChooserHook);

            // 主菜单：联机按钮
            HookEndpointManager.Add(HookInstaller.M(typeof(GameSelector), "ButtonDepress", new[] { typeof(int) }),
                (Action<Action<GameSelector, int>, GameSelector, int>)GameSelectorButtonDepressHook);

            // 棋盘主循环：快照节拍
            HookEndpointManager.Add(HookInstaller.M(typeof(Board), "Update", Type.EmptyTypes),
                (Action<Action<Board>, Board>)BoardUpdateHook);

            // 棋盘绘制后：远端光标 + 连接 HUD
            HookEndpointManager.Add(HookInstaller.M(typeof(Board), "Draw", new[] { typeof(Graphics) }),
                (Action<Action<Board, Graphics>, Board, Graphics>)BoardDrawHook);

            // 选卡界面绘制后：就绪门闩的等待提示（画在选卡层之上才可见）
            HookEndpointManager.Add(HookInstaller.M(typeof(SeedChooserScreen), "DrawOverlay", new[] { typeof(Graphics) }),
                (Action<Action<SeedChooserScreen, Graphics>, SeedChooserScreen, Graphics>)SeedChooserDrawOverlayHook);

            // 出怪权威随机：Client 抑制本地出怪，改为应用 Host 的 SpawnBatch
            HookEndpointManager.Add(HookInstaller.M(typeof(Board), "SpawnZombieWave", Type.EmptyTypes),
                (Action<Action<Board>, Board>)BoardSpawnZombieWaveHook);
            HookEndpointManager.Add(HookInstaller.M(typeof(Challenge), "SpawnZombieWave", Type.EmptyTypes),
                (Action<Action<Challenge>, Challenge>)ChallengeSpawnZombieWaveHook);

            // 种植：Client 转请求；Host 执行远端输入时直接走 orig
            HookEndpointManager.Add(HookInstaller.M(typeof(Board), "MouseUpWithPlant", new[] { typeof(int), typeof(int), typeof(int) }),
                (Action<Action<Board, int, int, int>, Board, int, int, int>)BoardMouseUpWithPlantHook);

            // 铲子：Client 用铲子点植物 → 转请求（Host 执行真正的铲除）
            HookEndpointManager.Add(HookInstaller.M(typeof(Board), "MouseDownWithTool",
                    new[] { typeof(int), typeof(int), typeof(int), typeof(CursorType), typeof(bool), typeof(bool) }),
                (Action<Action<Board, int, int, int, CursorType, bool, bool>, Board, int, int, int, CursorType, bool, bool>)BoardMouseDownWithToolHook);

            // 植物产出同步：Client 抑制自然产出，Host 检测产出后广播 SunProduced
            HookEndpointManager.Add(HookInstaller.M(typeof(Plant), "UpdateProductionPlant", Type.EmptyTypes),
                (Action<Action<Plant>, Plant>)PlantUpdateProductionHook);

            // 天降阳光同步：Client 抑制自然掉落，Host 掉落后广播 SkySun（落点/类型/节奏）
            HookEndpointManager.Add(HookInstaller.M(typeof(Board), "UpdateSunSpawning", Type.EmptyTypes),
                (Action<Action<Board>, Board>)BoardUpdateSunSpawningHook);

            // 暂停同步：任一方暂停 = 双方棋盘同步冻结
            HookEndpointManager.Add(HookInstaller.M(typeof(Board), "Pause", new[] { typeof(bool) }),
                (Action<Action<Board, bool>, Board, bool>)BoardPauseHook);

            // 加速倍率：AccelerationIncrease/Decrease 钩不上（已知坑，疑似内联）——
            // 改钩 MouseUpInternal，用"分子数值变化"检测加速操作（先例：游戏速度.py）
            HookEndpointManager.Add(HookInstaller.M(typeof(Board), "MouseUpInternal",
                    new[] { typeof(int), typeof(int), typeof(int), typeof(bool) }),
                (Action<Action<Board, int, int, int, bool>, Board, int, int, int, bool>)BoardMouseUpInternalHook);

            // 钉耙同步：Client 抑制本地随机放置，Host 放置后广播位置
            HookEndpointManager.Add(HookInstaller.M(typeof(Board), "PlaceRake", Type.EmptyTypes),
                (Action<Action<Board>, Board>)BoardPlaceRakeHook);

            // 开场预览僵尸：Client 抑制本地生成，Host 生成后广播（客户端镜像）
            HookEndpointManager.Add(HookInstaller.M(typeof(CutScene), "PlaceAZombie",
                    new[] { typeof(ZombieType), typeof(int), typeof(int) }),
                (Action<Action<CutScene, ZombieType, int, int>, CutScene, ZombieType, int, int>)CutScenePlaceAZombieHook);



            // 同步创建的实体登记 netId（仅 Client 应用 SpawnBatch 时触发）
            HookEndpointManager.Add(HookInstaller.M(typeof(Board), "AddZombieInRow", new[] { typeof(ZombieType), typeof(int), typeof(int), typeof(bool) }),
                (Func<Func<Board, ZombieType, int, int, bool, Zombie>, Board, ZombieType, int, int, bool, Zombie>)BoardAddZombieInRowHook);
            HookEndpointManager.Add(HookInstaller.M(typeof(Board), "AddPlant", new[] { typeof(int), typeof(int), typeof(SeedType), typeof(SeedType) }),
                (Func<Func<Board, int, int, SeedType, SeedType, Plant>, Board, int, int, SeedType, SeedType, Plant>)BoardAddPlantHook);

            ModEnv.Log("联机 Hooks 已注册（UpdateFrames/ButtonDepress/Board.Update/Board.Draw/" +
                       "SpawnZombieWave×2/MouseUpWithPlant/Coin.MouseDown/AddZombieInRow/AddPlant）");
        }

        // ------------------------------------------------------------ 主泵

        private static void LawnAppUpdateFramesHook(Action<LawnApp> orig, LawnApp self)
        {
            orig(self);
            try
            {
                Session.Pump(self);
            }
            catch (Exception ex)
            {
                ModEnv.LogOnce("主泵异常（重复不再记）: " + ex);
            }
        }

        // ------------------------------------------------------------ 开局就绪门闩

        /// <summary>
        /// 联机对局首次选卡完成（点"Let's Rock"）：先互发 Ready+卡组，双方都完成才
        /// 放行 EndSeedChooser（进入战场开场动画）。拦下时选卡界面保持原样，可继续改卡。
        /// CloseSeedChooser 可能被重复触发（再点一次开始按钮），逻辑必须幂等。
        /// </summary>
        private static void CutSceneEndSeedChooserHook(Action<CutScene> orig, CutScene self)
        {
            if (!Session.ReadyGateActive)
            {
                orig(self);
                return;
            }
            ReadLocalDeck(self.mBoard, out int[] types, out int[] imits);
            if (Session.NoteLocalReady(types, imits))
            {
                // 对方已就绪 → 直接放行
                Session.MarkStartExecuted();
                orig(self);
                return;
            }
            // 对方还在选卡：挂起，等对方 Ready 包（Session.OnRemoteReady）触发
            Session.HoldStart(() => orig(self));
        }

        /// <summary>从棋盘卡槽读出本地卡组（CloseSeedChooser 已完成 SetPacketType）。</summary>
        private static void ReadLocalDeck(Board board, out int[] types, out int[] imits)
        {
            types = new int[Protocol_MaxSeedSlots];
            imits = new int[Protocol_MaxSeedSlots];
            for (int i = 0; i < Protocol_MaxSeedSlots; i++)
            {
                types[i] = -1;
                imits[i] = -1;
            }
            var bank = board?.mSeedBank;
            if (bank == null)
            {
                return;
            }
            for (int i = 0; i < bank.mNumPackets && i < Protocol_MaxSeedSlots; i++)
            {
                var packet = bank.mSeedPackets[i];
                if (packet == null)
                {
                    continue;
                }
                types[i] = (int)packet.mPacketType;
                imits[i] = (int)packet.mImitaterType;
            }
        }

        private const int Protocol_MaxSeedSlots = 10;

        // ------------------------------------------------------------ 主菜单按钮

        private static void GameSelectorButtonDepressHook(Action<GameSelector, int> orig, GameSelector self, int theId)
        {
            if (theId == OnlineLobbyScreen.LobbyButtonId)
            {
                try
                {
                    OnlineLobbyScreen.Toggle(self.mApp);
                }
                catch (Exception ex)
                {
                    ModEnv.Log("打开联机面板异常: " + ex);
                }
                return;
            }
            orig(self, theId);
        }

        // ------------------------------------------------------------ 棋盘循环 / 绘制

        private static void BoardUpdateHook(Action<Board> orig, Board self)
        {
            orig(self);
            try
            {
                Session.TickHud();
                Session.OnBoardUpdate(self);
            }
            catch (Exception ex)
            {
                ModEnv.LogOnce("Board 同步异常（重复不再记）: " + ex);
            }
        }

        private static void BoardDrawHook(Action<Board, Graphics> orig, Board self, Graphics g)
        {
            orig(self, g);
            try
            {
                Hud.Draw(self, g);
            }
            catch
            {
                // HUD 绘制绝不能打断游戏渲染
            }
        }

        // ------------------------------------------------------------ 选卡等待提示

        private static void SeedChooserDrawOverlayHook(Action<SeedChooserScreen, Graphics> orig, SeedChooserScreen self, Graphics g)
        {
            orig(self, g);
            try
            {
                if (Session.ReadyGateActive && !Session.LevelActuallyStarted)
                {
                    var font = Resources.FONT_DWARVENTODCRAFT15;
                    if (Session.AwaitingReady)
                    {
                        const string waiting = "等待另一位玩家完成选卡…";
                        g.SetFont(font);
                        g.SetColor(new SexyColor(255, 235, 120, 240));
                        g.DrawString(waiting, (self.mWidth - font.StringWidth(waiting)) / 2, 40);
                    }
                    else if (Session.AllReadyNow)
                    {
                        const string ready = "对方已完成选卡";
                        g.SetFont(Resources.FONT_BRIANNETOD16);
                        g.SetColor(new SexyColor(160, 255, 160, 220));
                        g.DrawString(ready, (self.mWidth - Resources.FONT_BRIANNETOD16.StringWidth(ready)) / 2, 46);
                    }
                }
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------ 出怪抑制（Client）

        private static void BoardSpawnZombieWaveHook(Action<Board> orig, Board self)
        {
            if (Session.ClientSuppressionActive)
            {
                Core.ModEnv.Log("[波次] 客户端抑制本地出怪 wave=" + self.mCurrentWave);
                return; // 权威随机由 Host 下发
            }
            if (Session.IsHost && Session.SyncActive)
            {
                Core.ModEnv.Log("[波次] 主机出怪 wave=" + self.mCurrentWave);
            }
            orig(self);
        }

        private static void ChallengeSpawnZombieWaveHook(Action<Challenge> orig, Challenge self)
        {
            if (Session.ClientSuppressionActive)
            {
                Core.ModEnv.Log("[波次] 客户端抑制 Challenge 出怪");
                return;
            }
            orig(self);
        }

        // ------------------------------------------------------------ 种植转发（Client）/ 远端执行（Host）

        private static void BoardMouseUpWithPlantHook(Action<Board, int, int, int> orig, Board self, int x, int y, int theClickCount)
        {
            if (Session.ExecutingRemoteInput)
            {
                orig(self, x, y, theClickCount);
                return;
            }
            if (Session.ClientSuppressionActive
                && self.mCursorObject != null
                && self.mCursorObject.mCursorType == CursorType.PlantFromBank)
            {
                int gx = self.PixelToGridX(x, y);
                int gy = self.PixelToGridY(x, y);
                int slot = self.mCursorObject.mSeedBankIndex;
                // 卡组独立：用自己的阳光预检、自己的卡槽进冷却（在读光标类型之后、取消选择之前算费用）
                int cost = self.GetCurrentPlantCost(self.mCursorObject.mType, self.mCursorObject.mImitaterType);
                bool canPay = self.CanTakeSunMoney(cost);
                try
                {
                    self.DeselectSeedPacket();
                }
                catch
                {
                }
                var ownPacket = self.mSeedBank != null && slot >= 0 && slot < self.mSeedBank.mNumPackets
                    ? self.mSeedBank.mSeedPackets[slot]
                    : null;
                if (ownPacket != null && ownPacket.mRefreshing)
                {
                    return; // 冷却中（兜底：正常情况下选卡环节就会拦住）
                }
                if (gx >= 0 && gy >= 0 && slot >= 0 && canPay)
                {
                    Session.SendPlantRequest(Session.MySlot, slot, gx, gy);
                    try
                    {
                        self.TakeSunMoney(cost); // 阳光独立：自己付钱
                        ownPacket?.WasPlanted(); // 本地卡槽进入冷却（冷却归自己）
                        if (ownPacket != null)
                        {
                            // Deselect 会把卡槽重新激活，而冷却 tick 的条件是 !mActive && mRefreshing
                            // ——显式压回未激活态，否则冷却永远不走（2P 无冷却的根因）
                            ownPacket.mActive = false;
                            ownPacket.mRefreshCounter = 0;
                        }
                    }
                    catch
                    {
                    }
                    Core.ModEnv.Log("[种植] 客户端请求 slot=" + slot + " 类型=" + cost + "阳光 格=" + gx + "," + gy);
                }
                return; // 不执行本地种植，等 Host 的 Spawn 事件
            }
            orig(self, x, y, theClickCount);
        }

        // ------------------------------------------------------------ 加速倍率（MouseUpInternal 变化检测）

        private static int _lastAccelNum = -1;

        private static void BoardMouseUpInternalHook(
            Action<Board, int, int, int, bool> orig, Board self, int x, int y, int theClickCount, bool isTouch)
        {
            orig(self, x, y, theClickCount, isTouch);
            try
            {
                if (!Session.SyncActive || self == null)
                {
                    return;
                }
                Session.DetectAccelerationChange(self, ref _lastAccelNum);
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------ 开场预览僵尸 / 加速同步

        private static void CutScenePlaceAZombieHook(
            Action<CutScene, ZombieType, int, int> orig, CutScene self, ZombieType theZombieType, int theGridX, int theGridY)
        {
            if (Session.ClientSuppressionActive && !Session.SpawningMirrorCutsceneZombie)
            {
                return; // 客户端的预览僵尸由主机事件镜像
            }
            orig(self, theZombieType, theGridX, theGridY);
            if (Session.IsHost && Session.SyncActive && !Session.SpawningMirrorCutsceneZombie)
            {
                Session.OnHostCutsceneZombie((int)theZombieType, theGridX, theGridY);
            }
        }



        // ------------------------------------------------------------ 钉耙同步

        /// <summary>
        /// Client：抑制本地放置（钉耙位置含加权随机行，各掷各的必然不同步），
        /// 由 Host 的 RakePlaced 事件镜像。Host：orig 后把位置广播出去。
        /// </summary>
        private static void BoardPlaceRakeHook(Action<Board> orig, Board self)
        {
            if (Session.ClientSuppressionActive)
            {
                return; // 等主机的 RakePlaced 事件
            }
            int countBefore = self.mGridItems.Count;
            orig(self);
            if (Session.IsHost && Session.SyncActive && self.mGridItems.Count > countBefore)
            {
                var rake = self.mGridItems[self.mGridItems.Count - 1];
                if (rake.mGridItemType == GridItemType.Rake)
                {
                    Session.OnHostRakePlaced(self, rake.mGridX, rake.mGridY);
                }
            }
        }

        // ------------------------------------------------------------ 暂停同步

        private static void BoardPauseHook(Action<Board, bool> orig, Board self, bool thePause)
        {
            if (Session.ExecutingPauseSync)
            {
                orig(self, thePause); // 我们自己的同步应用，直接执行
                return;
            }
            orig(self, thePause);
            try
            {
                Session.OnLocalPauseChanged(self, thePause);
            }
            catch (Exception ex)
            {
                ModEnv.LogOnce("暂停同步异常（不再重复记）: " + ex);
            }
        }

        // ------------------------------------------------------------ 天降阳光同步

        /// <summary>
        /// Client：完全抑制本地天降阳光（快照的旧计数值会二次触发导致双份），
        /// 由 Host 的 SkySun 事件驱动同位置掉落。
        /// Host：orig 执行后 mNumSunsFallen 增加说明刚掉落 → 广播事件。
        /// </summary>
        private static void BoardUpdateSunSpawningHook(Action<Board> orig, Board self)
        {
            if (Session.ClientSuppressionActive)
            {
                return; // 天降阳光由 Host 事件驱动
            }
            int sunsBefore = self.mNumSunsFallen;
            orig(self);
            if (Session.IsHost && Session.SyncActive && self.mNumSunsFallen > sunsBefore)
            {
                Session.OnHostSkySunSpawned(self);
            }
        }

        // ------------------------------------------------------------ 植物产出同步

        /// <summary>
        /// Client：未同步过节奏的植物冻结计数（防止本地随机首产）；
        /// 已同步的植物自然倒数到 0 产出（与主机同时刻，误差 ≈ 半程 RTT）。
        /// Host：orig 执行后若 mLaunchCounter 被重置说明刚产出 → 广播新计数值。
        /// </summary>
        private static void PlantUpdateProductionHook(Action<Plant> orig, Plant self)
        {
            if (Session.ClientSuppressionActive && !Session.IsPlantProductionSynced(self))
            {
                // 尚未收到主机节奏：冻结在 1，不产出也不动画
                if (self.mLaunchCounter <= 1)
                {
                    self.mLaunchCounter = 1;
                }
                return;
            }
            int counterBefore = self.mLaunchCounter;
            orig(self);
            if (Session.IsHost && Session.SyncActive && self.mLaunchCounter > counterBefore)
            {
                Session.OnHostPlantProduced(self, self.mLaunchCounter);
            }
            else if (!Session.IsHost && Session.SyncActive
                && Session.IsPlantProductionSynced(self) && self.mLaunchCounter > counterBefore)
            {
                Core.ModEnv.Log("[产出] 客户端自然产出（节奏同步生效）counter=" + self.mLaunchCounter);
            }
        }

        // ------------------------------------------------------------ 铲子转发（Client）

        /// <summary>
        /// Client 持铲子点下植物：转 InputShovel 请求（Host 执行真正的铲除），
        /// 本地不执行（本地植物等 Host 的 Retire 事件移除）。
        /// 其余工具（浇壶/手套/金币等）不拦截，保持本地行为。
        /// </summary>
        private static void BoardMouseDownWithToolHook(
            Action<Board, int, int, int, CursorType, bool, bool> orig,
            Board self, int x, int y, int theClickCount, CursorType theCursorType, bool posScaled, bool isTouch)
        {
            if (Session.ClientSuppressionActive
                && theCursorType == CursorType.Shovel
                && theClickCount >= 0)
            {
                int gx = self.PixelToGridX(x, y);
                int gy = self.PixelToGridY(x, y);
                if (gx >= 0 && gy >= 0)
                {
                    Session.SendShovelRequest(Session.MySlot, gx, gy);
                    try
                    {
                        self.mApp?.PlayFoley(FoleyType.UseShovel); // 本地音效反馈
                    }
                    catch
                    {
                    }
                }
                return; // 本地不铲，等 Host 的 Retire 事件
            }
            orig(self, x, y, theClickCount, theCursorType, posScaled, isTouch);
        }

        // ------------------------------------------------------------ 同步实体登记（Client）

        private static Zombie BoardAddZombieInRowHook(Func<Board, ZombieType, int, int, bool, Zombie> orig,
            Board self, ZombieType theZombieType, int theRow, int theFromWave, bool theCover)
        {
            Zombie z = orig(self, theZombieType, theRow, theFromWave, theCover);
            try
            {
                if (z != null && Session.ApplyingSync && Session.RegisterNextId != 0)
                {
                    Session.Registry.Register(Session.RegisterNextId, z);
                    Session.RegisterNextId = 0;
                }
            }
            catch
            {
            }
            return z;
        }

        private static Plant BoardAddPlantHook(Func<Board, int, int, SeedType, SeedType, Plant> orig,
            Board self, int theGridX, int theGridY, SeedType theSeedType, SeedType theImitaterType)
        {
            Plant p = orig(self, theGridX, theGridY, theSeedType, theImitaterType);
            try
            {
                if (p != null && Session.ApplyingSync && Session.RegisterNextId != 0)
                {
                    Session.Registry.Register(Session.RegisterNextId, p);
                    Session.RegisterNextId = 0;
                }
            }
            catch
            {
            }
            return p;
        }
    }
}
