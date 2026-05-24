namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

public class AgentModelsTests
{
    #region AgentIntent Enum

    [Fact]
    public void AgentIntent_HasQandAAndCodeChange()
    {
        Enum.GetValues(typeof(AgentIntent)).Length.Should().Be(2);
        Enum.IsDefined(typeof(AgentIntent), AgentIntent.QandA).Should().BeTrue();
        Enum.IsDefined(typeof(AgentIntent), AgentIntent.CodeChange).Should().BeTrue();
    }

    #endregion

    #region AgentIntentMapper

    [Theory]
    [InlineData(AgentType.Ask, AgentIntent.QandA)]
    [InlineData(AgentType.Explore, AgentIntent.QandA)]
    [InlineData(AgentType.Plan, AgentIntent.CodeChange)]
    [InlineData(AgentType.Edit, AgentIntent.CodeChange)]
    public void ToIntent_MapsCorrectly(AgentType agentType, AgentIntent expected)
    {
        agentType.ToIntent().Should().Be(expected);
    }

    [Fact]
    public void ToIntent_DefaultMapsToQandA()
    {
        // Cast an invalid value to AgentType
        ((AgentType)99).ToIntent().Should().Be(AgentIntent.QandA);
    }

    #endregion

    #region AgentStep

    [Fact]
    public void AgentStep_Defaults_AreSetCorrectly()
    {
        var step = new AgentStep();

        step.Index.Should().Be(0);
        step.Title.Should().BeEmpty();
        step.Description.Should().BeEmpty();
        step.Status.Should().Be(AgentStepStatus.Pending);
        step.ResultSummary.Should().BeNull();
        step.RequiresApproval.Should().BeFalse();
        step.PendingCommand.Should().BeNull();
        step.AiResponse.Should().BeNull();
    }

    [Fact]
    public void AgentStep_CanSetAllProperties()
    {
        var step = new AgentStep
        {
            Index = 3,
            Title = "重构认证模块",
            Description = "将 JWT 验证逻辑提取到独立中间件",
            Status = AgentStepStatus.Completed,
            ResultSummary = "成功重构，所有测试通过",
            RequiresApproval = true,
            PendingCommand = "dotnet test",
            AiResponse = "已完成重构...",
        };

        step.Index.Should().Be(3);
        step.Title.Should().Be("重构认证模块");
        step.Description.Should().Contain("JWT");
        step.Status.Should().Be(AgentStepStatus.Completed);
        step.ResultSummary.Should().Contain("成功");
        step.RequiresApproval.Should().BeTrue();
        step.PendingCommand.Should().Be("dotnet test");
        step.AiResponse.Should().Contain("重构");
    }

    #endregion

    #region AgentStepStatus Enum

    [Fact]
    public void AgentStepStatus_HasSixValues()
    {
        Enum.GetValues(typeof(AgentStepStatus)).Length.Should().Be(6);
        Enum.IsDefined(typeof(AgentStepStatus), AgentStepStatus.Pending).Should().BeTrue();
        Enum.IsDefined(typeof(AgentStepStatus), AgentStepStatus.InProgress).Should().BeTrue();
        Enum.IsDefined(typeof(AgentStepStatus), AgentStepStatus.WaitingApproval).Should().BeTrue();
        Enum.IsDefined(typeof(AgentStepStatus), AgentStepStatus.Completed).Should().BeTrue();
        Enum.IsDefined(typeof(AgentStepStatus), AgentStepStatus.Skipped).Should().BeTrue();
        Enum.IsDefined(typeof(AgentStepStatus), AgentStepStatus.Failed).Should().BeTrue();
    }

    #endregion

    #region AgentTaskPlan

    [Fact]
    public void AgentTaskPlan_Defaults_AreSetCorrectly()
    {
        var plan = new AgentTaskPlan();

        plan.PlanId.Should().NotBeNullOrEmpty();
        plan.PlanId.Length.Should().Be(8); // Substring(0,8) of Guid
        plan.Intent.Should().Be(AgentIntent.QandA);
        plan.Title.Should().BeEmpty();
        plan.Steps.Should().BeEmpty();
        plan.ChangedFiles.Should().BeEmpty();
        plan.CurrentStepIndex.Should().Be(0);
        plan.IsCompleted.Should().BeFalse();
        plan.IsCancelled.Should().BeFalse();
        plan.PlanFilePath.Should().BeNull();
        plan.IsFromPlanAgent.Should().BeFalse();
    }

    [Fact]
    public void AgentTaskPlan_CanAddSteps()
    {
        var plan = new AgentTaskPlan
        {
            Title = "实现用户登录",
            Intent = AgentIntent.CodeChange,
        };
        plan.Steps.Add(new AgentStep { Index = 1, Title = "创建 User 模型" });
        plan.Steps.Add(new AgentStep { Index = 2, Title = "实现 AuthService" });

        plan.Steps.Should().HaveCount(2);
        plan.Steps[0].Title.Should().Be("创建 User 模型");
        plan.Title.Should().Be("实现用户登录");
        plan.Intent.Should().Be(AgentIntent.CodeChange);
    }

