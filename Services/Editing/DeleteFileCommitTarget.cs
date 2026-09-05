using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Editing
{
    /// <summary>
    /// 删除文件的提交目标。
    /// 只在用户确认后才备份并删除，撤销时文件不受影响。
    /// </summary>
    public sealed class DeleteFileCommitTarget : IProposalCommitTarget
    {
        private string? _backupPath;

        /// <summary>
        /// 被删除文件的原始路径。RollbackAsync 需要它把备份还原回原位置 ——
        /// 此前直接传空串导致 File.Copy 抛异常被吞、删除无法撤销（P0 数据丢失缺陷）。
        /// </summary>
        private string? _originalPath;

        public Task<PreflightResult> PreflightAsync(
            PreparedChangeSet change, CancellationToken cancellationToken)
        {
            if (!File.Exists(change.FilePath))
            {
                return Task.FromResult(PreflightResult.Fail(
                    "目标文件已不存在。", ConflictLevel.FileDeleted));
            }

            // 校验文件哈希以确保未被外部修改
            if (!string.IsNullOrEmpty(change.BaselineHash))
            {
                try
                {
                    string diskHash = ComputeSha256(change.FilePath);
                    if (!string.Equals(diskHash, change.BaselineHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(PreflightResult.Fail(
                            "文件在预览期间已被外部修改。", ConflictLevel.ContentChanged));
                    }
                }
                catch (Exception ex)
                {
                    return Task.FromResult(PreflightResult.Fail(
                        $"无法读取目标文件: {ex.Message}", ConflictLevel.ContentChanged));
                }
            }

            return Task.FromResult(PreflightResult.Ok());
        }

        public async Task<ApplyResult> CommitAsync(
            PreparedChangeSet change, CancellationToken cancellationToken)
        {
            try
            {
                _originalPath = change.FilePath;

                // 1. 创建备份
                _backupPath = BackupService.CreateBackup(change.FilePath);
                if (_backupPath == null)
                {
                    return ApplyResult.Failed(change.FilePath, "无法创建备份文件");
                }

                // 2. 删除
                await Task.Run(() => File.Delete(change.FilePath), cancellationToken);

                Logger.Info($"[DeleteFile] 已删除: {Path.GetFileName(change.FilePath)}");
                return ApplyResult.Ok(change.FilePath);
            }
            catch (Exception ex)
            {
                Logger.Error($"[DeleteFile] 删除失败: {change.FilePath} — {ex.Message}", ex);

                // 失败恢复
                if (_backupPath != null)
                {
                    BackupService.RestoreFromBackup(change.FilePath, _backupPath);
                    _backupPath = null;
                }

                return ApplyResult.Failed(change.FilePath, ex.Message);
            }
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (_backupPath != null)
            {
                if (string.IsNullOrEmpty(_originalPath))
                {
                    Logger.Error("[DeleteFile] 回滚失败：缺少原始路径（未经过 Commit 阶段），备份保留: " + _backupPath);
                }
                else
                {
                    BackupService.RestoreFromBackup(_originalPath, _backupPath);
                    Logger.Info("[DeleteFile] 已回滚删除: " + _originalPath);
                }
                _backupPath = null;
            }

            return Task.CompletedTask;
        }

        private static string ComputeSha256(string filePath)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hashBytes = sha256.ComputeHash(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }
}
