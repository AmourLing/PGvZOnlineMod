using System;
using Lawn;
using PGvZOnlineMod.Core;
using PGvZOnlineMod.Sync;
using Sexy;
using Sexy.TodLib;

namespace PGvZOnlineMod.Sync
{
    /// <summary>
    /// Host 端远端输入执行器：把客户端的请求翻译成与本地玩家完全相同的游戏调用。
    /// 卡组独立——远端玩家用"对方声明的卡组"（RemoteDeck）合成光标；
    /// 阳光独立——对方客户端本地已扣款，这里给主机棋盘垫付成本保证原生扣费成功，
    /// 执行完主机 mSunMoney 原样恢复。
    /// 种植走「合成光标 + MouseUpWithPlant」完整复用游戏的校验/融合/提示逻辑；
    /// 期间 ExecutingRemoteInput 守卫防止钩子把这次执行误判回本地玩家操作。
    /// </summary>
    public static class InputExecutor
    {
        public static void ExecutePlant(Board board, int playerSlot, int cardSlot, int gridX, int gridY)
        {
            try
            {
                if (board == null || board.mPaused)
                {
                    return;
                }
                if (playerSlot < 0 || playerSlot >= Session.MaxPlayers
                    || cardSlot < 0 || cardSlot >= Protocol.Packets.MaxSeedSlots)
                {
                    return;
                }
                int deckType = Session.RemoteDeckType[playerSlot][cardSlot];
                int deckImitater = Session.RemoteDeckImitater[playerSlot][cardSlot];
                if (deckType < 0)
                {
                    return; // 该玩家还没声明这个槽位的卡
                }
                if (gridX < 0 || gridY < 0 || gridY >= Constants.MAX_GRIDSIZEY)
                {
                    return;
                }
                var bank = board.mSeedBank;
                var cursor = board.mCursorObject;
                if (cursor == null)
                {
                    return;
                }

                // 主机自己卡槽的冷却状态先存后还——冷却属于对方，不能打在主机卡上
                var hostPacket = bank != null && cardSlot < bank.mNumPackets ? bank.mSeedPackets[cardSlot] : null;
                bool savedRefreshing = hostPacket?.mRefreshing ?? false;
                int savedCounter = hostPacket?.mRefreshCounter ?? 0;
                bool savedActive = hostPacket?.mActive ?? true;

                Session.ExecutingRemoteInput = true;
                try
                {
                    cursor.mCursorType = CursorType.PlantFromBank;
                    cursor.mType = (SeedType)deckType;
                    cursor.mImitaterType = (SeedType)deckImitater;
                    cursor.mSeedBankIndex = cardSlot;
                    int px = board.GridToPixelX(gridX, gridY);
                    int py = board.GridToPixelY(gridX, gridY);

                    // 阳光独立：对方的钱对方自己已扣（客户端本地预检+扣款）。
                    // 这里给主机棋盘"垫"上成本让原生扣费必成，执行完主机阳光原样恢复。
                    int sunBefore = board.mSunMoney;
                    try
                    {
                        int cost = board.GetCurrentPlantCost((SeedType)deckType, (SeedType)deckImitater);
                        if (cost > 0)
                        {
                            board.mSunMoney = sunBefore + cost;
                        }
                    }
                    catch
                    {
                    }
                    board.MouseUpWithPlant(px, py, 0);
                    board.mSunMoney = sunBefore;

                    if (hostPacket != null)
                    {
                        hostPacket.mRefreshing = savedRefreshing;
                        hostPacket.mRefreshCounter = savedCounter;
                        hostPacket.mActive = savedActive;
                    }
                    board.DeselectSeedPacket();
                }
                finally
                {
                    Session.ExecutingRemoteInput = false;
                    cursor.mCursorType = CursorType.Normal;
                }
            }
            catch (Exception ex)
            {
                ModEnv.Log("执行远端种植异常: " + ex);
            }
        }

