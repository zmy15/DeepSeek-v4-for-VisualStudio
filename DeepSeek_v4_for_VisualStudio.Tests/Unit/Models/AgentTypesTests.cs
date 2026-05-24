namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

public class AgentTypesTests
{
    #region AgentType Enum

    [Fact]
    public void AgentType_Enum_HasFiveValues()
    {
        Enum.GetValues(typeof(AgentType)).Length.Should().Be(5);
        Enum.IsDefined(typeof(AgentType), AgentType.Ask).Should().BeTrue();
        Enum.IsDefined(typeof(AgentType), AgentType.Explore).Should().BeTrue();
        Enum.IsDefined(typeof(AgentType), AgentType.Plan).Should().BeTrue();
        Enum.IsDefined(typeof(AgentType), AgentType.Edit).Should().BeTrue();
        Enum.IsDefined(typeof(AgentType), AgentType.Build).Should().BeTrue();
    }

    #endregion

    #region AgentDefinition

    [Fact]
    public void AgentDefinition_Defaults_AreSetCorrectly()
    {
        var def = new AgentDefinition();

        def.Type.Should().Be(default(AgentType)); // Ask = 0
        def.Name.Should().BeEmpty();
        def.Description.Should().BeEmpty();
        def.ArgumentHint.Should().BeEmpty();
        def.UserInvocable.Should().BeTrue();
        def.DisableModelInvocation.Should().BeFalse();
        def.AllowedTools.Should().BeEmpty();
        def.SubAgents.Should().BeEmpty();
        def.Handoffs.Should().BeEmpty();
        def.SystemPrompt.Should().BeEmpty();
    }

    [Fact]
    public void AgentDefinition_CanSetAllProperties()
    {
        var def = new AgentDefinition
        {
            Type = AgentType.Edit,
            Name = "edit",
            Description = "Edit agent",
            ArgumentHint = "[instructions]",
            UserInvocable = false,
            DisableModelInvocation = true,
            AllowedTools = new List<string> { "read_file", "write_file" },
            SubAgents = new List<AgentType> { AgentType.Explore },
            Handoffs = new List<AgentHandoff>
            {
                new() { Label = "Plan", TargetAgent = AgentType.Plan },
            },
            SystemPrompt = "You are an edit agent.",
        };

        def.Type.Should().Be(AgentType.Edit);
        def.Name.Should().Be("edit");
        def.Description.Should().Be("Edit agent");
        def.ArgumentHint.Should().Be("[instructions]");
        def.UserInvocable.Should().BeFalse();
        def.DisableModelInvocation.Should().BeTrue();
        def.AllowedTools.Should().Contain("read_file");
        def.SubAgents.Should().Contain(AgentType.Explore);
        def.Handoffs.Should().HaveCount(1);
        def.SystemPrompt.Should().Be("You are an edit agent.");
    }

    #endregion

    #region AgentHandoff

    [Fact]
    public void AgentHandoff_Defaults_AreSetCorrectly()
    {
        var handoff = new AgentHandoff();

        handoff.Label.Should().BeEmpty();
        handoff.TargetAgent.Should().Be(default(AgentType));
        handoff.Prompt.Should().BeEmpty();
        handoff.AutoSend.Should().BeFalse();
        handoff.ShowContinueOn.Should().BeTrue();
        handoff.Model.Should().BeNull();
    }

    [Fact]
    public void AgentHandoff_CanSetAllProperties()
    {
        var handoff = new AgentHandoff
        {
            Label = "Continue with Edit",
            TargetAgent = AgentType.Edit,
            Prompt = "Please implement the plan.",
            AutoSend = true,
            ShowContinueOn = false,
            Model = "deepseek-chat",
        };

        handoff.Label.Should().Be("Continue with Edit");
        handoff.TargetAgent.Should().Be(AgentType.Edit);
        handoff.Prompt.Should().Be("Please implement the plan.");
        handoff.AutoSend.Should().BeTrue();
        handoff.ShowContinueOn.Should().BeFalse();
        handoff.Model.Should().Be("deepseek-chat");
    }

