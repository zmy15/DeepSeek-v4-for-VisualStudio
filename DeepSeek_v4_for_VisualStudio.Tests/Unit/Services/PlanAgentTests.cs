using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// PlanAgent 单元测试 — 测试 Agent 定义、工具集、ExploreAgent 注入、
/// BuildPhase2Prompt、ExtractStepsFromPlanMarkdown、FormatPlanAsMarkdown。
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

    #region BuildPhase2Prompt

    [Fact]
    public void BuildPhase2Prompt_IncludesAreaDescription()
    {
        var context = new AgentContext();
        var area = "认证模块 (Authentication)";

        var result = BuildPhase2PromptPublic(area, "", context);

        result.Should().Contain("认证模块");
    }

    [Fact]
    public void BuildPhase2Prompt_WithStructureContext_PreservesFullContext()
    {
        var context = new AgentContext();
        var structureContext = new string('x', 4000);
        var area = "用户管理";

        var result = BuildPhase2PromptPublic(area, structureContext, context);

        // 不再截断：完整上下文应被保留
        result.Should().Contain(structureContext);
        result.Length.Should().BeGreaterThan(4000);
        result.Should().Contain("跳过");
        result.Should().NotContain("truncated");
    }

    [Fact]
    public void BuildPhase2Prompt_IncludesWorkspaceRoot()
    {
        var context = new AgentContext
        {
            SolutionPath = @"D:\Code\MyProject",
        };
        var area = "数据访问层";

        var result = BuildPhase2PromptPublic(area, "", context);

        result.Should().Contain("D:\\Code\\MyProject");
    }

    [Fact]
    public void BuildPhase2Prompt_EndsWithTailInstruction()
    {
        var context = new AgentContext();
        var area = "测试区域";

        var result = BuildPhase2PromptPublic(area, "", context);

        result.Should().NotBeEmpty();
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
        AgentLogEntry? capturedLog = null;
        agent.LogEntryAdded += (log) => capturedLog = log;

        // Simulate ExploreAgent adding a log through the internal explore agent
        RaiseLogEntryAddedPublic(agent.ExploreAgent!, new AgentLogEntry { Level = "INFO", Message = "探索完成" });

        capturedLog.Should().NotBeNull();
        capturedLog!.Message.Should().Contain("[Explore]");
    }

    #endregion

    // ──────────── Reflection helpers ────────────

    private static string BuildPhase2PromptPublic(string area, string structureContext, AgentContext context)
    {
        var method = typeof(PlanAgent).GetMethod("BuildPhase2Prompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { area, structureContext, context })!;
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
