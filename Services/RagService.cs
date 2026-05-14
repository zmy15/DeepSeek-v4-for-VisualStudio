using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// RAG 服务 — 管理 RAG 提供者的注册、选择和上下文注入。
    /// 
    /// 作为 RAG 集成的统一入口：
    /// 1. 支持注册多个 IRagProvider 实现
    /// 2. 根据配置选择活跃的提供者
    /// 3. 在每次对话前自动检索相关文档
    /// 4. 将检索结果注入到 ConversationContextManager
    /// </summary>
    public class RagService
    {
        private readonly Dictionary<string, IRagProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
        private string? _activeProviderName;
        private bool _isEnabled;

        // ── RAG 结果缓存（减少每轮查询变化导致的 Cache Miss）──
        private string? _lastQuery;
        private string? _lastContext;
        /// <summary>
        /// 查询相似度阈值（0.0~1.0）。当新查询与上次查询的 Jaccard 相似度 >= 此值时，复用缓存结果。
        /// 默认 0.6 表示新查询与上次查询有 60% 的词汇重叠时视为相似。
        /// </summary>
        public double CacheSimilarityThreshold { get; set; } = 0.6;

        /// <summary>RAG 是否已启用</summary>
        public bool IsEnabled
        {
            get => _isEnabled && _activeProvider != null && _activeProvider.IsAvailable;
            set => _isEnabled = value;
        }

        private IRagProvider? _activeProvider;
        /// <summary>当前活跃的 RAG 提供者</summary>
        public IRagProvider? ActiveProvider => _activeProvider;

        /// <summary>已注册的提供者名称列表</summary>
        public IReadOnlyList<string> RegisteredProviders => _providers.Keys.ToList();

        /// <summary>
        /// 注册一个 RAG 提供者。
        /// </summary>
        /// <param name="provider">提供者实例</param>
        public void RegisterProvider(IRagProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            _providers[provider.ProviderName] = provider;
            Logger.Info($"[RagService] 已注册 RAG 提供者: {provider.ProviderName} ({provider.Description})");
        }

        /// <summary>
        /// 注销一个 RAG 提供者。
        /// </summary>
        /// <param name="providerName">提供者名称</param>
        public void UnregisterProvider(string providerName)
        {
            if (_providers.Remove(providerName))
            {
                if (_activeProviderName == providerName)
                {
                    _activeProvider = null;
                    _activeProviderName = null;
                }
                Logger.Info($"[RagService] 已注销 RAG 提供者: {providerName}");
            }
        }

        /// <summary>
        /// 激活指定的 RAG 提供者并初始化。
        /// </summary>
        /// <param name="providerName">提供者名称</param>
        /// <param name="config">JSON 配置字符串</param>
        /// <returns>是否激活成功</returns>
        public async Task<bool> ActivateProviderAsync(string providerName, string config)
        {
            if (!_providers.TryGetValue(providerName, out var provider))
            {
                Logger.Warn($"[RagService] 未找到提供者: {providerName}");
                return false;
            }

            bool success = await provider.InitializeAsync(config);
            if (success)
            {
                _activeProvider = provider;
                _activeProviderName = providerName;
                _isEnabled = true;
                Logger.Info($"[RagService] 已激活 RAG 提供者: {providerName}");
            }
            else
            {
                Logger.Warn($"[RagService] 初始化 RAG 提供者失败: {providerName}");
            }

            return success;
        }

        /// <summary>
        /// 停用当前 RAG 提供者。
        /// </summary>
        public void DeactivateProvider()
        {
            _activeProvider = null;
            _activeProviderName = null;
            _isEnabled = false;
            Logger.Info("[RagService] RAG 已停用");
        }

        /// <summary>
        /// 根据用户查询检索相关文档，并返回格式化的上下文字符串。
        /// 这是注入到对话中的主要入口。
        /// 内置结果缓存：当新查询与上次查询高度相似时，复用上次的检索结果，
        /// 以减少 System Message 的变化，从而提升 DeepSeek Prompt Cache 命中率。
        /// </summary>
        /// <param name="query">用户查询</param>
        /// <param name="topK">返回的最大结果数</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>格式化的 RAG 上下文字符串，无结果时返回空字符串</returns>
        public async Task<string> RetrieveContextAsync(
            string query,
            int topK = 5,
            CancellationToken cancellationToken = default)
        {
            if (!IsEnabled || _activeProvider == null)
                return string.Empty;

            // ── 缓存检查：如果新查询与上次查询相似，直接复用缓存 ──
            if (!string.IsNullOrEmpty(_lastQuery) && !string.IsNullOrEmpty(_lastContext))
            {
                double similarity = ComputeQuerySimilarity(_lastQuery, query);
                if (similarity >= CacheSimilarityThreshold)
                {
                    Logger.Info($"[RagService] 🔄 复用缓存 (相似度: {similarity:F2} >= {CacheSimilarityThreshold}): \"{TruncateQuery(query)}\"");
                    return _lastContext;
                }
            }

            try
            {
                var results = await _activeProvider.SearchAsync(query, topK, cancellationToken);
                if (results == null || results.Count == 0)
                {
                    Logger.Info($"[RagService] 检索无结果: \"{TruncateQuery(query)}\"");
                    // 无结果时也缓存空字符串，避免对相同无结果查询重复检索
                    _lastQuery = query;
                    _lastContext = string.Empty;
                    return string.Empty;
                }

                string context = RagContextFormatter.FormatForContext(results);
                Logger.Info($"[RagService] 检索到 {results.Count} 条结果: \"{TruncateQuery(query)}\", " +
                    $"上下文长度: {context.Length} 字符");

                // ── 更新缓存 ──
                _lastQuery = query;
                _lastContext = context;

                return context;
            }
            catch (Exception ex)
            {
                Logger.Error($"[RagService] 检索异常: {ex.Message}", ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// 清除 RAG 结果缓存。在会话切换或用户显式要求刷新时调用。
        /// </summary>
        public void InvalidateCache()
        {
            if (_lastQuery != null)
            {
                Logger.Info("[RagService] 🗑️ RAG 缓存已清除");
                _lastQuery = null;
                _lastContext = null;
            }
        }

        /// <summary>
        /// 计算两个查询的词汇相似度（基于字符 3-gram 的 Jaccard 系数）。
        /// 对于相同话题的连续追问（如 "这段代码有什么问题？" → "那个变量呢？"），
        /// 相似度可能较低；但对于同一文件的重读等场景，查询高度重叠。
        /// </summary>
        private static double ComputeQuerySimilarity(string query1, string query2)
        {
            if (string.IsNullOrEmpty(query1) || string.IsNullOrEmpty(query2))
                return 0;

            // 使用 3-gram 字符级比较，对中英文混合友好
            var grams1 = GetCharGrams(query1, 3);
            var grams2 = GetCharGrams(query2, 3);

            if (grams1.Count == 0 && grams2.Count == 0)
                return 1.0;

            int intersection = 0;
            foreach (var g in grams1)
            {
                if (grams2.Contains(g))
                    intersection++;
            }

            int union = grams1.Count + grams2.Count - intersection;
            return union > 0 ? (double)intersection / union : 0;
        }

        private static HashSet<string> GetCharGrams(string text, int n)
        {
            var grams = new HashSet<string>();
            if (text.Length < n)
            {
                grams.Add(text);
                return grams;
            }
            for (int i = 0; i <= text.Length - n; i++)
            {
                grams.Add(text.Substring(i, n));
            }
            return grams;
        }

        /// <summary>
        /// 将当前解决方案中的文件批量添加到 RAG 知识库。
        /// </summary>
        /// <param name="files">要添加的文件解析结果列表</param>
        public async Task IndexFilesAsync(IEnumerable<FileParseResult> files)
        {
            if (!IsEnabled || _activeProvider == null || files == null)
                return;

            var documents = files
                .Where(f => f.Success && !string.IsNullOrEmpty(f.Content))
                .Select(f => new RagDocument
                {
                    Title = f.FileName,
                    Content = f.Content!,
                    SourcePath = f.FilePath,
                    DocumentType = "code",
                })
                .ToList();

            if (documents.Count > 0)
            {
                await _activeProvider.AddDocumentsAsync(documents);
                Logger.Info($"[RagService] 已索引 {documents.Count} 个文件");
            }
        }

        /// <summary>
        /// 获取 RAG 统计信息。
        /// </summary>
        public async Task<RagStats?> GetStatsAsync()
        {
            if (!IsEnabled || _activeProvider == null)
                return null;

            return await _activeProvider.GetStatsAsync();
        }

        private static string TruncateQuery(string query, int maxLen = 80)
        {
            return query.Length > maxLen ? query.Substring(0, maxLen) + "..." : query;
        }
    }
}
