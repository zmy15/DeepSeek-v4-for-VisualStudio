using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 集中式文件备份服务。
    /// 所有编辑操作（apply_patch / replace_string_in_file / multi_replace_string_in_file /
    /// insert_edit_into_file / create_file）在修改文件前统一在此创建备份。
    /// 
    /// 备份存放路径: %LOCALAPPDATA%\DeepSeekVS\backups\{timestamp}\{path_hash}\{filename}
    /// 
    /// 使用方式:
    /// - BeginSession() — 开始一次编辑会话，创建时间戳目录
    /// - CreateBackup(filePath) — 备份单个文件，返回备份路径
    /// - RestoreFromBackup(filePath, backupPath) — 从备份恢复并删除备份
    /// - CleanupBackup(backupPath) — 删除备份文件
    /// - RollbackAll(backups) — 回滚所有备份
    /// - EndSession() — 结束会话，清理空目录
    /// </summary>
    public static class BackupService
    {
        private static readonly string BaseBackupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekVS", "backups");

        private static string? _currentSessionDir;
        private static readonly object _sessionLock = new();

        /// <summary>
        /// 获取当前会话的备份目录路径。
        /// </summary>
        public static string? CurrentSessionDir
        {
            get
            {
                lock (_sessionLock)
                    return _currentSessionDir;
            }
        }

        /// <summary>
        /// 开始一次编辑会话，创建以当前时间戳命名的子目录。
        /// 同一会话内的所有备份存放在同一目录下。
        /// </summary>
        public static void BeginSession()
        {
            lock (_sessionLock)
            {
                if (_currentSessionDir != null)
                    return; // 已有活跃会话

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _currentSessionDir = Path.Combine(BaseBackupDir, timestamp);
                Directory.CreateDirectory(_currentSessionDir);
                Logger.Info($"[BackupService] 备份会话开始: {_currentSessionDir}");
            }
        }

        /// <summary>
        /// 结束当前编辑会话。如果会话目录为空则删除。
        /// </summary>
        public static void EndSession()
        {
            lock (_sessionLock)
            {
                if (_currentSessionDir == null)
                    return;

                try
                {
                    // 如果会话目录为空（所有备份已被清理），删除它
                    if (Directory.Exists(_currentSessionDir))
                    {
                        var remaining = Directory.GetFileSystemEntries(_currentSessionDir);
                        if (remaining.Length == 0)
                        {
                            Directory.Delete(_currentSessionDir);
                            Logger.Info($"[BackupService] 备份会话结束（目录已清空）: {_currentSessionDir}");
                        }
                        else
                        {
                            Logger.Info($"[BackupService] 备份会话结束（保留 {remaining.Length} 个文件）: {_currentSessionDir}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[BackupService] 清理会话目录失败: {ex.Message}");
                }
                finally
                {
                    _currentSessionDir = null;
                }
            }
        }

        /// <summary>
        /// 创建文件的备份。备份存放于当前会话目录下的 {path_hash}/{filename}。
        /// 文件不存在则返回 null。异常静默捕获并记录日志。
        /// </summary>
        /// <param name="filePath">原始文件绝对路径</param>
        /// <returns>备份文件路径，失败返回 null</returns>
        public static string? CreateBackup(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;

                // 确保会话已开始
                lock (_sessionLock)
                {
                    if (_currentSessionDir == null)
                        BeginSession();
                }

                string sessionDir = _currentSessionDir!;

                // 使用文件完整路径的哈希作为子目录名，避免路径中的非法字符
                string pathHash = ComputePathHash(filePath);
                string fileBackupDir = Path.Combine(sessionDir, pathHash);
                Directory.CreateDirectory(fileBackupDir);

                string fileName = Path.GetFileName(filePath);
                string backupPath = Path.Combine(fileBackupDir, fileName);

                File.Copy(filePath, backupPath, overwrite: true);
                Logger.Info($"[BackupService] 已创建备份: {filePath} → {backupPath}");
                return backupPath;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BackupService] 创建备份失败: {filePath} — {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从备份恢复文件并删除备份文件和空目录。
        /// backupPath 为 null 或文件不存在则无操作。
        /// 异常静默捕获并记录日志，保留备份文件让用户手动恢复。
        /// </summary>
        public static void RestoreFromBackup(string filePath, string? backupPath)
        {
            if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
                return;

            try
            {
                File.Copy(backupPath, filePath, overwrite: true);
                Logger.Info($"[BackupService] 已从备份恢复: {filePath} ← {backupPath}");

                // 删除备份文件
                File.Delete(backupPath);
                Logger.Info($"[BackupService] 已删除备份文件: {backupPath}");

                // 尝试删除备份的父目录（如果为空）
                TryDeleteEmptyParentDir(backupPath);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BackupService] 从备份恢复失败: {filePath} ← {backupPath} — {ex.Message}（备份文件已保留，请手动恢复）");
            }
        }

        /// <summary>
        /// 删除备份文件及其空父目录。
        /// backupPath 为 null 或文件不存在则无操作。
        /// 异常静默捕获并记录日志。
        /// </summary>
        public static void CleanupBackup(string? backupPath)
        {
            if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
                return;

            try
            {
                File.Delete(backupPath);
                Logger.Info($"[BackupService] 已清理备份: {backupPath}");

                // 尝试删除备份的父目录（如果为空）
                TryDeleteEmptyParentDir(backupPath);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BackupService] 清理备份失败: {backupPath} — {ex.Message}");
            }
        }

        /// <summary>
        /// 遍历备份字典，恢复所有已备份的文件。
        /// 用于事务失败时的统一回滚。单个文件恢复失败不中断其他文件的恢复。
        /// </summary>
        public static void RollbackAll(Dictionary<string, string?> backups)
        {
            foreach (var kvp in backups)
            {
                try
                {
                    RestoreFromBackup(kvp.Key, kvp.Value);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[BackupService] 回滚失败: {kvp.Key} — {ex.Message}（备份文件已保留，请手动恢复）");
                }
            }
        }

        #region Private Helpers

        /// <summary>
        /// 计算文件路径的 SHA256 哈希（取前 12 位十六进制），用作子目录名。
        /// </summary>
        private static string ComputePathHash(string filePath)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(filePath));
                return BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 12);
            }
        }

        /// <summary>
        /// 尝试删除备份文件的父目录（如果为空）。
        /// </summary>
        private static void TryDeleteEmptyParentDir(string backupPath)
        {
            try
            {
                string? parentDir = Path.GetDirectoryName(backupPath);
                if (parentDir != null && Directory.Exists(parentDir))
                {
                    var entries = Directory.GetFileSystemEntries(parentDir);
                    if (entries.Length == 0)
                    {
                        Directory.Delete(parentDir);
                        Logger.Info($"[BackupService] 已清理空目录: {parentDir}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BackupService] 清理空目录失败: {ex.Message}");
            }
        }

        #endregion
    }
}