    #endregion

    #region AgentRoutingResult

    [Fact]
    public void AgentRoutingResult_Defaults_AreSetCorrectly()
    {
        var result = new AgentRoutingResult();

        result.TargetAgent.Should().Be(AgentType.Ask);
        result.Confidence.Should().Be("medium");
        result.Reason.Should().BeNull();
        result.NeedsPlanning.Should().BeFalse();
        result.IsExplicit.Should().BeFalse();
    }

    [Fact]
    public void AgentRoutingResult_CanSetAllProperties()
    {
        var result = new AgentRoutingResult
        {
            TargetAgent = AgentType.Plan,
            Confidence = "high",
            Reason = "User requested planning",
            NeedsPlanning = true,
            IsExplicit = true,
        };

        result.TargetAgent.Should().Be(AgentType.Plan);
        result.Confidence.Should().Be("high");
        result.Reason.Should().Be("User requested planning");
        result.NeedsPlanning.Should().BeTrue();
        result.IsExplicit.Should().BeTrue();
    }

    #endregion

    #region AgentContext

    [Fact]
    public void AgentContext_Defaults_AreSetCorrectly()
    {
        var context = new AgentContext();

        context.SolutionPath.Should().BeNull();
        context.FileContext.Should().BeNull();
        context.ActivePlan.Should().BeNull();
        context.PlanFilePath.Should().BeNull();
        context.IsPlanningMode.Should().BeFalse();
        context.ConversationHistory.Should().BeEmpty();
    }

    [Fact]
    public void AgentContext_CanSetAllProperties()
    {
        var context = new AgentContext
        {
            SolutionPath = @"C:\Projects\MyApp\MyApp.sln",
            FileContext = "File contents here",
            PlanFilePath = @"C:\Projects\MyApp\plan.md",
            IsPlanningMode = true,
        };

        context.SolutionPath.Should().Be(@"C:\Projects\MyApp\MyApp.sln");
        context.FileContext.Should().Be("File contents here");
        context.PlanFilePath.Should().Be(@"C:\Projects\MyApp\plan.md");
        context.IsPlanningMode.Should().BeTrue();
    }

    #endregion

    #region AgentResult

    [Fact]
    public void AgentResult_Defaults_AreSetCorrectly()
    {
        var result = new AgentResult();

        result.Success.Should().BeTrue(); // 默认 true
        result.Content.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
        result.FileChanges.Should().BeEmpty();
    }

