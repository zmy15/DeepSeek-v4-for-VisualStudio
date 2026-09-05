using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// 计划构建结果对账：最终构建通过后回写步骤状态，
    /// 避免早期编译失败的中间结果被当作最终结论写进总结。
    /// </summary>
    internal static class PlanBuildOutcomeReconciler
    {
        private const int MaxResultChars = 400;

        /// <summary>
        /// 将计划标记为最终构建通过，并把因编译/构建问题失败的步骤回写为成功。
        /// 返回被回写的步骤数量；非构建类失败保持原样。
        /// </summary>
        internal static int ReconcileAfterBuildSuccess(
            AgentTaskPlan? plan, string? finalBuildResult, string successSummary)
        {
            if (plan == null) return 0;

            plan.FinalBuildSucceeded = true;
            plan.FinalBuildResult = Truncate(finalBuildResult, MaxResultChars);

            int reconciled = 0;
            foreach (var step in plan.Steps)
            {
                if (step.Status != AgentStepStatus.Failed
                    && step.Status != AgentStepStatus.Completed)
                {
                    continue;
                }

                if (!IsBuildRelated(step)) continue;

                // 保留原始失败信息，但用户可见结果以最终构建为准
                if (string.IsNullOrWhiteSpace(step.AiResponse))
                    step.AiResponse = step.ResultSummary;
                step.Status = AgentStepStatus.Completed;
                step.ResultSummary = successSummary;
                reconciled++;
            }

            plan.IsCompleted = plan.Steps.All(
                s => s.Status is AgentStepStatus.Completed or AgentStepStatus.Skipped);
            return reconciled;
        }

        /// <summary>
        /// 最终构建失败时清空成功标记，避免旧的“构建通过”状态污染后续总结。
        /// </summary>
        internal static void MarkBuildFailed(AgentTaskPlan? plan, string? finalBuildResult)
        {
            if (plan == null) return;
            plan.FinalBuildSucceeded = false;
            plan.FinalBuildResult = Truncate(finalBuildResult, MaxResultChars);
        }

        /// <summary>
        /// 判断文本是否像一次编译/构建失败的输出。
        /// </summary>
        internal static bool IsBuildFailureSummary(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string lower = text!.ToLowerInvariant();
            if (lower.Contains("0 个错误") || lower.Contains("0 errors") || lower.Contains("0 error"))
                return false;

            if (lower.Contains("error:") || lower.Contains("timeout:"))
            {
                return lower.Contains("构建") || lower.Contains("编译")
                    || lower.Contains("build") || lower.Contains("compile")
                    || lower.Contains("退出码") || lower.Contains("exit code");
            }

            return lower.Contains("构建失败")
                || lower.Contains("编译失败")
                || lower.Contains("build failed")
                || lower.Contains("cmake build failed")
                || lower.Contains("msbuild failed")
                || lower.Contains("exit code:")
                || lower.Contains("退出码")
                || lower.Contains("error cs")
                || lower.Contains("error lnk")
                || lower.Contains("error msb")
                || Regex.IsMatch(lower, @"\berror\s+(cs|c|lnk|msb|bc|fs|ts|rust)\d+\b");
        }

        /// <summary>
        /// 判断步骤是否与构建/编译/验证相关，且结果描述带有失败信号。
        /// 仅用于最终构建通过后的状态回写，避免误改其他类型失败步骤。
        /// </summary>
        private static bool IsBuildRelated(AgentStep step)
        {
            if (IsBuildFailureSummary(step.ResultSummary)) return true;
            if (!IsBuildRelatedStepTitle(step.Title)) return false;

            string? result = step.ResultSummary;
            return !string.IsNullOrWhiteSpace(result)
                && (result.Contains("失败", StringComparison.Ordinal)
                    || result.Contains("错误", StringComparison.Ordinal)
                    || result.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || result.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || result.Contains("Error: ", StringComparison.Ordinal));
        }

        private static bool IsBuildRelatedStepTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            return title.Contains("构建", StringComparison.Ordinal)
                || title.Contains("编译", StringComparison.Ordinal)
                || title.Contains("验证", StringComparison.Ordinal)
                || title.Contains("测试", StringComparison.Ordinal)
                || title.Contains("汇总", StringComparison.Ordinal)
                || title.Contains("build", StringComparison.OrdinalIgnoreCase)
                || title.Contains("compile", StringComparison.OrdinalIgnoreCase)
                || title.Contains("verify", StringComparison.OrdinalIgnoreCase)
                || title.Contains("test", StringComparison.OrdinalIgnoreCase)
                || title.Contains("run", StringComparison.OrdinalIgnoreCase);
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value!.Length <= maxLength) return value;
            return value!.Substring(0, maxLength);
        }
    }
}
