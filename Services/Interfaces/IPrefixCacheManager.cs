using DeepSeek_v4_for_VisualStudio.Models;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 前缀缓存稳定性管理器接口。
    /// </summary>
    public interface IPrefixCacheManager
    {
        bool IsPinned { get; }
        double StabilityRatio { get; }
        int TotalChecks { get; }
        int StableChecks { get; }
        string? PinnedCombinedFingerprint { get; }

        void Pin(string systemPromptFingerprint, string toolCatalogFingerprint, string combinedFingerprint);
        PrefixDriftInfo CheckAndUpdate(string currentSystemPromptFingerprint, string currentToolCatalogFingerprint, string currentCombinedFingerprint);
        PrefixDriftInfo CheckCurrentPrefix(string? systemPrompt, IEnumerable<ToolDefinition>? tools);
        void Reset();
    }
}
