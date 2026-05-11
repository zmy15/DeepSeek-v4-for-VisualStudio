using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 联网搜索优化：附件关键信息提取、搜索词 AI 优化、网页内容抓取。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Web Search Optimization

        /// <summary>
        /// 从已解析的附件内容中提取关键信息，用于优化联网搜索查询。
        /// 当用户上传文件并开启联网搜索时调用，在搜索优化之前执行。
        /// 使用 AI（非流式）从文件内容中提取核心主题、技术关键词、专有名词等，
        /// 返回简洁的摘要供搜索优化阶段使用。
        /// </summary>
        private async Task<string?> ExtractKeyInfoForSearchAsync(string fileContent, string userQuestion, CancellationToken ct)
        {
            if (_apiService == null || string.IsNullOrWhiteSpace(fileContent))
                return null;

            // 截断过长的文件内容，避免 token 消耗过多（取前 8000 字符）
            string truncatedContent = fileContent.Length > 8000
                ? fileContent.Substring(0, 8000) + "\n...[内容已截断]"
                : fileContent;

            var extractionPrompt = AiPrompts.BuildFileExtractionPrompt(userQuestion, truncatedContent);

            try
            {
                var extractionMessages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = AiPrompts.FileExtractionSystem },
                    new ChatApiMessage { Role = "user", Content = extractionPrompt },
                };

                Logger.Info("开始从附件提取关键信息用于搜索优化");
                var rawResponse = await _apiService.CompleteAsync(extractionMessages, ct);
                Logger.Info($"附件关键信息提取原始响应: {rawResponse}");

                string result = rawResponse?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(result) ||
                    result.Equals("NO_INFO", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                // 截断过长结果
                if (result.Length > 500)
                    result = result.Substring(0, 500);

                return result;
            }
            catch (OperationCanceledException)
            {
                Logger.Info("附件关键信息提取已取消");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"附件关键信息提取异常: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 调用 AI 分析用户问题和上下文，生成优化的搜索关键词。
        /// 百度引擎：返回严格 JSON（含 search_recency 时效过滤）。
        /// DuckDuckGo：仅返回优化后的纯文本关键词。
        /// </summary>
        private async Task<SearchQueryOptimization?> OptimizeSearchQueryAsync(string userQuery, CancellationToken ct, bool isBaiduSearch = true)
        {
            if (_apiService == null)
                return null;

            // ── 构建优化提示词 ──
            string contextSummary = string.Empty;
            var history = _contextManager.GetConversationHistory();
            if (history.Count > 1)
            {
                var recent = history
                    .Where(m => m.Role == "user")
                    .Reverse()
                    .Take(3)
                    .Reverse()
                    .Select(m => m.Content?.Length > 100 ? m.Content.Substring(0, 100) + "..." : m.Content);
                contextSummary = string.Join(" | ", recent);
            }

            string contextLine = string.IsNullOrWhiteSpace(contextSummary)
                ? $"用户问题：{userQuery}"
                : $"对话上下文：{contextSummary}\n用户问题：{userQuery}";

            string optimizationPrompt = AiPrompts.BuildSearchOptimizationPrompt(contextLine, isBaiduSearch);
            string systemPrompt = AiPrompts.GetSearchOptimizationSystemPrompt(isBaiduSearch);

            try
            {
                var optimizationMessages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = systemPrompt },
                    new ChatApiMessage { Role = "user", Content = optimizationPrompt },
                };

                Logger.Info($"开始 AI 搜索词优化 ({(isBaiduSearch ? "百度" : "DuckDuckGo")})，原始查询: \"{userQuery}\"");
                var rawResponse = await _apiService.CompleteAsync(optimizationMessages, ct);
                Logger.Info($"AI 搜索词优化原始响应: {rawResponse}");

                if (isBaiduSearch)
                {
                    return ParseAndValidateSearchOptimization(rawResponse, userQuery);
                }
                else
                {
                    string keyword = rawResponse?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(keyword) ||
                        keyword.Equals("NO_SEARCH", StringComparison.OrdinalIgnoreCase))
                    {
                        return new SearchQueryOptimization
                        {
                            SearchQuery = userQuery,
                            NeedSearch = keyword.Equals("NO_SEARCH", StringComparison.OrdinalIgnoreCase) ? false : true,
                        };
                    }
                    keyword = keyword.Trim('"', '\'', '`');
                    if (keyword.Length > 72)
                        keyword = keyword.Substring(0, 72);
                    return new SearchQueryOptimization
                    {
                        SearchQuery = keyword,
                        NeedSearch = true,
                    };
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Info("搜索词优化已取消");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"搜索词优化异常: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 解析并校验 AI 返回的搜索优化 JSON。
        /// 若 JSON 不合法或关键字段缺失，回退到原始查询。
        /// </summary>
        private static SearchQueryOptimization ParseAndValidateSearchOptimization(string rawResponse, string fallbackQuery)
        {
            try
            {
                string jsonStr = rawResponse.Trim();

                // 去掉可能的 markdown 代码块标记
                if (jsonStr.StartsWith("```"))
                {
                    int endOfFirstLine = jsonStr.IndexOf('\n');
                    if (endOfFirstLine > 0)
                        jsonStr = jsonStr.Substring(endOfFirstLine + 1);
                    if (jsonStr.EndsWith("```"))
                        jsonStr = jsonStr.Substring(0, jsonStr.Length - 3);
                    jsonStr = jsonStr.Trim();
                }

                var result = JsonSerializer.Deserialize<SearchQueryOptimization>(jsonStr,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (result == null)
                    throw new InvalidOperationException("JSON 解析结果为 null");

                if (string.IsNullOrWhiteSpace(result.SearchQuery))
                {
                    Logger.Info("AI 优化搜索词为空，使用原始查询");
                    return new SearchQueryOptimization
                    {
                        SearchQuery = fallbackQuery,
                        NeedSearch = result.NeedSearch,
                    };
                }

                // 校验 recency 值
                var validRecencies = new HashSet<string> { "week", "month", "semiyear", "year" };
                if (result.SearchRecency != null && !validRecencies.Contains(result.SearchRecency))
                {
                    Logger.Info($"无效的 search_recency 值: {result.SearchRecency}，已忽略");
                    result.SearchRecency = null;
                }

                Logger.Info($"搜索词优化成功: \"{fallbackQuery}\" → \"{result.SearchQuery}\"");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Info($"搜索优化 JSON 解析失败: {ex.Message}，使用原始查询 \"{fallbackQuery}\"");
                return new SearchQueryOptimization
                {
                    SearchQuery = fallbackQuery,
                    NeedSearch = true,
                };
            }
        }

        /// <summary>
        /// 异步抓取搜索结果中前几条 URL 的网页内容，用于增强搜索上下文。
        /// 这是"尽力而为"的后台操作，失败不影响主流程。
        /// </summary>
        private async Task EnrichSearchContextAsync(List<WebSearchResult> results, CancellationToken ct)
        {
            if (_webSearchService == null || results.Count == 0) return;

            try
            {
                int fetchCount = Math.Min(6, results.Count);
                for (int i = 0; i < fetchCount; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        string? pageContent = await _webSearchService.FetchWebPageContentAsync(results[i].Url, ct);
                        if (!string.IsNullOrWhiteSpace(pageContent))
                        {
                            string enriched = results[i].Snippet +
                                $"\n[网页内容摘要: {TruncateText(pageContent!, 300)}]";
                            results[i].Snippet = TruncateText(enriched, 800);
                            Logger.Info($"网页内容抓取成功: {results[i].Url}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Info($"网页内容抓取跳过 ({results[i].Url}): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"网页内容增强失败: {ex.Message}", ex);
            }
        }

        #endregion
    }
}
