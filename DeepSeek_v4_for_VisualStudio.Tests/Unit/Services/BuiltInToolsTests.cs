using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 独立内置工具定义测试 — 验证每个工具的 Name、Definition、DisplayText、ResultSummary。
/// </summary>
public class BuiltInToolsTests
{
    #region Tool Name & Definition

    [Fact]
    public void ListDirTool_HasCorrectName()
    {
        new ListDirTool().Name.Should().Be("list_dir");
    }

    [Fact]
    public void ReadFileTool_HasCorrectName()
    {
        new ReadFileTool(new()).Name.Should().Be("read_file");
    }

    [Fact]
    public void FileSearchTool_HasCorrectName()
    {
        new FileSearchTool().Name.Should().Be("file_search");
    }

    [Fact]
    public void GrepSearchTool_HasCorrectName()
    {
        new GrepSearchTool().Name.Should().Be("grep_search");
    }

    [Fact]
    public void GetErrorsTool_HasCorrectName()
    {
        new GetErrorsTool().Name.Should().Be("get_errors");
    }

    [Fact]
    public void FetchWebpageTool_HasCorrectName()
    {
        new FetchWebpageTool().Name.Should().Be("fetch_webpage");
    }

    [Fact]
    public void BuildSolutionTool_HasCorrectName()
    {
        new BuildSolutionTool().Name.Should().Be("build_solution");
    }

    [Fact]
    public void ReplaceStringInFileTool_HasCorrectName()
    {
        new ReplaceStringInFileTool().Name.Should().Be("replace_string_in_file");
    }

    [Fact]
    public void MultiReplaceStringInFileTool_HasCorrectName()
    {
        new MultiReplaceStringInFileTool().Name.Should().Be("multi_replace_string_in_file");
    }

    [Fact]
    public void CreateFileTool_HasCorrectName()
    {
        new CreateFileTool().Name.Should().Be("create_file");
    }

    [Fact]
    public void DeleteFileTool_HasCorrectName()
    {
        new DeleteFileTool().Name.Should().Be("delete_file");
    }

    [Fact]
    public void ApplyPatchTool_HasCorrectName()
    {
        new ApplyPatchTool().Name.Should().Be("apply_patch");
    }

    [Fact]
    public void CreateDirectoryTool_HasCorrectName()
    {
        new CreateDirectoryTool().Name.Should().Be("create_directory");
    }

    [Fact]
    public void RunInTerminalTool_HasCorrectName()
    {
        new RunInTerminalTool().Name.Should().Be("run_in_terminal");
    }

    [Fact]
    public void GetTerminalOutputTool_HasCorrectName()
    {
        new GetTerminalOutputTool().Name.Should().Be("get_terminal_output");
    }

    [Fact]
    public void AskQuestionsTool_HasCorrectName()
    {
        new AskQuestionsTool().Name.Should().Be("VisualStudio_askQuestions");
    }

    #endregion

    #region All Definitions Are Valid

    [Fact]
    public void AllTools_Definition_TypeIsFunction()
    {
        var tools = GetAllTools();
        foreach (var tool in tools)
            tool.GetDefinition().Type.Should().Be("function", $"{tool.Name} should have type 'function'");
    }

    [Fact]
    public void AllTools_Definition_HasNonEmptyName()
    {
        var tools = GetAllTools();
        foreach (var tool in tools)
            tool.GetDefinition().Function.Name.Should().NotBeNullOrEmpty($"{tool.Name} should have non-empty function name");
    }

    [Fact]
    public void AllTools_Definition_HasNonEmptyDescription()
    {
        var tools = GetAllTools();
        foreach (var tool in tools)
            tool.GetDefinition().Function.Description.Should().NotBeNullOrEmpty($"{tool.Name} should have description");
    }

    [Fact]
    public void AllTools_Definition_HasParameters()
    {
        var tools = GetAllTools();
        foreach (var tool in tools)
            tool.GetDefinition().Function.Parameters.Should().NotBeNull($"{tool.Name} should have parameters");
    }

    #endregion

    #region Display Text

    [Fact]
    public void AllTools_GetDisplayText_ReturnsNonEmpty()
    {
        var tools = GetAllTools();
        foreach (var tool in tools)
        {
            var text = tool.GetDisplayText(new Dictionary<string, JsonElement>());
            text.Should().NotBeNullOrEmpty($"{tool.Name} display text should not be empty");
        }
    }

    [Fact]
    public void GetDisplayText_WithFilePath_IncludesFileName()
    {
        var args = ParseArgs("{\"filePath\": \"C:\\\\test\\\\Program.cs\"}");

        var text = new ReadFileTool(new()).GetDisplayText(args);
        text.Should().Contain("Program.cs");

        var text2 = new ReplaceStringInFileTool().GetDisplayText(args);
        text2.Should().Contain("Program.cs");

        var text3 = new CreateFileTool().GetDisplayText(args);
        text3.Should().Contain("Program.cs");

        var text4 = new DeleteFileTool().GetDisplayText(args);
        text4.Should().Contain("Program.cs");
    }

