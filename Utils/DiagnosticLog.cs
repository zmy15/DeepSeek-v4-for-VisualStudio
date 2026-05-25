using System;
using System.IO;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// 独立于 Logger 的诊断日志，直接写入文件，零依赖。
    /// 用于 Package 初始化早期阶段（Logger 尚未就绪时）的故障排查。
    /// 
    /// 日志路径: %LocalAppData%\DeepSeekVS\diagnostic-{yyyy-MM-dd}.log
    /// 与 Logger 共享同一目录，但使用独立的 diagnostic- 前缀文件名。
    /// </summary>
    public static class DiagnosticLog
    {
        private static readonly string LogDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekVS");

        private static readonly object _lock = new();
        private static bool _directoryEnsured;

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
    }
}
