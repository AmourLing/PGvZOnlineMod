using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace PGvZOnlineMod.Core
{
    /// <summary>
    /// 联机配置（mods/PGvZOnlineMod/联机配置.json）。读不到/损坏时回退默认值并重建。
    /// </summary>
    public class OnlineConfig
    {
        public string Nickname = "玩家";
        public int HostPort = 27150;
        public string LastIp = "127.0.0.1";
        /// <summary>上次手动加入时用的端口（默认与建房端口一致）。</summary>
        public int LastPort = 27150;
        /// <summary>快照频率（Hz），Host 每 1/该值 秒发一帧。</summary>
        public int SnapshotHz = 20;
        /// <summary>远端光标同步频率（Hz）。</summary>
        public int CursorHz = 10;
        /// <summary>
        /// 按人数加压：每多一名玩家给僵尸血量追加的比例（0.35 = 三人局僵尸血量 1.7 倍）。
        /// 0 = 关闭加压。仅主机侧生效（生成时放大，随生成清单下发给各端）。
        /// </summary>
        public float ZombieHpPerExtraPlayer = 0.35f;
    }

    /// <summary>
    /// 日志 / 配置 / 路径。数据目录取模组 DLL 所在目录下的 PGvZOnlineMod/ 子目录
    /// （即 mods/PGvZOnlineMod/），桌面端可写。
    /// </summary>
    public static class ModEnv
    {
        private static readonly object _logLock = new();
        private static OnlineConfig _config;
        private static string _dataDir;
        private static string _logPath;
        private static string _lastOnceMessage;
        private static readonly int _pid = Process.GetCurrentProcess().Id;
        private const int MaxLogFiles = 20;

        /// <summary>每次启动生成新日志文件：Logs/联机_时间_P进程.log（自动清理，仅保留最近 20 个）。</summary>
        private static string ResolveLogPath()
        {
            string dir = Path.Combine(DataDir, "Logs");
            Directory.CreateDirectory(dir);
            try
            {
                var oldLogs = new DirectoryInfo(dir)
                    .GetFiles("联机_*.log")
                    .OrderBy(f => f.CreationTime)
                    .ToList();
                while (oldLogs.Count >= MaxLogFiles)
                {
                    oldLogs[0].Delete();
                    oldLogs.RemoveAt(0);
                }
            }
            catch
            {
            }
            return Path.Combine(dir, "联机_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_P" + _pid + ".log");
        }

        public static string DataDir
        {
            get
            {
                if (_dataDir != null)
                {
                    return _dataDir;
                }
                try
                {
                    string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    _dataDir = Path.Combine(string.IsNullOrEmpty(modDir) ? "." : modDir, "PGvZOnlineMod");
                }
                catch
                {
                    _dataDir = Path.Combine(".", "PGvZOnlineMod");
                }
                return _dataDir;
            }
        }

        public static OnlineConfig GetConfig()
        {
            if (_config != null)
            {
                return _config;
            }
            string path = Path.Combine(DataDir, "联机配置.json");
            bool existed = false;
            try
            {
                if (File.Exists(path))
                {
                    existed = true;
                    _config = JsonConvert.DeserializeObject<OnlineConfig>(File.ReadAllText(path));
                }
            }
            catch (Exception ex)
            {
                Log("配置读取失败，使用默认值: " + ex.Message);
            }
            _config ??= new OnlineConfig();
            if (string.IsNullOrWhiteSpace(_config.Nickname))
            {
                _config.Nickname = "玩家";
            }
            if (_config.HostPort is < 1024 or > 65535)
            {
                _config.HostPort = 27150;
            }
            if (_config.SnapshotHz is < 5 or > 60)
            {
                _config.SnapshotHz = 20;
            }
            if (_config.CursorHz is < 2 or > 60)
            {
                _config.CursorHz = 10;
            }
            // NaN/越界都要收：血量倍率参与生成，脏值会造出无敌僵尸
            if (float.IsNaN(_config.ZombieHpPerExtraPlayer) || _config.ZombieHpPerExtraPlayer < 0f
                || _config.ZombieHpPerExtraPlayer > 3f)
            {
                _config.ZombieHpPerExtraPlayer = 0.35f;
            }
            if (!existed)
            {
                // 首次运行自动落一份默认配置，玩家不用先进一次联机才能找到文件
                SaveConfig();
            }
            return _config;
        }

        public static void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                string path = Path.Combine(DataDir, "联机配置.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(GetConfig(), Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log("配置保存失败: " + ex.Message);
            }
        }

        public static void Log(string message)
        {
            try
            {
                lock (_logLock)
                {
                    if (_logPath == null)
                    {
                        _logPath = ResolveLogPath();
                    }
                    File.AppendAllText(_logPath,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "][P" + _pid + "] " + message + Environment.NewLine);
                }
            }
            catch
            {
                // 日志失败不能影响游戏
            }

            // 带控制台的游戏版本（Lawn.Console.exe）可见；普通版本无副作用
            try
            {
                Sexy.Debug.Log(Sexy.DebugType.Info, "[PGvZOnline] " + message);
            }
            catch
            {
            }
        }

        /// <summary>
        /// 去重日志：同一条消息只落一次。给每帧执行的热路径 catch 用，
        /// 避免 mod 处于坏状态时每帧刷一条（曾刷出 1.7 万行）。
        /// </summary>
        public static void LogOnce(string message)
        {
            if (_lastOnceMessage == message)
            {
                return;
            }
            _lastOnceMessage = message;
            Log(message);
        }
    }
}
