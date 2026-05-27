using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// BuiltInToolService 单元测试 — 测试工具注册中心、过滤、执行和静态方法。
/// </summary>
public class BuiltInToolServiceTests
{
    #region Tool Registration

    [Fact]
    public void Constructor_RegistersAll16Tools()
    {
        var service = new BuiltInToolService();

        var defs = service.GetFilteredToolDefinitions(null);

        defs.Should().HaveCount(16);
    }

    [Fact]
    public void GetFilteredToolDefinitions_WithWhitelist_ReturnsOnlyAllowed()
    {
        var service = new BuiltInToolService();
        var allowed = new List<string> { "read_file", "list_dir", "grep_search" };

        var defs = service.GetFilteredToolDefinitions(allowed);

        defs.Should().HaveCount(3);
        defs.Select(d => d.Function.Name).Should().BeEquivalentTo("read_file", "list_dir", "grep_search");
    }

    [Fact]
    public void GetFilteredToolDefinitions_WithEmptyWhitelist_ReturnsAll()
    {
        var service = new BuiltInToolService();

        var defs = service.GetFilteredToolDefinitions(new List<string>());

        defs.Should().HaveCount(16);
    }

    [Fact]
    public void GetFilteredToolDefinitions_WithNull_ReturnsAll()
    {
        var service = new BuiltInToolService();

        var defs = service.GetFilteredToolDefinitions(null);

        defs.Should().HaveCount(16);
    }

    #endregion

    #region IsBuiltInTool

    [Theory]
    [InlineData("list_dir", true)]
    [InlineData("read_file", true)]
    [InlineData("file_search", true)]
    [InlineData("grep_search", true)]
    [InlineData("get_errors", true)]
    [InlineData("fetch_webpage", true)]
    [InlineData("build_solution", true)]
    [InlineData("replace_string_in_file", true)]
    [InlineData("multi_replace_string_in_file", true)]
    [InlineData("create_file", true)]
    [InlineData("delete_file", true)]
    [InlineData("apply_patch", true)]
    [InlineData("create_directory", true)]
    [InlineData("run_in_terminal", true)]
    [InlineData("get_terminal_output", true)]
    [InlineData("VisualStudio_askQuestions", true)]
    [InlineData("unknown_tool", false)]
    [InlineData("github_repo", false)]
    [InlineData("semantic_search", false)]
    public void IsBuiltInTool_ReturnsCorrectResult(string toolName, bool expected)
    {
        BuiltInToolService.IsBuiltInTool(toolName).Should().Be(expected);
    }

    #endregion

    #region Static GetBuiltInToolDefinitions

    [Fact]
    public void GetBuiltInToolDefinitions_Returns16Tools()
    {
        var defs = BuiltInToolService.GetBuiltInToolDefinitions();

        defs.Should().HaveCount(16);
    }

    [Fact]
    public void GetBuiltInToolDefinitions_AllHaveTypeFunction()
    {
        var defs = BuiltInToolService.GetBuiltInToolDefinitions();

        defs.Should().AllSatisfy(d => d.Type.Should().Be("function"));
    }

    [Fact]
    public void GetBuiltInToolDefinitions_AllHaveNonEmptyName()
    {
        var defs = BuiltInToolService.GetBuiltInToolDefinitions();

        defs.Should().AllSatisfy(d => d.Function.Name.Should().NotBeNullOrEmpty());
    }

    #endregion

    #region Tool Execution

    [Fact]
    public async Task ExecuteBuiltInToolAsync_UnknownTool_ReturnsNull()
    {
        var service = new BuiltInToolService();

        var result = await service.ExecuteBuiltInToolAsync("nonexistent_tool", "{}");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteBuiltInToolAsync_ListDir_NoPath_ReturnsError()
    {
        var service = new BuiltInToolService();

        var result = await service.ExecuteBuiltInToolAsync("list_dir", "{}");

        result.Should().NotBeNull();
        result.Should().Contain("❌");
    }

    [Fact]
    public async Task ExecuteBuiltInToolAsync_CreateDirectory_ValidPath_Succeeds()
    {
        var service = new BuiltInToolService();
        var tempDir = Path.Combine(Path.GetTempPath(), $"test_mkdir_{Guid.NewGuid():N}");

        try
        {
            var args = JsonSerializer.Serialize(new { dirPath = tempDir });
            var result = await service.ExecuteBuiltInToolAsync("create_directory", args);

            result.Should().NotBeNull();
            result.Should().Contain("✅");
            Directory.Exists(tempDir).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir);
        }
    }

