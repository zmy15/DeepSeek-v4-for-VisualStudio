using System;
using System.Collections.Generic;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// 上下文统计信息 — 用于监控和管理 1M Token 上下文窗口使用情况。
    /// </summary>
    public class ContextStats
    {
        /// <summary>当前估算 Token 总数</summary>
        public int EstimatedTokens { get; set; }

        /// <summary>Token 预算上限（默认 900,000，为 1M 窗口留 100K 给输出）</summary>
        public int TokenBudget { get; set; } = 900_000;

        /// <summary>使用率 (0.0 ~ 1.0)</summary>
        public double UsageRatio => TokenBudget > 0 ? (double)EstimatedTokens / TokenBudget : 0;

        /// <summary>使用百分比 (0 ~ 100)</summary>
        public double UsagePercent => UsageRatio * 100;

        /// <summary>消息总条数</summary>
        public int MessageCount { get; set; }

        /// <summary>对话轮次数（user 消息数）</summary>
        public int TurnCount { get; set; }

        /// <summary>已压缩的轮次数</summary>
        public int CompressedTurns { get; set; }

        /// <summary>系统提示词 Token 数</summary>
        public int SystemPromptTokens { get; set; }

        /// <summary>工具调用结果 Token 数</summary>
        public int ToolResultTokens { get; set; }

        /// <summary>压缩摘要 Token 数</summary>
        public int CompressedSummaryTokens { get; set; }

        /// <summary>搜索上下文 Token 数</summary>
        public int SearchContextTokens { get; set; }

        /// <summary>获取详细统计信息字符串</summary>
        public string GetDetailedReport()
        {
            return $"=== 上下文统计 ===\n" +
                   $"Token: {EstimatedTokens:N0} / {TokenBudget:N0} ({UsagePercent:F1}%)\n" +
                   $"消息数: {MessageCount} | 轮次: {TurnCount} | 已压缩轮次: {CompressedTurns}\n" +
                   $"系统提示词: {SystemPromptTokens:N0} tokens\n" +
                   $"工具结果: {ToolResultTokens:N0} tokens\n" +
                   $"压缩摘要: {CompressedSummaryTokens:N0} tokens\n" +
                   $"搜索上下文: {SearchContextTokens:N0} tokens";
        }
    }

    /// <summary>
    /// 上下文压缩后的轮次摘要。
    /// 存储原始轮次被压缩后的精简信息，作为 system 消息注入。
    /// </summary>
    public class CompressedTurnSummary
    {
        /// <summary>压缩摘要文本（AI 生成的精简内容）</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>压缩的原始轮次范围（从第几轮到第几轮）</summary>
        public int FromTurn { get; set; }

        /// <summary>压缩的原始轮次范围（到第几轮）</summary>
        public int ToTurn { get; set; }

        /// <summary>压缩前的原始 Token 数</summary>
        public int OriginalTokens { get; set; }

        /// <summary>压缩后的 Token 数</summary>
        public int CompressedTokens { get; set; }

        /// <summary>压缩时间</summary>
        public DateTime CompressedAt { get; set; } = DateTime.Now;

        /// <summary>压缩率 (节省的百分比)</summary>
        public double CompressionRatio => OriginalTokens > 0
            ? 1.0 - (double)CompressedTokens / OriginalTokens
            : 0;
    }

    /// <summary>
    /// 上下文压缩配置。
    /// </summary>
    public class CompressionConfig
    {
        /// <summary>触发压缩的使用率阈值（默认 85%）</summary>
        public double CompressionThreshold { get; set; } = 0.85;

        /// <summary>触发严重压缩的使用率阈值（默认 95%）</summary>
        public double AggressiveThreshold { get; set; } = 0.95;

        /// <summary>压缩时保留最近的轮次数（不被压缩）</summary>
        public int PreserveRecentTurns { get; set; } = 3;

        /// <summary>每次压缩的最少轮次数</summary>
        public int MinTurnsToCompress { get; set; } = 2;

        /// <summary>是否启用自动压缩</summary>
        public bool AutoCompressEnabled { get; set; } = true;

        /// <summary>压缩用的提示词模板。{0}=被压缩的对话内容</summary>
        public string CompressionPrompt { get; set; } = AiPrompts.CompressionPromptTemplate;
    }
}