    [Fact]
    public void AgentTaskPlan_PlanId_IsUnique()
    {
        var plan1 = new AgentTaskPlan();
        var plan2 = new AgentTaskPlan();

        plan1.PlanId.Should().NotBe(plan2.PlanId);
    }

    #endregion

    #region FileChangeSummary

    [Fact]
    public void FileChangeSummary_Defaults_AreEmpty()
    {
        var summary = new FileChangeSummary();

        summary.FilePath.Should().BeEmpty();
        summary.LinesAdded.Should().Be(0);
        summary.LinesRemoved.Should().Be(0);
        summary.BriefDescription.Should().BeNull();
        summary.NewContent.Should().BeNull();
        summary.OriginalContent.Should().BeNull();
    }

    [Fact]
    public void FileChangeSummary_CanRecordChanges()
    {
        var summary = new FileChangeSummary
        {
            FilePath = "src/App.tsx",
            LinesAdded = 15,
            LinesRemoved = 3,
            BriefDescription = "重构 App 组件",
            NewContent = "export default App;",
            OriginalContent = "const App = () => null;",
        };

        summary.FilePath.Should().Be("src/App.tsx");
        summary.LinesAdded.Should().Be(15);
        summary.LinesRemoved.Should().Be(3);
        summary.BriefDescription.Should().Contain("App");
        summary.NewContent.Should().NotBeNull();
        summary.OriginalContent.Should().NotBeNull();
    }

    #endregion

    #region AgentPermissionRequest

    [Fact]
    public void AgentPermissionRequest_Defaults_AreSetCorrectly()
    {
        var request = new AgentPermissionRequest();

        request.RequestId.Should().NotBeNullOrEmpty();
        request.Title.Should().BeEmpty();
        request.Command.Should().BeEmpty();
        request.ActionType.Should().Be("command");
        request.FilePaths.Should().BeEmpty();
        request.Detail.Should().BeEmpty();
        ((object?)request.ResponseTcs).Should().BeNull();
    }

    [Fact]
    public void AgentPermissionRequest_CanSetForFileWrite()
    {
        var request = new AgentPermissionRequest
        {
            Title = "确认修改文件",
            Command = "replace_string_in_file",
            ActionType = "file_write",
            Detail = "即将修改 src/utils.ts 第 42 行",
        };

        request.Title.Should().Contain("修改");
        request.ActionType.Should().Be("file_write");
        request.Detail.Should().Contain("utils.ts");
    }

    [Fact]
    public void AgentPermissionRequest_CanSetForFileDelete()
    {
        var request = new AgentPermissionRequest
        {
            Title = "确认删除文件",
            ActionType = "file_delete",
            FilePaths = new List<string> { "temp.log", "cache.dat" },
        };

        request.ActionType.Should().Be("file_delete");
        request.FilePaths.Should().HaveCount(2);
        request.FilePaths.Should().Contain("temp.log");
    }

    #endregion

    #region AgentQuestionRequest

    [Fact]
    public void AgentQuestionRequest_Defaults_AreSetCorrectly()
    {
        var request = new AgentQuestionRequest();

        request.RequestId.Should().NotBeNullOrEmpty();
        request.Questions.Should().BeEmpty();
        ((object?)request.ResponseTcs).Should().BeNull();
    }

    [Fact]
    public void AgentQuestionRequest_CanAddQuestions()
    {
        var request = new AgentQuestionRequest();
        request.Questions.Add(new AgentQuestion
        {
            Header = "环境选择",
            Question = "请选择目标环境",
            Options = new List<QuestionOption>
            {
                new() { Label = "开发", Description = "开发环境" },
                new() { Label = "生产", Description = "生产环境" },
            },
            MultiSelect = false,
        });

        request.Questions.Should().HaveCount(1);
        request.Questions[0].Header.Should().Be("环境选择");
        request.Questions[0].Options.Should().HaveCount(2);
        request.Questions[0].MultiSelect.Should().BeFalse();
        request.Questions[0].AllowFreeformInput.Should().BeTrue(); // default
    }

    [Fact]
    public void AgentQuestion_CanBeFreeTextOnly()
    {
        var question = new AgentQuestion
        {
            Header = "说明",
            Question = "请详细描述你的需求",
        };

        question.Options.Should().BeNull();
        question.AllowFreeformInput.Should().BeTrue();
    }

    [Fact]
    public void AgentQuestion_CanDisableFreeform()
    {
        var question = new AgentQuestion
        {
            Header = "选择",
            Question = "请选择一项",
            Options = new List<QuestionOption>
            {
                new() { Label = "选项A" },
                new() { Label = "选项B" },
            },
            AllowFreeformInput = false,
        };

        question.AllowFreeformInput.Should().BeFalse();
    }

    #endregion
}
