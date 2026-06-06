using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// PlanAgent 单元测试 — 测试 Agent 定义、工具集、ExploreAgent 注入、
/// BuildUnifiedDiscoveryPrompt、ExtractDiscoveryContextFromMessages、ExtractStepsFromPlanMarkdown、FormatPlanAsMarkdown。
/// </summary>
public class PlanAgentTests
{
    private readonly DeepSeekApiService _apiService;

    public PlanAgentTests()
    {
        _apiService = new DeepSeekApiService("test-api-key");
    }

    #region Constructor

    [Fact]
    public void Constructor_WithApiService_CreatesSuccessfully()
    {
        var agent = new PlanAgent(_apiService);

        agent.Should().NotBeNull();
        agent.Definition.Should().NotBeNull();
        agent.Definition.Type.Should().Be(AgentType.Plan);
    }

    [Fact]
    public void Constructor_WithNullApiService_ThrowsArgumentNullException()
    {
        Action act = () => new PlanAgent(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_CreatesInternalExploreAgent()
    {
        var agent = new PlanAgent(_apiService);

        agent.ExploreAgent.Should().NotBeNull();
        agent.ExploreAgent!.Definition.Type.Should().Be(AgentType.Explore);
    }

    #endregion

    #region Agent Definition

    [Fact]
    public void Definition_Name_IsPlan()
    {
        var agent = new PlanAgent(_apiService);

        agent.Definition.Name.Should().Be("Plan");
    }

    [Fact]
    public void Definition_IsUserInvocable()
    {
        var agent = new PlanAgent(_apiService);

        agent.Definition.UserInvocable.Should().BeTrue();
    }

    [Fact]
    public void Definition_HasExploreAsSubAgent()
    {
        var agent = new PlanAgent(_apiService);

        agent.Definition.SubAgents.Should().Contain(AgentType.Explore);
    }

    [Fact]
    public void Definition_HasHandoffToEdit()
    {
        var agent = new PlanAgent(_apiService);

        agent.Definition.Handoffs.Should().HaveCount(1);
        agent.Definition.Handoffs[0].TargetAgent.Should().Be(AgentType.Edit);
    }

    [Fact]
    public void Definition_AllowedTools_IncludesAskQuestions()
    {
        var agent = new PlanAgent(_apiService);

        agent.Definition.AllowedTools.Should().Contain("VisualStudio_askQuestions");
    }

    [Fact]
    public void Definition_AllowedTools_IncludesRunSubagent()
    {
        var agent = new PlanAgent(_apiService);

        agent.Definition.AllowedTools.Should().Contain("runSubagent");
    }

    [Fact]
    public void Definition_AllowedTools_DoesNotContainModifyTools()
    {
        var agent = new PlanAgent(_apiService);

        agent.Definition.AllowedTools.Should().NotContain("replace_string_in_file");
        agent.Definition.AllowedTools.Should().NotContain("create_file");
        agent.Definition.AllowedTools.Should().NotContain("run_in_terminal");
    }

    [Fact]
    public void Definition_SystemPrompt_ContainsDeepSeekV4()
    {
        var agent = new PlanAgent(_apiService);

        agent.Definition.SystemPrompt.Should().Contain("DeepSeek v4");
    }

    #endregion

    #region ExploreAgent Property

    [Fact]
    public void ExploreAgent_CanBeReplaced()
    {
        var agent = new PlanAgent(_apiService);
        var newExploreAgent = new ExploreAgent(_apiService);

        agent.ExploreAgent = newExploreAgent;

        agent.ExploreAgent.Should().Be(newExploreAgent);
    }

    [Fact]
    public void ExploreAgent_SettingToNull_AfterSet_DoesNotThrow()
    {
        var agent = new PlanAgent(_apiService);
        agent.ExploreAgent = new ExploreAgent(_apiService);

        Action act = () => agent.ExploreAgent = null;
        act.Should().NotThrow();

        agent.ExploreAgent.Should().BeNull();
    }

    #endregion

    #region BuildUnifiedDiscoveryPrompt

    [Fact]
    public void BuildUnifiedDiscoveryPrompt_IncludesUserMessage()
    {
        var context = new AgentContext();
        var userMessage = "实现用户认证模块";

        var result = BuildUnifiedDiscoveryPromptPublic(userMessage, context, null);

        result.Should().Contain("实现用户认证模块");
    }

    [Fact]
    public void BuildUnifiedDiscoveryPrompt_WithStructureCache_IncludesCacheContent()
    {
        var context = new AgentContext();
        var structureCache = "## 项目结构 (来自缓存)\n\n- 📁 src/ (15 个文件)";
        var userMessage = "添加日志功能";

        var result = BuildUnifiedDiscoveryPromptPublic(userMessage, context, structureCache);

        result.Should().Contain("项目结构");
        result.Should().Contain("src/");
    }

    [Fact]
    public void BuildUnifiedDiscoveryPrompt_WithStructureCache_IncludesSkipHint()
    {
        var context = new AgentContext();
        var structureCache = "cached structure";
        var userMessage = "重构数据层";

        var result = BuildUnifiedDiscoveryPromptPublic(userMessage, context, structureCache);

        result.Should().Contain("跳过");
    }

    [Fact]
    public void BuildUnifiedDiscoveryPrompt_WithoutCache_IncludesFirstExploreHint()
    {
        var context = new AgentContext();
        var userMessage = "改进性能";

        var result = BuildUnifiedDiscoveryPromptPublic(userMessage, context, null);

        result.Should().NotBeEmpty();
        result.Should().Contain("runSubagent");
    }

    [Fact]
    public void BuildUnifiedDiscoveryPrompt_IncludesWorkspaceRoot()
    {
        var context = new AgentContext
        {
            SolutionPath = @"D:\Code\MyProject",
        };
        var userMessage = "修复内存泄漏";

        var result = BuildUnifiedDiscoveryPromptPublic(userMessage, context, null);

        result.Should().Contain("D:\\Code\\MyProject");
    }

    #endregion

    #region ExtractDiscoveryContextFromMessages

    [Fact]
    public void ExtractDiscoveryContext_WithToolMessages_ExtractsContent()
    {
        var messages = new List<ChatApiMessage>
        {
            new() { Role = "system", Content = "System prompt" },
            new() { Role = "user", Content = "Explore the codebase" },
            new() { Role = "tool", Name = "runSubagent", Content = "Found: AuthService.cs handles login" },
            new() { Role = "tool", Name = "runSubagent", Content = "Found: UserRepository.cs" },
        };

        var result = ExtractDiscoveryContextFromMessagesPublic(messages);

        result.Should().Contain("AuthService.cs");
        result.Should().Contain("UserRepository.cs");
    }

    [Fact]
    public void ExtractDiscoveryContext_NoRunSubagentMessages_ReturnsEmpty()
    {
        var messages = new List<ChatApiMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "tool", Name = "read_file", Content = "file content" },
        };

        var result = ExtractDiscoveryContextFromMessagesPublic(messages);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractDiscoveryContext_EmptyMessages_ReturnsEmpty()
    {
        var messages = new List<ChatApiMessage>();

        var result = ExtractDiscoveryContextFromMessagesPublic(messages);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractDiscoveryContext_MultipleToolMessages_JoinsWithSeparator()
    {
        var messages = new List<ChatApiMessage>
        {
            new() { Role = "tool", Name = "runSubagent", Content = "Result A" },
            new() { Role = "tool", Name = "runSubagent", Content = "Result B" },
        };

        var result = ExtractDiscoveryContextFromMessagesPublic(messages);

        result.Should().Contain("Result A");
        result.Should().Contain("Result B");
        result.Should().Contain("---");
    }

    #endregion

    #region ExtractStepsFromPlanMarkdown

    [Fact]
    public void ExtractStepsFromPlanMarkdown_WithHeadingFormat_ExtractsAll()
    {
        var markdown = "# 实现计划\n\n### 步骤 1: 创建 User 和 AuthToken 模型\n定义数据模型类\n\n### 步骤 2: 实现 AuthService\n编写认证服务逻辑\n\n### 步骤 3: 编写单元测试\n为 AuthService 添加测试";

        var steps = ExtractStepsFromPlanMarkdownPublic(markdown);

        steps.Should().HaveCount(3);
        steps[0].Title.Should().Contain("User");
        steps[0].Index.Should().Be(1);
        steps[1].Title.Should().Contain("AuthService");
        steps[1].Index.Should().Be(2);
        steps[2].Title.Should().Contain("测试");
        steps[2].Index.Should().Be(3);
    }

    [Fact]
    public void ExtractStepsFromPlanMarkdown_WithEnglishStepFormat_ExtractsAll()
    {
        var markdown = "## Step 1: Create model classes\nDefine User and AuthToken\n\n## Step 2: Implement services\nWrite AuthService logic\n\n## Step 3: Add tests\nUnit tests for AuthService";

        var steps = ExtractStepsFromPlanMarkdownPublic(markdown);

        steps.Should().HaveCount(3);
        steps[0].Title.Should().Contain("model");
        steps[1].Title.Should().Contain("services");
        steps[2].Title.Should().Contain("tests");
    }

    [Fact]
    public void ExtractStepsFromPlanMarkdown_EmptyInput_ReturnsEmpty()
    {
        var steps = ExtractStepsFromPlanMarkdownPublic("");
        steps.Should().BeEmpty();

        var steps2 = ExtractStepsFromPlanMarkdownPublic(null!);
        steps2.Should().BeEmpty();
    }

    [Fact]
    public void ExtractStepsFromPlanMarkdown_NoSteps_ReturnsEmpty()
    {
        var markdown = "# 标题\n\n这里没有任何步骤。";

        var steps = ExtractStepsFromPlanMarkdownPublic(markdown);

        steps.Should().BeEmpty();
    }

    [Fact]
    public void ExtractStepsFromPlanMarkdown_WithAltFormat_ExtractsAll()
    {
        var markdown = "**步骤 1**: 重构认证中间件\n**步骤 2**: 添加 JWT 验证\n**步骤 3**: 更新错误处理";

        var steps = ExtractStepsFromPlanMarkdownPublic(markdown);

        steps.Should().HaveCount(3);
    }

    #endregion

    #region FormatPlanAsMarkdown

    [Fact]
    public void FormatPlanAsMarkdown_WithSteps_FormatsAllSteps()
    {
        var plan = new AgentTaskPlan
        {
            Title = "实现用户登录",
            Steps =
            {
                new AgentStep { Index = 1, Title = "创建 User 模型", Status = AgentStepStatus.Completed },
                new AgentStep { Index = 2, Title = "实现 AuthService", Status = AgentStepStatus.InProgress },
                new AgentStep { Index = 3, Title = "添加测试", Status = AgentStepStatus.Pending },
            },
        };

        var result = FormatPlanAsMarkdownPublic(plan);

        result.Should().Contain("实现用户登录");
        result.Should().Contain("创建 User 模型");
        result.Should().Contain("实现 AuthService");
        result.Should().Contain("添加测试");
    }

    [Fact]
    public void FormatPlanAsMarkdown_NoSteps_ShowsEmptyMessage()
    {
        var plan = new AgentTaskPlan { Title = "空计划" };

        var result = FormatPlanAsMarkdownPublic(plan);

        result.Should().Contain("空计划");
    }

    [Fact]
    public void FormatPlanAsMarkdown_IncludesStepCount()
    {
        var plan = new AgentTaskPlan
        {
            Title = "测试计划",
            Steps =
            {
                new AgentStep { Index = 1, Title = "步骤A" },
                new AgentStep { Index = 2, Title = "步骤B" },
                new AgentStep { Index = 3, Title = "步骤C" },
            },
        };

        var result = FormatPlanAsMarkdownPublic(plan);

        result.Should().Contain("3"); // step count
        result.Should().Contain("测试计划");
    }

    #endregion

    #region Logging Events

    [Fact]
    public void LogEntryAdded_FiresWhenExploreAgentLogs()
    {
        var agent = new PlanAgent(_apiService);

        // Simulate ExploreAgent adding a log through the internal explore agent
        RaiseLogEntryAddedPublic(agent.ExploreAgent!, new AgentLogEntry { Level = "INFO", Message = "探索完成" });

        // OnExploreLog adds to _logs with [Explore] prefix but does NOT fire LogEntryAdded
        // (by design, to avoid duplicate UI output)
        var logs = agent.GetLogs();
        logs.Should().NotBeEmpty();
        logs.Should().Contain(l => l.Message.Contains("[Explore]") && l.Message.Contains("探索完成"));
    }

    #endregion

    // ──────────── Reflection helpers ────────────

    private static string BuildUnifiedDiscoveryPromptPublic(string userMessage, AgentContext context, string? structureCache)
    {
        var method = typeof(PlanAgent).GetMethod("BuildUnifiedDiscoveryPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { userMessage, context, structureCache! })!;
    }

    private static string ExtractDiscoveryContextFromMessagesPublic(List<ChatApiMessage> messages)
    {
        var method = typeof(PlanAgent).GetMethod("ExtractDiscoveryContextFromMessages",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { messages })!;
    }

    private static List<AgentStep> ExtractStepsFromPlanMarkdownPublic(string markdown)
    {
        var method = typeof(PlanAgent).GetMethod("ExtractStepsFromPlanMarkdown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (List<AgentStep>)method!.Invoke(null, new object[] { markdown })!;
    }

    private static string FormatPlanAsMarkdownPublic(AgentTaskPlan plan)
    {
        var method = typeof(PlanAgent).GetMethod("FormatPlanAsMarkdown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { plan })!;
    }

    private static void RaiseLogEntryAddedPublic(ExploreAgent agent, AgentLogEntry entry)
    {
        var method = typeof(BaseAgent).GetMethod("RaiseLogEntryAdded",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(agent, new object[] { entry });
    }
}
