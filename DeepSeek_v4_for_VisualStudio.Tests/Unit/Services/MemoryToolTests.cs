using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// MemoryTool 单元测试 — 测试工具定义、参数解析、路径解析。
/// </summary>
public class MemoryToolTests
{
    private readonly MemoryTool _tool;
    private readonly IMemoryService _memoryService;
    private readonly string _testSessionId;
    private readonly string _testSolutionPath;

    public MemoryToolTests()
    {
        _memoryService = new MemoryService();
        _testSessionId = "tool-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        _testSolutionPath = @"C:\TestProjects\TestSolution";
        _tool = new MemoryTool(_memoryService, () => _testSessionId, () => _testSolutionPath);
    }

    #region Tool Definition

    [Fact]
    public void Name_IsMemory()
    {
        _tool.Name.Should().Be("memory");
    }

    [Fact]
    public void GetDefinition_ReturnsValidToolDefinition()
    {
        var def = _tool.GetDefinition();

        def.Type.Should().Be("function");
        def.Function.Should().NotBeNull();
        def.Function.Name.Should().Be("memory");
        def.Function.Description.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Execute — View

    [Fact]
    public async Task Execute_View_File_ReturnsContent()
    {
        string content = "测试记忆内容";
        await _memoryService.CreateAsync(MemoryScope.User, "view-test.md", content);

        var args = new Dictionary<string, JsonElement>
        {
            ["command"] = JsonSerializer.SerializeToElement("view"),
            ["path"] = JsonSerializer.SerializeToElement("/memories/view-test.md"),
        };

        var result = await _tool.ExecuteAsync(args, null);

        result.Should().Contain(content);
        result.Should().Contain("view-test.md");

        await _memoryService.DeleteAsync(MemoryScope.User, "view-test.md");
    }

    [Fact]
    public async Task Execute_View_Directory_ReturnsEntries()
    {
        // Clean up from previous runs first
        try { await _memoryService.DeleteAsync(MemoryScope.User, "dir-test"); } catch { }

        await _memoryService.CreateAsync(MemoryScope.User, "dir-test/a.md", "a");
        await _memoryService.CreateAsync(MemoryScope.User, "dir-test/b.md", "b");

        var args = new Dictionary<string, JsonElement>
        {
            ["command"] = JsonSerializer.SerializeToElement("view"),
            ["path"] = JsonSerializer.SerializeToElement("/memories/dir-test"),
        };

        var result = await _tool.ExecuteAsync(args, null);

        result.Should().Contain("a.md");
        result.Should().Contain("b.md");

        await _memoryService.DeleteAsync(MemoryScope.User, "dir-test");
    }

    [Fact]
    public async Task Execute_View_Nonexistent_ReturnsError()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["command"] = JsonSerializer.SerializeToElement("view"),
            ["path"] = JsonSerializer.SerializeToElement("/memories/nonexistent.md"),
        };

        var result = await _tool.ExecuteAsync(args, null);

        result.Should().StartWith("❌");
    }

    #endregion

    #region Execute — Create

    [Fact]
    public async Task Execute_Create_StoresFile()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["command"] = JsonSerializer.SerializeToElement("create"),
            ["path"] = JsonSerializer.SerializeToElement("/memories/new-file.md"),
            ["file_text"] = JsonSerializer.SerializeToElement("新文件内容"),
        };

        var result = await _tool.ExecuteAsync(args, null);

        result.Should().Contain("已创建");

        // Verify it's actually stored
        var viewResult = await _memoryService.ViewAsync(MemoryScope.User, "new-file.md");
        viewResult.Content.Should().Be("新文件内容");

        await _memoryService.DeleteAsync(MemoryScope.User, "new-file.md");
    }

    [Fact]
    public async Task Execute_Create_MissingArgs_ReturnsError()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["command"] = JsonSerializer.SerializeToElement("create"),
        };

        var result = await _tool.ExecuteAsync(args, null);

        result.Should().StartWith("❌");
    }

    #endregion

    #region Execute — Session Scope

    [Fact]
    public async Task Execute_Create_SessionScope_UsesSessionId()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["command"] = JsonSerializer.SerializeToElement("create"),
            ["path"] = JsonSerializer.SerializeToElement("/memories/session/session-note.md"),
            ["file_text"] = JsonSerializer.SerializeToElement("会话笔记"),
        };

        var result = await _tool.ExecuteAsync(args, null);

        result.Should().Contain("已创建");

        // Verify in session scope
        var viewResult = await _memoryService.ViewAsync(MemoryScope.Session, "session-note.md", _testSessionId);
        viewResult.Content.Should().Be("会话笔记");

        await _memoryService.DeleteAsync(MemoryScope.Session, "session-note.md", _testSessionId);
    }

    #endregion

    #region Execute — Repo Scope

    [Fact]
    public async Task Execute_Create_RepoScope_UsesSolutionHash()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["command"] = JsonSerializer.SerializeToElement("create"),
            ["path"] = JsonSerializer.SerializeToElement("/memories/repo/project-notes.md"),
            ["file_text"] = JsonSerializer.SerializeToElement("项目笔记"),
        };

        var result = await _tool.ExecuteAsync(args, null);

        result.Should().Contain("已创建");

        var viewResult = await _memoryService.ViewAsync(MemoryScope.Repo, "project-notes.md", solutionPath: _testSolutionPath);
        viewResult.Content.Should().Be("项目笔记");

        await _memoryService.DeleteAsync(MemoryScope.Repo, "project-notes.md", solutionPath: _testSolutionPath);
    }

    #endregion

    #region Display & Summary

    [Fact]
    public void GetDisplayText_View_ReturnsFormattedString()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["command"] = JsonSerializer.SerializeToElement("view"),
            ["path"] = JsonSerializer.SerializeToElement("/memories/test.md"),
        };

        var text = _tool.GetDisplayText(args);

        text.Should().Contain("查看记忆");
    }

    [Fact]
    public void GetDisplayText_Create_ReturnsFormattedString()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["command"] = JsonSerializer.SerializeToElement("create"),
            ["path"] = JsonSerializer.SerializeToElement("/memories/test.md"),
        };

        var text = _tool.GetDisplayText(args);

        text.Should().Contain("创建记忆");
    }

    [Fact]
    public void GetResultSummary_Success_ReturnsSummary()
    {
        var summary = _tool.GetResultSummary("记忆文件已创建: user/test.md");

        summary.Should().Contain("已创建");
    }

    [Fact]
    public void GetResultSummary_Error_ReturnsErrorText()
    {
        var summary = _tool.GetResultSummary("❌ memory: 文件不存在");

        summary.Should().StartWith("❌");
    }

    [Fact]
    public void GetResultSummary_LongText_Truncated()
    {
        string longText = new string('x', 200);
        var summary = _tool.GetResultSummary(longText);

        summary.Length.Should().BeLessOrEqualTo(83); // 80 + "..."
    }

    #endregion

    #region Unknown Command

    [Fact]
    public async Task Execute_UnknownCommand_ReturnsError()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["command"] = JsonSerializer.SerializeToElement("invalid_command"),
        };

        var result = await _tool.ExecuteAsync(args, null);

        result.Should().StartWith("❌");
        result.Should().Contain("未知命令");
    }

    [Fact]
    public async Task Execute_MissingCommand_ReturnsError()
    {
        var args = new Dictionary<string, JsonElement>();

        var result = await _tool.ExecuteAsync(args, null);

        result.Should().StartWith("❌");
        result.Should().Contain("缺少 command");
    }

    #endregion
}
