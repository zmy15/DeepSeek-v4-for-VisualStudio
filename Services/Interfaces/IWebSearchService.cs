using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 联网搜索服务接口。
    /// </summary>
    public interface IWebSearchService : IDisposable
    {
        SearchProvider ActiveProvider { get; }
        bool IsBaiduQuotaExhausted { get; }

        void ConfigureBaiduSearch(string apiKey);
        Task<List<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default, string? searchRecency = null);
    }
}
