using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    public static class Logger
    {
        /// <summary>日志保留天数，超过此天数的日志文件将被自动清理</summary>
        private const int LogRetentionDays = 14;

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
            => Log("INFO", message, member);
        public static void Warn(string message, [CallerMemberName] string? member = null)
            => Log("WARN", message, member);

        public static void Error(string message, Exception? ex = null, [CallerMemberName] string? member = null)
            => Log("ERROR", $"{message} {ex?.Message}", member);

        private static void Log(string level, string message, string? member)
        {
            string log = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{member}] {message}";
            Debug.WriteLine(log);
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
                        Debug.WriteLine($"[Logger] 已清理过期日志: {file.Name}");
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