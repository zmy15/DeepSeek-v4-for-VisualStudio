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

            try
            {
                var results = await _activeProvider.SearchAsync(query, topK, cancellationToken);
                if (results == null || results.Count == 0)
                {
                    Logger.Info($"[RagService] 检索无结果: \"{TruncateQuery(query)}\"");
                    return string.Empty;
                }

                string context = RagContextFormatter.FormatForContext(results);
                Logger.Info($"[RagService] 检索到 {results.Count} 条结果: \"{TruncateQuery(query)}\", " +
                    $"上下文长度: {context.Length} 字符");

                return context;
            }
            catch (Exception ex)
            {
                Logger.Error($"[RagService] 检索异常: {ex.Message}", ex);
                return string.Empty;
            }
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
