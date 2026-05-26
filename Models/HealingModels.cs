using System;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    // ========================================================================
    // Healing 模型 — 编辑匹配失败时的自动修正机制
    // 参考: vscode-copilot-chat editFileHealing.tsx
    // ========================================================================

    /// <summary>
    /// Healing 请求：发送给 AI 模型修正匹配失败的编辑。
    /// </summary>
    public class HealingRequest
    {
        /// <summary>目标文件路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>文件当前完整内容</summary>
        public string CurrentFileContent { get; set; } = string.Empty;

        /// <summary>原始编辑操作类型</summary>
        public EditOperationType OriginalOperationType { get; set; }

        /// <summary>失败的 Patch 操作（apply_patch 模式）</summary>
        public PatchOperation? FailedPatch { get; set; }

        /// <summary>失败的 insert_edit_into_file 完整内容</summary>
        public string? FailedInsertEditContent { get; set; }

        /// <summary>失败的 replace_string 输入</summary>
        public ReplaceStringInput? FailedReplaceInput { get; set; }

        /// <summary>匹配失败的详细原因</summary>
        public string FailureReason { get; set; } = string.Empty;

        /// <summary>失败上下文：具体哪些 Hunk/区域未匹配</summary>
        public List<string>? FailedContextDetails { get; set; }
    }

    /// <summary>
    /// Healing 响应：AI 模型修正后的编辑。
    /// </summary>
    public class HealingResponse
    {
        /// <summary>是否成功修正</summary>
        public bool Success { get; set; }

        /// <summary>修正后的 Patch 操作（apply_patch 模式）</summary>
        public PatchOperation? CorrectedPatch { get; set; }

        /// <summary>修正后的完整文件内容（insert_edit_into_file 模式）</summary>
        public string? CorrectedInsertEditContent { get; set; }

        /// <summary>修正后的字符串替换（replace_string 模式）</summary>
        public ReplaceStringInput? CorrectedReplaceInput { get; set; }

        /// <summary>错误描述</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>使用的 Healing 模型名称</summary>
        public string? ModelUsed { get; set; }

        /// <summary>耗时（毫秒）</summary>
        public long ElapsedMs { get; set; }
    }

    /// <summary>
    /// Healing 策略配置。
    /// </summary>
    public class HealingConfig
    {
        /// <summary>是否启用 Healing</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>降级模型最大尝试次数</summary>
        public int MaxRetriesWithFallbackModel { get; set; } = 1;

        /// <summary>完整模型最大尝试次数</summary>
        public int MaxRetriesWithFullModel { get; set; } = 1;

        /// <summary>Healing 超时（秒）</summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>降级模型名称（如 copilot-fast）</summary>
        public string? FallbackModelName { get; set; }
    }

    /// <summary>
    /// Healing 统计信息（用于遥测）。
    /// </summary>
    public class HealingStats
    {
        /// <summary>总 Healing 请求次数</summary>
        public int TotalRequests { get; set; }

        /// <summary>成功次数</summary>
        public int SuccessCount { get; set; }

        /// <summary>降级模型成功次数</summary>
        public int FallbackModelSuccessCount { get; set; }

        /// <summary>完整模型成功次数</summary>
        public int FullModelSuccessCount { get; set; }

        /// <summary>完全失败次数</summary>
        public int TotalFailures { get; set; }

        /// <summary>平均耗时（毫秒）</summary>
        public double AverageElapsedMs { get; set; }
    }
}
