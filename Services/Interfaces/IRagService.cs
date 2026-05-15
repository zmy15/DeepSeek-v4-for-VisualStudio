using DeepSeek_v4_for_VisualStudio.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// RAG 服务接口 — 管理 RAG 提供者的注册、选择和上下文注入。
    /// </summary>
    public interface IRagService
    {
        bool IsEnabled { get; set; }
        double CacheSimilarityThreshold { get; set; }
        IRagProvider? ActiveProvider { get; }
        IReadOnlyList<string> RegisteredProviders { get; }

        void RegisterProvider(IRagProvider provider);
        void UnregisterProvider(string providerName);
        Task<bool> ActivateProviderAsync(string providerName, string config);
        Task<string> RetrieveContextAsync(string query, int topK = 5, CancellationToken cancellationToken = default);
        Task IndexFilesAsync(IEnumerable<FileParseResult> files);
        Task<RagStats?> GetStatsAsync();
    }
}
