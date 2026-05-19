using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// DeepSeek API 服务接口。
    /// </summary>
    public interface IDeepSeekApiService : IDisposable
    {
        /// <summary>最近一次 API 调用的 Usage 信息</summary>
        DeepSeekUsage? LastUsage { get; }

        /// <summary>累计 Cache 命中 token 数（跨所有 API 调用，含 Agent 内部调用）</summary>
        long TotalCacheHitTokens { get; }

        /// <summary>累计 Cache 未命中 token 数</summary>
        long TotalCacheMissTokens { get; }

        /// <summary>累计 Prompt token 数</summary>
        long TotalPromptTokens { get; }

        /// <summary>累计 Completion token 数</summary>
        long TotalCompletionTokens { get; }

        /// <summary>累计 Cache 命中率（0.0 ~ 1.0）</summary>
        double TotalCacheHitRate { get; }

        /// <summary>重置累计统计</summary>
        void ResetAccumulatedStats();

        /// <summary>更新使用的模型</summary>
        void UpdateModel(string model);

        /// <summary>配置思考模式</summary>
        void ConfigureThinking(bool enabled, string effort = "high");

        /// <summary>流式聊天调用</summary>
        /// <param name="toolChoice">工具调用策略: "auto"(默认), "none"(禁用), "required"(强制). null 表示仅在有 tools 时启用 auto</param>
        IAsyncEnumerable<string> ChatStreamAsync(
            IEnumerable<ChatApiMessage> messages,
            List<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default,
            int? maxTokens = null,
            string? toolChoice = null);

        /// <summary>非流式完整调用</summary>
        Task<string> CompleteAsync(
            IEnumerable<ChatApiMessage> messages,
            CancellationToken cancellationToken = default);

        /// <summary>验证 API Key 是否有效</summary>
        Task<string?> ValidateApiKeyAsync();
    }
}
