using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// file_search 工具 — Glob 模式文件名搜索。
    /// </summary>
    public class FileSearchTool : BuiltInToolBase
    {
        public override string Name => "file_search";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "file_search",
                    Description = L["tool.file_search.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new
                            {
                                type = "string",
                                description = "Glob 搜索模式，如 **/*.cs 或 src/**/*.ts"
                            },
                            maxResults = new
                            {
                                type = "integer",
                                description = "最大返回结果数，默认 50"
                            }
                        },
                        required = new[] { "query" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string fsQuery = GetStringArg(args, "query");
            return string.IsNullOrEmpty(fsQuery)
                ? "🔍 搜索文件"
                : $"🔍 搜索文件 `{TruncateText(fsQuery, 60)}`";
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "（无返回结果）";
            if (toolResult.StartsWith("❌")) return toolResult;

            var fsFirstLine = toolResult.Split('\n')[0];
            var fsMatch = System.Text.RegularExpressions.Regex.Match(
                fsFirstLine, @"(?:找到|Found|found)\s*(\d+|>\d+)\s*个?\s*(?:文件|files?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (fsMatch.Success)
                return $"✅ {fsMatch.Value.Trim()}";
            var fsLines = toolResult.Split('\n');
            int fsCount = fsLines.Count(l => l.TrimStart().StartsWith("- `"));
            return $"✅ 找到 {fsCount} 个文件";
        }

        public override Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            workspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);
            string query = GetStringArg(args, "query");
            if (string.IsNullOrEmpty(query))
                return Task.FromResult("❌ file_search: 缺少 query 参数");

            int maxResults = GetIntArg(args, "maxResults", 50);
            string searchRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(searchRoot))
                return Task.FromResult($"❌ 工作区目录不存在: {searchRoot}");

            try
            {
                var results = new List<string>();
                string pattern = query;
                bool recursive = pattern.Contains("**");
                string cleanPattern = pattern.Replace("**/", "").Replace("**", "");

                string searchDir = searchRoot;
                string filePattern = cleanPattern;

                int lastSlash = cleanPattern.LastIndexOfAny(new[] { '/', '\\' });
                if (lastSlash >= 0)
                {
                    string subDir = cleanPattern.Substring(0, lastSlash);
                    filePattern = cleanPattern.Substring(lastSlash + 1);
                    string candidateDir = Path.Combine(searchRoot, subDir);
                    // ── 防止 Path.Combine 因绝对路径越权：如 F:\outside 会直接返回自身 ──
                    string resolvedDir = Path.GetFullPath(candidateDir);
                    string resolvedRoot = Path.GetFullPath(searchRoot);
                    if (resolvedDir.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || resolvedDir.Equals(resolvedRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        if (Directory.Exists(resolvedDir))
                            searchDir = resolvedDir;
                    }
                    // 否则保持 searchRoot，不越权
                }

                if (string.IsNullOrEmpty(filePattern))
                    filePattern = "*";

                const int maxFilesToEnumerate = 10000;
                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

                int totalFound = 0;
                foreach (var f in Directory.EnumerateFiles(searchDir, filePattern, searchOption))
                {
                    totalFound++;
                    if (totalFound > maxFilesToEnumerate) break;
                    if (results.Count >= maxResults) break;

                    string relativePath = f;
                    if (f.StartsWith(searchRoot, StringComparison.OrdinalIgnoreCase))
                        relativePath = f.Substring(searchRoot.Length).TrimStart('\\', '/');
                    results.Add(relativePath);
                }

                var sb = new StringBuilder();
                string countInfo = totalFound > maxFilesToEnumerate
                    ? $"找到 >{maxFilesToEnumerate} 个文件（已截断）"
                    : $"找到 {totalFound} 个文件";
                sb.AppendLine($"🔍 文件搜索: \"{query}\" ({countInfo}" + (results.Count < totalFound ? $"，显示前 {results.Count}" : "") + ")");
                sb.AppendLine();
                foreach (var r in results)
                    sb.AppendLine($"- `{r}`");

                if (totalFound > maxFilesToEnumerate)
                    sb.AppendLine($"> ⚠️ 匹配文件过多（>{maxFilesToEnumerate}），仅枚举了前 {maxFilesToEnumerate} 个。请使用更精确的搜索模式。");

                return Task.FromResult(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Task.FromResult($"❌ 文件搜索失败: {ex.Message}");
            }
        }
    }
}
