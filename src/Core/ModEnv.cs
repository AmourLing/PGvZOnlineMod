using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace PGvZOnlineMod.Core
{
    /// <summary>
    /// 一台跨互联网中继服务器（联机页左列的一项）。
    /// "局域网"那条不在此列——它走广播与扫段，不依赖任何服务器。
    /// </summary>
    public class ServerEntry
    {
        public string Name = "";
        /// <summary>IP 或域名。中继走 UDP，不是 wss/http，这里只写主机部分。</summary>
        public string Host = "";
        /// <summary>中继的控制端口（转发端口由服务端在应答里给出）。</summary>
        public int Port = 27270;
    }

    /// <summary>
    /// 联机配置（mods/PGvZOnlineMod/联机配置.json）。读不到/损坏时回退默认值并重建。
    /// 显示名不在这里——用游戏自己的玩家名 mPlayerInfo.mName，联机页不再单独设昵称。
    /// </summary>
    public class OnlineConfig
    {
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

        /// <summary>玩家自己添加的中继服务器（联机页左列，"局域网"之外的那些）。</summary>
        public List<ServerEntry> Servers = new List<ServerEntry>();

        /// <summary>
        /// 默认中继是否已经给过一次。用这个标记而不是"列表空不空"：
        /// 玩家用 × 删掉默认中继后，下次启动不该又冒出来（按"空就补"就会）。
        /// </summary>
        public bool DefaultRelayOffered;
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

        /// <summary>作者自建的中继（阿里云上海）。装上就在联机页左列能直接选中，不必先手填。</summary>
        public const string DefaultRelayName = "自家中继（上海）";
        public const string DefaultRelayHost = "47.116.78.238";
        public const int DefaultRelayPort = 27270;

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
            string raw = null;
            try
            {
                if (File.Exists(path))
                {
                    existed = true;
                    raw = File.ReadAllText(path);
                    _config = JsonConvert.DeserializeObject<OnlineConfig>(raw);
                }
            }
            catch (Exception ex)
            {
                Log("配置读取失败，使用默认值: " + ex.Message);
            }
            _config ??= new OnlineConfig();
            // 服务器列表可能来自玩家手改的 JSON：先去掉首尾空白（粘一个 " 1.2.3.4 " 会让
            // Dns 解析直接失败），脏条目丢掉，最多留 8 台（左列画得下），端口越界回落
            _config.Servers ??= new List<ServerEntry>();
            foreach (var s in _config.Servers)
            {
                if (s == null)
                {
                    continue;
                }
                s.Host = (s.Host ?? "").Trim();
                s.Name = (s.Name ?? "").Trim();
            }
            _config.Servers.RemoveAll(s => s == null || s.Host.Length == 0);
            if (_config.Servers.Count > 8)
            {
                _config.Servers.RemoveRange(8, _config.Servers.Count - 8);
            }
            foreach (var s in _config.Servers)
            {
                if (s.Port < 1024 || s.Port > 65535)
                {
                    s.Port = 27270;
                }
                if (s.Name.Length == 0)
                {
                    s.Name = s.Host;
                }
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
            // 默认中继：作者自己那台服务器，装上就能在联机页直接选中，不必先手填。
            // 用 DefaultRelayOffered 标记只给一次，而不是"列表空就补"——
            // 后者会让玩家用 × 删掉它之后，下次启动又冒出来。
            // 列表里已经有这台（发布模板自带）时也不重复加。
            bool seeded = false;
            if (!_config.DefaultRelayOffered)
            {
                _config.DefaultRelayOffered = true;
                seeded = true;
                bool has = false;
                foreach (var s in _config.Servers)
                {
                    if (s != null && s.Host == DefaultRelayHost)
                    {
                        has = true;
                        break;
                    }
                }
                if (!has)
                {
                    _config.Servers.Add(new ServerEntry
                    {
                        Name = DefaultRelayName, Host = DefaultRelayHost, Port = DefaultRelayPort,
                    });
                }
            }
            if (!existed || seeded)
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
