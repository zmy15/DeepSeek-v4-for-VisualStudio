using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// grep_search 工具 — 正则/文本内容全局搜索。
    /// </summary>
    public class GrepSearchTool : BuiltInToolBase
    {
        private static readonly HashSet<string> ExcludeDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", "bin", "obj", "packages", ".vs", "Debug", "Release",
            "__pycache__", ".venv", "venv", "dist", "build", ".next", ".nuget"
        };

        public override string Name => "grep_search";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "grep_search",
                    Description = L["tool.grep_search.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "搜索关键词或正则表达式" },
                            isRegexp = new { type = "boolean", description = "是否为正则表达式搜索，默认 false" },
                            includePattern = new { type = "string", description = "限制搜索的文件 glob 模式，如 **/*.cs，可选" },
                            maxResults = new { type = "integer", description = "最大返回结果数，默认 30" }
                        },
                        required = new[] { "query", "isRegexp" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string grepQuery = GetStringArg(args, "query");
            string incPattern = GetStringArg(args, "includePattern");
            string grepDesc = string.IsNullOrEmpty(grepQuery)
                ? "🔎 搜索文本"
                : $"🔎 搜索文本 `{TruncateText(grepQuery, 40)}`";
            if (!string.IsNullOrEmpty(incPattern))
                grepDesc += $" 在 `{TruncateText(incPattern, 40)}` 中";
            return grepDesc;
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "（无返回结果）";
            if (toolResult.StartsWith("❌")) return toolResult;

            var gsFirstLine = toolResult.Split('\n')[0];
            var gsMatch = Regex.Match(
                gsFirstLine, @"(?:找到|Found|found)\s*(\d+|>\d+)\s*(?:处|个)?\s*(?:匹配|matches?)",
                RegexOptions.IgnoreCase);
            if (gsMatch.Success)
                return $"✅ {gsMatch.Value.Trim()}";
            return "✅ 搜索完成";
        }

        public override Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            workspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);
            string query = GetStringArg(args, "query");
            if (string.IsNullOrEmpty(query))
                return Task.FromResult("❌ grep_search: 缺少 query 参数");

            bool isRegexp = GetBoolArg(args, "isRegexp");
            string? includePattern = GetStringArg(args, "includePattern");
            int maxResults = GetIntArg(args, "maxResults", 30);

            string searchRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(searchRoot))
                return Task.FromResult($"❌ 工作区目录不存在: {searchRoot}");

            try
            {
                var results = new List<string>();
                var searchOption = SearchOption.AllDirectories;

                string fileGlob = string.IsNullOrEmpty(includePattern) ? "*.*" : includePattern;
                string cleanGlob = fileGlob.Replace("**/", "").Replace("**", "");
                string searchDir = searchRoot;

                if (cleanGlob.Contains('/') || cleanGlob.Contains('\\'))
                {
                    int lastSlash = cleanGlob.LastIndexOfAny(new[] { '/', '\\' });
                    string subDir = cleanGlob.Substring(0, lastSlash);
                    cleanGlob = cleanGlob.Substring(lastSlash + 1);
                    string candidateDir = Path.Combine(searchRoot, subDir);
                    // ── 防止 Path.Combine 因绝对路径越权 ──
                    string resolvedDir = Path.GetFullPath(candidateDir);
                    string resolvedRoot = Path.GetFullPath(searchRoot);
                    if ((resolvedDir.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                         || resolvedDir.Equals(resolvedRoot, StringComparison.OrdinalIgnoreCase))
                        && Directory.Exists(resolvedDir))
                    {
                        searchDir = resolvedDir;
                    }
                }
                if (string.IsNullOrEmpty(cleanGlob))
                    cleanGlob = "*.*";

                const int maxFilesToSearch = 5000;
                IEnumerable<string> filesToSearch;
                try
                {
                    filesToSearch = Directory.EnumerateFiles(searchDir, cleanGlob, searchOption)
                        .Take(maxFilesToSearch);
                }
                catch
                {
                    filesToSearch = Directory.EnumerateFiles(searchRoot, "*.*", SearchOption.AllDirectories)
                        .Take(maxFilesToSearch);
                }

                Regex? regex = null;
                if (isRegexp)
                {
                    try { regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)); }
                    catch { return Task.FromResult($"❌ 无效的正则表达式: {query}"); }
                }

                foreach (var file in filesToSearch)
                {
                    if (results.Count >= maxResults) break;

                    string dirName = Path.GetDirectoryName(file) ?? "";
                    if (ExcludeDirs.Any(d =>
                        dirName.IndexOf(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
                        || dirName.EndsWith(Path.DirectorySeparatorChar + d, StringComparison.OrdinalIgnoreCase)
                        || dirName.Equals(d, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    try
                    {
                        var lines = File.ReadAllLines(file);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (results.Count >= maxResults) break;

                            bool match;
                            if (isRegexp && regex != null)
                                match = regex.IsMatch(lines[i]);
                            else
                                match = lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

                            if (match)
                            {
                                string relativePath = file;
                                if (file.StartsWith(searchRoot, StringComparison.OrdinalIgnoreCase))
                                    relativePath = file.Substring(searchRoot.Length).TrimStart('\\', '/');
                                results.Add($"{relativePath}:{i + 1}: {lines[i].Trim().Truncate(200)}");
                            }
                        }
                    }
                    catch { /* 跳过无法读取的文件 */ }
                }

                var sb = new StringBuilder();
                sb.AppendLine($"🔍 文本搜索: \"{query}\" (找到 {results.Count} 处匹配)");
                sb.AppendLine();
                if (results.Count == 0)
                    sb.AppendLine("（未找到匹配结果）");
                else
                    foreach (var r in results)
                        sb.AppendLine($"- `{r}`");

                return Task.FromResult(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Task.FromResult($"❌ 文本搜索失败: {ex.Message}");
            }
        }
    }
}