        /// <summary>
        /// 远端玩家把"不属于卡槽的卡"种下去：19 天降种子的雨、地上掉的可用种子包、
        /// 以及传送带关里带子上的卡（客户端在钩子里改道到这里）。
        /// 主机这边临时造一枚同类型的"幽灵种子包"挂到光标上，让原生 MouseUpWithPlant 走完
        /// 自己的落点校验、融合与提示，原生分支收尾时会 Die 掉光标上那枚硬币——死的是幽灵，
        /// 不是主机自己地上还没捡的那一枚（种子包各收各的，跟阳光一个道理）。
        /// </summary>
        public static void ExecutePlantCoin(Board board, int seedType, int imitaterType, int gridX, int gridY)
        {
            try
            {
                if (board == null || board.mPaused)
                {
                    return;
                }
                // 只认枚举里真实存在的植物：SeedTypeCount 是游戏自己标的分界，
                // 66 往后是宝石按钮/老虎机图标/僵尸卡这些"伪种子"，种下去只会出事
                if (seedType < 0 || seedType >= (int)SeedType.SeedTypeCount
                    || imitaterType < (int)SeedType.None || imitaterType >= (int)SeedType.SeedTypeCount)
                {
                    return;
                }
                if (gridX < 0 || gridY < 0 || gridY >= Constants.MAX_GRIDSIZEY)
                {
                    return;
                }
                var cursor = board.mCursorObject;
                if (cursor == null)
                {
                    return;
                }

                Session.ExecutingRemoteInput = true;
                Coin ghost = null;
                try
                {
                    ghost = board.AddCoin(-500, -500, CoinType.UsableSeedPacket, CoinMotion.Coin);
                    ghost.mUsableSeedType = (SeedType)seedType;
                    cursor.mCursorType = CursorType.PlantFromUsableCoin;
                    cursor.mType = (SeedType)seedType;
                    cursor.mImitaterType = (SeedType)imitaterType;
                    cursor.mCoinID = ghost;
                    board.MouseUpWithPlant(
                        board.GridToPixelX(gridX, gridY), board.GridToPixelY(gridX, gridY), 0);
                }
                finally
                {
                    // 放不下时原生路径会提前 return，光标仍挂着幽灵硬币；
                    // 必须先摘光标再 Die——Coin.Die 里有"光标不能正拿着自己"的断言。
                    if (ghost != null && ReferenceEquals(cursor.mCoinID, ghost))
                    {
                        cursor.mCoinID = null;
                    }
                    cursor.mCursorType = CursorType.Normal;
                    cursor.mType = SeedType.None;
                    if (ghost != null && !ghost.mDead)
                    {
                        ghost.Die();
                    }
                    Session.ExecutingRemoteInput = false;
                }
            }
            catch (Exception ex)
            {
                ModEnv.Log("执行远端种子包种植异常: " + ex);
            }
        }

        public static void ExecuteShovel(Board board, int gridX, int gridY)
        {
            try
            {
                if (board == null || board.mPaused)
                {
                    return;
                }
                if (gridX < 0 || gridY < 0 || gridY >= Constants.MAX_GRIDSIZEY)
                {
                    return;
                }
                var cursor = board.mCursorObject;
                if (cursor == null)
                {
                    return;
                }
                // 合成铲子光标 + 原生 MouseDownWithTool：完整复用游戏的原生铲除逻辑
                // （普通植物/南瓜头/缠绕水草等细节全部一致），期间守卫防误转发
                Session.ExecutingRemoteInput = true;
                try
                {
                    cursor.mCursorType = CursorType.Shovel;
                    int px = board.GridToPixelX(gridX, gridY);
                    int py = board.GridToPixelY(gridX, gridY);
                    board.MouseDownWithTool(px, py, 0, CursorType.Shovel, false, false);
                    cursor.mCursorType = CursorType.Normal;
                }
                finally
                {
                    Session.ExecutingRemoteInput = false;
                    cursor.mCursorType = CursorType.Normal;
                }
            }
            catch (Exception ex)
            {
                ModEnv.Log("执行远端铲除异常: " + ex);
            }
        }

    }
}
