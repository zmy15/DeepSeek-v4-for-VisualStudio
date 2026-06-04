using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// fetch_webpage 工具 — 抓取网页内容（支持递归深度抓取）。
    /// </summary>
    public class FetchWebpageTool : BuiltInToolBase
    {
        private readonly WebSearchService? _webSearchService;

        public FetchWebpageTool(WebSearchService? webSearchService = null)
        {
            _webSearchService = webSearchService;
        }

        public override string Name => "fetch_webpage";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "fetch_webpage",
                    Description = L["tool.fetch_webpage.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            url = new { type = "string", description = LocalizationService.Instance["tool.fetchWebpage.param.url"] },
                            maxDepth = new { type = "integer", description = LocalizationService.Instance["tool.fetchWebpage.param.maxDepth"] },
                            maxContentLength = new { type = "integer", description = LocalizationService.Instance["tool.fetchWebpage.param.maxContentLength"] }
                        },
                        required = new[] { "url" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string url = GetStringArg(args, "url");
            return string.IsNullOrEmpty(url)
                ? LocalizationService.Instance["tool.fetchWebpage.fetching"]
                : LocalizationService.Instance.Format("tool.fetchWebpage.fetchingUrl", TruncateText(url, 60));
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("❌")) return toolResult;
            return LocalizationService.Instance.Format("tool.fetchWebpage.complete", toolResult.Length);
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string url = GetStringArg(args, "url");
            int maxDepth = GetIntArg(args, "maxDepth", 1);
            int maxContentLength = GetIntArg(args, "maxContentLength", 3000);

            if (string.IsNullOrWhiteSpace(url))
                return LocalizationService.Instance["tool.fetchWebpage.missingUrl"];

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return LocalizationService.Instance.Format("tool.fetchWebpage.invalidUrl", url);

            if (maxDepth < 1) maxDepth = 1; else if (maxDepth > 3) maxDepth = 3;
            if (maxContentLength < 500) maxContentLength = 500; else if (maxContentLength > 10000) maxContentLength = 10000;

            if (_webSearchService == null)
                return LocalizationService.Instance["tool.fetchWebpage.serviceNotInit"];

            try
            {
                string safeUrl = WebSearchService.EncodeUrlHostname(url);
                Logger.Info($"[fetch_webpage] 开始抓取: {safeUrl}, 深度={maxDepth}");

                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allContents = new List<string>();
                await FetchRecursiveAsync(safeUrl, maxDepth, maxContentLength, visited, allContents);

                if (allContents.Count == 0)
                    return LocalizationService.Instance.Format("tool.fetchWebpage.noContent", url);

                var sb = new StringBuilder();
                sb.AppendLine($"=== 网页内容: {url} ===");
                sb.AppendLine();
                for (int i = 0; i < allContents.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("--- 相关链接内容 ---");
                        sb.AppendLine();
                    }
                    sb.AppendLine(allContents[i]);
                }
                sb.AppendLine();
                sb.AppendLine("=== 网页内容结束 ===");

                string result = sb.ToString();
                Logger.Info($"[fetch_webpage] 抓取完成: {url}, 共 {allContents.Count} 个页面, {result.Length} 字符");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"[fetch_webpage] 抓取异常 ({url}): {ex.Message}", ex);
                return LocalizationService.Instance.Format("tool.fetchWebpage.failed", ex.Message);
            }
        }

        private async Task FetchRecursiveAsync(
            string url, int remainingDepth, int maxContentLength,
            HashSet<string> visited, List<string> allContents)
        {
            if (remainingDepth <= 0 || visited.Contains(url) || visited.Count >= 10)
                return;

            visited.Add(url);

            string? content = await _webSearchService!.FetchWebPageContentAsync(url);
            if (string.IsNullOrWhiteSpace(content))
                return;

            allContents.Add(content);

            if (remainingDepth > 1)
            {
                var childUrls = WebSearchService.ExtractUrls(content);
                int childCount = 0;
                foreach (string childUrl in childUrls)
                {
                    if (childCount >= 3 || visited.Count >= 10)
                        break;
                    if (!visited.Contains(childUrl))
                    {
                        childCount++;
                        await FetchRecursiveAsync(childUrl, remainingDepth - 1, maxContentLength, visited, allContents);
                    }
                }
            }
        }
    }
}
