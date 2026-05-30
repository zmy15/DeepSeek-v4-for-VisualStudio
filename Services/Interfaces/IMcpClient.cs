using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// MCP 传输客户端统一接口。
    /// 支持 stdio 和 HTTP 两种传输方式。
    /// </summary>
    public interface IMcpClient : IDisposable
    {
        /// <summary>服务器显示名称</summary>
        string ServerName { get; }

        /// <summary>传输类型: "stdio" 或 "http"</summary>
        string Transport { get; }

        /// <summary>是否已连接并完成初始化握手</summary>
        bool IsConnected { get; }

        /// <summary>服务端初始化信息</summary>
        InitializeResult? ServerInfo { get; }

        /// <summary>工具列表</summary>
        List<McpTool> Tools { get; }

        /// <summary>
        /// 建立连接并完成 MCP 初始化握手。
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <param name="progress">可选的进度回调，用于 UI 报告当前阶段</param>
        Task ConnectAsync(CancellationToken cancellationToken = default, Action<string>? progress = null);

        /// <summary>
        /// 调用 MCP 工具。
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="arguments">工具参数</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task<ToolCallResult> CallToolAsync(string toolName, Dictionary<string, object> arguments, CancellationToken cancellationToken = default);

        /// <summary>
        /// 刷新工具列表。
        /// </summary>
        Task RefreshToolsAsync(CancellationToken cancellationToken = default);
    }
}
