using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.Agents;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// PlanBuildOutcomeReconciler 的回归测试：
/// 最终构建通过后，曾因编译问题标记为失败的步骤应按最终结果回写为成功。
/// </summary>
public class PlanBuildOutcomeReconcilerTests
{
    [Fact]
    public void ReconcileAfterBuildSuccess_MarksBuildFailedStepsCompleted()
    {
        var plan = new AgentTaskPlan
        {
            Steps =
            {
                new AgentStep
                {
                    Index = 1,
                    Title = "新增 MVCC 可见性测试",
                    Status = AgentStepStatus.Failed,
                    ResultSummary = "Error: CMake 构建失败（退出码: 2）",
                },
                new AgentStep
                {
                    Index = 2,
                    Title = "新增 JOIN 执行器测试",
                    Status = AgentStepStatus.Failed,
                    ResultSummary = "Error: CMake 构建失败（退出码: 2）",
                },
            },
        };

        int reconciled = PlanBuildOutcomeReconciler.ReconcileAfterBuildSuccess(
            plan, " CMake 构建成功", " 最终构建验证通过");

        reconciled.Should().Be(2);
        plan.FinalBuildSucceeded.Should().BeTrue();
        plan.IsCompleted.Should().BeTrue();
        plan.Steps.Should().AllSatisfy(s => s.Status.Should().Be(AgentStepStatus.Completed));
        plan.Steps.Should().AllSatisfy(s => s.ResultSummary.Should().Be(" 最终构建验证通过"));
    }

    [Fact]
    public void ReconcileAfterBuildSuccess_KeepsNonBuildFailuresFailed()
    {
        var plan = new AgentTaskPlan
        {
            Steps =
            {
                new AgentStep
                {
                    Index = 1,
                    Title = "加载配置文件",
                    Status = AgentStepStatus.Failed,
                    ResultSummary = "文件写入失败: permission denied",
                },
                new AgentStep
                {
                    Index = 2,
                    Title = "最终验证与汇总",
                    Status = AgentStepStatus.Failed,
                    ResultSummary = "Error: CMake 构建失败（退出码: 2）",
                },
            },
        };

        int reconciled = PlanBuildOutcomeReconciler.ReconcileAfterBuildSuccess(
            plan, " CMake 构建成功", " 最终构建验证通过");

        reconciled.Should().Be(1);
        plan.Steps[0].Status.Should().Be(AgentStepStatus.Failed);
        plan.Steps[1].Status.Should().Be(AgentStepStatus.Completed);
        plan.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void MarkBuildFailed_ClearsFinalBuildSucceeded()
    {
        var plan = new AgentTaskPlan();
        plan.FinalBuildSucceeded = true;

        PlanBuildOutcomeReconciler.MarkBuildFailed(plan, "Error: CMake 构建失败");

        plan.FinalBuildSucceeded.Should().BeFalse();
    }
}
