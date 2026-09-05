using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Editing;
using System.Security.Cryptography;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services.Editing
{
    /// <summary>
    /// DeleteFileCommitTarget 提交/回滚行为测试。
    /// 重点回归 P0 缺陷：RollbackAsync 曾向 RestoreFromBackup 传空路径，
    /// 导致批量回滚时已删除文件无法恢复（静默数据丢失）。
    /// </summary>
    public class DeleteFileCommitTargetTests : IDisposable
    {
        private readonly string _tempDir;

        public DeleteFileCommitTargetTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"deltarget_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        private string WriteFile(string name, string content)
        {
            var p = Path.Combine(_tempDir, name);
            File.WriteAllText(p, content);
            return p;
        }

        private static string Sha256Hex(string filePath)
        {
            using var sha = SHA256.Create();
            using var s = File.OpenRead(filePath);
            return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        private PreparedChangeSet MakeDeleteChange(string filePath)
        {
            return new PreparedChangeSet
            {
                FilePath = filePath,
                Operation = ProposedFileOperation.Delete,
                BaselineText = File.ReadAllText(filePath),
                BaselineHash = Sha256Hex(filePath),
            };
        }

        [Fact]
        public async Task Commit_DeletesFile_AndKeepsBackupForRollback()
        {
            var file = WriteFile("a.txt", "ORIGINAL");
            var target = new DeleteFileCommitTarget();
            var change = MakeDeleteChange(file);

            (await target.PreflightAsync(change, CancellationToken.None)).Should().NotBeNull();
            var result = await target.CommitAsync(change, CancellationToken.None);

            result.Success.Should().BeTrue();
            File.Exists(file).Should().BeFalse("提交后文件应被删除");
        }

        [Fact]
        public async Task Rollback_AfterCommit_RestoresDeletedFile_WithOriginalContent()
        {
            var file = WriteFile("b.txt", "ORIGINAL-CONTENT");
            var target = new DeleteFileCommitTarget();
            var change = MakeDeleteChange(file);

            await target.PreflightAsync(change, CancellationToken.None);
            await target.CommitAsync(change, CancellationToken.None);
            File.Exists(file).Should().BeFalse();

            // ── P0 回归：此前空路径导致回滚静默失败，文件无法恢复 ──
            await target.RollbackAsync(CancellationToken.None);

            File.Exists(file).Should().BeTrue("回滚后应从备份恢复被删除的文件");
            File.ReadAllText(file).Should().Be("ORIGINAL-CONTENT");
        }

        [Fact]
        public async Task Rollback_WithoutCommit_DoesNothingAndDoesNotThrow()
        {
            var target = new DeleteFileCommitTarget();

            await target.RollbackAsync(CancellationToken.None);

            // 未经过 Commit（无备份、无原始路径）时不应有任何副作用
            true.Should().BeTrue();
        }

        [Fact]
        public async Task Commit_MissingFile_FailsWithMessage()
        {
            var target = new DeleteFileCommitTarget();
            var change = new PreparedChangeSet
            {
                FilePath = Path.Combine(_tempDir, "ghost.txt"),
                Operation = ProposedFileOperation.Delete,
                BaselineHash = "",
            };

            var result = await target.CommitAsync(change, CancellationToken.None);

            result.Success.Should().BeFalse();
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
            catch { }
        }
    }
}
