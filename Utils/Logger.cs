using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

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

        /// <summary>当前日志输出级别。通过 Options 页面或代码设置。</summary>
        public static LogLevel Level = LogLevel.Info;

        /// <summary>是否同时写入 VS 输出窗口（调试时开启）。</summary>
        public static bool WriteToOutputWindow = false;

        private static readonly string LogDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "DeepSeekVS");

        private static readonly object _cleanupLock = new object();
        private static DateTime _lastCleanupDate = DateTime.MinValue;
        private static bool _logDirectoryEnsured;

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

        public static void Error(string message, Exception? ex = null, [CallerMemberName] string? member = null)
            => Log("ERROR", $"{message} {ex?.Message}", member, LogLevel.Error);

        /// <summary>输出调试日志（仅 LogLevel ≤ Debug 时输出）。</summary>
        public static void Debug(string message, [CallerMemberName] string? member = null)
            => Log("DEBUG", message, member, LogLevel.Debug);

        /// <summary>输出追踪日志（仅 LogLevel ≤ Trace 时输出）。</summary>
        public static void Trace(string message, [CallerMemberName] string? member = null)
            => Log("TRACE", message, member, LogLevel.Trace);

        private static void Log(string level, string message, string? member, LogLevel minLevel)
        {
            // ── 级别过滤 ──
            if (Level == LogLevel.Off) return;
            if (Level > minLevel) return;

            string log = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{member}] {message}";
            System.Diagnostics.Debug.WriteLine(log);

            // ── VS 输出窗口（调试时实时可见）──
            if (WriteToOutputWindow)
            {
                try
                {
                    // 使用反射调用避免 JIT 编译时解析 EnvDTE 程序集
                    var pane = GetOutputWindowPane();
                    if (pane != null)
                    {
                        var method = pane.GetType().GetMethod("OutputString");
                        method?.Invoke(pane, new object[] { log + Environment.NewLine });
                    }
                }
                catch { }
            }

            try
            {
                EnsureLogDirectory();
                File.AppendAllText(GetLogFilePath(), log + Environment.NewLine);

                // 每天只执行一次过期清理
                if (_lastCleanupDate < DateTime.Today)
                {
                    lock (_cleanupLock)
                    {
                        if (_lastCleanupDate < DateTime.Today)
                        {
                            CleanOldLogs();
                            _lastCleanupDate = DateTime.Today;
                        }
                    }
                }
            }
            catch { /* 写入失败不影响主流程 */ }
        }

        #region VS Output Window

        private static object? _outputPane;
        private static bool _outputPaneResolved;

        /// <summary>获取 VS 输出窗口的自定义窗格（用于实时调试日志）。</summary>
        private static object? GetOutputWindowPane()
        {
            if (_outputPaneResolved) return _outputPane;

            try
            {
                // 使用反射访问 EnvDTE，避免 JIT 编译时强制解析 EnvDTE 程序集
                var dteType = Type.GetType("EnvDTE.DTE, EnvDTE, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
                if (dteType == null)
                {
                    // 尝试替代强名称版本
                    dteType = Type.GetType("EnvDTE.DTE, EnvDTE");
                }
                if (dteType == null) { _outputPaneResolved = true; return null; }

                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(dteType);
                if (dte == null) { _outputPaneResolved = true; return null; }

                // dte.Windows.Item(vsWindowKindOutput)
                var windowsProp = dteType.GetProperty("Windows");
                if (windowsProp == null) { _outputPaneResolved = true; return null; }
                var windows = windowsProp.GetValue(dte, null);

                var windowsType = windows.GetType();
                var itemMethod = windowsType.GetMethod("Item");
                if (itemMethod == null) { _outputPaneResolved = true; return null; }

                // EnvDTE.Constants.vsWindowKindOutput = "{34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3}"
                var window = itemMethod.Invoke(windows, new object[] { "{34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3}" });

                var objectProp = window?.GetType().GetProperty("Object");
                var outputWin = objectProp?.GetValue(window, null);
                if (outputWin == null) { _outputPaneResolved = true; return null; }

                // 获取 OutputWindowPanes 集合
                var panesProp = outputWin.GetType().GetProperty("OutputWindowPanes");
                var panes = panesProp?.GetValue(outputWin, null);
                if (panes == null) { _outputPaneResolved = true; return null; }

                // 查找 "DeepSeek" 窗格
                var enumerator = (System.Collections.IEnumerable)panes;
                object paneResult = null;
                foreach (object p in enumerator)
                {
                    var nameProp = p.GetType().GetProperty("Name");
                    var name = nameProp?.GetValue(p, null) as string;
                    if (name == "DeepSeek")
                    {
                        paneResult = p;
                        break;
                    }
                }

                // 如果未找到则创建
                if (paneResult == null)
                {
                    var addMethod = panes.GetType().GetMethod("Add");
                    if (addMethod != null)
                        paneResult = addMethod.Invoke(panes, new object[] { "DeepSeek" });
                }

                if (paneResult != null)
                {
                    paneResult.GetType().GetMethod("Activate")?.Invoke(paneResult, null);
                    _outputPane = paneResult;
                }
            }
            catch { /* DTE 不可用时静默失败 */ }
            _outputPaneResolved = true;
            return _outputPane;
        }

        #endregion

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