    [Fact]
    public void AgentResult_SuccessResult_HasContent()
    {
        var result = new AgentResult
        {
            Success = true,
            Content = "Task completed.",
        };

        result.Success.Should().BeTrue();
        result.Content.Should().Be("Task completed.");
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void AgentResult_FailureResult_HasError()
    {
        var result = new AgentResult
        {
            Success = false,
            ErrorMessage = "Something went wrong.",
        };

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Something went wrong.");
    }

    #endregion

    #region SubagentTask / SubagentResult

    [Fact]
    public void SubagentTask_Defaults_AreSetCorrectly()
    {
        var task = new SubagentTask();

        task.TaskId.Should().NotBeNullOrEmpty();
        task.AgentType.Should().Be(AgentType.Explore);
        task.Prompt.Should().BeEmpty();
        task.SearchArea.Should().BeNull();
    }

    [Fact]
    public void SubagentTask_CanSetAllProperties()
    {
        var task = new SubagentTask
        {
            TaskId = "task-1",
            AgentType = AgentType.Ask,
            Prompt = "Find all usages of Foo",
            SearchArea = "src directory",
        };

        task.TaskId.Should().Be("task-1");
        task.AgentType.Should().Be(AgentType.Ask);
        task.Prompt.Should().Be("Find all usages of Foo");
        task.SearchArea.Should().Be("src directory");
    }

    [Fact]
    public void SubagentResult_Defaults_AreSetCorrectly()
    {
        var result = new SubagentResult();

        result.TaskId.Should().BeEmpty();
        result.Success.Should().BeTrue(); // 默认 true
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void SubagentResult_CanSetAllProperties()
    {
        var result = new SubagentResult
        {
            TaskId = "task-1",
            Success = true,
            Findings = "Found 5 usages",
            ErrorMessage = null,
        };

        result.TaskId.Should().Be("task-1");
        result.Success.Should().BeTrue();
        result.Findings.Should().Be("Found 5 usages");
        result.RelevantFiles.Should().BeEmpty();
        result.KeySymbols.Should().BeEmpty();
    }

    #endregion

    #region AgentIntentMapper

    [Fact]
    public void AgentIntentMapper_Ask_ReturnsQandA()
    {
        var intent = AgentType.Ask.ToIntent();
        intent.Should().Be(AgentIntent.QandA);
    }

    [Fact]
    public void AgentIntentMapper_Explore_ReturnsQandA()
    {
        var intent = AgentType.Explore.ToIntent();
        intent.Should().Be(AgentIntent.QandA);
    }

    [Fact]
    public void AgentIntentMapper_Plan_ReturnsCodeChange()
    {
        var intent = AgentType.Plan.ToIntent();
        intent.Should().Be(AgentIntent.CodeChange);
    }

    [Fact]
    public void AgentIntentMapper_Edit_ReturnsCodeChange()
    {
        var intent = AgentType.Edit.ToIntent();
        intent.Should().Be(AgentIntent.CodeChange);
    }

    [Fact]
    public void AgentIntentMapper_AllAgentTypes_ReturnDefinedValue()
    {
        foreach (AgentType type in Enum.GetValues(typeof(AgentType)))
        {
            var intent = type.ToIntent();
            // 所有值都应返回有效的 AgentIntent 枚举值
            Enum.IsDefined(typeof(AgentIntent), intent).Should().BeTrue($"AgentType.{type} should map to a valid AgentIntent");
        }
    }

    #endregion

    #region AgentSuggestionItem

    [Fact]
    public void AgentSuggestionItem_Defaults_AreSetCorrectly()
    {
        var item = new AgentSuggestionItem();

        item.Name.Should().BeEmpty();
        item.AgentType.Should().Be(AgentType.Ask);
        item.Description.Should().BeEmpty();
        item.Icon.Should().BeEmpty();
    }

    [Fact]
    public void AgentSuggestionItem_DisplayPrefix_ReturnsAtSignWithName()
    {
        var item = new AgentSuggestionItem
        {
            Name = "explore",
            AgentType = AgentType.Explore,
        };

        item.DisplayPrefix.Should().Be("@explore");
    }

    [Fact]
    public void AgentSuggestionItem_TooltipText_ContainsTypeAndName()
    {
        var item = new AgentSuggestionItem
        {
            Name = "edit",
            Description = "Edit your code",
            AgentType = AgentType.Edit,
        };

        item.TooltipText.Should().Contain("@edit");
        item.TooltipText.Should().Contain("Edit your code");
    }

    #endregion

    #region AgentFileChangeEventArgs

    [Fact]
    public void AgentFileChangeEventArgs_Defaults_AreSetCorrectly()
    {
        var args = new AgentFileChangeEventArgs();

        args.PlanId.Should().BeEmpty();
        args.ChangeType.Should().Be("modify");
        args.FilePath.Should().BeEmpty();
        args.Detail.Should().BeEmpty();
    }

    [Fact]
    public void AgentFileChangeEventArgs_CanSetAllProperties()
    {
        var args = new AgentFileChangeEventArgs
        {
            PlanId = "abc-123",
            ChangeType = "modify",
            FilePath = "Program.cs",
            Detail = "Added new method",
        };

        args.PlanId.Should().Be("abc-123");
        args.ChangeType.Should().Be("modify");
        args.FilePath.Should().Be("Program.cs");
        args.Detail.Should().Be("Added new method");
    }

    #endregion

    #region AgentPermissionRequest

    [Fact]
    public void AgentPermissionRequest_Defaults_AreSetCorrectly()
    {
        var req = new AgentPermissionRequest();

        req.RequestId.Should().NotBeNullOrEmpty();
        req.Title.Should().BeEmpty();
        req.Command.Should().BeEmpty();
        req.ActionType.Should().Be("command");
        req.FilePaths.Should().BeEmpty();
    }

    [Fact]
    public void AgentPermissionRequest_CanSetAllProperties()
    {
        var req = new AgentPermissionRequest
        {
            RequestId = "req-1",
            Title = "Delete file",
            Command = "Delete test.txt?",
            ActionType = "file_delete",
            FilePaths = new List<string> { "test.txt" },
        };

        req.RequestId.Should().Be("req-1");
        req.Title.Should().Be("Delete file");
        req.Command.Should().Be("Delete test.txt?");
        req.ActionType.Should().Be("file_delete");
        req.FilePaths.Should().Contain("test.txt");
    }

    #endregion

    #region AgentLogEntry

    [Fact]
    public void AgentLogEntry_Defaults_AreSetCorrectly()
    {
        var entry = new AgentLogEntry();

        entry.Level.Should().Be("INFO");
        entry.Message.Should().BeEmpty();
        entry.Timestamp.Should().BeAfter(DateTime.MinValue);
    }

    [Fact]
    public void AgentLogEntry_CanSetAllProperties()
    {
        var now = DateTime.Now;
        var entry = new AgentLogEntry
        {
            Level = "WARN",
            Message = "Step completed",
            Timestamp = now,
        };

        entry.Level.Should().Be("WARN");
        entry.Message.Should().Be("Step completed");
        entry.Timestamp.Should().Be(now);
    }

    #endregion

    #region AgentStep / AgentTaskPlan

    [Fact]
    public void AgentStep_Defaults_AreSetCorrectly()
    {
        var step = new AgentStep();

        step.Index.Should().Be(0);
        step.Title.Should().BeEmpty();
        step.Description.Should().BeEmpty();
        step.Status.Should().Be(AgentStepStatus.Pending);
    }

    [Fact]
    public void AgentStep_CanSetAllProperties()
    {
        var step = new AgentStep
        {
            Index = 1,
            Title = "Analyze code",
            Description = "Run static analysis",
            Status = AgentStepStatus.Completed,
        };

        step.Index.Should().Be(1);
        step.Title.Should().Be("Analyze code");
        step.Description.Should().Be("Run static analysis");
        step.Status.Should().Be(AgentStepStatus.Completed);
    }

    [Fact]
    public void AgentTaskPlan_Defaults_AreSetCorrectly()
    {
        var plan = new AgentTaskPlan();

        plan.PlanId.Should().NotBeNullOrEmpty(); // GUID 自动生成
        plan.Title.Should().BeEmpty();
        plan.Steps.Should().BeEmpty();
        plan.PlanFilePath.Should().BeNull();
        plan.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void AgentTaskPlan_CanSetAllProperties()
    {
        var plan = new AgentTaskPlan
        {
            PlanId = "custom-id",
            Title = "Refactor module",
            Steps = new List<AgentStep>
            {
                new() { Title = "Step 1" },
                new() { Title = "Step 2" },
            },
            PlanFilePath = @"C:\plan.md",
        };

        plan.PlanId.Should().Be("custom-id");
        plan.Title.Should().Be("Refactor module");
        plan.Steps.Should().HaveCount(2);
        plan.PlanFilePath.Should().Be(@"C:\plan.md");
    }

    [Fact]
    public void AgentTaskPlan_AutoGeneratedPlanId_IsNotEmpty()
    {
        var plan = new AgentTaskPlan();

        plan.PlanId.Should().NotBeNullOrEmpty();
    }

    #endregion
}
