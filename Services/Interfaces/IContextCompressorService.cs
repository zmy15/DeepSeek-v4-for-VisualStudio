using DeepSeek_v4_for_VisualStudio.Models;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 上下文压缩服务接口 — 当 Token 预算接近上限时压缩早期消息为摘要。
    /// </summary>
    public interface IContextCompressorService
    {
        IReadOnlyList<CompressedTurnSummary> CompressedSummaries { get; }
        CompressionConfig Config { get; }
        int TotalCompressedTokens { get; }

        string GetCompressedContextText();
        void Clear();
    }
}
