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

        /// <summary>从持久化数据恢复累计统计（重启后调用）</summary>
        void RestoreAccumulatedStats(long hitTokens, long missTokens, long promptTokens, long completionTokens);

        /// <summary>更新使用的模型</summary>
        void UpdateModel(string model);

        /// <summary>配置思考模式</summary>
        void ConfigureThinking(bool enabled, string effort = "high");

        /// <summary>流式聊天调用</summary>
        /// <param name="toolChoice">工具调用策略: "auto"(默认), "none"(禁用), "required"(强制). null 表示仅在有 tools 时启用 auto</param>
        /// <param name="temperature">采样温度 (0.0 ~ 2.0)。null 表示不设置（使用 API 默认值）</param>
        IAsyncEnumerable<string> ChatStreamAsync(
            IEnumerable<ChatApiMessage> messages,
            List<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default,
            int? maxTokens = null,
            string? toolChoice = null,
            double? temperature = null);

        /// <summary>非流式完整调用</summary>
        Task<string> CompleteAsync(
            IEnumerable<ChatApiMessage> messages,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// FIM（Fill-In-the-Middle）补全调用，用于代码自动补全场景。
        /// 端点: POST https://api.deepseek.com/beta/completions
        /// </summary>
        /// <param name="prompt">光标前的代码（prefix）</param>
        /// <param name="suffix">光标后的代码（suffix）</param>
        /// <param name="maxTokens">最大生成 token 数</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>模型生成的补全文本</returns>
        Task<string> FimCompletionAsync(
            string prompt,
            string? suffix = null,
            int? maxTokens = null,
            CancellationToken cancellationToken = default);

        /// <summary>验证 API Key 是否有效</summary>
        Task<string?> ValidateApiKeyAsync();
    }
}
