using DeepSeek_v4_for_VisualStudio.Models;
using System.Collections.Generic;

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

        // ── 缓存友好窗口（v1.1.10）──
        /// <summary>缓存窗口最大 token 数（0 = 禁用）</summary>
        int CacheWindowMaxTokens { get; set; }
        /// <summary>缓存窗口最大轮次数</summary>
        int CacheWindowMaxTurns { get; set; }
        /// <summary>缓存窗口最大条目数（兜底保护，0 = 不限）</summary>
        int CacheWindowMaxEntries { get; set; }

        // ── 系统/上下文设置 ──
        void SetSystemPrompt(string? prompt);
        void SetSolutionPath(string? solutionPath);
        void SetSearchContext(string? searchContext);
        void SetSkillContext(string? skillContext);
        void SetRagContext(string? ragContext);
        void SetMemoryContext(string? memoryContext);
        void SetCompressor(ContextCompressorService? compressor);
        void SetActiveFileTracker(IActiveFileTracker? tracker);
        void SetWorkspaceRoot(string? workspaceRoot);

        // ── 前缀冻结（v1.1.9 前缀缓存优化）──
        /// <summary>
        /// 冻结系统提示词为不可变前缀。调用后 messages[0] 在整个会话期间保持不变。
        /// </summary>
        void FreezeSystemPrompt();

        /// <summary>
        /// 获取已冻结的系统提示词（null = 尚未冻结）。
        /// </summary>
        string? GetFixedSystemPrompt();

        // ── 消息管理 ──
        void AddUserMessage(string content);
        void AddUserMessage(string content, List<ChatContentPart>? multimodalContent);
        void AddAssistantMessage(string? content, string? reasoningContent = null, List<ToolCall>? toolCalls = null);
        void AddToolResult(string toolCallId, string toolName, string result);
        void AddCustomMessage(string role, string content);

        // ── 上下文获取 ──
        List<ChatApiMessage> BuildApiMessages();
        List<ChatApiMessage> BuildApiMessagesRecentTurns(int maxTurns);
        string GetDebugSummary();

        /// <summary>
        /// 构建易变上下文块（Working Set + RAG）。
        /// 这些内容在同一会话内可能每轮都变，应放在消息数组末尾以保护前缀缓存。
        /// 与 BuildDynamicContextBlock() 分离：后者包含稳定的内容（压缩摘要、搜索、记忆），
        /// 始终占据 messages[2] 固定位置。
        /// </summary>
        string? BuildVolatileContextBlock();
        bool PersistCurrentVolatileSnapshot();

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
