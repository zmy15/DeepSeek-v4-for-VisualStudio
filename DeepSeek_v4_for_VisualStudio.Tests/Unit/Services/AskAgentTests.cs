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
        agent.Definition.SystemPrompt.Should().Contain("git");
        agent.Definition.SystemPrompt.Should().Contain("只读");
    }

    [Fact]
    public void SummaryPrompts_AllowFreeMarkdownOutput()
    {
        var handoffPrompt = global::DeepSeek_v4_for_VisualStudio.Services.LocalizationService.Instance["agent.edit.handoffAskPrompt"];
        var polishPrompt = global::DeepSeek_v4_for_VisualStudio.Services.AiPrompts.SummaryPolishSystemPrompt;

        handoffPrompt.Should().Contain("Markdown");
        handoffPrompt.Should().Contain("Mermaid");
        handoffPrompt.Should().Contain("LaTeX");
        handoffPrompt.Should().Contain("不要求固定结构");

        polishPrompt.Should().Contain("Markdown");
        polishPrompt.Should().Contain("Mermaid");
        polishPrompt.Should().Contain("LaTeX");
        polishPrompt.Should().Contain("不要求固定结构");
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
        agent.Definition.AllowedTools.Should().Contain("git");
        agent.Definition.AllowedTools.Should().Contain("run_in_terminal");
        agent.Definition.AllowedTools.Should().Contain("get_terminal_output");
        // Ask agent has built-in search/read tools for self-service code lookup
        agent.Definition.AllowedTools.Should().Contain("symbol_search");
        agent.Definition.AllowedTools.Should().Contain("file_search");
        agent.Definition.AllowedTools.Should().Contain("grep_search");
        agent.Definition.AllowedTools.Should().Contain("read_file");
        agent.Definition.AllowedTools.Should().Contain("list_dir");
        agent.Definition.AllowedTools.Should().Contain("get_errors");
    }

    [Fact]
    public void Definition_AllowedTools_DoesNotContainModifyTools()
    {
        var agent = new AskAgent(_apiService);

        agent.Definition.AllowedTools.Should().NotContain("replace_string_in_file");
        agent.Definition.AllowedTools.Should().NotContain("create_file");
        agent.Definition.AllowedTools.Should().NotContain("delete_file");
    }

    #endregion

    #region AskTools Static Array

    [Fact]
    public void AskTools_ContainsDelegationAndUtilityTools()
    {
        AskAgent.AskTools.Should().Contain("runSubagent");
        AskAgent.AskTools.Should().Contain("request_handoff");
        AskAgent.AskTools.Should().Contain("fetch_webpage");
        AskAgent.AskTools.Should().Contain("capture_window");
        AskAgent.AskTools.Should().Contain("memory");
        AskAgent.AskTools.Should().Contain("git");
        AskAgent.AskTools.Should().Contain("run_in_terminal");
        AskAgent.AskTools.Should().Contain("get_terminal_output");
        // Ask agent has built-in search/read tools
        AskAgent.AskTools.Should().Contain("symbol_search");
        AskAgent.AskTools.Should().Contain("file_search");
        AskAgent.AskTools.Should().Contain("grep_search");
        AskAgent.AskTools.Should().Contain("read_file");
        AskAgent.AskTools.Should().Contain("list_dir");
        AskAgent.AskTools.Should().Contain("get_errors");
    }

    [Fact]
    public void AskTools_DoesNotContainModifyTools()
    {
        AskAgent.AskTools.Should().NotContain("replace_string_in_file");
        AskAgent.AskTools.Should().NotContain("create_file");
        AskAgent.AskTools.Should().NotContain("create_directory");
        AskAgent.AskTools.Should().NotContain("delete_file");
        AskAgent.AskTools.Should().NotContain("apply_patch");
    }

    #endregion

    #region BuildContextualPrompt

    [Fact]
    public void BuildContextAwareMessages_UsesSessionCurrentUser_AsStandardTurn()
    {
        var contextManager = new ConversationContextManager();
        contextManager.AddUserMessage("你好");

        var context = new AgentContext
        {
            ContextManager = contextManager,
            CurrentUserContent = "你好",
        };
        var agent = new AskAgent(_apiService)
        {
            Context = context,
        };

        var method = typeof(BaseAgent).GetMethod(
            "BuildContextAwareMessages",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(string), typeof(string), typeof(int), typeof(bool) },
            modifiers: null);
        method.Should().NotBeNull();

        var messages = (List<ChatApiMessage>)method!.Invoke(
            agent,
            new object[] { "AskAgent system prompt", string.Empty, int.MaxValue, true })!;

        messages.Count(m => m.Role == "user").Should().Be(1);
        messages.Last(m => m.Role == "user").Content.Should().Be("你好");
        messages.Last().Role.Should().Be("system");
        messages.Last().Content.Should().Be("AskAgent system prompt");
        messages.Count(m => m.Role == "system" && m.Content!.Contains("文件读取规则"))
            .Should().Be(1);
    }

    [Fact]
    public void BuildContextAwareMessages_HandoffPrefix_PlacesBoundaryToolsAndUserCorrectly()
    {
        var contextManager = new ConversationContextManager();
        contextManager.SetIdeContext("[IDE Context] Active File: Test.cs");

        var context = new AgentContext
        {
            ContextManager = contextManager,
            ForwardedMessages = new List<ChatApiMessage>
            {
                new() { Role = "system", Content = "stable system" },
                new() { Role = "assistant", Content = "explore" },
                new() { Role = "tool", Content = "result" },
            },
        };
        var agent = new AskAgent(_apiService)
        {
            Context = context,
        };

        var method = typeof(BaseAgent).GetMethod(
            "BuildContextAwareMessages",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(string), typeof(string), typeof(int), typeof(bool) },
            modifiers: null);
        method.Should().NotBeNull();

        var messages = (List<ChatApiMessage>)method!.Invoke(
            agent,
            new object[] { "Edit agent prompt", "handoff user", int.MaxValue, false })!;

        context.HandoffPrefixLength.Should().Be(3);
        context.ToolHistoryInsertIndex.Should().Be(6);
        messages[3].Role.Should().Be("system");
        messages[3].Content.Should().NotBeNullOrWhiteSpace();
        messages[4].Role.Should().Be("system");
        messages[4].Content.Should().Contain("[IDE Context]");
        messages[5].Role.Should().Be("user");
        messages[5].Content.Should().Be("handoff user");
        messages[6].Role.Should().Be("system");
        messages[6].Content.Should().Be("Edit agent prompt");
    }

    [Fact]
    public void BuildContextualPrompt_WithFileContext_IncludesItWithoutQuestionWrapper()
    {
        var context = new AgentContext
        {
            FileContext = "File content context",
        };

        var result = BuildContextualPromptPublic("帮我分析项目结构", context);

        result.Should().Contain("File content context");
        result.Should().Contain("帮我分析项目结构");
        result.Should().NotContain("[用户问题]");
    }

    [Fact]
    public void BuildContextualPrompt_WithoutFileContext_ExcludesSolutionMetadata()
    {
        var context = new AgentContext
        {
            FileContext = null,
        };

        var result = BuildContextualPromptPublic("帮我分析项目结构", context);

        result.Should().NotContain("当前解决方案");
        result.Should().NotContain("[用户问题]");
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
    public void BuildSummaryMarkdown_WithChanges_UsesAiSummaryDirectly()
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

        result.Should().Contain("AI 生成的变更摘要");
        result.Should().NotContain("测试计划");
        result.Should().NotContain("a.ts");
        result.Should().NotContain("b.ts");
    }

    [Fact]
    public void BuildSummaryMarkdown_WithAiSummary_UsesAiSummaryDirectly()
    {
        var plan = new AgentTaskPlan
        {
            Title = "测试计划",
            ChangedFiles =
            {
                new FileChangeSummary { FilePath = "src/a.ts", LinesAdded = 10, LinesRemoved = 2 },
            },
        };

        string aiSummary = """
            ## 自由总结

            本次实现了完整登录流程。

            ```mermaid
            flowchart LR
                A[登录] --> B[令牌]
            ```
            """;

        var result = BuildSummaryMarkdownPublic(plan, aiSummary);

        result.Should().Be(aiSummary);
        result.Should().NotContain("测试计划");
        result.Should().NotContain("a.ts");
        result.Should().NotContain("步骤执行详情");
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
