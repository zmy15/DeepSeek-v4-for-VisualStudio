using DeepSeek_v4_for_VisualStudio.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 内置工作区工具服务接口 — 为 Agent 提供无需外部 MCP 服务器的本地工具。
    /// </summary>
    public interface IBuiltInToolService
    {
        /// <summary>获取经过过滤的工具定义列表</summary>
        List<ToolDefinition> GetFilteredToolDefinitions(List<string>? allowedTools);

        /// <summary>执行指定的内置工具，返回执行结果；若不是内置工具则返回 null</summary>
        /// <param name="cancellationToken">可选取消令牌，传递给工具以支持停止按钮中断</param>
        Task<string?> ExecuteBuiltInToolAsync(string toolName, string argumentsJson, string? workspaceRoot = null, CancellationToken cancellationToken = default);
    }
}
