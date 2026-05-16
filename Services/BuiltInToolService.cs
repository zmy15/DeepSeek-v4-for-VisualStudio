using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 内置工作区工具服务 — 为 Agent 提供无需外部 MCP 服务器的本地工具。
    /// 
    /// 提供只读探索工具：list_dir, read_file, file_search, grep_search, get_errors, fetch_webpage
    /// 所有工具以 OpenAI function calling 格式定义，与 MCP 工具统一。
    /// </summary>
    public class BuiltInToolService : IBuiltInToolService
    {
        private readonly McpManagerService? _mcpManager;
        private readonly WebSearchService? _webSearchService;
        private readonly IBuildService? _buildService;

        public BuiltInToolService(McpManagerService? mcpManager = null, WebSearchService? webSearchService = null, IBuildService? buildService = null)
        {
            _mcpManager = mcpManager;
            _webSearchService = webSearchService;
            _buildService = buildService;
        }

        #region Tool Definitions (OpenAI Function Calling 格式)

        /// <summary>
        /// 获取所有内置工作区工具的定义列表。
        /// 这些工具名称与各 Agent 的 AllowedTools 白名单一致。
        /// </summary>
        public static List<ToolDefinition> GetBuiltInToolDefinitions()
        {
            return new List<ToolDefinition>
            {
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "list_dir",
                        Description = "列出指定目录的内容。返回子目录和文件名列表。用于探索项目结构。\n" +
                            "重要：如果不知道绝对路径，先使用当前工作区根目录。路径必须是 Windows 绝对路径（如 F:\\project\\src），不要使用 Linux 风格路径（如 /home/user）。",
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new
                                {
                                    type = "string",
                                    description = "要列出的目录的绝对路径（Windows 格式，如 C:\\Users\\... 或 F:\\VSCode\\...）"
                                }
                            },
                            required = new[] { "path" }
                        }
                    }
                },
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "read_file",
                        Description = "读取文件内容。可指定行范围以读取大文件的部分内容。路径必须是 Windows 绝对路径。",
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                filePath = new
                                {
                                    type = "string",
                                    description = "要读取的文件的绝对路径（Windows 格式）"
                                },
                                startLine = new
                                {
                                    type = "integer",
                                    description = "起始行号（1-based），可选，默认为 1"
                                },
                                endLine = new
                                {
                                    type = "integer",
                                    description = "结束行号（1-based，包含），可选，默认读取到文件末尾"
                                }
                            },
                            required = new[] { "filePath" }
                        }
                    }
                },
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "file_search",
                        Description = "使用 glob 模式搜索文件。支持 ** 递归匹配。例如: **/*.cs, src/**/*.ts。在工作区根目录下搜索。",
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
                },
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "grep_search",
                        Description = "在文件中搜索文本或正则表达式。用于查找特定符号、函数名、字符串等。在工作区根目录下搜索。",
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                query = new
                                {
                                    type = "string",
                                    description = "搜索关键词或正则表达式"
                                },
                                isRegexp = new
                                {
                                    type = "boolean",
                                    description = "是否为正则表达式搜索，默认 false"
                                },
                                includePattern = new
                                {
                                    type = "string",
                                    description = "限制搜索的文件 glob 模式，如 **/*.cs，可选"
                                },
                                maxResults = new
                                {
                                    type = "integer",
                                    description = "最大返回结果数，默认 30"
                                }
                            },
                            required = new[] { "query", "isRegexp" }
                        }
                    }
                },
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "get_errors",
                        Description = "获取当前工作区中的编译错误和警告。用于检查代码质量。",
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                filePaths = new
                                {
                                    type = "array",
                                    items = new { type = "string" },
                                    description = "可选，指定要检查的文件路径列表。不指定则获取所有文件的错误。"
                                }
                            },
                            required = new string[] { }
                        }
                    }
                },
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "fetch_webpage",
                        Description = "获取指定 URL 的网页内容并提取纯文本。支持递归抓取页面中引用的相关链接。\n" +
                            "使用场景：用户提供了网页链接需要阅读内容时调用此工具。\n" +
                            "域名会自动进行 Punycode 编码以防止同形异义攻击（IDN Homograph Attack）。\n" +
                            "注意：此工具只能获取 HTTP/HTTPS 网页，且每次调用应只传入一个 URL。",
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                url = new
                                {
                                    type = "string",
                                    description = "要抓取内容的网页 URL（必须是完整的 HTTP 或 HTTPS URL）"
                                },
                                maxDepth = new
                                {
                                    type = "integer",
                                    description = "递归抓取的最大深度（默认为 1，即只抓取当前页面）。设为 2 则会额外抓取页面中的链接，以此类推。最大不超过 3。"
                                },
                                maxContentLength = new
                                {
                                    type = "integer",
                                    description = "返回内容的最大字符数（默认 3000）。超出部分会被截断并标注。"
                                }
                            },
                            required = new[] { "url" }
                        }
                    }
                },
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "build_solution",
                        Description = "构建/编译当前解决方案。用于验证代码修改后是否能成功编译。\n" +
                            "返回构建成功/失败状态及编译错误详情。\n" +
                            "使用场景：修改代码后，调用此工具验证修改是否引入编译错误。\n" +
                            "注意：此工具会触发完整的解决方案构建，可能需要一些时间。",
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                configuration = new
                                {
                                    type = "string",
                                    description = "构建配置（如 Debug 或 Release）。省略则使用当前活动配置。"
                                }
                            },
                            required = new string[] { }
                        }
                    }
                }
            };
        }

        /// <summary>
        /// 根据 Agent 白名单获取过滤后的工具定义。
        /// 合并内置工具和 MCP 外部工具（如果有配置）。
        /// </summary>
        public List<ToolDefinition> GetFilteredToolDefinitions(List<string>? allowedTools)
        {
            var definitions = new List<ToolDefinition>();

            // ── 1. 添加内置工具（按白名单过滤）──
            var builtInDefs = GetBuiltInToolDefinitions();
            if (allowedTools != null && allowedTools.Count > 0)
            {
                var whitelist = new HashSet<string>(allowedTools, StringComparer.OrdinalIgnoreCase);
                builtInDefs = builtInDefs.Where(d => whitelist.Contains(d.Function.Name)).ToList();
            }
            definitions.AddRange(builtInDefs);

            // ── 2. 添加 MCP 外部工具（按白名单过滤）──
            if (_mcpManager != null && _mcpManager.AllTools.Count > 0)
            {
                var mcpDefs = _mcpManager.GetFilteredToolDefinitions(allowedTools);
                definitions.AddRange(mcpDefs);
            }

            if (allowedTools != null)
            {
                Logger.Info($"[BuiltInTool] 工具定义: {definitions.Count} 个 (内置: {builtInDefs.Count}, MCP: {definitions.Count - builtInDefs.Count})");
            }

            return definitions;
        }

        #endregion

        #region Tool Execution

        /// <summary>
        /// 执行内置工具调用。返回工具执行结果文本。如果工具不是内置的，返回 null。
        /// </summary>
        public async Task<string?> ExecuteBuiltInToolAsync(
            string toolName, string argumentsJson, string? workspaceRoot = null)
        {
            try
            {
                // 解析参数 JSON
                var args = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(argumentsJson)
                    ?? new Dictionary<string, System.Text.Json.JsonElement>();

                return toolName switch
                {
                    "list_dir" => await ListDirAsync(args, workspaceRoot),
                    "read_file" => await ReadFileAsync(args, workspaceRoot),
                    "file_search" => await FileSearchAsync(args, workspaceRoot),
                    "grep_search" => await GrepSearchAsync(args, workspaceRoot),
                    "get_errors" => await GetErrorsAsync(args),
                    "fetch_webpage" => await FetchWebpageAsync(args),
                    "build_solution" => await BuildSolutionAsync(args, workspaceRoot),
                    _ => null  // 不是内置工具
                };
            }
            catch (Exception ex)
            {
                return $"❌ 内置工具执行异常 ({toolName}): {ex.Message}";
            }
        }

        /// <summary>
        /// 判断给定工具名是否为内置工具。
        /// </summary>
        public static bool IsBuiltInTool(string toolName)
        {
            return toolName switch
            {
                "list_dir" or "read_file" or "file_search" or "grep_search" or "get_errors" or "fetch_webpage" or "build_solution" => true,
                _ => false
            };
        }

        #endregion

        #region Tool Implementations

        /// <summary>
        /// 规范化工作区根目录：如果是文件路径则取其目录。
        /// </summary>
        private static string? NormalizeWorkspaceRoot(string? workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot))
                return null;

            try
            {
                // 如果是文件路径，取其目录
                if (File.Exists(workspaceRoot))
                    return Path.GetDirectoryName(workspaceRoot);
                if (Directory.Exists(workspaceRoot))
                    return workspaceRoot;
            }
            catch { }

            return workspaceRoot;
        }

        private static Task<string> ListDirAsync(Dictionary<string, System.Text.Json.JsonElement> args, string? workspaceRoot)
        {
            workspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);
            string path = GetStringArg(args, "path");
            
            // ── 空路径或相对路径 → 尝试使用工作区根目录 ──
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
            {
                if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot))
                {
                    path = workspaceRoot;
                }
                else
                {
                    return Task.FromResult("❌ list_dir: 缺少有效的绝对路径参数。请使用 Windows 绝对路径（如 C:\\project\\src）。" 
                        + (string.IsNullOrEmpty(workspaceRoot) ? "" : $" 当前工作区: {workspaceRoot}"));
                }
            }

            if (!Directory.Exists(path))
            {
                // ── 提供有用的错误信息，引导 AI 使用工作区根目录 ──
                string suggestion = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
                    ? $"\n💡 提示: 当前工作区根目录是 \"{workspaceRoot}\"，请使用此路径或其中的子目录。"
                    : "\n💡 提示: 请使用 Windows 绝对路径格式（如 C:\\Users\\...\\project\\src）。";
                return Task.FromResult($"❌ 目录不存在: {path}{suggestion}");
            }

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"📁 目录: {path}");
                sb.AppendLine();

                // 先列出子目录
                var dirs = Directory.GetDirectories(path);
                if (dirs.Length > 0)
                {
                    sb.AppendLine("### 子目录");
                    foreach (var d in dirs.OrderBy(d => d).Take(100))
                    {
                        string name = Path.GetFileName(d);
                        sb.AppendLine($"- 📁 {name}/");
                    }
                    if (dirs.Length > 100)
                        sb.AppendLine($"... 还有 {dirs.Length - 100} 个子目录");
                    sb.AppendLine();
                }

                // 再列出文件
                var files = Directory.GetFiles(path);
                if (files.Length > 0)
                {
                    sb.AppendLine("### 文件");
                    foreach (var f in files.OrderBy(f => f).Take(100))
                    {
                        string name = Path.GetFileName(f);
                        sb.AppendLine($"- 📄 {name}");
                    }
                    if (files.Length > 100)
                        sb.AppendLine($"... 还有 {files.Length - 100} 个文件");
                }

                if (dirs.Length == 0 && files.Length == 0)
                    sb.AppendLine("（空目录）");

                return Task.FromResult(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Task.FromResult($"❌ 列出目录失败: {ex.Message}");
            }
        }

        private static Task<string> ReadFileAsync(Dictionary<string, System.Text.Json.JsonElement> args, string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");
            if (string.IsNullOrEmpty(filePath))
                return Task.FromResult("❌ read_file: 缺少 filePath 参数");

            if (!File.Exists(filePath))
            {
                string wsHint = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
                    ? $"\n💡 当前工作区根目录: `{workspaceRoot}`，请使用此目录下的绝对路径。"
                    : "";
                return Task.FromResult($"❌ 文件不存在: {filePath}{wsHint}");
            }

            try
            {
                int startLine = GetIntArg(args, "startLine", 1);
                int endLine = GetIntArg(args, "endLine", int.MaxValue);

                var lines = File.ReadAllLines(filePath);
                int totalLines = lines.Length;

                startLine = Math.Max(1, Math.Min(startLine, totalLines));
                endLine = Math.Max(startLine, Math.Min(endLine, totalLines));

                var sb = new StringBuilder();
                sb.AppendLine($"📄 文件: {filePath} (共 {totalLines} 行，显示 {startLine}-{endLine})");
                sb.AppendLine();

                for (int i = startLine - 1; i < endLine; i++)
                {
                    sb.AppendLine($"{i + 1}: {lines[i]}");
                }

                string result = sb.ToString().TrimEnd();
                if (result.Length > 50000)
                    result = result.Substring(0, 50000) + "\n\n... (内容已截断)";

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                return Task.FromResult($"❌ 读取文件失败: {ex.Message}");
            }
        }

        private static Task<string> FileSearchAsync(Dictionary<string, System.Text.Json.JsonElement> args, string? workspaceRoot)
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
                // 将 glob 模式转换为 Directory.GetFiles 可用格式
                var results = new List<string>();
                string pattern = query;

                // 处理 ** 递归模式
                bool recursive = pattern.Contains("**");
                string cleanPattern = pattern.Replace("**/", "").Replace("**", "");

                // 如果模式包含目录分隔符，从指定子目录搜索
                string searchDir = searchRoot;
                string filePattern = cleanPattern;

                int lastSlash = cleanPattern.LastIndexOfAny(new[] { '/', '\\' });
                if (lastSlash >= 0)
                {
                    string subDir = cleanPattern.Substring(0, lastSlash);
                    filePattern = cleanPattern.Substring(lastSlash + 1);
                    string candidateDir = Path.Combine(searchRoot, subDir);
                    if (Directory.Exists(candidateDir))
                        searchDir = candidateDir;
                }

                if (string.IsNullOrEmpty(filePattern))
                    filePattern = "*";

                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var files = Directory.GetFiles(searchDir, filePattern, searchOption);

                foreach (var f in files.Take(maxResults))
                {
                    string relativePath = f;
                    if (f.StartsWith(searchRoot, StringComparison.OrdinalIgnoreCase))
                        relativePath = f.Substring(searchRoot.Length).TrimStart('\\', '/');
                    results.Add(relativePath);
                }

                var sb = new StringBuilder();
                sb.AppendLine($"🔍 文件搜索: \"{query}\" (找到 {files.Length} 个文件" + (files.Length > maxResults ? $"，显示前 {maxResults}" : "") + ")");
                sb.AppendLine();
                foreach (var r in results)
                    sb.AppendLine($"- `{r}`");

                return Task.FromResult(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Task.FromResult($"❌ 文件搜索失败: {ex.Message}");
            }
        }

        private static Task<string> GrepSearchAsync(Dictionary<string, System.Text.Json.JsonElement> args, string? workspaceRoot)
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

                // 确定要搜索的文件
                IEnumerable<string> filesToSearch;
                string fileGlob = string.IsNullOrEmpty(includePattern) ? "*.*" : includePattern;

                // 处理 glob 中的 **
                string cleanGlob = fileGlob.Replace("**/", "").Replace("**", "");
                string searchDir = searchRoot;
                if (cleanGlob.Contains('/') || cleanGlob.Contains('\\'))
                {
                    int lastSlash = cleanGlob.LastIndexOfAny(new[] { '/', '\\' });
                    string subDir = cleanGlob.Substring(0, lastSlash);
                    cleanGlob = cleanGlob.Substring(lastSlash + 1);
                    string candidateDir = Path.Combine(searchRoot, subDir);
                    if (Directory.Exists(candidateDir))
                        searchDir = candidateDir;
                }
                if (string.IsNullOrEmpty(cleanGlob))
                    cleanGlob = "*.*";

                try
                {
                    filesToSearch = Directory.GetFiles(searchDir, cleanGlob, searchOption);
                }
                catch
                {
                    filesToSearch = Directory.GetFiles(searchRoot, "*.*", SearchOption.AllDirectories);
                }

                // 排除常见的非代码目录
                var excludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "node_modules", ".git", "bin", "obj", "packages", ".vs", "Debug", "Release",
                    "__pycache__", ".venv", "venv", "dist", "build", ".next", ".nuget"
                };

                Regex? regex = null;
                if (isRegexp)
                {
                    try { regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
                    catch { return Task.FromResult($"❌ 无效的正则表达式: {query}"); }
                }

                foreach (var file in filesToSearch)
                {
                    if (results.Count >= maxResults) break;

                    // 跳过排除目录中的文件
                    string dirName = Path.GetDirectoryName(file) ?? "";
                    if (excludeDirs.Any(d => dirName.IndexOf(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
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
                    catch
                    {
                        // 跳过无法读取的文件
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine($"🔍 文本搜索: \"{query}\" (找到 {results.Count} 处匹配)");
                sb.AppendLine();
                if (results.Count == 0)
                    sb.AppendLine("（未找到匹配结果）");
                else
                {
                    foreach (var r in results)
                        sb.AppendLine($"- `{r}`");
                }

                return Task.FromResult(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Task.FromResult($"❌ 文本搜索失败: {ex.Message}");
            }
        }

        private static Task<string> GetErrorsAsync(Dictionary<string, System.Text.Json.JsonElement> args)
        {
            // 获取编译错误 — 这是一个简化实现，后续可通过 DTE.ErrorList 获取更准确的错误
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("🔧 编译错误检查");
                sb.AppendLine();
                sb.AppendLine("> 注意: 内置 get_errors 为简化实现。完整错误列表请查看 Visual Studio 的\"错误列表\"窗口。");
                sb.AppendLine("> 建议使用 `dotnet build` 命令获取详细编译结果。");
                return Task.FromResult(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Task.FromResult($"❌ 获取错误失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 抓取网页内容工具 — 对应 fetch_webpage。
        /// 
        /// 流程：
        /// 1. 对 URL 域名做 Punycode 编码（防同形异义攻击）
        /// 2. 委托 WebSearchService 抓取网页内容
        /// 3. 如果 maxDepth > 1，从页面内容中提取链接并递归抓取
        /// 4. 返回格式化的纯文本内容
        /// </summary>
        private async Task<string> FetchWebpageAsync(Dictionary<string, System.Text.Json.JsonElement> args)
        {
            string url = GetStringArg(args, "url");
            int maxDepth = GetIntArg(args, "maxDepth", 1);
            int maxContentLength = GetIntArg(args, "maxContentLength", 3000);

            // 参数验证
            if (string.IsNullOrWhiteSpace(url))
                return "❌ fetch_webpage: 缺少必需的 url 参数。";

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return $"❌ fetch_webpage: URL 必须以 http:// 或 https:// 开头。收到: {url}";

            // 限制递归深度
            if (maxDepth < 1) maxDepth = 1; else if (maxDepth > 3) maxDepth = 3;
            if (maxContentLength < 500) maxContentLength = 500; else if (maxContentLength > 10000) maxContentLength = 10000;

            if (_webSearchService == null)
                return "❌ fetch_webpage: WebSearchService 未初始化，无法抓取网页。";

            try
            {
                // ── Punycode 编码域名 ──
                string safeUrl = WebSearchService.EncodeUrlHostname(url);

                Logger.Info($"[fetch_webpage] 开始抓取: {safeUrl}, 深度={maxDepth}");

                // ── 递归抓取 ──
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allContents = new List<string>();
                await FetchRecursiveAsync(safeUrl, maxDepth, maxContentLength, visited, allContents);

                if (allContents.Count == 0)
                    return $"⚠️ fetch_webpage: 无法从 {url} 提取到有效内容。网站可能使用 JavaScript 动态加载，或需要登录。";

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
                    string content = allContents[i];
                    if (content.Length > maxContentLength)
                        content = content.Substring(0, maxContentLength) + $"\n\n... [内容已截断，原文共 {allContents[i].Length} 字符]";
                    sb.AppendLine(content);
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
                return $"❌ fetch_webpage: 抓取失败 - {ex.Message}";
            }
        }

        /// <summary>
        /// 递归抓取网页内容及其引用的链接。
        /// </summary>
        private async Task FetchRecursiveAsync(
            string url,
            int remainingDepth,
            int maxContentLength,
            HashSet<string> visited,
            List<string> allContents)
        {
            if (remainingDepth <= 0 || visited.Contains(url) || visited.Count >= 10)
                return;

            visited.Add(url);

            string? content = await _webSearchService!.FetchWebPageContentAsync(url);
            if (string.IsNullOrWhiteSpace(content))
                return;

            allContents.Add(content);

            // 如果还有剩余深度，从内容中提取链接并递归抓取
            if (remainingDepth > 1)
            {
                var childUrls = WebSearchService.ExtractUrls(content);
                // 只抓取前 3 个相关链接，避免无限扩展
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

        #endregion

        #region Build Tool

        /// <summary>
        /// 执行 build_solution 工具 — 构建/编译当前解决方案。
        /// 委托给 IBuildService 执行实际的 VS SDK 构建交互。
        /// </summary>
        private async Task<string> BuildSolutionAsync(Dictionary<string, System.Text.Json.JsonElement> args, string? workspaceRoot)
        {
            if (_buildService == null)
                return "❌ build_solution: 构建服务未初始化。请在 VS 中打开解决方案后重试。";

            try
            {
                Logger.Info($"[BuiltInTool] build_solution 开始 (workspaceRoot={workspaceRoot ?? "(null)"})");
                string result = await _buildService.BuildAsync(workspaceRoot, CancellationToken.None);
                Logger.Info($"[BuiltInTool] build_solution 完成: {(result.Length > 200 ? result.Substring(0, 200) + "..." : result)}");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"[BuiltInTool] build_solution 异常: {ex.Message}", ex);
                return $"❌ 构建失败: {ex.Message}";
            }
        }

        #endregion

        #region Arg Helpers

        private static string GetStringArg(Dictionary<string, System.Text.Json.JsonElement> args, string key)
        {
            if (args.TryGetValue(key, out var element))
            {
                if (element.ValueKind == System.Text.Json.JsonValueKind.String)
                    return element.GetString() ?? string.Empty;
                if (element.ValueKind == System.Text.Json.JsonValueKind.Null)
                    return string.Empty;
                return element.ToString();
            }
            return string.Empty;
        }

        private static int GetIntArg(Dictionary<string, System.Text.Json.JsonElement> args, string key, int defaultValue)
        {
            if (args.TryGetValue(key, out var element) && element.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                try { return element.GetInt32(); }
                catch { return defaultValue; }
            }
            return defaultValue;
        }

        private static bool GetBoolArg(Dictionary<string, System.Text.Json.JsonElement> args, string key)
        {
            if (args.TryGetValue(key, out var element))
            {
                if (element.ValueKind == System.Text.Json.JsonValueKind.True) return true;
                if (element.ValueKind == System.Text.Json.JsonValueKind.False) return false;
            }
            return false;
        }

        #endregion
    }
}
