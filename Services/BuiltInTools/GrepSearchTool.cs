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
                            query = new { type = "string", description = LocalizationService.Instance["tool.grepSearch.param.query"] },
                            isRegexp = new { type = "boolean", description = LocalizationService.Instance["tool.grepSearch.param.isRegexp"] },
                            includePattern = new { type = "string", description = LocalizationService.Instance["tool.grepSearch.param.includePattern"] },
                            maxResults = new { type = "integer", description = LocalizationService.Instance["tool.grepSearch.param.maxResults"] }
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
                ? LocalizationService.Instance["tool.grepSearch.searching"]
                : LocalizationService.Instance.Format("tool.grepSearch.searchingQuery", TruncateText(grepQuery, 40));
            if (!string.IsNullOrEmpty(incPattern))
                grepDesc += $" 在 `{TruncateText(incPattern, 40)}` 中";
            return grepDesc;
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("❌")) return toolResult;

            var gsFirstLine = toolResult.Split('\n')[0];
            var gsMatch = Regex.Match(
                gsFirstLine, @"(?:找到|Found|found)\s*(\d+|>\d+)\s*(?:处|个)?\s*(?:匹配|matches?)",
                RegexOptions.IgnoreCase);
            if (gsMatch.Success)
                return $"✅ {gsMatch.Value.Trim()}";
            return LocalizationService.Instance["tool.grepSearch.complete"];
        }

        public override Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            workspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);
            string query = GetStringArg(args, "query");
            if (string.IsNullOrEmpty(query))
                return Task.FromResult(LocalizationService.Instance["tool.grepSearch.missingQuery"]);

            bool isRegexp = GetBoolArg(args, "isRegexp");
            string? includePattern = GetStringArg(args, "includePattern");
            int maxResults = GetIntArg(args, "maxResults", 30);

            string searchRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(searchRoot))
                return Task.FromResult(LocalizationService.Instance.Format("tool.grepSearch.workspaceNotExist", searchRoot));

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
                    catch { return Task.FromResult(LocalizationService.Instance.Format("tool.grepSearch.invalidRegex", query)); }
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
                sb.AppendLine(LocalizationService.Instance.Format("tool.grepSearch.resultHeader", query, results.Count));
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
                return Task.FromResult(LocalizationService.Instance.Format("tool.grepSearch.failed", ex.Message));
            }
        }
    }
}
