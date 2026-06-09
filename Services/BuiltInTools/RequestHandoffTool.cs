using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// request_handoff 工具 — Agent 间移交的专用 JSON 格式入口。
    /// 
    /// 当 Agent 需要将整个任务移交给另一个 Agent 时，调用此工具声明移交意图。
    /// 与 runSubagent 的区别：request_handoff 是完整控制权移交，
    /// runSubagent 是子任务委派（调用方等待结果继续）。
    /// 
    /// 移交格式 (JSON):
    /// {
    ///   "targetAgent": "Edit|Ask|Plan|Build|Explore",
    ///   "reason": "简短说明为什么移交",
    ///   "taskDescription": "给目标 Agent 的完整任务描述",
    ///   "chainBack": false
    /// }
    /// </summary>
    public class RequestHandoffTool : BuiltInToolBase
    {
        private readonly Func<HandoffRequest, Task> _handoffHandler;

        /// <summary>
        /// 创建 RequestHandoffTool 实例。
        /// </summary>
        /// <param name="handoffHandler">
        /// 移交处理器：接收 HandoffRequest，将其存储为 Agent 的待处理移交。
        /// 回调由 BaseAgent 注入。
        /// </param>
        public RequestHandoffTool(Func<HandoffRequest, Task> handoffHandler)
        {
            _handoffHandler = handoffHandler ?? throw new ArgumentNullException(nameof(handoffHandler));
        }

        public override string Name => "request_handoff";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "request_handoff",
                    Description = L["tool.request_handoff.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            targetAgent = new
                            {
                                type = "string",
                                @enum = new[] { "Edit", "Ask", "Plan", "Build", "Explore" },
                                description = LocalizationService.Instance["tool.requestHandoff.param.targetAgent"]
                            },
                            reason = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.requestHandoff.param.reason"]
                            },
                            taskDescription = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.requestHandoff.param.taskDescription"]
                            },
                            chainBack = new
                            {
                                type = "boolean",
                                description = LocalizationService.Instance["tool.requestHandoff.param.chainBack"]
                            }
                        },
                        required = new[] { "targetAgent", "reason", "taskDescription" }
                    }
                }
            };
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string targetAgentStr = GetStringArg(args, "targetAgent");
            string reason = GetStringArg(args, "reason");
            string taskDescription = GetStringArg(args, "taskDescription");
            bool chainBack = GetBoolArg(args, "chainBack", false);

            if (string.IsNullOrWhiteSpace(targetAgentStr))
                return "❌ request_handoff: 缺少 targetAgent 参数。可选值: Edit, Ask, Plan, Build, Explore";

            if (string.IsNullOrWhiteSpace(taskDescription))
                return "❌ request_handoff: 缺少 taskDescription 参数。请描述目标 Agent 需要执行的任务。";

            // 解析目标 Agent 类型
            AgentType targetAgent = targetAgentStr.ToLowerInvariant() switch
            {
                "edit" => AgentType.Edit,
                "ask" => AgentType.Ask,
                "plan" => AgentType.Plan,
                "build" => AgentType.Build,
                "explore" => AgentType.Explore,
                _ => AgentType.Ask
            };

            var request = new HandoffRequest
            {
                SourceAgent = AgentType.Ask, // 由 BaseAgent 在执行时覆写
                TargetAgent = targetAgent,
                Reason = reason,
                TaskDescription = taskDescription,
                ChainBack = chainBack,
                AutoSend = true
            };

            Logger.Info($"[RequestHandoff] {targetAgentStr} ← {reason.Truncate(80)}");

            await _handoffHandler(request);

            // ── 如果 HandoffHandler 拒绝了移交（如显式路由模式），返回拒绝原因给 AI ──
            if (request.Rejected)
            {
                return $"🚫 移交被拒绝: {request.RejectReason}";
            }

            return LocalizationService.Instance.Format("tool.requestHandoff.handoffRequested", targetAgentStr, reason);
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string target = GetStringArg(args, "targetAgent") ?? "?";
            string reason = GetStringArg(args, "reason")?.Truncate(50) ?? "移交任务";
            return LocalizationService.Instance.Format("tool.requestHandoff.handoffTo", target, reason);
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "移交完成";
            if (toolResult.StartsWith("🔄 HANDOFF_REQUESTED")) return LocalizationService.Instance["tool.requestHandoff.completed"];
            return toolResult.Truncate(80);
        }
    }
}
