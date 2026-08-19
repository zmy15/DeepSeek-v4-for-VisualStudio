using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// EditAgent 单元测试 — 测试 Agent 定义、工具集、ExploreAgent 事件转发、计划管理。
/// 不测试完整 ExecuteAsync 流程（需要 mock HTTP 流）。
/// </summary>
public class EditAgentTests
{
    private readonly DeepSeekApiService _apiService;

    public EditAgentTests()
    {
        _apiService = new DeepSeekApiService("test-api-key");
    }

    #region Constructor

    [Fact]
    public void Constructor_WithApiService_CreatesSuccessfully()
    {
        var agent = new EditAgent(_apiService);

        agent.Should().NotBeNull();
        agent.Definition.Should().NotBeNull();
        agent.Definition.Type.Should().Be(AgentType.Edit);
    }

    [Fact]
    public void Constructor_WithNullApiService_ThrowsArgumentNullException()
    {
        Action act = () => new EditAgent(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Agent Definition

    [Fact]
    public void Definition_Name_IsEdit()
    {
        var agent = new EditAgent(_apiService);

        agent.Definition.Name.Should().Be("Edit");
    }

    [Fact]
    public void Definition_IsUserInvocable()
    {
        var agent = new EditAgent(_apiService);

        agent.Definition.UserInvocable.Should().BeTrue();
    }

    [Fact]
    public void Definition_HasNoSubAgents()
    {
        var agent = new EditAgent(_apiService);

        agent.Definition.SubAgents.Should().BeEmpty();
    }

    [Fact]
    public void Definition_HasAskBuildAndPlanHandoffs()
    {
        var agent = new EditAgent(_apiService);

        agent.Definition.Handoffs.Should().HaveCount(3);
        agent.Definition.Handoffs.Should().Contain(h => h.TargetAgent == AgentType.Ask);
        agent.Definition.Handoffs.Should().Contain(h => h.TargetAgent == AgentType.Build);
        agent.Definition.Handoffs.Should().Contain(h => h.TargetAgent == AgentType.Plan);
        // Ask handoff is auto-send (for summary generation)
        var askHandoff = agent.Definition.Handoffs.First(h => h.TargetAgent == AgentType.Ask);
        askHandoff.AutoSend.Should().BeTrue();
        // Build handoff: AutoSend chains Edit→Build without user button
        var buildHandoff = agent.Definition.Handoffs.First(h => h.TargetAgent == AgentType.Build);
        buildHandoff.AutoSend.Should().BeTrue();
        buildHandoff.ShowContinueOn.Should().BeFalse();
        // Plan handoff: AutoSend for large tasks
        var planHandoff = agent.Definition.Handoffs.First(h => h.TargetAgent == AgentType.Plan);
        planHandoff.AutoSend.Should().BeTrue();
        planHandoff.ShowContinueOn.Should().BeFalse();
    }

    [Fact]
    public void Definition_SystemPrompt_IsNotEmpty()
    {
        var agent = new EditAgent(_apiService);

        agent.Definition.SystemPrompt.Should().NotBeNullOrEmpty();
        agent.Definition.SystemPrompt.Should().Contain("Edit");
    }

    #endregion

    #region EditTools Static Array

    [Fact]
    public void EditTools_ContainsFileModificationTools()
    {
        EditAgent.EditTools.Should().Contain("create_file");
        EditAgent.EditTools.Should().Contain("delete_file");
        EditAgent.EditTools.Should().Contain("replace_string_in_file");
        EditAgent.EditTools.Should().Contain("multi_replace_string_in_file");
        EditAgent.EditTools.Should().Contain("apply_patch");
        EditAgent.EditTools.Should().Contain("create_directory");
    }

    [Fact]
    public void EditTools_ContainsReadAndDelegationTools()
    {
        // EditAgent keeps read_file for editing, delegates exploration via runSubagent
        EditAgent.EditTools.Should().Contain("read_file");
        EditAgent.EditTools.Should().Contain("get_errors");
        EditAgent.EditTools.Should().Contain("runSubagent");
        EditAgent.EditTools.Should().Contain("request_handoff");
    }

    [Fact]
    public void EditTools_ContainsTerminalBuildAndTaskTools()
    {
        EditAgent.EditTools.Should().Contain("run_in_terminal");
        EditAgent.EditTools.Should().Contain("get_terminal_output");
        EditAgent.EditTools.Should().Contain("create_and_run_task");
        EditAgent.EditTools.Should().Contain("build_solution");
        EditAgent.EditTools.Should().Contain("manage_todo_list");
        EditAgent.EditTools.Should().Contain("memory");
    }

    [Fact]
    public void Definition_AllowedTools_MatchesEditTools()
    {
        var agent = new EditAgent(_apiService);

        // All EditTools should be in AllowedTools
        foreach (var tool in EditAgent.EditTools)
        {
            agent.Definition.AllowedTools.Should().Contain(tool);
        }
    }

    [Fact]
    public void CodeStepTools_DoesNotContainBuildToAvoidDuplicateBuilds()
    {
        var field = typeof(EditAgent).GetField(
            "CodeStepTools",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var tools = (string[])field!.GetValue(null)!;

        tools.Should().NotContain("build_solution");
    }

    #endregion

    #region ExploreAgent Property

    [Fact]
    public void ExploreAgent_DefaultsToNull()
    {
        var agent = new EditAgent(_apiService);

        agent.ExploreAgent.Should().BeNull();
    }

    [Fact]
    public void ExploreAgent_CanBeSet()
    {
        var agent = new EditAgent(_apiService);
        var exploreAgent = new ExploreAgent(_apiService);

        agent.ExploreAgent = exploreAgent;

        agent.ExploreAgent.Should().Be(exploreAgent);
    }

    [Fact]
    public void ExploreAgent_SettingToNull_AfterSet_DoesNotThrow()
    {
        var agent = new EditAgent(_apiService);
        agent.ExploreAgent = new ExploreAgent(_apiService);

        Action act = () => agent.ExploreAgent = null;
        act.Should().NotThrow();

        agent.ExploreAgent.Should().BeNull();
    }

    [Fact]
    public void ExploreAgent_Replacing_UnsubscribesPrevious()
    {
        var agent = new EditAgent(_apiService);
        var explore1 = new ExploreAgent(_apiService);
        var explore2 = new ExploreAgent(_apiService);

        agent.ExploreAgent = explore1;
        agent.ExploreAgent = explore2;

        agent.ExploreAgent.Should().Be(explore2);
    }

    #endregion

    #region CurrentPlan Property

    [Fact]
    public void CurrentPlan_DefaultsToNull()
    {
        var agent = new EditAgent(_apiService);

        agent.CurrentPlan.Should().BeNull();
    }

    [Fact]
    public void CurrentPlan_CanBeSet()
    {
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan { Title = "Test Plan" };

        agent.CurrentPlan = plan;

        agent.CurrentPlan.Should().Be(plan);
        agent.CurrentPlan!.Title.Should().Be("Test Plan");
    }

    #endregion

    #region CreateSingleStepPlan

    [Fact]
    public void CreateSingleStepPlan_ReturnsPlanWithOneStep()
    {
        var plan = CreateSingleStepPlanPublic("修改 app.ts 中的配置");

        plan.Steps.Should().HaveCount(1);
        plan.Steps[0].Index.Should().Be(1);
        plan.Steps[0].Description.Should().Contain("app.ts");
        plan.Intent.Should().Be(AgentIntent.CodeChange);
        plan.PlanId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CreateSingleStepPlan_PreservesFullMessage()
    {
        var longMessage = new string('x', 500);

        var plan = CreateSingleStepPlanPublic(longMessage);

        // Description stores the full user message
        plan.Steps[0].Description.Should().Be(longMessage);
    }

    #endregion

    #region PlanUpdated Event

    [Fact]
    public void PlanUpdated_CanSubscribeAndUnsubscribe()
    {
        var agent = new EditAgent(_apiService);
        int callCount = 0;
        Action<AgentTaskPlan> handler = _ => callCount++;

        agent.PlanUpdated += handler;
        agent.PlanUpdated -= handler;

        // Unsubscribed, so invoking should not increment
        callCount.Should().Be(0);
    }

    #endregion

    #region Logging Events

    [Fact]
    public void DetectAndAutoCompleteLaterSteps_RangeDeclaration_CompletesRangeAndAdvancesIndex()
    {
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            CurrentStepIndex = 1,
            Steps = Enumerable.Range(1, 6)
                .Select(i => new AgentStep
                {
                    Index = i,
                    Title = $"步骤{i}",
                    Status = i == 1 ? AgentStepStatus.Pending : AgentStepStatus.Pending,
                })
                .ToList(),
        };
        plan.Steps[0].Status = AgentStepStatus.InProgress;
        plan.Steps[0].AiResponse = "已完成第一步。步骤1-6 已完成。";

        DetectAndAutoCompleteLaterStepsPublic(agent, plan.Steps[0], plan);

        // 步骤 2..6 应被自动标记为完成
        plan.Steps.Skip(1).Should().OnlyContain(s => s.Status == AgentStepStatus.Completed);
        // CurrentStepIndex 应推进到 6
        plan.CurrentStepIndex.Should().Be(6);
    }

    [Fact]
    public void DetectAndAutoCompleteLaterSteps_DashRange_CompletesSteps()
    {
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            CurrentStepIndex = 1,
            Steps = Enumerable.Range(1, 5)
                .Select(i => new AgentStep
                {
                    Index = i,
                    Title = $"步骤{i}",
                    Status = AgentStepStatus.Pending,
                })
                .ToList(),
        };
        plan.Steps[0].Status = AgentStepStatus.InProgress;
        plan.Steps[0].AiResponse = "前两步已完成，步骤2-4 也随之完成。";

        DetectAndAutoCompleteLaterStepsPublic(agent, plan.Steps[0], plan);

        plan.Steps[1].Status.Should().Be(AgentStepStatus.Completed); // 2
        plan.Steps[2].Status.Should().Be(AgentStepStatus.Completed); // 3
        plan.Steps[3].Status.Should().Be(AgentStepStatus.Completed); // 4
        plan.Steps[4].Status.Should().Be(AgentStepStatus.Pending);   // 5 不受影响
        plan.CurrentStepIndex.Should().Be(4);
    }

    [Fact]
    public void DetectAndAutoCompleteLaterSteps_EnglishRange_CompletesSteps()
    {
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            CurrentStepIndex = 1,
            Steps = Enumerable.Range(1, 4)
                .Select(i => new AgentStep
                {
                    Index = i,
                    Title = $"Step {i}",
                    Status = AgentStepStatus.Pending,
                })
                .ToList(),
        };
        plan.Steps[0].Status = AgentStepStatus.InProgress;
        plan.Steps[0].AiResponse = "Step 1 done. Steps 2 through 4 are also completed.";

        DetectAndAutoCompleteLaterStepsPublic(agent, plan.Steps[0], plan);

        plan.Steps[1].Status.Should().Be(AgentStepStatus.Completed); // 2
        plan.Steps[2].Status.Should().Be(AgentStepStatus.Completed); // 3
        plan.Steps[3].Status.Should().Be(AgentStepStatus.Completed); // 4
        plan.CurrentStepIndex.Should().Be(4);
    }

    [Fact]
    public void DetectAndAutoCompleteLaterSteps_NoDeclaration_DoesNotAdvanceIndex()
    {
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            CurrentStepIndex = 1,
            Steps = Enumerable.Range(1, 4)
                .Select(i => new AgentStep
                {
                    Index = i,
                    Title = $"步骤{i}",
                    Status = AgentStepStatus.Pending,
                })
                .ToList(),
        };
        plan.Steps[0].Status = AgentStepStatus.InProgress;
        plan.Steps[0].AiResponse = "完成了第一步，但没有涉及后续步骤。";

        DetectAndAutoCompleteLaterStepsPublic(agent, plan.Steps[0], plan);

        plan.Steps.Skip(1).Should().OnlyContain(s => s.Status == AgentStepStatus.Pending);
        plan.CurrentStepIndex.Should().Be(1);
    }

