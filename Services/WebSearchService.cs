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
    /// 搜索优先级：百度千帆 API（需 API Key）→ DuckDuckGo（免费备用）。
    ///
    /// ⚠️ 计费提醒：
    /// - 百度搜索：每月免费 1500 次（约每天 50 次），超出后按量后付费
    ///   详情: https://cloud.baidu.com/doc/qianfan/s/Mmh4sv6ec
    /// - DuckDuckGo：完全免费，但结果质量可能不如百度
    /// - 当百度额度耗尽时会自动切换到 DuckDuckGo
    /// </summary>
    public class WebSearchService : IDisposable
    {
        #region Constants

        /// <summary>百度千帆搜索 API 端点</summary>
        private const string BaiduSearchApiUrl = "https://qianfan.baidubce.com/v2/ai_search/web_search";

        /// <summary>DuckDuckGo Lite 搜索端点（免费备用，无 JS，易解析）</summary>
        private const string DuckDuckGoLiteUrl = "https://lite.duckduckgo.com/lite/";

        /// <summary>搜索结果最大条数</summary>
        private const int MaxSearchResults = 10;

        /// <summary>请求超时（秒）</summary>
        private const int RequestTimeoutSeconds = 15;

        /// <summary>百度 API 额度耗尽错误码（常见值）</summary>
        private const int BaiduQuotaExhaustedCode = 17;

        #endregion

        #region Properties

        private readonly HttpClient _httpClient;
        private string? _baiduApiKey;
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
                Logger.Info("搜索提供商切换为: 百度千帆");
            }
            else
            {
                _activeProvider = SearchProvider.DuckDuckGo;
                Logger.Info("未配置百度 API Key，使用 DuckDuckGo 搜索");
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
                        Logger.Info("百度搜索返回空结果，尝试 DuckDuckGo");
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
                Logger.Info("联网搜索已取消");
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
                sb.AppendLine($"    URL: {r.Url}");
                sb.AppendLine($"    摘要: {r.Snippet}");
                if (!string.IsNullOrWhiteSpace(r.Date))
                    sb.AppendLine($"    日期: {r.Date}");
                sb.AppendLine();
            }

            sb.AppendLine("=== 搜索结果结束 ===");
            sb.AppendLine("请基于以上联网搜索结果回答用户的问题。如果搜索结果不相关或不足以回答问题，请如实告知用户。");
            return sb.ToString();
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
                Logger.Info("百度搜索额度状态已重置，重新尝试百度搜索");
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
            if (string.IsNullOrWhiteSpace(url)) return null;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Accept", "text/html,application/xhtml+xml");
                request.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9");

                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return null;

                var html = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(html)) return null;

                // ── 简易 HTML → 纯文本提取 ──
                string text = ExtractTextFromHtml(html);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (Exception ex)
            {
                Logger.Info($"网页内容抓取失败 ({url}): {ex.Message}");
                return null;
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
                return "百度 API Key 未配置";

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
                        return "百度搜索请求频率超限，请稍后重试。";
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
                        Snippet = string.IsNullOrWhiteSpace(snippet) ? "(无摘要)" : snippet,
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
            if (string.IsNullOrEmpty(snippet)) return "(无摘要)";
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

                // 截断到合理长度（最多2000字符）
                if (text.Length > 2000)
                    text = text.Substring(0, 2000) + "...";

                return text.Trim();
            }
            catch
            {
                return StripHtmlTags(html).Trim();
            }
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// 搜索提供商枚举。
    /// </summary>
    public enum SearchProvider
    {
        /// <summary>DuckDuckGo（完全免费，无需 API Key，备用方案）</summary>
        DuckDuckGo,

        /// <summary>百度千帆 AI 搜索（每月免费 1500 次，需 API Key）</summary>
        Baidu,
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
