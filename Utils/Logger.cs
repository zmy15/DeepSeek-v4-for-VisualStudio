using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// 日志输出级别（从低到高）。
    /// </summary>
    public enum LogLevel
    {
        /// <summary>输出所有日志（最详细）</summary>
        Trace,
        /// <summary>调试信息 + Info/Warn/Error</summary>
        Debug,
        /// <summary>常规信息 + Warn/Error</summary>
        Info,
        /// <summary>警告 + Error</summary>
        Warn,
        /// <summary>仅错误</summary>
        Error,
        /// <summary>关闭日志输出</summary>
        Off,
    }

    public static class Logger
    {
        /// <summary>日志保留天数，超过此天数的日志文件将被自动清理</summary>
        private const int LogRetentionDays = 14;

        /// <summary>
        /// 输出窗口窗格 GUID，用于 IVsOutputWindow.CreatePane/GetPane。
        /// </summary>
        private static readonly Guid PaneGuid = new Guid("{E5F8A1B2-C3D4-4E6F-8A9B-0C1D2E3F4A5B}");

        // ── 线程安全的配置字段 ──

        private static volatile LogLevel _level = LogLevel.Info;
        /// <summary>当前日志输出级别。通过 Options 页面或代码设置。</summary>
        public static LogLevel Level
        {
            get => _level;
            set => _level = value;
        }

        private static volatile bool _writeToOutputWindow = true;
        /// <summary>是否同时写入 VS 输出窗口。默认开启，可在选项页面关闭。</summary>
        public static bool WriteToOutputWindow
        {
            get => _writeToOutputWindow;
            set => _writeToOutputWindow = value;
        }

        private static readonly string LogDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "DeepSeekVS");

        private static readonly object _cleanupLock = new object();
        // _lastCleanupDate 的读写由 _cleanupLock 保护（DateTime 不能声明为 volatile）
        private static DateTime _lastCleanupDate = DateTime.MinValue;
        private static bool _logDirectoryEnsured;

        // ── IVsOutputWindow 窗格（线程安全延迟初始化）──

        private static readonly object _paneLock = new object();
        private static volatile IVsOutputWindowPane? _pane;
        private static volatile bool _paneResolved;

        /// <summary>
        /// 初始化日志系统（输出窗口窗格、日志目录）。
        /// 必须在 VS 主线程调用，建议在 Package.InitializeAsync 中调用。
        /// </summary>
        /// <param name="serviceProvider">VS 服务提供程序（通常是 AsyncPackage 实例）。</param>
        public static void Initialize(IServiceProvider serviceProvider)
        {
            // 确保日志目录存在
            EnsureLogDirectory();

            // 初始化输出窗口窗格
            lock (_paneLock)
            {
                if (_paneResolved) return;

                try
                {
                    var outWindow = serviceProvider.GetService(typeof(SVsOutputWindow)) as IVsOutputWindow;
                    if (outWindow == null)
                    {
                        _paneResolved = true;
                        return;
                    }

                    var guid = PaneGuid; // 本地副本，ref 参数需要
                    outWindow.CreatePane(ref guid, "DeepSeek Chat", 1, 1);
                    outWindow.GetPane(ref guid, out _pane);
                }
                catch { /* VS 输出窗口不可用时静默失败 */ }

                _paneResolved = true;
            }
        }

        /// <summary>
        /// 确保日志目录存在。使用延迟初始化替代静态构造函数，
        /// 避免在 OS 加载程序锁内执行 I/O 操作触发 LoaderLock MDA。
        /// </summary>
        private static void EnsureLogDirectory()
        {
            if (_logDirectoryEnsured) return;
            lock (_cleanupLock)
            {
                if (_logDirectoryEnsured) return;
                try
                {
                    Directory.CreateDirectory(LogDirectory);
                }
                catch { /* 忽略目录创建错误 */ }
                _logDirectoryEnsured = true;
            }
        }

        /// <summary>获取当天的日志文件路径（按日期区分：extension-2026-05-13.log）</summary>
        private static string GetLogFilePath()
        {
            string date = DateTime.Now.ToString("yyyy-MM-dd");
            return Path.Combine(LogDirectory, $"extension-{date}.log");
        }

        public static void Info(string message, [CallerMemberName] string? member = null)
            => Log("INFO", message, member, LogLevel.Info);
        public static void Warn(string message, [CallerMemberName] string? member = null)
            => Log("WARN", message, member, LogLevel.Warn);

        /// <summary>输出错误日志，包含完整异常信息（消息 + 堆栈跟踪）。</summary>
        public static void Error(string message, Exception? ex = null, [CallerMemberName] string? member = null)
        {
            string fullMessage = ex != null
                ? $"{message} | Exception: {ex}"
                : message;
            Log("ERROR", fullMessage, member, LogLevel.Error);
        }

        /// <summary>输出调试日志（仅 LogLevel ≤ Debug 时输出）。</summary>
        public static void Debug(string message, [CallerMemberName] string? member = null)
            => Log("DEBUG", message, member, LogLevel.Debug);

        /// <summary>输出追踪日志（仅 LogLevel ≤ Trace 时输出）。</summary>
        public static void Trace(string message, [CallerMemberName] string? member = null)
            => Log("TRACE", message, member, LogLevel.Trace);

        private static void Log(string level, string message, string? member, LogLevel minLevel)
        {
            // ── 级别过滤（volatile 读取保证跨线程可见性）──
            var currentLevel = _level;
            if (currentLevel == LogLevel.Off) return;
            if (currentLevel > minLevel) return;

            string log = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{member}] {message}";
            System.Diagnostics.Debug.WriteLine(log);

            // ── VS 输出窗口（通过 IVsOutputWindowPane，无反射）──
            if (_writeToOutputWindow && _pane != null)
            {
                try
                {
                    _pane.OutputString(log + Environment.NewLine);
                }
                catch { /* 输出窗口写入失败不影响主流程 */ }
            }

            // ── 文件日志 ──
            try
            {
                EnsureLogDirectory();
                File.AppendAllText(GetLogFilePath(), log + Environment.NewLine);

                // 每天只执行一次过期清理
                var lastCleanup = _lastCleanupDate;
                var today = DateTime.Today;
                if (lastCleanup < today)
                {
                    lock (_cleanupLock)
                    {
                        if (_lastCleanupDate < today)
                        {
                            CleanOldLogs();
                            _lastCleanupDate = today;
                        }
                    }
                }
            }
            catch { /* 写入失败不影响主流程 */ }
        }

        /// <summary>清理超过保留期限的日志文件</summary>
        private static void CleanOldLogs()
        {
            try
            {
                DateTime cutoff = DateTime.Today.AddDays(-LogRetentionDays);

                var oldFiles = Directory.GetFiles(LogDirectory, "extension-*.log")
                    .Select(f => new FileInfo(f))
                    .Where(fi => fi.LastWriteTime < cutoff);

                foreach (var file in oldFiles)
                {
                    try
                    {
                        file.Delete();
                        System.Diagnostics.Debug.WriteLine($"[Logger] 已清理过期日志: {file.Name}");
                    }
                    catch
                    {
                        // 单个文件删除失败不影响其他文件清理
                    }
                }
            }
            catch
            {
                // 清理失败不影响日志写入
            }
        }
    }
}