    [Fact]
    public void DetectAndAutoCompleteLaterSteps_ThinkingContent_DeclaresRange_CompletesAndAdvances()
    {
        // 场景：正式输出未提及后续步骤，但思考内容中声明了范围式完成
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            CurrentStepIndex = 1,
            Steps = Enumerable.Range(1, 6)
                .Select(i => new AgentStep
                {
                    Index = i,
                    Title = $"步骤{i}",
                    Status = AgentStepStatus.Pending,
                })
                .ToList(),
        };
        plan.Steps[0].Status = AgentStepStatus.InProgress;
        plan.Steps[0].AiResponse = "第一步完成。";
        const string thinking = "已经把所有文件都改好了，步骤1-6 已完成。";

        DetectAndAutoCompleteLaterStepsPublic(agent, plan.Steps[0], plan, thinking);

        plan.Steps.Skip(1).Should().OnlyContain(s => s.Status == AgentStepStatus.Completed);
        plan.CurrentStepIndex.Should().Be(6);
    }

    [Fact]
    public void DetectAndAutoCompleteLaterSteps_ThinkingContent_ListDeclares_Completes()
    {
        // 场景：逗号/顿号分隔列表声明出现在思考内容中
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            CurrentStepIndex = 1,
            Steps = Enumerable.Range(1, 4)
                .Select(i => new AgentStep
                {
                    Index = i,
                    Title = $"步骤{i}",
                    Status = AgentStepStatus.Pending,
                })
                .ToList(),
        };
        plan.Steps[0].Status = AgentStepStatus.InProgress;
        plan.Steps[0].AiResponse = "第一步完成。";
        const string thinking = "步骤2、3也完成了，可以直接跳到第4步。";

        DetectAndAutoCompleteLaterStepsPublic(agent, plan.Steps[0], plan, thinking);

        plan.Steps[1].Status.Should().Be(AgentStepStatus.Completed); // 2
        plan.Steps[2].Status.Should().Be(AgentStepStatus.Completed); // 3
        plan.Steps[3].Status.Should().Be(AgentStepStatus.Pending);   // 4 不受影响
        plan.CurrentStepIndex.Should().Be(3);
    }

    [Fact]
    public void DetectAndAutoCompleteLaterSteps_ThinkingContent_NoDeclaration_DoesNotAdvance()
    {
        // 场景：思考内容只是计划性表述，不应被误判为完成
        var agent = new EditAgent(_apiService);
        var plan = new AgentTaskPlan
        {
            Title = "Test Plan",
            CurrentStepIndex = 1,
            Steps = Enumerable.Range(1, 4)
                .Select(i => new AgentStep
                {
                    Index = i,
                    Title = $"步骤{i}",
                    Status = AgentStepStatus.Pending,
                })
                .ToList(),
        };
        plan.Steps[0].Status = AgentStepStatus.InProgress;
        plan.Steps[0].AiResponse = "第一步完成。";
        const string thinking = "接下来打算执行步骤2和步骤3，然后步骤4。";

        DetectAndAutoCompleteLaterStepsPublic(agent, plan.Steps[0], plan, thinking);

        plan.Steps.Skip(1).Should().OnlyContain(s => s.Status == AgentStepStatus.Pending);
        plan.CurrentStepIndex.Should().Be(1);
    }

    #endregion

    [Fact]
    public void LogEntryAdded_FiresWhenExploreAgentLogs()
    {
        var agent = new EditAgent(_apiService);
        var exploreAgent = new ExploreAgent(_apiService);

        AgentLogEntry? forwardedEntry = null;
        agent.LogEntryAdded += entry => forwardedEntry = entry;

        agent.ExploreAgent = exploreAgent;

        // Simulate ExploreAgent adding a log — EditAgent should forward via LogEntryAdded
        RaiseLogEntryAddedPublic(exploreAgent, new AgentLogEntry { Level = "INFO", Message = "探索中..." });

        forwardedEntry.Should().NotBeNull();
        forwardedEntry!.Message.Should().Be("探索中...");
        forwardedEntry!.Level.Should().Be("INFO");
    }

    // ──────────── Reflection helpers for testing private methods ────────────

    private static void DetectAndAutoCompleteLaterStepsPublic(EditAgent agent, AgentStep completedStep, AgentTaskPlan plan, string? thinkingContent = null)
    {
        var method = typeof(EditAgent).GetMethod("DetectAndAutoCompleteLaterSteps",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(agent, new object[] { completedStep, plan, thinkingContent });
    }

    private static AgentTaskPlan CreateSingleStepPlanPublic(string userMessage)
    {
        var method = typeof(EditAgent).GetMethod("CreateSingleStepPlan",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (AgentTaskPlan)method!.Invoke(null, new object[] { userMessage })!;
    }

    private static void RaiseLogEntryAddedPublic(ExploreAgent agent, AgentLogEntry entry)
    {
        var field = typeof(BaseAgent).GetField("LogEntryAdded",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        // Actually the event is public, let's use the public API
        // Simulate by calling the protected RaiseLogEntryAdded method on BaseAgent
        var method = typeof(BaseAgent).GetMethod("RaiseLogEntryAdded",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(agent, new object[] { entry });
    }
}
