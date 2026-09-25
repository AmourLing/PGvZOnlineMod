using System;
using System.IO;
using System.Reflection;
using MonoMod.RuntimeDetour.HookGen;

namespace PGvZOnlineMod.Core
{
    /// <summary>
    /// 依赖预加载。Windows 版游戏在 LawnDLL 里自带 Lidgren/Newtonsoft（随 deps.json 加载），
    /// Android 版没带 —— 所以这两个 DLL 随模组分发（mods/PGvZOnlineMod/ 下），
    /// 启动时先按简单名探测（Windows 命中游戏已加载的同一份，零冲突），失败再 LoadFrom 自带副本。
    /// 必须在任何触碰 Lidgren/Newtonsoft 类型的 IL 之前调用（PerformModuleReload 最前），
    /// 否则 JIT 解析字段类型时会抛 TypeLoadException。
    /// </summary>
    public static class NetAsm
    {
        private static bool _done;

        public static void EnsureDependenciesLoaded()
        {
            if (_done)
            {
                return;
            }
            _done = true;
            Assembly lidgren = TryLoad("Lidgren.Network.Core");
            TryLoad("Newtonsoft.Json");
            if (lidgren != null)
            {
                InstallLidgrenCompatFix(lidgren);
            }
        }

        private static Assembly TryLoad(string simpleName)
        {
            // 1) 已加载或可从游戏依赖闭包解析（Windows 正常路径）
            try
            {
                return Assembly.Load(new AssemblyName(simpleName));
            }
            catch
            {
            }

            // 2) 模组自带副本：mods/PGvZOnlineMod/ 下，其次 mods/ 顶层
            try
            {
                string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                string[] candidates =
                {
                    Path.Combine(baseDir, "PGvZOnlineMod", simpleName + ".dll"),
                    Path.Combine(baseDir, simpleName + ".dll"),
                };
                foreach (var path in candidates)
                {
                    if (File.Exists(path))
                    {
                        Assembly asm = Assembly.LoadFrom(path);
                        ModEnv.Log("已加载模组自带依赖 " + simpleName + " ← " + path);
                        return asm;
                    }
                }
                ModEnv.Log("警告: 找不到依赖 " + simpleName + "（需要 mods/PGvZOnlineMod/" + simpleName + ".dll），联机功能不可用");
            }
            catch (Exception ex)
            {
                ModEnv.Log("加载依赖 " + simpleName + " 失败: " + ex.Message);
            }
            return null;
        }

        /// <summary>
        /// Android 兼容补丁：Mono 在 Android 上网卡枚举可能为空
        /// （GetNetworkInterface() 返回 null），Lidgren 两处不空安全：
        ///  1) GetMacAddressBytes() 返回 null → NetPeer.InitializeNetwork 里 .Length
        ///     直接 NullReferenceException，peer 永远起不来（建房/搜房全挂）；
        ///  2) GetBroadcastAddress() 返回 null → DiscoverLocalPeers 里
        ///     new IPEndPoint(null, port) 抛参数异常，自动搜房失效。
        /// 用 MonoMod 给这两个方法打空安全补丁（同一机制在本工程离线回归里已实证可用；
        /// Windows 上原实现正常则原样透传，无副作用）。
        /// </summary>
        private static void InstallLidgrenCompatFix(Assembly lidgren)
        {
            try
            {
                var netUtility = lidgren.GetType("Lidgren.Network.NetUtility");
                if (netUtility == null)
                {
                    return;
                }

                // 补丁 1：MAC 字节空安全（只参与生成 peer 唯一 id 的哈希，固定字节即可）
                var mac = netUtility.GetMethod("GetMacAddressBytes",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (mac != null)
                {
                    // "PGVZON" 的 ASCII
                    byte[] fallback = { 0x50, 0x47, 0x56, 0x5A, 0x4F, 0x4E };
                    Func<Func<byte[]>, byte[]> macHook = orig =>
                    {
                        try
                        {
                            byte[] bytes = orig();
                            return bytes != null && bytes.Length > 0 ? bytes : fallback;
                        }
                        catch
                        {
                            return fallback;
                        }
                    };
                    HookEndpointManager.Add(mac, macHook);
                    ModEnv.Log("已安装 Lidgren 兼容补丁（GetMacAddressBytes 空安全）");
                }

                // 补丁 2：广播地址空安全（失败时退回 255.255.255.255 全网广播）
                var broadcast = netUtility.GetMethod("GetBroadcastAddress",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (broadcast != null)
                {
                    Func<Func<System.Net.IPAddress>, System.Net.IPAddress> bcHook = orig =>
                    {
                        try
                        {
                            return orig() ?? System.Net.IPAddress.Broadcast;
                        }
                        catch
                        {
                            return System.Net.IPAddress.Broadcast;
                        }
                    };
                    HookEndpointManager.Add(broadcast, bcHook);
                    ModEnv.Log("已安装 Lidgren 兼容补丁（GetBroadcastAddress 空安全）");
                }
            }
            catch (Exception ex)
            {
                ModEnv.Log("Lidgren 兼容补丁安装失败: " + ex.Message);
            }
        }
    }
}
