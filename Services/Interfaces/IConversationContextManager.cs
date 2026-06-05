using DeepSeek_v4_for_VisualStudio.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 统一上下文管理器接口 — 负责多轮对话历史的存储、拼接与 Token 预算管理。
    /// </summary>
    public interface IConversationContextManager
    {
        // ── 属性 ──
        int TokenBudget { get; set; }
        int AutoTrimTurns { get; set; }
        int TurnCount { get; }
        int MessageCount { get; }
        int EstimatedTokens { get; }
        bool IsEmpty { get; }
        double UsageRatio { get; }
        double UsagePercent { get; }
        string? RagContext { get; }

        // ── 系统/上下文设置 ──
        void SetSystemPrompt(string? prompt);
        void SetSearchContext(string? searchContext);
        void SetSkillContext(string? skillContext);
        void SetRagContext(string? ragContext);
        void SetMemoryContext(string? memoryContext);
        void SetCompressor(ContextCompressorService? compressor);

        // ── 消息管理 ──
        void AddUserMessage(string content);
        Task AddUserMessageAsync(string content, CancellationToken cancellationToken = default);
        void AddAssistantMessage(string? content, string? reasoningContent = null, List<ToolCall>? toolCalls = null);
        void AddToolResult(string toolCallId, string toolName, string result);
        void AddCustomMessage(string role, string content);

        // ── 上下文获取 ──
        List<ChatApiMessage> BuildApiMessages();
        List<ChatApiMessage> BuildApiMessagesRecentTurns(int maxTurns);
        string GetDebugSummary();

        // ── Token 估算校准 ──
        /// <summary>
        /// 使用 API 返回的实际 prompt_tokens 校准本地字符级估算。
        /// 应在每次 Chat API 调用完成后调用，以逐步修正估算偏差。
        /// </summary>
        /// <param name="actualPromptTokens">API usage 中的实际 prompt_tokens</param>
        void CalibrateFromApiUsage(long actualPromptTokens);

        // ── 工具方法 ──
        void Clear();
    }
}
