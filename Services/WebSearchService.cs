using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 联网搜索服务。
    /// 搜索优先级：百度千帆 API（需 API Key）→ Bing API（需 Azure Key）→ DuckDuckGo（免费备用）。
    ///
    ///  计费提醒：
    /// - 百度搜索：每月免费 1500 次（约每天 50 次），超出后按量后付费
    ///   详情: https://cloud.baidu.com/doc/qianfan/s/Mmh4sv6ec
    /// - Bing 搜索：每月免费 1000 次（S1 免费层），超出后按量付费
    ///   获取 Key: https://portal.azure.com/ → 创建 Bing Search 资源
    /// - DuckDuckGo：完全免费，但结果质量可能不如百度/Bing
    /// - 当百度/Bing 额度耗尽时会自动切换到 DuckDuckGo
    /// </summary>
    public class WebSearchService : IWebSearchService
    {
        #region Constants

        /// <summary>百度千帆搜索 API 端点</summary>
        private const string BaiduSearchApiUrl = "https://qianfan.baidubce.com/v2/ai_search/web_search";

        /// <summary>Bing Web Search API v7 端点</summary>
        private const string BingSearchApiUrl = "https://api.bing.microsoft.com/v7.0/search";

        /// <summary>DuckDuckGo Lite 搜索端点（免费备用，无 JS，易解析）</summary>
        private const string DuckDuckGoLiteUrl = "https://lite.duckduckgo.com/lite/";

        /// <summary>搜索结果最大条数</summary>
        private const int MaxSearchResults = 10;

        /// <summary>请求超时（秒）</summary>
        private const int RequestTimeoutSeconds = 15;

        /// <summary>百度 API 额度耗尽错误码（常见值）</summary>
        private const int BaiduQuotaExhaustedCode = 17;

        /// <summary>fetch_webpage 结果文本中图片 URL 块的起始标记。</summary>
        public const string WebImagesBlockStart = "[WEB_IMAGES]";

        /// <summary>fetch_webpage 结果文本中图片 URL 块的结束标记。</summary>
        public const string WebImagesBlockEnd = "[/WEB_IMAGES]";

        /// <summary>单页网页最多提取的图片 URL 数（防止视觉模型上下文被图片撑爆）。</summary>
        public const int MaxImagesPerPage = 8;

        /// <summary>一次 fetch_webpage 结果最多携带的图片总数（递归抓取多页时兜底）。</summary>
        public const int MaxTotalImages = 10;

        /// <summary>单页网页最多提取的子链接数（供递归抓取，实际只会追 3 个）。</summary>
        public const int MaxLinksPerPage = 24;

        /// <summary>
        /// 视觉模型（deepseek-v4-flash-vision-exp）支持的图片扩展名。
        /// DeepSeek 官方支持 JPEG/PNG/GIF/WebP，且按文件实际内容而非文件名判定；
        /// 客户端无法廉价读取文件头，只能按路径扩展名做预过滤，
        /// 避免把 .svg 等不支持格式作为 image_url 直传导致 HTTP 400。
        /// </summary>
        private static readonly HashSet<string> VisionSupportedImageExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png", ".jpg", ".jpeg", ".gif", ".webp",
            };

        #endregion

        #region Properties

        private readonly HttpClient _httpClient;
        private string? _baiduApiKey;
        private string? _bingApiKey;
        private SearchProvider _activeProvider;
        private bool _isBaiduQuotaExhausted;

        /// <summary>
        /// 获取当前正在使用的搜索提供商。
        /// </summary>
        public SearchProvider ActiveProvider => _activeProvider;

        /// <summary>
        /// 百度额度是否已耗尽（本次会话内）。
        /// </summary>
        public bool IsBaiduQuotaExhausted => _isBaiduQuotaExhausted;

        #endregion

        #region Constructors

        public WebSearchService()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 DeepSeekVS/1.0");

            _activeProvider = SearchProvider.DuckDuckGo;
            _isBaiduQuotaExhausted = false;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 配置百度千帆搜索 API Key。
        /// 设置后优先使用百度搜索，额度耗尽时自动回退到 DuckDuckGo。
        /// </summary>
        /// <param name="apiKey">百度千帆平台的 API Key（AppBuilder API Key）</param>
        public void ConfigureBaiduSearch(string apiKey)
        {
            _baiduApiKey = apiKey;
            _isBaiduQuotaExhausted = false;

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                _activeProvider = SearchProvider.Baidu;
                Logger.Info(LocalizationService.Instance["websearch.providerSwitched"]);
            }
            else
            {
                _activeProvider = SearchProvider.DuckDuckGo;
                Logger.Info(LocalizationService.Instance["websearch.baiduNotConfigured"]);
            }
        }

        /// <summary>
        /// 配置 Bing Web Search API Key（Azure 订阅密钥）。
        /// 设置后优先使用 Bing 搜索。
        /// </summary>
        /// <param name="apiKey">Azure Portal 中 Bing Search 资源的订阅密钥</param>
        public void ConfigureBingSearch(string apiKey)
        {
            _bingApiKey = apiKey;

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                _activeProvider = SearchProvider.Bing;
                Logger.Info("搜索提供商切换为: Bing (Azure)");
            }
            else
            {
                _activeProvider = SearchProvider.DuckDuckGo;
                Logger.Info("未配置 Bing API Key，使用 DuckDuckGo 搜索");
            }
        }

        /// <summary>
        /// 执行联网搜索，返回格式化的搜索结果。
        /// 自动处理百度额度耗尽 → DuckDuckGo 的切换。
        /// </summary>
        /// <param name="query">搜索关键词</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <param name="searchRecency">时效过滤（可选）：week/month/semiyear/year</param>
        /// <returns>搜索结果列表（标题、URL、摘要）</returns>
        public async Task<List<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default, string? searchRecency = null)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<WebSearchResult>();

            Logger.Info($"开始联网搜索: \"{query}\", 提供商: {_activeProvider}");

            try
            {
                var ct = cancellationToken;

                // ── 百度搜索（优先） ──
                if (_activeProvider == SearchProvider.Baidu && !_isBaiduQuotaExhausted)
                {
                    try
                    {
                        var results = await SearchBaiduAsync(query, ct, searchRecency);
                        if (results.Count > 0)
                            return results;

                        // 百度返回空结果，可能是额度问题，回退到 DuckDuckGo
                        _activeProvider = SearchProvider.DuckDuckGo;
                        Logger.Info(LocalizationService.Instance["websearch.baiduEmptyResult"]);
                    }
                    catch (BaiduQuotaExhaustedException ex)
                    {
                        Logger.Info($"百度额度已耗尽: {ex.Message}，切换到 DuckDuckGo");
                        _isBaiduQuotaExhausted = true;
                        _activeProvider = SearchProvider.DuckDuckGo;
                    }
                    catch (ApiKeyInvalidException)
                    {
                        // 重新抛出，让调用方（DeepSeekChatControl）处理：显示错误并停止，不静默回退
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"百度搜索失败: {ex.Message}", ex);
                        // 网络错误等不切换 provider，下次重试
                    }
                }

                // ── Bing 搜索 ──
                if (_activeProvider == SearchProvider.Bing)
                {
                    try
                    {
                        var results = await SearchBingAsync(query, ct, searchRecency);
                        if (results.Count > 0)
                            return results;

                        // Bing 返回空结果，回退到 DuckDuckGo
                        _activeProvider = SearchProvider.DuckDuckGo;
                        Logger.Info("Bing 搜索返回空结果，切换到 DuckDuckGo");
                    }
                    catch (ApiKeyInvalidException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Bing 搜索失败: {ex.Message}", ex);
                    }
                }

                // ── DuckDuckGo（备用） ──
                return await SearchDuckDuckGoAsync(query, cancellationToken);
            }
            catch (ApiKeyInvalidException)
            {
                // 重新抛出，让调用方（DeepSeekChatControl）处理：显示错误并停止
                throw;
            }
            catch (OperationCanceledException)
            {
                Logger.Info(LocalizationService.Instance["websearch.cancelled"]);
                return new List<WebSearchResult>();
            }
            catch (Exception ex)
            {
                Logger.Error($"联网搜索失败: {ex.Message}", ex);
                return new List<WebSearchResult>();
            }
        }

        /// <summary>
        /// 将搜索结果格式化为可供 AI 使用的上下文字符串。
        /// </summary>
        public static string FormatSearchResultsForContext(List<WebSearchResult> results)
        {
            if (results == null || results.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("=== 联网搜索结果 ===");
            sb.AppendLine();

            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                sb.AppendLine($"[{i + 1}] {r.Title}");
                sb.AppendLine($"URL: {r.Url}");
                sb.AppendLine($"摘要: {r.Snippet}");
                if (!string.IsNullOrWhiteSpace(r.Date))
                    sb.AppendLine($"日期: {r.Date}");
                sb.AppendLine();
            }

            sb.AppendLine("=== 搜索结果结束 ===");
            sb.AppendLine(AiPrompts.WebSearchContextInstruction);
            return sb.ToString();
        }

        /// <summary>
        /// 批量抓取多个 URL 的网页内容，并格式化为上下文文本。
        /// 这是"尽力而为"的操作：单个 URL 失败不影响其他 URL。
        /// </summary>
        /// <param name="urls">待抓取的 URL 列表</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="maxFetchCount">最多抓取的 URL 数量（默认 5）</param>
        /// <param name="maxContentLength">单个页面内容最大字符数（默认 2000）</param>
        /// <returns>格式化的链接上下文文本，如果没有成功抓取到内容则返回空字符串</returns>
        public async Task<string> FetchUrlContextAsync(
            List<string> urls,
            CancellationToken ct = default,
            int maxFetchCount = 5,
            int maxContentLength = 2000)
        {
            if (urls == null || urls.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            int fetchedCount = 0;

            foreach (string url in urls)
            {
                if (fetchedCount >= maxFetchCount || ct.IsCancellationRequested)
                    break;

                try
                {
                    string? content = await FetchWebPageContentAsync(url, ct);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        fetchedCount++;
                        // RAG-MARK: no-truncate — 不再截断网页内容
                        // RAG-SOURCE: web-fetch 搜索结果链接网页内容

                        sb.AppendLine($"--- 链接 [{fetchedCount}]: {url} ---");
                        sb.AppendLine(content);
                        sb.AppendLine();
                        Logger.Info($"链接内容抓取成功 ({url}): {content.Length} 字符");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info($"链接内容抓取跳过 ({url}): {ex.Message}");
                }
            }

            if (fetchedCount == 0)
                return string.Empty;

            // 添加头部说明
            var header = new StringBuilder();
            header.AppendLine("=== 用户消息中的链接内容 ===");
            header.AppendLine($"共抓取 {fetchedCount} 个链接的内容：");
            header.AppendLine();
            header.Append(sb.ToString());
            header.AppendLine("=== 链接内容结束 ===");
            header.AppendLine(AiPrompts.WebFetchContextInstruction);

            return header.ToString();
        }

        /// <summary>
        /// 重置百度额度耗尽标记（新会话开始时调用）。
        /// </summary>
        public void ResetQuotaState()
        {
            if (_isBaiduQuotaExhausted && !string.IsNullOrWhiteSpace(_baiduApiKey))
            {
                _isBaiduQuotaExhausted = false;
                _activeProvider = SearchProvider.Baidu;
                Logger.Info(LocalizationService.Instance["websearch.baiduQuotaReset"]);
            }
        }

        /// <summary>
        /// 从指定 URL 抓取网页内容并提取纯文本。
        /// 用于增强搜索结果的上下文信息。
        /// 这是"尽力而为"的操作，失败时返回 null 不影响主流程。
        /// </summary>
        /// <param name="url">网页 URL</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>提取的纯文本内容，失败返回 null</returns>
        public async Task<string?> FetchWebPageContentAsync(string url, CancellationToken ct = default)
        {
            var page = await FetchWebPageContentWithImagesAsync(url, ct);
            return page?.Text;
        }

        /// <summary>
        /// 从指定 URL 抓取网页内容，同时提取纯文本、该页内图片与子链接的绝对 URL。
        /// 图片 URL 供视觉模型直读（只传 http(s) 链接，不下载、不转 base64），
        /// 子链接供 fetch_webpage 递归抓取。
        /// 这是"尽力而为"的操作，失败时返回 null 不影响主流程。
        /// </summary>
        /// <param name="url">网页 URL</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="maxContentLength">正文最大字符数；&lt;=0 表示不截断</param>
        /// <returns>抓取结果（纯文本 + 图片 + 子链接），失败返回 null</returns>
        public async Task<WebPageContent?> FetchWebPageContentWithImagesAsync(
            string url, CancellationToken ct = default, int maxContentLength = 0)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            try
            {
                // ── Punycode 编码域名，防止同形异义攻击（Homograph Attack）──
                url = EncodeUrlHostname(url);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Accept", "text/html,application/xhtml+xml");
                request.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9");

                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return null;

                var html = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(html)) return null;

                // 重定向后以最终 URL 作为相对路径基准，保证相对地址解析正确。
                string baseUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? url;

                // ── 简易 HTML → 纯文本提取 ──
                string text = ExtractTextFromHtml(html);
                if (string.IsNullOrWhiteSpace(text)) return null;

                // maxContentLength > 0 时截断正文（fetch_webpage 的 maxContentLength 参数）
                if (maxContentLength > 0 && text.Length > maxContentLength)
                    text = text.Substring(0, maxContentLength);

                var images = ExtractImageUrls(html, baseUrl);
                var links = ExtractLinkUrls(html, baseUrl);
                return new WebPageContent { Text = text, ImageUrls = images, LinkUrls = links };
            }
            catch (Exception ex)
            {
                Logger.Info($"网页内容抓取失败 ({url}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 对 URL 中的主机名（域名）进行 Punycode 编码，防止同形异义攻击（IDN Homograph Attack）。
        /// 
        /// 同形异义攻击示例：
        /// 攻击者注册使用西里尔字母 'а' (U+0430) 替代拉丁字母 'a' (U+0061) 的域名，
        /// 例如 "аррӏе.com" 看起来像 "apple.com"，实际指向恶意站点。
        /// Punycode 编码将这些 Unicode 域名转为 "xn--" 前缀的 ASCII 形式，
        /// 使浏览器和 HTTP 客户端能够正确区分和处理。
        /// 
        /// 纯 ASCII 域名的 URL 不做任何修改直接返回。
        /// </summary>
        /// <param name="url">原始 URL（可能包含 Unicode 域名）</param>
        /// <returns>域名经过 Punycode 编码后的 URL</returns>
        public static string EncodeUrlHostname(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            try
            {
                // 尝试解析 URL
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return url; // 无法解析的 URL 原样返回

                string host = uri.Host;

                // 快速路径：纯 ASCII 域名无需编码
                bool hasNonAscii = false;
                foreach (char c in host)
                {
                    if (c > 127)
                    {
                        hasNonAscii = true;
                        break;
                    }
                }
                if (!hasNonAscii)
                    return url;

                // 使用 .NET IdnMapping 进行 Punycode 编码
                var idn = new System.Globalization.IdnMapping
                {
                    UseStd3AsciiRules = true
                };
                string punycodeHost = idn.GetAscii(host);

                // 重建 URL：替换主机名部分
                string encodedUrl = url.Replace(
                    uri.Scheme + "://" + host,
                    uri.Scheme + "://" + punycodeHost);

                if (encodedUrl != url)
                {
                    Logger.Info($"[Punycode] 域名编码: {host} → {punycodeHost}");
                }

                return encodedUrl;
            }
            catch (Exception ex)
            {
                Logger.Info($"[Punycode] 域名编码失败 ({url}): {ex.Message}");
                return url; // 编码失败时原样返回
            }
        }

        /// <summary>
        /// 验证百度千帆 API Key 是否有效。
        /// 发送一个最小搜索请求，检查响应码。
        /// </summary>
        /// <returns>null 表示有效，否则返回错误描述</returns>
        public async Task<string?> ValidateBaiduApiKeyAsync()
        {
            if (string.IsNullOrWhiteSpace(_baiduApiKey))
                return LocalizationService.Instance["websearch.baiduApiKeyMissing"];

            try
            {
                var requestBody = new Dictionary<string, object>
                {
                    ["messages"] = new[] { new { role = "user", content = "test" } },
                    ["search_source"] = "baidu_search_v2",
                    ["resource_type_filter"] = new[] { new { type = "web", top_k = 1 } }
                };

                var jsonContent = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                });

                using var request = new HttpRequestMessage(HttpMethod.Post, BaiduSearchApiUrl)
                {
                    Content = new StringContent(jsonContent, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-Appbuilder-Authorization", $"Bearer {_baiduApiKey!}");

                using var response = await _httpClient.SendAsync(request);

                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    int statusCode = (int)response.StatusCode;
                    if (statusCode == 401 || statusCode == 403)
                    {
                        string detail = TryExtractBaiduError(responseJson);
                        return $"百度 API Key 无效或已过期 (HTTP {statusCode})。\n" +
                               $"请通过 工具 → 选项 → DeepSeek Chat → Web Search 重新配置。\n" +
                               $"获取 Key: https://console.bce.baidu.com/ai_apaas/accessKey\n" +
                               (string.IsNullOrEmpty(detail) ? "" : $"详情: {detail}");
                    }
                    if (statusCode == 429)
                        return LocalizationService.Instance["service.webSearch.rateLimit"];
                    return $"百度搜索返回 HTTP {statusCode}，请稍后重试。";
                }

                // 检查业务错误码
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("code", out var codeElement))
                {
                    int code = codeElement.GetInt32();
                    string msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    if (IsAuthError(code, msg))
                    {
                        return $"百度 API Key 认证失败 (code={code})。\n" +
                               $"请确认 Key 来自千帆 AppBuilder 控制台。\n" +
                               $"获取 Key: https://console.bce.baidu.com/ai_apaas/accessKey\n" +
                               $"详情: {msg}";
                    }
                }

                return null; // 有效
            }
            catch (ApiKeyInvalidException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"百度 API 连接失败: {ex.Message}";
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        #endregion

        #region Private Methods - Baidu Qianfan

        /// <summary>
        /// 通过百度千帆 AI 搜索 API 进行网页搜索。
        /// API 文档: https://cloud.baidu.com/doc/qianfan/s/2mh4su4uy
        /// </summary>
        private async Task<List<WebSearchResult>> SearchBaiduAsync(string query, CancellationToken ct, string? searchRecency = null)
        {
            if (string.IsNullOrWhiteSpace(_baiduApiKey))
            {
                Logger.Info("百度 API Key 未配置");
                return new List<WebSearchResult>();
            }

            var requestBody = new Dictionary<string, object>
            {
                ["messages"] = new[]
                {
                    new { role = "user", content = TruncateQuery(query, 72) }
                },
                ["search_source"] = "baidu_search_v2",
                ["resource_type_filter"] = new[]
                {
                    new { type = "web", top_k = MaxSearchResults }
                }
            };

            if (!string.IsNullOrWhiteSpace(searchRecency))
            {
                requestBody["search_recency_filter"] = searchRecency!;
            }

            var jsonContent = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, BaiduSearchApiUrl)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Appbuilder-Authorization", $"Bearer {_baiduApiKey!}");

            using var response = await _httpClient.SendAsync(request, ct);

            var responseJson = await response.Content.ReadAsStringAsync();

            // ── 检查错误响应 ──
            if (!response.IsSuccessStatusCode)
            {
                int statusCode = (int)response.StatusCode;
                Logger.Error($"百度搜索返回 HTTP {statusCode}: {responseJson}");

                if (statusCode == 401 || statusCode == 403)
                {
                    string detail = TryExtractBaiduError(responseJson);
                    throw new ApiKeyInvalidException(
                        $"百度 API Key 无效或已过期 (HTTP {statusCode})。\n" +
                        $"请通过 工具 → 选项 → DeepSeek Chat → Web Search 重新配置。\n" +
                        $"获取 Key: https://console.bce.baidu.com/ai_apaas/accessKey\n" +
                        (string.IsNullOrEmpty(detail) ? "" : $"详情: {detail}"));
                }

                CheckBaiduQuotaError(responseJson);
                return new List<WebSearchResult>();
            }

            // ── 解析成功响应 ──
            try
            {
                using var doc = JsonDocument.Parse(responseJson);

                // 检查业务错误码
                if (doc.RootElement.TryGetProperty("code", out var codeElement))
                {
                    int errorCode = codeElement.GetInt32();
                    string errorMsg = doc.RootElement.TryGetProperty("message", out var msgElement)
                        ? msgElement.GetString() ?? "" : "";

                    Logger.Error($"百度搜索业务错误: code={errorCode}, message={errorMsg}");

                    // 检查认证错误
                    if (IsAuthError(errorCode, errorMsg))
                    {
                        throw new ApiKeyInvalidException(
                            $"百度 API Key 认证失败 (code={errorCode})。\n" +
                            $"请确认 Key 来自千帆 AppBuilder 控制台。\n" +
                            $"获取 Key: https://console.bce.baidu.com/ai_apaas/accessKey\n" +
                            $"详情: {errorMsg}");
                    }

                    if (IsQuotaExhaustedError(errorCode, errorMsg))
                    {
                        throw new BaiduQuotaExhaustedException(
                            $"百度搜索额度已耗尽 (code={errorCode})，已自动切换至 DuckDuckGo。\n" +
                            $"请前往 https://console.bce.baidu.com/ai_apaas/resource 开通后付费或等待次日重置。");
                    }

                    return new List<WebSearchResult>();
                }

                // 解析 references 数组
                var results = new List<WebSearchResult>();
                if (doc.RootElement.TryGetProperty("references", out var references))
                {
                    foreach (var refItem in references.EnumerateArray())
                    {
                        string type = refItem.TryGetProperty("type", out var t) ? t.GetString() ?? "web" : "web";
                        if (type != "web") continue; // 只取网页结果

                        results.Add(new WebSearchResult
                        {
                            Title = refItem.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                            Url = refItem.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                            Snippet = refItem.TryGetProperty("content", out var content)
                                ? TruncateSnippet(content.GetString() ?? "")
                                : "",
                            Date = refItem.TryGetProperty("date", out var date) ? date.GetString() ?? "" : "",
                        });
                    }
                }

                Logger.Info($"百度搜索完成，获取 {results.Count} 条结果");
                return results;
            }
            catch (JsonException ex)
            {
                Logger.Error($"解析百度搜索响应 JSON 失败: {ex.Message}", ex);
                return new List<WebSearchResult>();
            }
        }

        /// <summary>
        /// 检查百度 API 是否返回额度耗尽错误。
        /// </summary>
        private static void CheckBaiduQuotaError(string responseJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("code", out var codeElement))
                {
                    int code = codeElement.GetInt32();
                    string msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    if (IsQuotaExhaustedError(code, msg))
                    {
                        throw new BaiduQuotaExhaustedException(
                            $"百度搜索额度已耗尽 (code={code})，已自动切换至 DuckDuckGo。\n" +
                            $"请前往 https://console.bce.baidu.com/ai_apaas/resource 开通后付费或等待每日重置。");
                    }
                }
            }
            catch (BaiduQuotaExhaustedException)
            {
                throw;
            }
            catch
            {
                // 非 JSON 响应或解析失败，忽略
            }
        }

        /// <summary>
        /// 判断是否为额度耗尽错误。
        /// </summary>
        private static bool IsQuotaExhaustedError(int code, string message)
        {
            if (code == BaiduQuotaExhaustedCode) return true;

            var lowerMsg = message.ToLowerInvariant();
            return lowerMsg.Contains("quota") ||
                   lowerMsg.Contains("exceeded") ||
                   lowerMsg.Contains("limit") ||
                   lowerMsg.Contains("insufficient") ||
                   lowerMsg.Contains("额度") ||
                   lowerMsg.Contains("超出") ||
                   lowerMsg.Contains("免费") ||
                   lowerMsg.Contains("余额不足");
        }

        /// <summary>
        /// 判断是否为认证相关错误（API Key 无效等）。
        /// </summary>
        private static bool IsAuthError(int code, string message)
        {
            if (code == 1 || code == 2 || code == 111 || code == 112) return true; // 常见认证错误码

            var lowerMsg = message.ToLowerInvariant();
            return lowerMsg.Contains("auth") ||
                   lowerMsg.Contains("invalid") ||
                   lowerMsg.Contains("apikey") ||
                   lowerMsg.Contains("unauthorized") ||
                   lowerMsg.Contains("认证失败") ||
                   lowerMsg.Contains("ak/sk") ||
                   lowerMsg.Contains("access key") ||
                   lowerMsg.Contains("token") ||
                   lowerMsg.Contains("permission");
        }

        /// <summary>
        /// 尝试从百度错误响应中提取可读的错误消息。
        /// </summary>
        private static string TryExtractBaiduError(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson)) return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("message", out var msg))
                    return msg.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("error_msg", out var emsg))
                    return emsg.GetString() ?? "";
            }
            catch { }
            return responseJson.Length > 200 ? responseJson.Substring(0, 200) : responseJson;
        }

        #endregion

        #region Private Methods - Bing

        /// <summary>
        /// 通过 Bing Web Search API v7 进行搜索。
        /// API 文档: https://learn.microsoft.com/en-us/bing/search-apis/bing-web-search/reference/endpoints
        /// </summary>
        private async Task<List<WebSearchResult>> SearchBingAsync(string query, CancellationToken ct, string? searchRecency = null)
        {
            if (string.IsNullOrWhiteSpace(_bingApiKey))
            {
                Logger.Info("Bing API Key 未配置");
                return new List<WebSearchResult>();
            }

            // 构建 URL 参数
            var uriBuilder = new StringBuilder(BingSearchApiUrl);
            uriBuilder.Append("?q=");
            uriBuilder.Append(Uri.EscapeDataString(query));
            uriBuilder.Append("&count=");
            uriBuilder.Append(MaxSearchResults);
            uriBuilder.Append("&mkt=zh-CN");
            uriBuilder.Append("&textFormat=Raw");

            if (!string.IsNullOrWhiteSpace(searchRecency))
            {
                // 将 searchRecency 映射为 Bing freshness 参数
                string? freshness = searchRecency switch
                {
                    "week" => "Week",
                    "month" => "Month",
                    "semiyear" => null, // Bing 不支持半年, 忽略
                    "year" => null,      // Bing 不支持年, 忽略
                    _ => null
                };
                if (freshness != null)
                {
                    uriBuilder.Append("&freshness=");
                    uriBuilder.Append(freshness);
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.ToString());
            request.Headers.Add("Ocp-Apim-Subscription-Key", _bingApiKey!);

            using var response = await _httpClient.SendAsync(request, ct);
            var responseJson = await response.Content.ReadAsStringAsync();

            // ── 检查错误响应 ──
            if (!response.IsSuccessStatusCode)
            {
                int statusCode = (int)response.StatusCode;
                Logger.Error($"Bing 搜索返回 HTTP {statusCode}: {responseJson}");

                if (statusCode == 401 || statusCode == 403)
                {
                    string detail = TryExtractBingError(responseJson);
                    throw new ApiKeyInvalidException(
                        $"Bing API Key 无效或已过期 (HTTP {statusCode})。\n" +
                        $"请在 Azure Portal → Bing Search 资源 → Keys and Endpoint 中获取正确密钥。\n" +
                        (string.IsNullOrEmpty(detail) ? "" : $"详情: {detail}"));
                }

                return new List<WebSearchResult>();
            }

            // ── 解析成功响应 ──
            try
            {
                using var doc = JsonDocument.Parse(responseJson);

                var results = new List<WebSearchResult>();

                // 检查是否有错误（Bing 有时返回 200 但包含错误对象）
                if (doc.RootElement.TryGetProperty("_type", out var typeEl) &&
                    typeEl.GetString() == "ErrorResponse")
                {
                    string errorMsg = TryExtractBingError(responseJson);
                    Logger.Error($"Bing 搜索业务错误: {errorMsg}");
                    return new List<WebSearchResult>();
                }

                // 解析 webPages
                if (doc.RootElement.TryGetProperty("webPages", out var webPages) &&
                    webPages.TryGetProperty("value", out var value))
                {
                    foreach (var page in value.EnumerateArray())
                    {
                        results.Add(new WebSearchResult
                        {
                            Title = page.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                            Url = page.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                            Snippet = page.TryGetProperty("snippet", out var snippet)
                                ? TruncateSnippet(snippet.GetString() ?? "")
                                : "",
                            Date = page.TryGetProperty("datePublished", out var date) ? date.GetString() ?? "" : "",
                        });
                    }
                }

                Logger.Info($"Bing 搜索完成，获取 {results.Count} 条结果");
                return results;
            }
            catch (JsonException ex)
            {
                Logger.Error($"解析 Bing 搜索响应 JSON 失败: {ex.Message}", ex);
                return new List<WebSearchResult>();
            }
        }

        /// <summary>
        /// 尝试从 Bing 错误响应中提取可读的错误消息。
        /// </summary>
        private static string TryExtractBingError(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson)) return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                    errors.GetArrayLength() > 0)
                {
                    var first = errors[0];
                    string msg = first.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    string code = first.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                    return string.IsNullOrEmpty(code) ? msg : $"[{code}] {msg}";
                }
            }
            catch { }
            return responseJson.Length > 200 ? responseJson.Substring(0, 200) : responseJson;
        }

        #endregion

        #region Private Methods - DuckDuckGo

        /// <summary>
        /// 通过 DuckDuckGo Lite 搜索（免费备用，无需 API Key）。
        /// </summary>
        private async Task<List<WebSearchResult>> SearchDuckDuckGoAsync(string query, CancellationToken ct)
        {
            var results = new List<WebSearchResult>();

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("q", query),
            });

            using var response = await _httpClient.PostAsync(DuckDuckGoLiteUrl, content, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync();
            results = ParseDuckDuckGoLiteHtml(html);

            Logger.Info($"DuckDuckGo 搜索完成，获取 {results.Count} 条结果");
            return results;
        }

        /// <summary>
        /// 解析 DuckDuckGo Lite HTML，提取搜索结果。
        /// </summary>
        private static List<WebSearchResult> ParseDuckDuckGoLiteHtml(string html)
        {
            var results = new List<WebSearchResult>();

            try
            {
                var linkMatches = Regex.Matches(html,
                    @"<a\s+[^>]*href\s*=\s*""(?<url>[^""]+)""[^>]*>(?<title>.*?)</a>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                var snippetMatches = Regex.Matches(html,
                    @"<span\s+class\s*=\s*""[^""]*snippet[^""]*""[^>]*>(?<snippet>.*?)</span>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                for (int i = 0; i < linkMatches.Count && results.Count < MaxSearchResults; i++)
                {
                    var linkMatch = linkMatches[i];
                    string rawUrl = linkMatch.Groups["url"].Value;
                    string rawTitle = linkMatch.Groups["title"].Value;

                    if (string.IsNullOrWhiteSpace(rawUrl) ||
                        rawUrl.StartsWith("//duckduckgo.com") ||
                        rawUrl.StartsWith("/") ||
                        rawUrl.Contains("duckduckgo.com"))
                        continue;

                    string title = StripHtmlTags(HttpUtility.HtmlDecode(rawTitle)).Trim();
                    if (string.IsNullOrWhiteSpace(title))
                        continue;

                    string url = rawUrl;
                    if (url.StartsWith("//"))
                        url = "https:" + url;

                    string snippet = string.Empty;
                    if (i < snippetMatches.Count)
                    {
                        snippet = StripHtmlTags(HttpUtility.HtmlDecode(
                            snippetMatches[i].Groups["snippet"].Value)).Trim();
                    }

                    if (string.IsNullOrWhiteSpace(snippet))
                    {
                        int linkEnd = linkMatch.Index + linkMatch.Length;
                        int nextTag = html.IndexOf('<', linkEnd);
                        if (nextTag > linkEnd)
                        {
                            string afterLink = html.Substring(linkEnd, nextTag - linkEnd);
                            snippet = StripHtmlTags(HttpUtility.HtmlDecode(afterLink)).Trim();
                        }
                    }

                    results.Add(new WebSearchResult
                    {
                        Title = title,
                        Url = url,
                        Snippet = string.IsNullOrWhiteSpace(snippet) ? LocalizationService.Instance["service.webSearch.noSummary"] : snippet,
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"解析 DuckDuckGo 搜索结果异常: {ex.Message}", ex);
            }

            return results;
        }

        #endregion

        #region Private Methods - Helpers

        /// <summary>
        /// 截断查询词，百度 API 限制 72 字符（一个汉字 = 2 字符）。
        /// </summary>
        private static string TruncateQuery(string query, int maxBytes)
        {
            if (string.IsNullOrEmpty(query)) return query;
            int byteCount = Encoding.UTF8.GetByteCount(query);
            if (byteCount <= maxBytes) return query;

            // 按字符逐步截断直到字节数 <= maxBytes
            var sb = new StringBuilder();
            foreach (char c in query)
            {
                if (Encoding.UTF8.GetByteCount(sb.ToString() + c) > maxBytes)
                    break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 截断过长的摘要文本。
        /// </summary>
        private static string TruncateSnippet(string snippet, int maxLength = 500)
        {
            if (string.IsNullOrEmpty(snippet)) return LocalizationService.Instance["service.webSearch.noSummary"];
            if (snippet.Length <= maxLength) return snippet.Trim();
            return snippet.Substring(0, maxLength).Trim() + "...";
        }

        /// <summary>
        /// 移除 HTML 标签，保留纯文本。
        /// </summary>
        private static string StripHtmlTags(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string result = Regex.Replace(input, @"<[^>]+>", " ");
            result = Regex.Replace(result, @"\s+", " ").Trim();
            result = HttpUtility.HtmlDecode(result);
            return result;
        }

        /// <summary>
        /// 从 HTML 中提取纯文本内容（简易实现）。
        /// 移除 script/style 标签后，提取 body 内的可见文本。
        /// </summary>
        private static string ExtractTextFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;

            try
            {
                // 移除 script 和 style 内容
                html = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                html = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
                html = Regex.Replace(html, @"<head[^>]*>.*?</head>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

                // 提取 body 内容
                var bodyMatch = Regex.Match(html, @"<body[^>]*>(.*?)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                string bodyContent = bodyMatch.Success ? bodyMatch.Groups[1].Value : html;

                // 移除所有 HTML 标签
                string text = StripHtmlTags(bodyContent);

                // RAG-MARK: no-truncate — 不再截断提取的网页文本
                // RAG-SOURCE: web-fetch 网页文本提取（HTML 解析结果）

                return text.Trim();
            }
            catch
            {
                return StripHtmlTags(html).Trim();
            }
        }

        /// <summary>
        /// 从 HTML 中提取 &lt;img&gt; 标签的图片绝对 URL（供视觉模型直读）。
        /// 支持 src 与懒加载的 data-src；跳过 data: 内联、javascript: 等无效值，
        /// 相对路径基于页面最终 URL 转为绝对 http(s) 链接。
        /// </summary>
        /// <param name="html">原始 HTML</param>
        /// <param name="baseUrl">页面绝对 URL（用于解析相对路径）</param>
        /// <param name="maxImages">最多提取的图片数（默认每页上限）</param>
        public static List<string> ExtractImageUrls(string html, string baseUrl, int maxImages = MaxImagesPerPage)
        {
            var urls = new List<string>();
            if (string.IsNullOrWhiteSpace(html) || maxImages <= 0) return urls;

            Uri? baseUri = null;
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBase) &&
                (parsedBase.Scheme == Uri.UriSchemeHttp || parsedBase.Scheme == Uri.UriSchemeHttps))
            {
                baseUri = parsedBase;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 同时匹配 src 与 data-src（懒加载图片）
            foreach (Match m in Regex.Matches(
                html,
                @"<img\b[^>]*?\b(?:src|data-src)\s*=\s*(['""])(.*?)\1",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string? absolute = NormalizeWebUrl(m.Groups[2].Value, baseUri);
                if (absolute == null) continue;
                if (seen.Add(absolute))
                {
                    urls.Add(absolute);
                    if (urls.Count >= maxImages) break;
                }
            }

            return urls;
        }

        /// <summary>
        /// 从 HTML 中提取 &lt;a&gt; 标签的链接绝对 URL（供递归抓取子页面）。
        /// 相对路径基于页面最终 URL 转为绝对 http(s) 链接，
        /// 过滤 mailto:/tel:/javascript: 等非网页链接。
        /// </summary>
        /// <param name="html">原始 HTML</param>
        /// <param name="baseUrl">页面绝对 URL（用于解析相对路径）</param>
        /// <param name="maxLinks">最多提取的链接数（默认每页上限）</param>
        public static List<string> ExtractLinkUrls(string html, string baseUrl, int maxLinks = MaxLinksPerPage)
        {
            var urls = new List<string>();
            if (string.IsNullOrWhiteSpace(html) || maxLinks <= 0) return urls;

            Uri? baseUri = null;
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBase) &&
                (parsedBase.Scheme == Uri.UriSchemeHttp || parsedBase.Scheme == Uri.UriSchemeHttps))
            {
                baseUri = parsedBase;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in Regex.Matches(
                html,
                @"<a\b[^>]*?\bhref\s*=\s*(['""])(.*?)\1",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string? absolute = NormalizeWebUrl(m.Groups[2].Value, baseUri);
                if (absolute == null) continue;
                if (seen.Add(absolute))
                {
                    urls.Add(absolute);
                    if (urls.Count >= maxLinks) break;
                }
            }

            return urls;
        }

        /// <summary>
        /// 将 src/href 值规范化为绝对 http(s) URL（图片与链接共用）。
        /// 过滤 data: / javascript: / about: / mailto: / tel: / 空锚点等无法或不应直传的值。
        /// </summary>
        private static string? NormalizeWebUrl(string? raw, Uri? baseUri)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim();

            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
                || raw == "#")
            {
                return null;
            }

            try
            {
                raw = HttpUtility.HtmlDecode(raw);

                if (Uri.TryCreate(raw, UriKind.Absolute, out var abs))
                {
                    return (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps)
                        ? abs.AbsoluteUri : null;
                }

                if (baseUri != null && Uri.TryCreate(baseUri, raw, out var rel))
                {
                    return (rel.Scheme == Uri.UriSchemeHttp || rel.Scheme == Uri.UriSchemeHttps)
                        ? rel.AbsoluteUri : null;
                }
            }
            catch
            {
                // 解析失败视为无效 URL
            }

            return null;
        }

        /// <summary>
        /// 在工具结果文本末尾追加图片 URL 块（供视觉模型消费）。
        /// 没有有效图片时原样返回，不追加任何内容。
        /// </summary>
        public static string AppendWebImagesBlock(string content, IEnumerable<string>? imageUrls)
        {
            var urls = imageUrls?
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxTotalImages)
                .ToList();

            if (urls == null || urls.Count == 0)
                return content;

            var sb = new StringBuilder(content);
            sb.AppendLine();
            sb.AppendLine(WebImagesBlockStart);
            foreach (string u in urls)
                sb.AppendLine(u);
            sb.AppendLine(WebImagesBlockEnd);
            return sb.ToString();
        }

        /// <summary>
        /// 解析工具结果文本中的图片 URL 块，返回移除图片块后的干净文本与图片 URL 列表。
        /// 没有图片块时返回原文与空列表。
        /// </summary>
        public static (string CleanText, List<string> ImageUrls) ParseWebImagesBlock(string raw)
        {
            var urls = new List<string>();
            if (string.IsNullOrEmpty(raw))
                return (raw ?? string.Empty, urls);

            int start = raw.IndexOf(WebImagesBlockStart, StringComparison.Ordinal);
            if (start < 0)
                return (raw, urls);

            int contentStart = start + WebImagesBlockStart.Length;
            int end = raw.IndexOf(WebImagesBlockEnd, contentStart, StringComparison.Ordinal);
            if (end < 0)
                return (raw, urls);

            string cleanText = raw.Remove(start, (end + WebImagesBlockEnd.Length) - start).TrimEnd();

            string block = raw.Substring(contentStart, end - contentStart);
            foreach (string line in block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string u = line.Trim();
                if (Uri.TryCreate(u, UriKind.Absolute, out var parsed)
                    && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                {
                    urls.Add(u);
                }
            }

            return (cleanText, urls);
        }

        /// <summary>
        /// 过滤出视觉模型支持的图片 URL（按路径扩展名判断，忽略 query/fragment）。
        /// 非支持格式（如 .svg/.bmp/.tiff/.ico）或无扩展名的 URL 会被丢弃，
        /// 避免作为 image_url 直传视觉模型导致 HTTP 400。
        /// 所有 URL 都被过滤掉时返回 null（而非空列表），供调用方直接判断。
        /// </summary>
        public static List<string>? FilterVisionImageUrls(IEnumerable<string>? urls)
        {
            if (urls == null)
                return null;

            var result = new List<string>();
            foreach (string u in urls)
            {
                if (string.IsNullOrWhiteSpace(u))
                    continue;

                string? ext = GetUrlPathExtension(u);
                if (ext != null && VisionSupportedImageExtensions.Contains(ext))
                    result.Add(u);
            }

            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// 提取 URL 路径部分的扩展名（含点，如 ".svg" / ".png"），忽略 query 与 fragment。
        /// 无法解析为绝对 http(s) URL 或路径无扩展名时返回 null。
        /// </summary>
        private static string? GetUrlPathExtension(string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return null;
                if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    return null;

                string ext = Path.GetExtension(uri.AbsolutePath);
                return string.IsNullOrEmpty(ext) ? null : ext;
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>抓取到的网页内容：纯文本 + 该页图片/子链接的绝对 URL。</summary>
    public sealed class WebPageContent
    {
        /// <summary>提取后的网页纯文本。</summary>
        public string? Text { get; set; }

        /// <summary>网页内图片的绝对 http(s) URL 列表（供视觉模型直读）。</summary>
        public List<string> ImageUrls { get; set; } = new();

        /// <summary>网页内 &lt;a&gt; 链接的绝对 http(s) URL 列表（供递归抓取子页面）。</summary>
        public List<string> LinkUrls { get; set; } = new();
    }

    /// <summary>
    /// 搜索提供商枚举。
    /// </summary>
    public enum SearchProvider
    {
        /// <summary>DuckDuckGo（完全免费，无需 API Key，备用方案）</summary>
        DuckDuckGo,

        /// <summary>百度千帆 AI 搜索（每月免费 1500 次，需 API Key）</summary>
        Baidu,

        /// <summary>Bing Web Search API v7（需 Azure 订阅 Key，免费层 1000 次/月）</summary>
        Bing,
    }

    /// <summary>
    /// 单条网络搜索结果。
    /// </summary>
    public class WebSearchResult
    {
        /// <summary>结果标题</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>结果 URL</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>结果摘要</summary>
        public string Snippet { get; set; } = string.Empty;

        /// <summary>发布日期（百度 API 返回，DuckDuckGo 可能为空）</summary>
        public string Date { get; set; } = string.Empty;
    }

    /// <summary>
    /// 百度搜索额度耗尽时抛出的异常。
    /// </summary>
    internal class BaiduQuotaExhaustedException : Exception
    {
        public BaiduQuotaExhaustedException(string message) : base(message) { }
    }

    /// <summary>
    /// API Key 无效或认证失败时抛出的异常。
    /// 用于 DeepSeek API 和百度 API 的认证错误统一处理。
    /// </summary>
    public class ApiKeyInvalidException : Exception
    {
        public ApiKeyInvalidException(string message) : base(message) { }
    }

    #endregion
}
