using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.Editing;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services.Editing;

/// <summary>
/// P0-1 回归测试：StagedEditWorkspace 写穿落盘 + 仅内存撤销 → 崩溃即数据不可恢复。
/// 修复后：首次接触文件时落一份磁盘备份（BackupService），RestoreToBaseline 优先从磁盘备份恢复，
/// 进程崩溃/OOM 后仍可撤销；ConfirmAll 时清理已确认的磁盘备份。
/// </summary>
public class StagedEditWorkspaceBackupTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _backupRoot;

    public StagedEditWorkspaceBackupTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"staged_ws_test_{Guid.NewGuid():N}");
        _backupRoot = Path.Combine(Path.GetTempPath(), $"staged_ws_backup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_backupRoot);

        // 测试隔离：备份根目录指向临时目录，绝不触碰真实 %LOCALAPPDATA%\DeepSeekVS\backups
        BackupService.BaseDirOverride = _backupRoot;
        BackupService.EndSession();
    }

    public void Dispose()
    {
        BackupService.EndSession();
        BackupService.BaseDirOverride = null;
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
        try { if (Directory.Exists(_backupRoot)) Directory.Delete(_backupRoot, true); } catch { }
    }

    private string WriteSourceFile(string name, string content)
    {
        var p = Path.Combine(_tempRoot, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static int CountBackupFiles(string backupRoot)
    {
        if (!Directory.Exists(backupRoot)) return 0;
        return Directory.GetFiles(backupRoot, "*", SearchOption.AllDirectories).Length;
    }

    [Fact]
    public void WriteFile_FirstTouch_CreatesDiskBackup()
    {
        var file = WriteSourceFile("a.txt", "ORIGINAL");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "MODIFIED");

        // 修复核心：首次接触非新建文件必须留下磁盘备份（崩溃后仍可恢复）
        CountBackupFiles(_backupRoot).Should().Be(1, "WriteFile 首次接触应创建磁盘备份");
    }

    [Fact]
    public void ReadFile_PrefersOpenDocumentContentProvider()
    {
        var file = WriteSourceFile("a.txt", "DISK");

        var ws = new StagedEditWorkspace
        {
            OpenDocumentContentProvider = _ => "BUFFER",
        };

        ws.ReadFile(file).Should().Be("BUFFER", "打开文档应以编辑器 buffer 内容为读写基准");
    }

    [Fact]
    public void WriteFile_UsesOpenBufferAsUndoBaseline()
    {
        var file = WriteSourceFile("a.txt", "DISK");

        var ws = new StagedEditWorkspace
        {
            OpenDocumentContentProvider = _ => "USER UNSAVED",
        };

        ws.WriteFile(file, "AI EDIT");

        var batch = ws.ToPreparedChangeBatch();
        var change = batch.Changes.Should().ContainSingle().Subject;
        change.BaselineText.Should().Be("USER UNSAVED", "撤销基线必须包含用户未保存修改");
        change.ProposedText.Should().Be("AI EDIT");
    }

    [Fact]
    public void RestoreToBaseline_RestoresFromDiskBackup()
    {
        var file = WriteSourceFile("a.txt", "ORIGINAL");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "MODIFIED");
        File.ReadAllText(file).Should().Be("MODIFIED");

        ws.RestoreToBaseline();

        File.Exists(file).Should().BeTrue();
        File.ReadAllText(file).Should().Be("ORIGINAL", "撤销应从磁盘备份恢复原文");
    }

    [Fact]
    public void DeleteFile_FirstTouch_CreatesDiskBackup_AndRestoreRecovers()
    {
        var file = WriteSourceFile("a.txt", "ORIGINAL");

        var ws = new StagedEditWorkspace();
        ws.DeleteFile(file);
        File.Exists(file).Should().BeFalse("DeleteFile 应删除磁盘文件");

        CountBackupFiles(_backupRoot).Should().Be(1, "DeleteFile 首次接触应创建磁盘备份");

        ws.RestoreToBaseline();

        File.Exists(file).Should().BeTrue("撤销删除应从磁盘备份恢复文件");
        File.ReadAllText(file).Should().Be("ORIGINAL");
    }

    [Fact]
    public void NewFile_Write_DoesNotCreateBackup_AndRestoreDeletesIt()
    {
        var file = Path.Combine(_tempRoot, "new.txt");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "CONTENT");

        // 新建文件无原始内容可备份 → 不应产生备份
        CountBackupFiles(_backupRoot).Should().Be(0);

        ws.RestoreToBaseline();
        File.Exists(file).Should().BeFalse("新建文件撤销应回到'不存在'状态");
    }

    [Fact]
    public void ConfirmAll_CleansUpDiskBackups()
    {
        var file = WriteSourceFile("a.txt", "ORIGINAL");

        var ws = new StagedEditWorkspace();
        ws.WriteFile(file, "KEPT");
        CountBackupFiles(_backupRoot).Should().Be(1);

        ws.ConfirmAll();

        // 已确认（保留）的改动不再需要撤销备份 → 应清理，避免备份泄露
        CountBackupFiles(_backupRoot).Should().Be(0, "ConfirmAll 应清理已确认的磁盘备份");
        File.ReadAllText(file).Should().Be("KEPT");
    }
}
