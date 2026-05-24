using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// Agent 调度器接口 — 多 Agent 系统的中央路由。
    /// </summary>
    public interface IAgentDispatcher : IDisposable
    {
        AskAgent AskAgent { get; }
        ExploreAgent ExploreAgent { get; }
        PlanAgent PlanAgent { get; }
        EditAgent EditAgent { get; }
        BuildAgent BuildAgent { get; }

        AgentType ActiveAgentType { get; }
        AgentTaskPlan? ActivePlan { get; set; }
        AgentPermissionRequest? PendingPermission { get; }
        List<string>? ActiveAgentAllowedTools { get; }
        ConversationContextManager? ContextManager { get; set; }

        /// <summary>设置 MCP 管理器</summary>
        void SetMcpManager(IMcpManagerService? mcpManager);

        /// <summary>更新 MCP 管理器（从具体类型）</summary>
        void UpdateMcpManager(McpManagerService mcpManager);

        /// <summary>执行 Agent 任务</summary>
        Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context, AgentRoutingResult? routingOverride = null);

        /// <summary>执行 Handoff 链</summary>
        Task<AgentResult> ExecuteHandoffAsync(AgentHandoff handoff, AgentContext context);
    }
}
