using System;
using System.Reflection;
using IronPython.Runtime;
using PGvZOnlineMod;

[assembly: PythonModule("PGvZOnlineMod", typeof(PGvZOnlineMod.PythonModule))]

namespace PGvZOnlineMod
{
    /// <summary>
    /// 游戏的 IronPython 模块加载器（IronPyInteractive.RunAllModules）会把 mods/ 顶层的 DLL
    /// LoadFrom + LoadAssembly + ImportModule(程序集名)。带 [PythonModule] 属性的程序集被
    /// import 时，IronPython 反射调用 PerformModuleReload —— 这里就是联机 mod 的入口
    /// （工程约定与 植物娘AI对话/CSMod 一致）。
    /// </summary>
    public static class PythonModule
    {
        private static int _installCount;

        public static bool IsInstalled => _installCount > 0;

        public static string Status { get; private set; } = "not installed";

        public static void PerformModuleReload(PythonContext context, PythonDictionary dictionary)
        {
            try
            {
                // 1) 先加载依赖（Android 版游戏不带 Lidgren/Newtonsoft，用模组自带副本）；
                //    必须先于任何触碰这两个库类型的代码，否则 JIT 抛 TypeLoadException
                Core.NetAsm.EnsureDependenciesLoaded();
                // 2) 物化配置与数据目录（首次运行自动写默认 联机配置.json）
                var config = Core.ModEnv.GetConfig();
                Core.ModEnv.Log("配置已就绪: " + Core.ModEnv.DataDir + "，端口=" + config.HostPort
                    + "，已存服务器 " + (config.Servers?.Count ?? 0) + " 台");
                Hooks.HookInstaller.Install();
                Status = "installed (repeat #" + Interlocked.Increment(ref _installCount) + ")";
            }
            catch (Exception ex)
            {
                Status = "install failed: " + ex;
            }
            Core.ModEnv.Log("PGvZOnlineMod " + Status);
        }
    }
}
