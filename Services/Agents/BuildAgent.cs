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
        /// Build Agent 工具集 — 构建、诊断、探索、编辑全覆盖。
        /// 与 EditAgent 工具集对齐，专注于编译驱动的代码修复。
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
            // 代码探索
            "file_search",
            "grep_search",
            "list_dir",
            "get_changed_files",
            // 终端
            "run_in_terminal",
            "get_terminal_output",
            // 任务管理
            "manage_todo_list",
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

            // ── 附加文件内容（用户通过 @file 或附件面板附加的文件）──
            if (!string.IsNullOrEmpty(context.FileContext))
            {
                sb.AppendLine(context.FileContext);
                sb.AppendLine();
            }

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
        #endregion
    }
}
