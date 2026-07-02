using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// BackupService 单元测试 — 验证集中式备份服务的创建、恢复、清理、回滚等功能。
/// 备份路径: %LOCALAPPDATA%\DeepSeekVS\backups\{timestamp}\{hash}\{filename}
/// </summary>
public class BackupServiceTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        // ── 结束活动会话（清理静态状态）──
        BackupService.EndSession();

        foreach (var file in _tempFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        // ── 清理可能残留的会话目录 ──
        var baseBackupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekVS", "backups");
        if (Directory.Exists(baseBackupDir))
        {
            try
            {
                foreach (var subDir in Directory.GetDirectories(baseBackupDir))
                {
                    try { Directory.Delete(subDir, true); } catch { }
                }
            }
            catch { }
        }
    }

    #region Helpers

    private string CreateTempFile(string? content = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"backup_test_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content ?? $"Test content for backup - {Guid.NewGuid()}");
        _tempFiles.Add(path);
        return path;
    }

    private string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), $"backup_test_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private bool DirectoryIsEmpty(string path)
    {
        return Directory.GetFileSystemEntries(path).Length == 0;
    }

    #endregion

    #region Session Management

    [Fact]
    public void BeginSession_CreatesTimestampedDirectory()
    {
        BackupService.BeginSession();

        var sessionDir = BackupService.CurrentSessionDir;
        sessionDir.Should().NotBeNull();
        Directory.Exists(sessionDir).Should().BeTrue();

        // 路径应包含 DeepSeekVS\backups\
        sessionDir.Should().Contain("DeepSeekVS");
        sessionDir.Should().Contain("backups");
    }

    [Fact]
    public void BeginSession_CalledTwice_DoesNotChangeSessionDir()
    {
        BackupService.BeginSession();
        var firstDir = BackupService.CurrentSessionDir;

        BackupService.BeginSession();
        var secondDir = BackupService.CurrentSessionDir;

        secondDir.Should().Be(firstDir);
    }

    [Fact]
    public void EndSession_RemovesEmptySessionDirectory()
    {
        BackupService.BeginSession();
        var sessionDir = BackupService.CurrentSessionDir;
        sessionDir.Should().NotBeNull();

        BackupService.EndSession();

        // 空会话目录应被删除
        Directory.Exists(sessionDir).Should().BeFalse();
        BackupService.CurrentSessionDir.Should().BeNull();
    }

    [Fact]
    public void EndSession_WithoutBeginSession_DoesNotThrow()
    {
        // 确保没有活动的会话
        if (BackupService.CurrentSessionDir != null)
            BackupService.EndSession();

        // 直接调用 EndSession 不应抛出异常
        var act = () => BackupService.EndSession();
        act.Should().NotThrow();
    }

    #endregion

    #region CreateBackup

    [Fact]
    public void CreateBackup_ExistingFile_CreatesBackup()
    {
        var filePath = CreateTempFile("Original content for backup test");
        var originalContent = File.ReadAllText(filePath);

        var backupPath = BackupService.CreateBackup(filePath);

        backupPath.Should().NotBeNull();
        File.Exists(backupPath).Should().BeTrue();
        File.ReadAllText(backupPath).Should().Be(originalContent);
    }

    [Fact]
    public void CreateBackup_NonExistingFile_ReturnsNull()
    {
        var nonExistingPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.txt");

        var backupPath = BackupService.CreateBackup(nonExistingPath);

        backupPath.Should().BeNull();
    }

    [Fact]
    public void CreateBackup_AutoStartsSession_IfNotStarted()
    {
        // 确保没有活跃 session
        if (BackupService.CurrentSessionDir != null)
            BackupService.EndSession();

        var filePath = CreateTempFile("Auto-start session test");

        var backupPath = BackupService.CreateBackup(filePath);

        // 应自动创建了 session
        BackupService.CurrentSessionDir.Should().NotBeNull();
        backupPath.Should().NotBeNull();
    }

    [Fact]
    public void CreateBackup_PlacesBackupInSessionDirectory()
    {
        BackupService.BeginSession();
        var sessionDir = BackupService.CurrentSessionDir!;

        var filePath = CreateTempFile("Session dir test");
        var backupPath = BackupService.CreateBackup(filePath);

        backupPath.Should().StartWith(sessionDir);
    }

    [Fact]
    public void CreateBackup_UsesPathHashForSubdirectory()
    {
        var filePath = CreateTempFile("Hash test");

        var backupPath = BackupService.CreateBackup(filePath);

        // 备份文件的父目录名应是文件路径的 SHA256 前12位哈希
        var parentDir = Path.GetDirectoryName(backupPath);
        parentDir.Should().NotBeNull();
        var hashDirName = Path.GetFileName(parentDir);
        hashDirName.Should().NotBeNullOrEmpty();
        hashDirName.Length.Should().Be(12); // SHA256 前12位
    }

    [Fact]
    public void CreateBackup_SameFileTwice_OverwritesBackup()
    {
        var filePath = CreateTempFile("Content v1");

        var backup1 = BackupService.CreateBackup(filePath);
        File.ReadAllText(backup1!).Should().Be("Content v1");

        // 修改原文件内容
        File.WriteAllText(filePath, "Content v2");

        var backup2 = BackupService.CreateBackup(filePath);

        // 第二次备份应覆盖，内容应为 v2
        backup2.Should().Be(backup1);
        File.ReadAllText(backup2!).Should().Be("Content v2");
    }

    [Fact]
    public void CreateBackup_TwoDifferentFiles_GoDifferentSubdirs()
    {
        var file1 = CreateTempFile("File 1");
        var file2 = CreateTempFile("File 2");

        var backup1 = BackupService.CreateBackup(file1);
        var backup2 = BackupService.CreateBackup(file2);

        var dir1 = Path.GetDirectoryName(backup1);
        var dir2 = Path.GetDirectoryName(backup2);

        // 不同文件的路径哈希应该不同
        dir1.Should().NotBe(dir2);
    }

    #endregion

    #region RestoreFromBackup

    [Fact]
    public void RestoreFromBackup_RestoresOriginalContent()
    {
        var filePath = CreateTempFile("Original content to restore");
        var originalContent = File.ReadAllText(filePath);

        var backupPath = BackupService.CreateBackup(filePath);

        // 修改原文件
        File.WriteAllText(filePath, "Modified content");

        BackupService.RestoreFromBackup(filePath, backupPath);

        // 应恢复为原始内容
        File.ReadAllText(filePath).Should().Be(originalContent);
    }

    [Fact]
    public void RestoreFromBackup_DeletesBackupFile()
    {
        var filePath = CreateTempFile("Restore and delete test");
        var backupPath = BackupService.CreateBackup(filePath);

        File.WriteAllText(filePath, "Changed");

        BackupService.RestoreFromBackup(filePath, backupPath);

        File.Exists(backupPath).Should().BeFalse();
    }

    [Fact]
    public void RestoreFromBackup_NullBackupPath_DoesNotThrow()
    {
        var filePath = CreateTempFile();

        var act = () => BackupService.RestoreFromBackup(filePath, null);
        act.Should().NotThrow();
    }

    [Fact]
    public void RestoreFromBackup_NonExistingBackupFile_DoesNotThrow()
    {
        var filePath = CreateTempFile();
        var fakeBackupPath = Path.Combine(Path.GetTempPath(), $"nonexistent_backup_{Guid.NewGuid():N}.txt");

        var act = () => BackupService.RestoreFromBackup(filePath, fakeBackupPath);
        act.Should().NotThrow();
    }

    #endregion

    #region CleanupBackup

    [Fact]
    public void CleanupBackup_DeletesBackupFile()
    {
        var filePath = CreateTempFile("Cleanup test");
        var backupPath = BackupService.CreateBackup(filePath);

        BackupService.CleanupBackup(backupPath);

        File.Exists(backupPath).Should().BeFalse();
    }

    [Fact]
    public void CleanupBackup_RemovesEmptyParentDirectory()
    {
        var filePath = CreateTempFile("Cleanup dir test");
        var backupPath = BackupService.CreateBackup(filePath);
        var parentDir = Path.GetDirectoryName(backupPath);

        BackupService.CleanupBackup(backupPath);

        // 父目录应被清理（如果为空）
        Directory.Exists(parentDir).Should().BeFalse();
    }

    [Fact]
    public void CleanupBackup_NullBackupPath_DoesNotThrow()
    {
        var act = () => BackupService.CleanupBackup(null);
        act.Should().NotThrow();
    }

    [Fact]
    public void CleanupBackup_NonExistingFile_DoesNotThrow()
    {
        var fakePath = Path.Combine(Path.GetTempPath(), $"nonexistent_cleanup_{Guid.NewGuid():N}.txt");

        var act = () => BackupService.CleanupBackup(fakePath);
        act.Should().NotThrow();
    }

    #endregion

    #region RollbackAll

    [Fact]
    public void RollbackAll_RestoresAllFiles()
    {
        var file1 = CreateTempFile("File 1 original");
        var file2 = CreateTempFile("File 2 original");

        var backup1 = BackupService.CreateBackup(file1);
        var backup2 = BackupService.CreateBackup(file2);

        // 修改两个文件
        File.WriteAllText(file1, "File 1 modified");
        File.WriteAllText(file2, "File 2 modified");

        var backups = new Dictionary<string, string?>
        {
            { file1, backup1 },
            { file2, backup2 },
        };

        BackupService.RollbackAll(backups);

        File.ReadAllText(file1).Should().Be("File 1 original");
        File.ReadAllText(file2).Should().Be("File 2 original");
    }

    [Fact]
    public void RollbackAll_DeletesBackupFiles()
    {
        var file1 = CreateTempFile("Rollback delete test");
        var backup1 = BackupService.CreateBackup(file1);

        File.WriteAllText(file1, "Changed");

        var backups = new Dictionary<string, string?>
        {
            { file1, backup1 },
        };

        BackupService.RollbackAll(backups);

        File.Exists(backup1).Should().BeFalse();
    }

    [Fact]
    public void RollbackAll_PartialFailure_ContinuesForRemaining()
    {
        var file1 = CreateTempFile("File A original");
        var file2 = CreateTempFile("File B original");
        var nonExistingFile = Path.Combine(Path.GetTempPath(), $"nonexistent_rollback_{Guid.NewGuid():N}.txt");

        var backup1 = BackupService.CreateBackup(file1);
        var backup2 = BackupService.CreateBackup(file2);

        File.WriteAllText(file1, "File A modified");
        File.WriteAllText(file2, "File B modified");

        var backups = new Dictionary<string, string?>
        {
            { file1, backup1 },
            { nonExistingFile, "fake_backup_path" }, // 无效条目，应跳过
            { file2, backup2 },
        };

        var act = () => BackupService.RollbackAll(backups);
        act.Should().NotThrow();

        // file1 和 file2 应被恢复
        File.ReadAllText(file1).Should().Be("File A original");
        File.ReadAllText(file2).Should().Be("File B original");
    }

    [Fact]
    public void RollbackAll_EmptyDictionary_DoesNotThrow()
    {
        var act = () => BackupService.RollbackAll(new Dictionary<string, string?>());
        act.Should().NotThrow();
    }

    #endregion

    #region End-to-End Transaction Scenario

    [Fact]
    public void FullTransaction_Success_CleansUpAllBackups()
    {
        // ── 模拟完整编辑事务 ──
        var file1 = CreateTempFile("Transaction file 1");
        var file2 = CreateTempFile("Transaction file 2");

        BackupService.BeginSession();

        var backup1 = BackupService.CreateBackup(file1);
        var backup2 = BackupService.CreateBackup(file2);

        // 模拟编辑成功
        File.WriteAllText(file1, "Transaction file 1 edited");
        File.WriteAllText(file2, "Transaction file 2 edited");

        // 全部成功 → 清理备份
        BackupService.CleanupBackup(backup1);
        BackupService.CleanupBackup(backup2);

        BackupService.EndSession();

        // 备份文件应已删除
        File.Exists(backup1!).Should().BeFalse();
        File.Exists(backup2!).Should().BeFalse();

        // 会话目录应已清空
        var sessionDir = BackupService.CurrentSessionDir;
        sessionDir.Should().BeNull();
    }

    [Fact]
    public void FullTransaction_Failure_RollbackAllFiles()
    {
        var file1 = CreateTempFile("Rollback file 1 original");
        var file2 = CreateTempFile("Rollback file 2 original");

        BackupService.BeginSession();

        var backup1 = BackupService.CreateBackup(file1);
        var backup2 = BackupService.CreateBackup(file2);

        // 模拟编辑 file1 成功，file2 失败
        File.WriteAllText(file1, "Rollback file 1 edited");

        // 事务失败 → 全部回滚
        var backups = new Dictionary<string, string?>
        {
            { file1, backup1 },
            { file2, backup2 },
        };
        BackupService.RollbackAll(backups);

        // 两个文件都应恢复为原始内容
        File.ReadAllText(file1).Should().Be("Rollback file 1 original");
        File.ReadAllText(file2).Should().Be("Rollback file 2 original"); // 未被修改，不变

        BackupService.EndSession();
    }

    #endregion

    #region Concurrency & Edge Cases

    [Fact]
    public void CreateBackup_SpecialCharInFileName_WorksCorrectly()
    {
        // 文件名包含中文和特殊字符
        var filePath = CreateTempFile("测试备份 特殊!@# 字符");
        var originalContent = File.ReadAllText(filePath);

        var backupPath = BackupService.CreateBackup(filePath);

        backupPath.Should().NotBeNull();
        File.ReadAllText(backupPath!).Should().Be(originalContent);
    }

    [Fact]
    public void CreateBackup_LongPath_WorksCorrectly()
    {
        // 长路径文件
        var tempDir = CreateTempDir();
        var longName = new string('a', 50);
        var longPath = Path.Combine(tempDir, $"{longName}.txt");
        File.WriteAllText(longPath, "Long path content");
        _tempFiles.Add(longPath);

        var backupPath = BackupService.CreateBackup(longPath);

        backupPath.Should().NotBeNull();
        File.Exists(backupPath).Should().BeTrue();
    }

    [Fact]
    public void CreateBackup_EmptyFile_WorksCorrectly()
    {
        var filePath = CreateTempFile(string.Empty);

        var backupPath = BackupService.CreateBackup(filePath);

        backupPath.Should().NotBeNull();
        File.ReadAllText(backupPath!).Should().Be(string.Empty);
    }

    [Fact]
    public void CreateBackup_LargeFile_WorksCorrectly()
    {
        // 较大文件（约 100KB）
        var largeContent = new string('X', 100_000);
        var filePath = CreateTempFile(largeContent);

        var backupPath = BackupService.CreateBackup(filePath);

        backupPath.Should().NotBeNull();
        new FileInfo(backupPath!).Length.Should().BeGreaterOrEqualTo(100_000);
    }

    [Fact]
    public async Task CreateBackup_ConcurrentAccess_DifferentFiles_Works()
    {
        var files = new List<string>();
        for (int i = 0; i < 10; i++)
            files.Add(CreateTempFile($"Concurrent file {i}"));

        var tasks = new List<Task<string?>>();
        foreach (var file in files)
        {
            tasks.Add(Task.Run(() => BackupService.CreateBackup(file)));
        }

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Should().NotBeNull());
        results.Should().AllSatisfy(r => File.Exists(r).Should().BeTrue());
    }

    #endregion

    #region Backup Directory Structure

    [Fact]
    public void BackupDirectory_IsUnderLocalAppData()
    {
        var filePath = CreateTempFile("Directory test");
        var backupPath = BackupService.CreateBackup(filePath);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        backupPath.Should().StartWith(localAppData);
    }

    [Fact]
    public void BackupDirectory_ContainsDeepSeekVS()
    {
        var filePath = CreateTempFile("DeepSeekVS path test");
        var backupPath = BackupService.CreateBackup(filePath);

        backupPath.Should().Contain("DeepSeekVS");
        backupPath.Should().Contain("backups");
    }

    [Fact]
    public void BackupFilename_MatchesOriginalFilename()
    {
        var filePath = CreateTempFile("Filename test");
        var originalFilename = Path.GetFileName(filePath);

        var backupPath = BackupService.CreateBackup(filePath);

        var backupFilename = Path.GetFileName(backupPath);
        backupFilename.Should().Be(originalFilename);
    }

    #endregion
}
