using System;
using System.Reflection;
using MonoMod.RuntimeDetour.HookGen;

namespace PGvZOnlineMod.Hooks
{
    /// <summary>
    /// Hook 安装入口。与游戏自身的 LawnMod.MonoModUtils.HookTo 同源
    /// （底层都是 MonoMod.RuntimeDetour.HookGen.HookEndpointManager），可与其他 mod 钩子共存。
    /// 委托签名固定 (orig, self, args...)。重复安装幂等。
    /// </summary>
    internal static class HookInstaller
    {
        private static bool _installed;

        /// <summary>按名字+精确参数类型取方法（DeclaredOnly，避开重载绑错）。</summary>
        internal static MethodInfo M(Type type, string name, Type[] args)
        {
            var m = type.GetMethod(name,
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null, args, null);
            if (m == null)
            {
                throw new MissingMethodException(type.FullName, name);
            }
            return m;
        }

        public static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            GameHooks.Install();
        }
    }
}
