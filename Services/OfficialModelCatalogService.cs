using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// DeepSeek 官方模型目录缓存。启动时通过 OpenAI 兼容的 GET /models
    /// 获取可用模型；请求失败时保留内置目录作为降级列表。
    /// </summary>
    public static class OfficialModelCatalogService
    {
        private static readonly object StateGate = new();
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

        private static IReadOnlyList<string> _models = DeepSeekModelCatalog.All;
        private static bool _hasRemoteModels;
        private static string _lastApiKey = string.Empty;
        private static DateTimeOffset _lastFetchUtc = DateTimeOffset.MinValue;
        private static Task<IReadOnlyList<string>>? _inFlight;
        private static Guid _activeFetchId;

        /// <summary>远程模型列表发生变化时触发；回调方需自行切换回 UI 线程。</summary>
        public static event Action? ModelsChanged;

        public static IReadOnlyList<string> GetModels()
        {
            lock (StateGate)
                return _models;
        }

        public static bool HasRemoteModels
        {
            get
            {
                lock (StateGate)
                    return _hasRemoteModels;
            }
        }

        /// <summary>官方模式没有选中模型时的默认模型；优先取接口返回的第一项。</summary>
        public static string GetDefaultModel()
            => GetModels().FirstOrDefault() ?? DeepSeekModelCatalog.Pro;

        /// <summary>
        /// 获取或刷新官方模型列表。相同 Key 的成功结果在缓存期内直接复用；
        /// Key 变化会强制重新请求，失败时返回当前降级列表。
        /// </summary>
        public static Task<IReadOnlyList<string>> RefreshAsync(
            string? apiKey,
            CancellationToken cancellationToken = default,
            HttpMessageHandler? httpMessageHandler = null)
        {
            var normalizedKey = (apiKey ?? string.Empty).Trim();
            lock (StateGate)
            {
                var keyChanged = !string.Equals(_lastApiKey, normalizedKey, StringComparison.Ordinal);
                if (!keyChanged && _inFlight != null)
                    return _inFlight;

                var isFresh = _hasRemoteModels &&
                    DateTimeOffset.UtcNow - _lastFetchUtc < CacheLifetime;
                if (normalizedKey.Length == 0)
                {
                    _lastApiKey = normalizedKey;
                    return Task.FromResult(GetModels());
                }

                if (!keyChanged && isFresh)
                    return Task.FromResult(GetModels());

                _lastApiKey = normalizedKey;
                var fetchId = Guid.NewGuid();
                _activeFetchId = fetchId;
                _inFlight = FetchCoreAsync(
                    normalizedKey, cancellationToken, httpMessageHandler, fetchId);
                return _inFlight;
            }
        }

        private static async Task<IReadOnlyList<string>> FetchCoreAsync(
            string apiKey,
            CancellationToken cancellationToken,
            HttpMessageHandler? httpMessageHandler,
            Guid fetchId)
        {
            try
            {
                var models = await ModelFetchService.FetchModelsAsync(
                    DeepSeekApiService.DefaultBaseUrl,
                    apiKey,
                    cancellationToken,
                    httpMessageHandler);

                bool changed;
                bool isActive;
                lock (StateGate)
                {
                    isActive = _activeFetchId == fetchId &&
                        string.Equals(_lastApiKey, apiKey, StringComparison.Ordinal);
                    if (isActive)
                    {
                        changed = !_hasRemoteModels || !models.SequenceEqual(_models);
                        _models = models;
                        _hasRemoteModels = true;
                        _lastFetchUtc = DateTimeOffset.UtcNow;
                        _inFlight = null;
                    }
                    else
                    {
                        changed = false;
                    }
                }

                if (!isActive)
                {
                    Logger.Info("[Models] 忽略已过期的官方模型列表响应");
                    return GetModels();
                }

                Logger.Info($"[Models] 官方模型列表刷新成功: {models.Count} 个模型");
                if (changed)
                    ModelsChanged?.Invoke();
                return models;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Models] 官方模型列表刷新失败，使用内置目录: {ex.Message}");
                return GetModels();
            }
            finally
            {
                lock (StateGate)
                    if (_activeFetchId == fetchId)
                        _inFlight = null;
            }
        }

        internal static void ResetForTests(IReadOnlyList<string>? models = null)
        {
            lock (StateGate)
            {
                _models = models ?? DeepSeekModelCatalog.All;
                _hasRemoteModels = models != null;
                _lastApiKey = string.Empty;
                _lastFetchUtc = DateTimeOffset.MinValue;
                _inFlight = null;
                _activeFetchId = Guid.Empty;
            }
        }
    }
}
