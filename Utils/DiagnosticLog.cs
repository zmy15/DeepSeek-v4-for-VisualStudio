using System;
using System.IO;
using System.Linq;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// 独立于 Logger 的诊断日志，直接写入文件，零依赖。
    /// 用于 Package 初始化早期阶段（Logger 尚未就绪时）的故障排查。
    /// 
    /// 日志路径: %LocalAppData%\DeepSeekVS\diagnostic-{yyyy-MM-dd}.log
    /// 与 Logger 共享同一目录，但使用独立的 diagnostic- 前缀文件名。
    /// 日志文件保留 14 天，超过保留期的文件会在写入时自动清理。
    /// </summary>
    public static class DiagnosticLog
    {
        private const int DiagnosticLogRetentionDays = 14;

        private static readonly string LogDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekVS");

        private static readonly object _lock = new();
        private static readonly object _cleanupLock = new();
        private static bool _directoryEnsured;
        private static DateTime _lastCleanupDate = DateTime.MinValue;

        /// <summary>
        /// 写入一条诊断日志。同时输出到 Debug.WriteLine 以便 DebugView 捕获。
        /// 所有异常静默忽略，确保诊断日志写入失败不影响主流程。
        /// </summary>
        public static void Write(string message)
        {
            // Debug 输出始终保留，方便开发时用 DebugView 实时查看
            System.Diagnostics.Debug.WriteLine(message);

            try
            {
                EnsureDirectory();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
                lock (_lock)
                {
                    File.AppendAllText(GetFilePath(), line + Environment.NewLine);
                }

                CleanupOldLogsIfNeeded();
            }
            catch
            {
                // 诊断日志写入失败不能影响主流程
            }
        }

        private static void EnsureDirectory()
        {
            if (_directoryEnsured) return;
            lock (_lock)
            {
                if (_directoryEnsured) return;
                Directory.CreateDirectory(LogDirectory);
                _directoryEnsured = true;
            }
        }

        private static string GetFilePath()
        {
            return Path.Combine(LogDirectory, $"diagnostic-{DateTime.Now:yyyy-MM-dd}.log");
        }

        private static void CleanupOldLogsIfNeeded()
        {
            var today = DateTime.Today;
            var lastCleanup = _lastCleanupDate;
            if (lastCleanup >= today) return;

            lock (_cleanupLock)
            {
                if (_lastCleanupDate >= today) return;

                CleanupOldLogs();
                _lastCleanupDate = today;
            }
        }

        private static void CleanupOldLogs()
        {
            try
            {
                var cutoff = DateTime.Today.AddDays(-DiagnosticLogRetentionDays);

                var oldFiles = Directory.GetFiles(LogDirectory, "diagnostic-*.log")
                    .Select(file => new FileInfo(file))
                    .Where(file => file.LastWriteTime < cutoff);

                foreach (var file in oldFiles)
                {
                    try
                    {
                        file.Delete();
                        System.Diagnostics.Debug.WriteLine($"[DiagnosticLog] 已清理过期日志: {file.Name}");
                    }
                    catch
                    {
                        // 单个文件删除失败不影响其他文件清理
                    }
                }
            }
            catch
            {
                // 清理失败不影响诊断日志写入
            }
        }
    }
}
