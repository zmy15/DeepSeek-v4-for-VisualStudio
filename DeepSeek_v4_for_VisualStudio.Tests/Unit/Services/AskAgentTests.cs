using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// AskAgent 单元测试 — 测试 Agent 定义、工具集和纯逻辑方法。
/// </summary>
public class AskAgentTests
{
    private readonly DeepSeekApiService _apiService;

    public AskAgentTests()
    {
        _apiService = new DeepSeekApiService("test-api-key");
    }

    #region Constructor

    [Fact]
    public void Constructor_WithApiService_CreatesSuccessfully()
    {
        var agent = new AskAgent(_apiService);

        agent.Should().NotBeNull();
        agent.Definition.Should().NotBeNull();
        agent.Definition.Type.Should().Be(AgentType.Ask);
    }

    [Fact]
    public void Constructor_WithNullApiService_ThrowsArgumentNullException()
    {
        Action act = () => new AskAgent(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Agent Definition

    [Fact]
    public void Definition_Name_IsAsk()
    {
        var agent = new AskAgent(_apiService);

        agent.Definition.Name.Should().Be("Ask");
    }

    [Fact]
    public void Definition_IsUserInvocable()
    {
        var agent = new AskAgent(_apiService);

        agent.Definition.UserInvocable.Should().BeTrue();
    }

    [Fact]
    public void Definition_HasNoSubAgents()
    {
        var agent = new AskAgent(_apiService);

        agent.Definition.SubAgents.Should().BeEmpty();
    }

    [Fact]
    public void Definition_HasHandoffs_ToEditPlanAndBuild()
    {
        var agent = new AskAgent(_apiService);

        // AskAgent 有 3 个 Handoff 目标
        agent.Definition.Handoffs.Should().HaveCount(3);
        agent.Definition.Handoffs.Should().Contain(h => h.TargetAgent == AgentType.Edit);
        agent.Definition.Handoffs.Should().Contain(h => h.TargetAgent == AgentType.Plan);
        agent.Definition.Handoffs.Should().Contain(h => h.TargetAgent == AgentType.Build);
    }

    [Fact]
    public void Definition_SystemPrompt_IsNotEmpty()
    {
        var agent = new AskAgent(_apiService);

        agent.Definition.SystemPrompt.Should().NotBeNullOrEmpty();
        agent.Definition.SystemPrompt.Should().Contain("Ask");
    }

    [Fact]
    public void Definition_AllowedTools_ContainsDelegationAndUtilityTools()
    {
        var agent = new AskAgent(_apiService);

        // Ask agent can delegate via runSubagent and handoff to other agents
        agent.Definition.AllowedTools.Should().Contain("runSubagent");
        agent.Definition.AllowedTools.Should().Contain("request_handoff");
        agent.Definition.AllowedTools.Should().Contain("fetch_webpage");
        agent.Definition.AllowedTools.Should().Contain("memory");
    }

    [Fact]
    public void Definition_AllowedTools_DoesNotContainModifyTools()
    {
        var agent = new AskAgent(_apiService);

        agent.Definition.AllowedTools.Should().NotContain("replace_string_in_file");
        agent.Definition.AllowedTools.Should().NotContain("create_file");
        agent.Definition.AllowedTools.Should().NotContain("delete_file");
        agent.Definition.AllowedTools.Should().NotContain("run_in_terminal");
    }

    #endregion

    #region AskTools Static Array

    [Fact]
    public void AskTools_ContainsDelegationAndUtilityTools()
    {
        AskAgent.AskTools.Should().Contain("runSubagent");
        AskAgent.AskTools.Should().Contain("request_handoff");
        AskAgent.AskTools.Should().Contain("fetch_webpage");
        AskAgent.AskTools.Should().Contain("memory");
    }

    [Fact]
    public void AskTools_DoesNotContainModifyTools()
    {
        AskAgent.AskTools.Should().NotContain("replace_string_in_file");
        AskAgent.AskTools.Should().NotContain("create_file");
        AskAgent.AskTools.Should().NotContain("create_directory");
        AskAgent.AskTools.Should().NotContain("delete_file");
        AskAgent.AskTools.Should().NotContain("run_in_terminal");
        AskAgent.AskTools.Should().NotContain("apply_patch");
    }

    #endregion

    #region BuildContextualPrompt

    [Fact]
    public void BuildContextualPrompt_WithSolutionPath_IncludesIt()
    {
        var context = new AgentContext
        {
            SolutionPath = @"C:\Projects\MyApp\MyApp.sln",
        };

        var result = BuildContextualPromptPublic("帮我分析项目结构", context);

        result.Should().Contain("MyApp.sln");
        result.Should().Contain("当前解决方案");
    }

    [Fact]
    public void BuildContextualPrompt_WithoutSolutionPath_ExcludesIt()
    {
        var context = new AgentContext
        {
            SolutionPath = null,
        };

        var result = BuildContextualPromptPublic("帮我分析项目结构", context);

        result.Should().NotContain("当前解决方案");
    }

    [Fact]
    public void BuildContextualPrompt_AlwaysIncludesUserMessage()
    {
        var context = new AgentContext();

        var result = BuildContextualPromptPublic("我的问题是这个", context);

        result.Should().Contain("我的问题是这个");
    }

    #endregion

    #region ParseCodeChangesFromResult (inherited from BaseAgent)

    [Fact]
    public void ParseCodeChangesFromResult_NullOrEmpty_ReturnsEmpty()
    {
        var result1 = ParseCodeChangesPublic(null!);
        var result2 = ParseCodeChangesPublic("");
        var result3 = ParseCodeChangesPublic("   ");

        result1.Should().BeEmpty();
        result2.Should().BeEmpty();
        result3.Should().BeEmpty();
    }

    [Fact]
    public void ParseCodeChangesFromResult_FileFormat_ParsesPathAndContent()
    {
        var input = @"```file: src/app.ts
export const App = () => <div>Hello</div>;
```";

        var changes = ParseCodeChangesPublic(input);

        changes.Should().HaveCount(1);
        changes[0].FilePath.Should().Be("src/app.ts");
        changes[0].NewContent.Should().Contain("Hello");
    }

    [Fact]
    public void ParseCodeChangesFromResult_MultipleFiles_ParsesAll()
    {
        var input = @"```file: src/a.ts
content a
```
```file: src/b.ts
content b
```";

        var changes = ParseCodeChangesPublic(input);

        changes.Should().HaveCount(2);
        changes[0].FilePath.Should().Be("src/a.ts");
        changes[1].FilePath.Should().Be("src/b.ts");
    }

    [Fact]
    public void ParseCodeChangesFromResult_InsertEditFormat_Parsed()
    {
        var input = @"```insert_edit_into_file: src/utils.ts
const x = 1;
// ...existing code...
const y = 2;
```";

        var changes = ParseCodeChangesPublic(input);

        changes.Should().HaveCount(1);
        changes[0].FilePath.Should().Be("src/utils.ts");
        changes[0].BriefDescription.Should().Contain("insert_edit");
    }

    [Fact]
    public void ParseCodeChangesFromResult_PatchFormat_Parsed()
    {
        var input = @"*** Begin Patch
*** Update File: src/config.json
+  ""debug"": true,
*** End Patch";

        var changes = ParseCodeChangesPublic(input);

        changes.Should().HaveCount(1);
        changes[0].FilePath.Should().Be("src/config.json");
        changes[0].BriefDescription.Should().Contain("patch");
    }

    #endregion

    #region BuildSummaryMarkdown

    [Fact]
    public void BuildSummaryMarkdown_WithChanges_IncludesFileList()
    {
        var plan = new AgentTaskPlan
        {
            Title = "测试计划",
            ChangedFiles =
            {
                new FileChangeSummary { FilePath = "src/a.ts", LinesAdded = 10, LinesRemoved = 2 },
                new FileChangeSummary { FilePath = "src/b.ts", LinesAdded = 5, LinesRemoved = 0 },
            },
        };

        var result = BuildSummaryMarkdownPublic(plan, "AI 生成的变更摘要");

        result.Should().Contain("测试计划");
        // BuildSummaryMarkdown uses Path.GetFileName() so only filename shown
        result.Should().Contain("a.ts");
        result.Should().Contain("b.ts");
        result.Should().Contain("AI 生成的变更摘要");
    }

    [Fact]
    public void BuildSummaryMarkdown_NoChanges_ShowsEmptyMessage()
    {
        var plan = new AgentTaskPlan { Title = "空计划" };

        var result = BuildSummaryMarkdownPublic(plan, null);

        result.Should().Contain("空计划");
    }

    #endregion

    // ──────────── Reflection helpers for testing private methods ────────────

    private static string BuildSummaryMarkdownPublic(AgentTaskPlan plan, string? aiSummary)
    {
        var method = typeof(AskAgent).GetMethod("BuildSummaryMarkdown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object?[] { plan, aiSummary })!;
    }

    private static string BuildContextualPromptPublic(string userMessage, AgentContext context)
    {
        var method = typeof(AskAgent).GetMethod("BuildContextualPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { userMessage, context })!;
    }

    private static List<FileChangeSummary> ParseCodeChangesPublic(string aiResult)
    {
        var method = typeof(BaseAgent).GetMethod("ParseCodeChangesFromResult",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (List<FileChangeSummary>)method!.Invoke(null, new object[] { aiResult })!;
    }
}
