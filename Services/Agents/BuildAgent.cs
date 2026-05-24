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
    /// - 诊断编译错误并自动修复
    /// - 循环修复直到编译通过
    /// - 可独立调用（用户遇到编译错误时显式 @build）
    /// - 作为 Edit Agent 完成后的 Handoff 目标
    /// 
    /// 设计原则：
    /// - build_solution → get_errors → read_file → replace_string_in_file → build_solution 循环
    /// - 最多修复 3 次，但新错误不计入限制
    /// - 编辑后由 EditAgent 自动移交至此 Agent
    /// </summary>
    public class BuildAgent : BaseAgent
    {
        /// <summary>
        /// Build Agent 工具集 — 构建、诊断、只读、修复。
        /// </summary>
        public static readonly string[] BuildTools = new[]
        {
            "build_solution",
            "get_errors",
            "read_file",
            "replace_string_in_file",
            "multi_replace_string_in_file",
            "create_file",
            "run_in_terminal",
            "get_terminal_output",
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
            return GetCommonSystemPromptPrefix() + LocalizationService.Instance["system.agent.buildPromptFragment"];
        }

        #endregion

        #region Execute

        /// <summary>
        /// Build Agent 执行入口。
        /// 接受编译错误上下文，循环诊断并修复。
        /// </summary>
        public override async Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
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

                // ── 构建消息列表 ──
                var messages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = Definition.SystemPrompt },
                    new ChatApiMessage { Role = "user", Content = enhancedMessage }
                };

                // ── 使用工具调用循环 ──
                string aiResponse = await CallAiWithToolLoopAsync(
                    messages,
                    workspaceRoot,
                    ct,
                    maxTokens: 8192,
                    toolWhitelist: new List<string>(BuildTools),
                    onToolCall: (toolSummary) =>
                    {
                        AddLog("INFO", toolSummary);
                    });

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
            sb.AppendLine(userMessage);

            // ── 如果有活动计划，附加变更文件信息 ──
            if (context.ActivePlan != null && context.ActivePlan.ChangedFiles.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 已修改的文件");
                foreach (var change in context.ActivePlan.ChangedFiles)
                {
                    sb.AppendLine($"- `{change.FilePath}` (+{change.LinesAdded} -{change.LinesRemoved})");
                }
            }

            // ── 如果有累积上下文，附加之 ──
            if (!string.IsNullOrEmpty(context.AccumulatedContext))
            {
                sb.AppendLine();
                sb.AppendLine("## 前置步骤上下文");
                sb.AppendLine(context.AccumulatedContext);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 从 AgentContext 解析工作区根目录。
        /// </summary>
        private static string GetWorkspaceRoot(AgentContext context)
        {
            string root = context.SolutionPath ?? string.Empty;
            if (!string.IsNullOrEmpty(root) && File.Exists(root))
                root = Path.GetDirectoryName(root) ?? root;
            return root;
        }

        /// <summary>
        /// 检测 AI 回复中是否表明编译仍存在错误。
        /// </summary>
        private static bool HasBuildFailure(string aiResponse)
        {
            if (string.IsNullOrWhiteSpace(aiResponse)) return false;

            string lower = aiResponse.ToLowerInvariant();

            // 明确失败信号
            bool hasFailure = lower.Contains("build failed")
                || lower.Contains("构建失败")
                || lower.Contains("编译失败")
                || lower.Contains("error cs")
                || lower.Contains("error lnk")
                || lower.Contains("error c2")
                || lower.Contains("error msb");

            // 排除误报：如果包含明确的成功信号，不算失败
            bool hasSuccess = lower.Contains("build succeeded")
                || lower.Contains("0 个错误")
                || lower.Contains("0 errors")
                || lower.Contains("✅")
                || lower.Contains("编译通过")
                || lower.Contains("构建成功");

            return hasFailure && !hasSuccess;
        }

        #endregion
    }
}
