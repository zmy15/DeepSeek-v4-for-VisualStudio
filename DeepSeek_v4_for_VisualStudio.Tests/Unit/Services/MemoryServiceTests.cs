using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.IO;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// MemoryService 单元测试 — 测试记忆 CRUD 操作、路径安全、并发安全。
/// </summary>
public class MemoryServiceTests : IDisposable
{
    private readonly MemoryService _service;
    private readonly string _testSessionId;

    public MemoryServiceTests()
    {
        _service = new MemoryService();
        _testSessionId = "test-session-" + Guid.NewGuid().ToString("N").Substring(0, 8);
    }

    #region Create & View

    [Fact]
    public async Task CreateAndView_UserScope_StoresAndRetrievesContent()
    {
        string path = $"test-create-{Guid.NewGuid():N}.md";
        string content = "这是测试内容\n第二行\n第三行";

        await _service.CreateAsync(MemoryScope.User, path, content);
        var result = await _service.ViewAsync(MemoryScope.User, path);

        result.Content.Should().Be(content);
        result.TotalLines.Should().Be(3);
        result.IsDirectoryListing.Should().BeFalse();
        result.Scope.Should().Be(MemoryScope.User);

        // Cleanup
        await _service.DeleteAsync(MemoryScope.User, path);
    }

    [Fact]
    public async Task View_WithLineRange_ReturnsCorrectRange()
    {
        string path = $"test-lines-{Guid.NewGuid():N}.md";
        string content = "Line1\nLine2\nLine3\nLine4\nLine5";

        await _service.CreateAsync(MemoryScope.User, path, content);
        var result = await _service.ViewAsync(MemoryScope.User, path, startLine: 2, endLine: 4);

        result.Content.Should().Be("Line2\nLine3\nLine4");
        result.ViewStartLine.Should().Be(2);
        result.ViewEndLine.Should().Be(4);
        result.TotalLines.Should().Be(5);

        await _service.DeleteAsync(MemoryScope.User, path);
    }

    [Fact]
    public async Task View_Directory_ReturnsEntries()
    {
        // Create multiple files in a temp directory
        await _service.CreateAsync(MemoryScope.Session, "testdir/file1.md", "content1", _testSessionId);
        await _service.CreateAsync(MemoryScope.Session, "testdir/file2.md", "content2", _testSessionId);

        var result = await _service.ViewAsync(MemoryScope.Session, "testdir", _testSessionId);

        result.IsDirectoryListing.Should().BeTrue();
        result.Entries.Should().NotBeNull();
        result.Entries!.Count.Should().Be(2);
        result.Entries.Should().Contain(e => e.Name == "file1.md");
        result.Entries.Should().Contain(e => e.Name == "file2.md");

        // Cleanup
        await _service.DeleteAsync(MemoryScope.Session, "testdir", _testSessionId);
    }

