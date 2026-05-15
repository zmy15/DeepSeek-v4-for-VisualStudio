using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// MCP 管理器服务接口 — 管理多个 MCP 服务器连接和工具聚合。
    /// </summary>
    public interface IMcpManagerService : IDisposable
    {
        IReadOnlyList<McpTool> AllTools { get; }

        List<ToolDefinition> GetToolDefinitions();
        List<ToolDefinition> GetFilteredToolDefinitions(List<string>? allowedTools);
        Task InitializeAsync(List<McpServerConfig> configs, CancellationToken cancellationToken = default);
        Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default);
    }
}
