using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 对话补全 Provider 的协议无关边界。
    /// 当前由 OpenAI-compatible / DeepSeek 实现；Anthropic 与 Responses API 后续按此接口接入。
    /// </summary>
    public interface IChatCompletionProvider : IDisposable
    {
        /// <summary>Provider 标识，例如 DeepSeek 或 OpenAI-compatible。</summary>
        string ProviderId { get; }

        /// <summary>最近一次 API 调用的 Usage 信息。</summary>
        DeepSeekUsage? LastUsage { get; }

        /// <summary>最近一次 Chat 调用实际发送的消息快照。</summary>
        IReadOnlyList<ChatApiMessage>? LastSentMessages { get; }

        /// <summary>累计 Cache 命中 token 数。</summary>
        long TotalCacheHitTokens { get; }

        /// <summary>累计 Cache 未命中 token 数。</summary>
        long TotalCacheMissTokens { get; }

        /// <summary>累计 Prompt token 数。</summary>
        long TotalPromptTokens { get; }

        /// <summary>累计 Completion token 数。</summary>
        long TotalCompletionTokens { get; }

        /// <summary>累计 Cache 命中率（0.0 ~ 1.0）。</summary>
        double TotalCacheHitRate { get; }

        /// <summary>前缀缓存稳定性管理器。</summary>
        PrefixCacheManager? PrefixCache { get; set; }

        /// <summary>当前 API Base URL。</summary>
        string BaseUrl { get; }

        /// <summary>当前模型标识。</summary>
        string CurrentModel { get; }

        /// <summary>当前端点是否具备视觉能力。</summary>
        bool CurrentIsVision { get; }

        /// <summary>当前是否为自定义端点。</summary>
        bool CurrentIsCustom { get; }

        /// <summary>重置累计统计。</summary>
        void ResetAccumulatedStats();

        /// <summary>从持久化数据恢复累计统计。</summary>
        void RestoreAccumulatedStats(
            long hitTokens,
            long missTokens,
            long promptTokens,
            long completionTokens,
            double costYuan,
            double costUsd);

        /// <summary>运行时更新模型。</summary>
        void UpdateModel(string model);

        /// <summary>运行时更新 API Key。</summary>
        void UpdateApiKey(string apiKey);

        /// <summary>运行时更新 API Base URL。</summary>
        void UpdateBaseUrl(string? baseUrl);

        /// <summary>运行时一次性应用端点配置。</summary>
        void UpdateEndpoint(DeepSeekEndpointConfig config);

        /// <summary>流式聊天调用。</summary>
        IAsyncEnumerable<string> ChatStreamAsync(
            IEnumerable<ChatApiMessage> messages,
            List<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default,
            int? maxTokens = null,
            string? toolChoice = null,
            double? temperature = null,
            string? responseFormat = null,
            string? model = null,
            bool? thinkingEnabled = null);

        /// <summary>非流式完整调用。</summary>
        Task<string> CompleteAsync(
            IEnumerable<ChatApiMessage> messages,
            CancellationToken cancellationToken = default,
            string? responseFormat = null);

        /// <summary>验证 API Key 是否有效。</summary>
        Task<string?> ValidateApiKeyAsync();
    }
}