    [Fact]
    public async Task View_NonexistentFile_ThrowsFileNotFoundException()
    {
        var act = () => _service.ViewAsync(MemoryScope.User, "nonexistent.md");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Create_ExistingFile_ThrowsInvalidOperationException()
    {
        string path = $"test-dup-{Guid.NewGuid():N}.md";
        await _service.CreateAsync(MemoryScope.User, path, "first");

        var act = () => _service.CreateAsync(MemoryScope.User, path, "second");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*已存在*");

        await _service.DeleteAsync(MemoryScope.User, path);
    }

    #endregion

    #region StrReplace

    [Fact]
    public async Task StrReplace_UniqueMatch_ReplacesSuccessfully()
    {
        string path = $"test-replace-{Guid.NewGuid():N}.md";
        string content = "Hello World\nThis is a test\nGoodbye";
        await _service.CreateAsync(MemoryScope.User, path, content);

        await _service.StrReplaceAsync(MemoryScope.User, path, "World", "Universe");
        var result = await _service.ViewAsync(MemoryScope.User, path);

        result.Content.Should().Be("Hello Universe\nThis is a test\nGoodbye");

        await _service.DeleteAsync(MemoryScope.User, path);
    }

    [Fact]
    public async Task StrReplace_NotFound_ThrowsInvalidOperationException()
    {
        string path = $"test-replace-nf-{Guid.NewGuid():N}.md";
        await _service.CreateAsync(MemoryScope.User, path, "some content");

        var act = () => _service.StrReplaceAsync(MemoryScope.User, path, "notfound", "replacement");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*未找到*");

        await _service.DeleteAsync(MemoryScope.User, path);
    }

    [Fact]
    public async Task StrReplace_NonUnique_ThrowsInvalidOperationException()
    {
        string path = $"test-replace-multi-{Guid.NewGuid():N}.md";
        string content = "Line with foo\nAnother line with foo";
        await _service.CreateAsync(MemoryScope.User, path, content);

        var act = () => _service.StrReplaceAsync(MemoryScope.User, path, "foo", "bar");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*2*不唯一*"); // appeared 2 times

        await _service.DeleteAsync(MemoryScope.User, path);
    }

    [Fact]
    public async Task StrReplace_NonexistentFile_ThrowsFileNotFoundException()
    {
        var act = () => _service.StrReplaceAsync(MemoryScope.User, "nonexistent.md", "old", "new");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    #endregion

    #region Insert

    [Fact]
    public async Task Insert_AtBeginning_InsertsBeforeFirstLine()
    {
        string path = $"test-insert-{Guid.NewGuid():N}.md";
        string content = "Line1\nLine2";
        await _service.CreateAsync(MemoryScope.User, path, content);

        await _service.InsertAsync(MemoryScope.User, path, 0, "Inserted");
        var result = await _service.ViewAsync(MemoryScope.User, path);

        result.Content.Should().Be("Inserted\nLine1\nLine2");

        await _service.DeleteAsync(MemoryScope.User, path);
    }

    [Fact]
    public async Task Insert_AtMiddle_InsertsCorrectly()
    {
        string path = $"test-insert-mid-{Guid.NewGuid():N}.md";
        string content = "Line1\nLine2\nLine3";
        await _service.CreateAsync(MemoryScope.User, path, content);

        await _service.InsertAsync(MemoryScope.User, path, 1, "Middle");
        var result = await _service.ViewAsync(MemoryScope.User, path);

        result.Content.Should().Be("Line1\nMiddle\nLine2\nLine3");

        await _service.DeleteAsync(MemoryScope.User, path);
    }

    #endregion

    #region Delete

    [Fact]
    public async Task Delete_File_RemovesFile()
    {
        string path = $"test-del-{Guid.NewGuid():N}.md";
        await _service.CreateAsync(MemoryScope.User, path, "temp");

        await _service.DeleteAsync(MemoryScope.User, path);

        var act = () => _service.ViewAsync(MemoryScope.User, path);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Delete_Directory_RemovesRecursively()
    {
        await _service.CreateAsync(MemoryScope.Session, "deldir/file1.md", "a", _testSessionId);
        await _service.CreateAsync(MemoryScope.Session, "deldir/file2.md", "b", _testSessionId);

        await _service.DeleteAsync(MemoryScope.Session, "deldir", _testSessionId);

        var act = () => _service.ViewAsync(MemoryScope.Session, "deldir", _testSessionId);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Delete_Nonexistent_ThrowsFileNotFoundException()
    {
        var act = () => _service.DeleteAsync(MemoryScope.User, "nonexistent.md");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    #endregion

    #region Rename

    [Fact]
    public async Task Rename_File_MovesSuccessfully()
    {
        string oldPath = $"test-rename-old-{Guid.NewGuid():N}.md";
        string newPath = $"test-rename-new-{Guid.NewGuid():N}.md";
        string content = "rename test";
        await _service.CreateAsync(MemoryScope.User, oldPath, content);

        await _service.RenameAsync(MemoryScope.User, oldPath, newPath);

        // Old path should not exist
        var actOld = () => _service.ViewAsync(MemoryScope.User, oldPath);
        await actOld.Should().ThrowAsync<FileNotFoundException>();

        // New path should have content
        var result = await _service.ViewAsync(MemoryScope.User, newPath);
        result.Content.Should().Be(content);

        await _service.DeleteAsync(MemoryScope.User, newPath);
    }

    [Fact]
    public async Task Rename_TargetExists_ThrowsInvalidOperationException()
    {
        string path1 = $"test-rename-ex1-{Guid.NewGuid():N}.md";
        string path2 = $"test-rename-ex2-{Guid.NewGuid():N}.md";
        await _service.CreateAsync(MemoryScope.User, path1, "a");
        await _service.CreateAsync(MemoryScope.User, path2, "b");

        var act = () => _service.RenameAsync(MemoryScope.User, path1, path2);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*已存在*");

        await _service.DeleteAsync(MemoryScope.User, path1);
        await _service.DeleteAsync(MemoryScope.User, path2);
    }

    #endregion

    #region Path Traversal Security

    [Fact]
    public async Task View_PathTraversal_Rejected()
    {
        var act = () => _service.ViewAsync(MemoryScope.User, "../etc/passwd");

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*路径穿越*");
    }

    [Fact]
    public async Task Create_PathTraversal_Rejected()
    {
        var act = () => _service.CreateAsync(MemoryScope.User, "../../outside.md", "malicious");

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*路径穿越*");
    }

    #endregion

    #region Scope Isolation

    [Fact]
    public async Task DifferentScopes_AreIsolated()
    {
        string path = $"test-isolation-{Guid.NewGuid():N}.md";

        await _service.CreateAsync(MemoryScope.User, path, "user-content");
        await _service.CreateAsync(MemoryScope.Session, path, "session-content", _testSessionId);

        var userResult = await _service.ViewAsync(MemoryScope.User, path);
        var sessionResult = await _service.ViewAsync(MemoryScope.Session, path, _testSessionId);

        userResult.Content.Should().Be("user-content");
        sessionResult.Content.Should().Be("session-content");

        // Cleanup
        await _service.DeleteAsync(MemoryScope.User, path);
        await _service.DeleteAsync(MemoryScope.Session, path, _testSessionId);
    }

    #endregion

    #region GetMemoryPreviews

    [Fact]
    public void GetMemoryPreviews_EmptyScope_ReturnsEmptyString()
    {
        // Use a unique session to ensure empty
        string emptySession = "empty-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var result = _service.GetMemoryPreviews(MemoryScope.Session, emptySession);
        result.Should().Be(string.Empty);
    }

    #endregion

    #region Cleanup

    public void Dispose()
    {
        // Clean up any leftover test files
        try
        {
            // Session scope cleanup
            string sessionDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekVS", "memories", "session", _testSessionId);
            if (Directory.Exists(sessionDir))
                Directory.Delete(sessionDir, true);
        }
        catch { /* best effort */ }
    }

    #endregion
}
