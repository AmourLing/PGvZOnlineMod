using System;
using System.Collections.Concurrent;

namespace PGvZOnlineMod.Core
{
    /// <summary>
    /// 网络线程 → 游戏主线程 的投递队列。
    /// Lidgren 在自己的线程收包；所有会触碰游戏对象的工作都 Post 进来，
    /// 由 LawnApp.UpdateFrames 钩子里的 Pump() 在主线程统一消费。
    /// </summary>
    public static class MainThreadQueue
    {
        private static readonly ConcurrentQueue<Action> s_queue = new();

        public static void Post(Action action)
        {
            if (action != null)
            {
                s_queue.Enqueue(action);
            }
        }

        public static void Pump()
        {
            while (s_queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    ModEnv.Log("主线程任务异常: " + ex);
                }
            }
        }
    }
}
