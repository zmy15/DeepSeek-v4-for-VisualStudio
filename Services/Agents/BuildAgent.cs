using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// Build Agent — 构建修复代理。
    /// 
    /// 职责：
    /// - 编译验证代码变更
    /// - 诊断编译错误并直接修复 bug（拥有完整编辑权限）
    /// - 循环修复直到编译通过
    /// - 可独立调用（用户遇到编译错误时显式 @build）
    /// - 作为 Edit Agent 完成后的 Handoff 目标
    /// - 可与 Edit Agent 双向移交（复杂重构场景移交 Edit，编译修复场景接受 Edit 移交）
    /// 
    /// 设计原则：
    /// - build_solution → get_errors → read_file → replace_string_in_file → build_solution 循环
    /// - 拥有完整编辑工具集，可直接修复任何编译错误和代码 bug
    /// - 最多尝试修复 3 次，但新错误不计入限制
    /// - 修复过程中可使用 file_search/grep_search/list_dir 探索代码结构
    /// </summary>
    public class BuildAgent : BaseAgent
    {
        /// <summary>
        /// Build Agent 工具集 — 构建、诊断、编辑全覆盖。
        /// 代码库探索（搜索、列表、grep）通过 runSubagent 委派给 ExploreAgent。
        /// read_file 保留用于修复前确认文件内容（利用 ExploreAgent 预热缓存）。
        /// </summary>
        public static readonly string[] BuildTools = new[]
        {
            // 构建与诊断
            "build_solution",
            "get_errors",
            // 文件读写与编辑
            "read_file",
            "replace_string_in_file",
            "multi_replace_string_in_file",
            "create_file",
            "delete_file",
            "apply_patch",
            "create_directory",
            // 子代理委派与移交
            "runSubagent",
            "request_handoff",
            // 终端
            "run_in_terminal",
            "get_terminal_output",
            // 任务管理
            "manage_todo_list",
            // Git 版本控制
            "git",
            // 记忆
            "memory",
        };

        public BuildAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Build) { }

        #region Agent Definition

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Build,
                Name = "Build",
                Description = LocalizationService.Instance["agent.build.description"],
                ArgumentHint = LocalizationService.Instance["agent.build.argumentHint"],
                UserInvocable = true,
                DisableModelInvocation = false,
                AllowedTools = new List<string>(BuildTools),
                SubAgents = new List<AgentType>(),
                Handoffs = new List<AgentHandoff>
                {
                    new AgentHandoff
                    {
                        Label = LocalizationService.Instance["agent.build.handoffEditLabel"],
                        TargetAgent = AgentType.Edit,
                        Prompt = LocalizationService.Instance["agent.build.handoffEditPrompt"],
                        AutoSend = false,
                        ShowContinueOn = true,
                    },
                },
                SystemPrompt = BuildSystemPrompt(),
            };
        }

        private static string BuildSystemPrompt()
        {
            return LocalizationService.Instance["system.agent.buildPromptFragment"]
                + "\n\n## 🔌 MCP 外部工具\n"
                + "你可能拥有从 MCP 服务器导入的外部工具（如部署、CI/CD 触发、依赖管理等）。\n"
                + "这些写类工具可在构建-修复循环中使用，帮助你完成更完整的验证和部署流程。";
        }

        #endregion

        #region Execute

        /// <summary>
        /// Build Agent 执行入口。
        /// 接受编译错误上下文，循环诊断并修复。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            // ── 清空上次执行的日志，防止旧日志干扰判断 ──
            _logs.Clear();

            // ── 清理上次执行的移交状态，防止跨调用污染 ──
            PendingHandoffRequest = null;

            var L = LocalizationService.Instance;
            AddLog("INFO", string.Format(L["agent.log.buildStarted"], userMessage.Truncate(100)));

            var result = new AgentResult
            {
                AgentType = AgentType.Build,
                Success = true,
            };

            var ct = context.CancellationToken;
            string workspaceRoot = GetWorkspaceRoot(context);

            try
            {
                // ── 构建上下文增强消息 ──
                string enhancedMessage = BuildEnhancedUserMessage(userMessage, context);

                // ── 构建消息列表（含对话历史 + Agent 专属 prompt）──
                var messages = BuildContextAwareMessages(
                    Definition.SystemPrompt,
                    enhancedMessage,
                    maxRecentTurns: int.MaxValue);

                // ── 使用工具调用循环 ──
                var thinkingBuilder = new StringBuilder();
                string aiResponse = await CallAiWithToolLoopAsync(
                    messages,
                    workspaceRoot,
                    ct,
                    maxTokens: 8192,
                    toolWhitelist: new List<string>(BuildTools),
                    onThinking: (thinking) =>
                    {
                        thinkingBuilder.Append(thinking);
                        context.OnThinkingChunk?.Invoke(thinking);
                    },
                    onToolCall: (toolSummary) =>
                    {
                        AddLog("INFO", toolSummary);
                    });

                // ── 保存推理内容，供 UI 渲染思考面板 ──
                if (thinkingBuilder.Length > 0)
                    result.ReasoningContent = thinkingBuilder.ToString();

                result.Content = aiResponse;

                // ── 判断最终是否还有编译错误 ──
                if (HasBuildFailure(aiResponse))
                {
                    AddLog("WARN", L["agent.log.buildStillHasErrors"]);
                    result.Content += "\n\n⚠️ " + L["agent.log.buildStillHasErrors"];
                }
                else
                {
                    AddLog("INFO", L["agent.log.buildPassed"]);
                }

                // ── AI 通过 request_handoff 工具主动请求移交（优先于程序化移交）──
                if (PendingHandoffRequest != null)
                {
                    result.Handoff = ConvertHandoffRequestToHandoff(PendingHandoffRequest);
                }
                // ── 携带计划上下文，链回 Ask Agent 生成最终变更总结 ──
                else if (context.ActivePlan != null && context.ActivePlan.IsCompleted)
                {
                    result.Plan = context.ActivePlan;
                    result.FileChanges = context.ActivePlan.ChangedFiles;

                    // ── 构建详细的 Handoff Prompt（包含文件变更统计、步骤详情）──
                    string summaryPrompt = BuildAskHandoffPrompt(context.ActivePlan);
                    result.Handoff = new AgentHandoff
                    {
                        Label = L["agent.edit.handoffAskLabel"],
                        TargetAgent = AgentType.Ask,
                        Prompt = summaryPrompt,
                        AutoSend = true,
                        ShowContinueOn = false,
                    };
                }
                else
                {
                    // ── 无计划上下文时也移交 Ask 生成总结 ──
                    result.Handoff = new AgentHandoff
                    {
                        Label = L["agent.edit.handoffAskLabel"],
                        TargetAgent = AgentType.Ask,
                        Prompt = L["agent.build.handoffAskPrompt"] ?? "请总结以上构建结果。",
                        AutoSend = true,
                        ShowContinueOn = false,
                    };
                }

                result.Logs.AddRange(_logs);
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = L["agent.log.buildCancelled"];
                AddLog("WARN", L["agent.log.buildCancelled"]);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AddLog("ERROR", string.Format(L["agent.log.buildFailed"], ex.Message));
            }

            return result;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// 构建增强用户消息：附加当前编译错误上下文和变更文件信息。
        /// </summary>
        private string BuildEnhancedUserMessage(string userMessage, AgentContext context)
        {
            var sb = new StringBuilder();

            // ── 缓存优化：稳定性高的内容放前面 ──

            // 第1层：附加文件内容（用户提供，会话内稳定）
            if (!string.IsNullOrEmpty(context.FileContext))
            {
                sb.AppendLine(context.FileContext);
                sb.AppendLine();
            }

            // 第2层：已修改文件列表（同步骤内不变）
            if (context.ActivePlan != null && context.ActivePlan.ChangedFiles.Count > 0)
            {
                sb.AppendLine("## 已修改的文件");
                foreach (var change in context.ActivePlan.ChangedFiles)
                {
                    sb.AppendLine($"- `{change.FilePath}` (+{change.LinesAdded} -{change.LinesRemoved})");
                }
                sb.AppendLine();
            }

            // 第3层：前置步骤上下文（每步追加，前缀稳定）
            if (!string.IsNullOrEmpty(context.AccumulatedContext))
            {
                sb.AppendLine("## 前置步骤上下文");
                sb.AppendLine(context.AccumulatedContext);
                sb.AppendLine();
            }

            // 第4层：用户消息（每问必变，放最后）
            sb.AppendLine(userMessage);

            return sb.ToString();
        }

        /// <summary>
        /// 从 AgentContext 解析工作区根目录。
        /// </summary>
        private static new string GetWorkspaceRoot(AgentContext context)
        {
            string? sln = context.SolutionPath;
            if (!string.IsNullOrEmpty(sln))
            {
                if (Directory.Exists(sln))
                    return sln;
                string? dir = Path.GetDirectoryName(sln);
                if (!string.IsNullOrEmpty(dir))
                    return dir;
            }
            return string.Empty;
        }

        /// <summary>
        /// 构建指向 AskAgent 的 Handoff Prompt，包含文件变更统计和步骤执行详情。
        /// 确保 BuildAgent→AskAgent 路径与 EditAgent→AskAgent 路径有相同的信息密度。
        /// </summary>
        private static string BuildAskHandoffPrompt(AgentTaskPlan plan)
        {
            var L = LocalizationService.Instance;
            var sb = new StringBuilder();

            sb.AppendLine(L["agent.edit.handoffAskPrompt"]);
            sb.AppendLine();
            sb.AppendLine($"**{L["edit.summary.taskLabel"]}**: {plan.Title}");
            sb.AppendLine($"**{L["edit.summary.fileCount"]}**: {plan.ChangedFiles.Count}");
            sb.AppendLine();

            // ── 合并相同文件的变更记录 ──
            if (plan.ChangedFiles.Count > 0)
            {
                var mergedFiles = plan.ChangedFiles
                    .GroupBy(c => NormalizePath(c.FilePath), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        FileName = Path.GetFileName(g.First().FilePath),
                        LinesAdded = g.Sum(c => c.LinesAdded),
                        LinesRemoved = g.Sum(c => c.LinesRemoved),
                    })
                    .ToList();

                sb.AppendLine(L.Format("edit.summary.changeStats",
                    mergedFiles.Sum(c => c.LinesAdded),
                    mergedFiles.Sum(c => c.LinesRemoved),
                    mergedFiles.Count));
                sb.AppendLine();
                sb.AppendLine(L["edit.summary.modifiedFiles"]);
                foreach (var file in mergedFiles)
                {
                    sb.AppendLine($"- **{file.FileName}** (+{file.LinesAdded} -{file.LinesRemoved})");
                }
                sb.AppendLine();
            }

            // ── 步骤执行情况 ──
            if (plan.Steps.Count > 0)
            {
                sb.AppendLine("## 步骤执行情况");
                foreach (var step in plan.Steps)
                {
                    string statusIcon = step.Status == AgentStepStatus.Completed ? "✅"
                        : step.Status == AgentStepStatus.Failed ? "❌"
                        : step.Status == AgentStepStatus.Skipped ? "⏭️"
                        : "🔄";
                    string summary = !string.IsNullOrWhiteSpace(step.ResultSummary)
                        ? step.ResultSummary!
                        : "(无)";
                    sb.AppendLine($"- {statusIcon} **步骤 {step.Index}**: {step.Title} — {summary}");
                }
                sb.AppendLine();
            }

            // ── 注明构建已完成 ──
            sb.AppendLine("✅ 构建验证已完成，请生成最终变更总结。");

            return sb.ToString();
        }
        #endregion
    }
}