    [Fact]
    public void GetDisplayText_ReadFile_WithLineRange_ShowsRange()
    {
        var args = ParseArgs("{\"filePath\": \"C:\\\\test\\\\Program.cs\", \"startLine\": 10, \"endLine\": 50}");

        var text = new ReadFileTool(new()).GetDisplayText(args);
        text.Should().Contain("10");
        text.Should().Contain("50");
    }

    [Fact]
    public void GetDisplayText_MultiReplace_ShowsCount()
    {
        var args = ParseArgs(@"{""replacements"": [{""filePath"": ""a.cs"", ""oldString"": ""x"", ""newString"": ""y""}, {""filePath"": ""b.cs"", ""oldString"": ""z"", ""newString"": ""w""}]}");

        var text = new MultiReplaceStringInFileTool().GetDisplayText(args);
        text.Should().Contain("2");
    }

    #endregion

    #region Result Summary

    [Fact]
    public void AllTools_GetResultSummary_WithSuccess_ReturnsNonEmpty()
    {
        var tools = GetAllTools();
        foreach (var tool in tools)
        {
            var summary = tool.GetResultSummary("✅ success");
            summary.Should().NotBeNullOrEmpty($"{tool.Name} result summary should not be empty");
        }
    }

    [Fact]
    public void AllTools_GetResultSummary_WithError_ReturnsError()
    {
        var tools = GetAllTools();
        foreach (var tool in tools)
        {
            var summary = tool.GetResultSummary("❌ something went wrong");
            summary.Should().Contain("❌", $"{tool.Name} should preserve error prefix");
        }
    }

    [Fact]
    public void GetResultSummary_ListDir_CountsItems()
    {
        var result = "📁 目录: C:\\test\n\n### 子目录\n- 📁 src/\n- 📁 lib/\n\n### 文件\n- 📄 Program.cs\n- 📄 README.md";

        var summary = new ListDirTool().GetResultSummary(result);
        summary.Should().Contain("2 个子目录");
        summary.Should().Contain("2 个文件");
    }

    [Fact]
    public void GetResultSummary_ReadFile_ShowsLineCount()
    {
        var result = "📄 文件: test.cs (共 150 行，显示 1-150)\n\n1: using System;\n2: ...";

        var summary = new ReadFileTool(new()).GetResultSummary(result);
        summary.Should().Contain("150");
        summary.Should().Contain("读取完成");
    }

    [Fact]
    public void GetResultSummary_FileSearch_ShowsCount()
    {
        var result = "🔍 文件搜索: \"*.cs\" (找到 42 个文件)\n\n- `src/a.cs`\n- `src/b.cs`";

        var summary = new FileSearchTool().GetResultSummary(result);
        summary.Should().Contain("42");
    }

    [Fact]
    public void GetResultSummary_GetErrors_NoErrors_ShowsSuccess()
    {
        var summary = new GetErrorsTool().GetResultSummary("0 个错误");
        summary.Should().Contain("✅");
        summary.Should().Contain("无编译错误");
    }

    [Fact]
    public void GetResultSummary_BuildSolution_Success_ShowsSuccess()
    {
        var summary = new BuildSolutionTool().GetResultSummary("构建成功");
        summary.Should().Be("✅ 构建成功");
    }

    [Fact]
    public void GetResultSummary_BuildSolution_Failed_ShowsWarning()
    {
        var summary = new BuildSolutionTool().GetResultSummary("构建失败: 3 errors");
        summary.Should().Be("⚠️ 构建失败");
    }

    [Fact]
    public void GetResultSummary_RunInTerminal_ExitCode0_ShowsSuccess()
    {
        var summary = new RunInTerminalTool().GetResultSummary("exit code: 0");
        summary.Should().Contain("✅");
    }

    #endregion

    #region Helpers

    private static BuiltInToolBase[] GetAllTools()
    {
        return new BuiltInToolBase[]
        {
            new ListDirTool(),
            new ReadFileTool(new()),
            new FileSearchTool(),
            new GrepSearchTool(),
            new GetErrorsTool(),
            new FetchWebpageTool(),
            new BuildSolutionTool(),
            new ReplaceStringInFileTool(),
            new MultiReplaceStringInFileTool(),
            new CreateFileTool(),
            new DeleteFileTool(),
            new ApplyPatchTool(),
            new CreateDirectoryTool(),
            new RunInTerminalTool(),
            new GetTerminalOutputTool(),
            new AskQuestionsTool(),
        };
    }

    private static Dictionary<string, JsonElement> ParseArgs(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
               ?? new Dictionary<string, JsonElement>();
    }

    #endregion
}