    [Fact]
    public async Task ExecuteBuiltInToolAsync_ReadFile_NonexistentFile_ReturnsError()
    {
        var service = new BuiltInToolService();
        var args = JsonSerializer.Serialize(new { filePath = @"C:\nonexistent_file_xyz123.txt" });

        var result = await service.ExecuteBuiltInToolAsync("read_file", args);

        result.Should().NotBeNull();
        result.Should().Contain("❌");
    }

    [Fact]
    public async Task ExecuteBuiltInToolAsync_GetTerminalOutput_NoId_ReturnsError()
    {
        var service = new BuiltInToolService();

        var result = await service.ExecuteBuiltInToolAsync("get_terminal_output", "{}");

        result.Should().NotBeNull();
        result.Should().Contain("❌");
    }

    #endregion

    #region GetToolCallDisplayText

    [Fact]
    public void GetToolCallDisplayText_ListDir_ReturnsFormattedText()
    {
        var args = JsonSerializer.Serialize(new { path = @"C:\test" });

        var text = BuiltInToolService.GetToolCallDisplayText("list_dir", args);

        text.Should().Contain("📂");
        text.Should().Contain("test");
    }

    [Fact]
    public void GetToolCallDisplayText_ReadFile_ReturnsFormattedText()
    {
        var args = JsonSerializer.Serialize(new { filePath = @"C:\test\Program.cs" });

        var text = BuiltInToolService.GetToolCallDisplayText("read_file", args);

        text.Should().Contain("📄");
        text.Should().Contain("Program.cs");
    }

    [Fact]
    public void GetToolCallDisplayText_UnknownTool_ReturnsGenericText()
    {
        var text = BuiltInToolService.GetToolCallDisplayText("unknown_tool", "{}");

        text.Should().Contain("🔧");
    }

    [Fact]
    public void GetToolCallDisplayText_InvalidJson_DoesNotThrow()
    {
        var text = BuiltInToolService.GetToolCallDisplayText("read_file", "not valid json");

        text.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region GetToolResultSummary

    [Fact]
    public void GetToolResultSummary_EmptyResult_ReturnsEmptyIndicator()
    {
        var summary = BuiltInToolService.GetToolResultSummary("read_file", "");

        summary.Should().Be("（无返回结果）");
    }

    [Fact]
    public void GetToolResultSummary_ErrorResult_ReturnsErrorDirectly()
    {
        var summary = BuiltInToolService.GetToolResultSummary("read_file", "❌ 文件不存在");

        summary.Should().Be("❌ 文件不存在");
    }

    [Fact]
    public void GetToolResultSummary_BuildSuccess_ReturnsSuccessIndicator()
    {
        var summary = BuiltInToolService.GetToolResultSummary("build_solution", "构建成功！0 errors");

        summary.Should().Be("✅ 构建成功");
    }

    [Fact]
    public void GetToolResultSummary_UnknownTool_ReturnsTruncatedText()
    {
        var longResult = new string('x', 200);

        var summary = BuiltInToolService.GetToolResultSummary("unknown_tool", longResult);

        summary.Should().NotBeNullOrEmpty();
        summary.Length.Should().BeLessOrEqualTo(83); // 80 chars + "…"
    }

    #endregion

    #region FileReadCache

    [Fact]
    public void GetFileReadCacheSnapshot_InitiallyEmpty()
    {
        var service = new BuiltInToolService();

        var snapshot = service.GetFileReadCacheSnapshot();

        snapshot.Should().BeEmpty();
    }

    [Fact]
    public void InvalidateFileReadCache_DoesNotThrow()
    {
        var service = new BuiltInToolService();

        service.InvalidateFileReadCache(@"C:\nonexistent.txt");
        service.InvalidateFileReadCache(new[] { @"C:\a.txt", @"C:\b.txt" });

        // No exception means pass
    }

    #endregion
}
