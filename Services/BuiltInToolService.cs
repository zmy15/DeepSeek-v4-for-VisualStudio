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

        /// <summary>
        /// i18n 便捷访问器。
        /// </summary>
        private static LocalizationService L => LocalizationService.Instance;

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
                        Description = L["tool.list_dir.desc"],
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
                        Description = L["tool.read_file.desc"],
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
                },
                new ToolDefinition
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
                        Description = L["tool.get_errors.desc"],
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
                        Description = L["tool.fetch_webpage.desc"],
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
                        Description = L["tool.build_solution.desc"],
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
                },
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "replace_string_in_file",
                        Description = L["tool.replace_string_in_file.desc"],
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                filePath = new
                                {
                                    type = "string",
                                    description = "要修改的文件的绝对路径（Windows 格式）"
                                },
                                oldString = new
                                {
                                    type = "string",
                                    description = "要替换的原始文本（必须精确匹配，包括所有空白和缩进）"
                                },
                                newString = new
                                {
                                    type = "string",
                                    description = "替换后的新文本"
                                }
                            },
                            required = new[] { "filePath", "oldString", "newString" }
                        }
                    }
                },
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "multi_replace_string_in_file",
                        Description = L["tool.multi_replace_string_in_file.desc"],
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                replacements = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            filePath = new { type = "string", description = "要修改的文件的绝对路径" },
                                            oldString = new { type = "string", description = "要替换的原始文本" },
                                            newString = new { type = "string", description = "替换后的新文本" }
                                        },
                                        required = new[] { "filePath", "oldString", "newString" }
                                    },
                                    description = "替换操作数组"
                                }
                            },
                            required = new[] { "replacements" }
                        }
                    }
                },
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "create_file",
                        Description = L["tool.create_file.desc"],
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                filePath = new
                                {
                                    type = "string",
                                    description = "要创建/覆盖的文件的绝对路径（Windows 格式）"
                                },
                                content = new
                                {
                                    type = "string",
                                    description = "文件的完整内容"
                                }
                            },
                            required = new[] { "filePath", "content" }
                        }
                    }
                },
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "run_in_terminal",
                        Description = L["tool.run_in_terminal.desc"],
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                command = new
                                {
                                    type = "string",
                                    description = "要执行的 shell 命令"
                                },
                                explanation = new
                                {
                                    type = "string",
                                    description = "命令用途的简短说明"
                                },
                                mode = new
                                {
                                    type = "string",
                                    description = "执行模式：sync（等待完成）或 async（后台运行）。默认 sync。",
                                    @enum = new[] { "sync", "async" }
                                }
                            },
                            required = new[] { "command", "explanation" }
                        }
                    }
                },
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "get_terminal_output",
                        Description = L["tool.get_terminal_output.desc"],
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                id = new
                                {
                                    type = "string",
                                    description = "终端执行 ID（由 run_in_terminal 异步模式返回）"
                                }
                            },
                            required = new[] { "id" }
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
                    "replace_string_in_file" => await ReplaceStringInFileAsync(args, workspaceRoot),
                    "multi_replace_string_in_file" => await MultiReplaceStringInFileAsync(args, workspaceRoot),
                    "create_file" => await CreateFileAsync(args, workspaceRoot),
                    "run_in_terminal" => await RunInTerminalAsync(args),
                    "get_terminal_output" => await GetTerminalOutputAsync(args),
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
                "list_dir" or "read_file" or "file_search" or "grep_search" or "get_errors"
                    or "fetch_webpage" or "build_solution"
                    or "replace_string_in_file" or "multi_replace_string_in_file" or "create_file"
                    or "run_in_terminal" or "get_terminal_output" => true,
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
            // ── 委托给 BuildService.CollectBuildErrors() 获取真实编译错误 ──
            try
            {
                string errors = BuildService.CollectBuildErrors();

                if (!string.IsNullOrWhiteSpace(errors))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("🔧 编译错误检查");
                    sb.AppendLine();
                    sb.AppendLine(errors);
                    return Task.FromResult(sb.ToString().TrimEnd());
                }

                // ── 无编译错误时的回退信息 ──
                return Task.FromResult("🔧 编译错误检查: 未检测到编译错误。如果刚完成构建且预期有错误，请先调用 build_solution 触发编译。");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BuiltInTool] get_errors 异常: {ex.Message}");
                return Task.FromResult($"❌ 获取编译错误失败: {ex.Message}\n💡 建议使用 build_solution 工具触发编译并获取错误详情。");
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

        #region Edit Tools

        /// <summary>
        /// 执行 replace_string_in_file 工具 — 在文件中精确替换字符串。
        /// </summary>
        private static async Task<string> ReplaceStringInFileAsync(Dictionary<string, System.Text.Json.JsonElement> args, string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");
            string oldString = GetStringArg(args, "oldString");
            string newString = GetStringArg(args, "newString");

            if (string.IsNullOrEmpty(filePath))
                return "❌ replace_string_in_file: 缺少 filePath 参数";
            if (string.IsNullOrEmpty(oldString))
                return "❌ replace_string_in_file: 缺少 oldString 参数";

            filePath = ResolvePath(filePath, workspaceRoot);

            if (!File.Exists(filePath))
                return $"❌ replace_string_in_file: 文件不存在: {filePath}";

            try
            {
                string content = await Task.Run(() => File.ReadAllText(filePath, System.Text.Encoding.UTF8));
                string normalizedContent = content.Replace("\r\n", "\n").Replace("\r", "\n");
                string normalizedOld = oldString.Replace("\r\n", "\n").Replace("\r", "\n");
                string normalizedNew = newString.Replace("\r\n", "\n").Replace("\r", "\n");

                int index = normalizedContent.IndexOf(normalizedOld, StringComparison.Ordinal);
                if (index < 0)
                    return $"❌ replace_string_in_file: 未在文件中找到要替换的文本。请用 read_file 确认文件当前内容。文件: {Path.GetFileName(filePath)}";

                // 检查是否有多处匹配（oldString 不唯一）
                int secondIndex = normalizedContent.IndexOf(normalizedOld, index + 1, StringComparison.Ordinal);
                if (secondIndex >= 0)
                    return $"❌ replace_string_in_file: oldString 在文件中匹配了多处（至少位置 {index} 和 {secondIndex}）。请使用包含更多上下文的更精确字符串，或使用 multi_replace_string_in_file。";

                string newContent = normalizedContent.Substring(0, index)
                    + normalizedNew
                    + normalizedContent.Substring(index + normalizedOld.Length);

                // 恢复 CRLF 行尾
                newContent = newContent.Replace("\n", "\r\n");

                await Task.Run(() => File.WriteAllText(filePath, newContent, System.Text.Encoding.UTF8));
                return $"✅ 已替换: {Path.GetFileName(filePath)}";
            }
            catch (Exception ex)
            {
                return $"❌ replace_string_in_file 失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 执行 multi_replace_string_in_file 工具 — 批量字符串替换。
        /// </summary>
        private static async Task<string> MultiReplaceStringInFileAsync(Dictionary<string, System.Text.Json.JsonElement> args, string? workspaceRoot)
        {
            if (!args.TryGetValue("replacements", out var element) ||
                element.ValueKind != System.Text.Json.JsonValueKind.Array)
                return "❌ multi_replace_string_in_file: 缺少 replacements 数组参数";

            var results = new List<string>();
            int successCount = 0;
            int failCount = 0;

            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;

                var singleArgs = new Dictionary<string, System.Text.Json.JsonElement>();
                foreach (var prop in item.EnumerateObject())
                    singleArgs[prop.Name] = prop.Value;

                string filePath = GetStringArg(singleArgs, "filePath");
                string oldStr = GetStringArg(singleArgs, "oldString");
                string newStr = GetStringArg(singleArgs, "newString");

                if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(oldStr))
                {
                    failCount++;
                    continue;
                }

                string result = await ReplaceStringInFileAsync(singleArgs, workspaceRoot);
                results.Add($"{Path.GetFileName(filePath)}: {result}");
                if (result.StartsWith("✅")) successCount++;
                else failCount++;
            }

            string summary = $"🔧 multi_replace_string_in_file: 成功 {successCount}, 失败 {failCount}";
            return summary + "\n" + string.Join("\n", results);
        }

        /// <summary>
        /// 执行 create_file 工具 — 创建或覆盖文件。
        /// </summary>
        private static async Task<string> CreateFileAsync(Dictionary<string, System.Text.Json.JsonElement> args, string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");
            string content = GetStringArg(args, "content");

            if (string.IsNullOrEmpty(filePath))
                return "❌ create_file: 缺少 filePath 参数";

            filePath = ResolvePath(filePath, workspaceRoot);

            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string normalizedContent = (content ?? string.Empty)
                    .Replace("\r\n", "\n").Replace("\r", "\n")
                    .Replace("\n", "\r\n"); // 统一为 CRLF

                bool existed = File.Exists(filePath);
                await Task.Run(() => File.WriteAllText(filePath, normalizedContent, System.Text.Encoding.UTF8));

                return existed
                    ? $"✅ 已覆盖文件: {Path.GetFileName(filePath)}"
                    : $"✅ 已创建文件: {Path.GetFileName(filePath)}";
            }
            catch (Exception ex)
            {
                return $"❌ create_file 失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 执行 run_in_terminal 工具 — 在终端中运行命令。
        /// 由于在 VS 扩展环境中，此工具通过 System.Diagnostics.Process 启动命令。
        /// </summary>
        private static async Task<string> RunInTerminalAsync(Dictionary<string, System.Text.Json.JsonElement> args)
        {
            string command = GetStringArg(args, "command");
            string mode = GetStringArg(args, "mode");

            if (string.IsNullOrEmpty(command))
                return "❌ run_in_terminal: 缺少 command 参数";

            bool isAsync = string.Equals(mode, "async", StringComparison.OrdinalIgnoreCase);

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null)
                    return "❌ run_in_terminal: 无法启动进程";

                if (isAsync)
                {
                    // 异步模式：启动后立即返回
                    string pid = process.Id.ToString();
                    _ = Task.Run(() => { process.WaitForExit(); });
                    return $"🚀 终端命令已启动 (PID: {pid}, 模式: async)\n命令: {command}";
                }
                else
                {
                    // 同步模式：等待完成
                    string stdout = await process.StandardOutput.ReadToEndAsync();
                    string stderr = await process.StandardError.ReadToEndAsync();
                    await Task.Run(() => process.WaitForExit());

                    var sb = new StringBuilder();
                    sb.AppendLine($"📟 终端输出 (退出码: {process.ExitCode}):");
                    if (!string.IsNullOrWhiteSpace(stdout))
                    {
                        sb.AppendLine(stdout.Length > 50000 ? stdout.Substring(0, 50000) + "\n... (已截断)" : stdout);
                    }
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        sb.AppendLine("--- STDERR ---");
                        sb.AppendLine(stderr.Length > 10000 ? stderr.Substring(0, 10000) + "\n... (已截断)" : stderr);
                    }
                    return sb.ToString().TrimEnd();
                }
            }
            catch (Exception ex)
            {
                return $"❌ run_in_terminal 失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 执行 get_terminal_output 工具 — 获取终端输出。
        /// 由于终端生命周期管理的限制，此工具返回简要说明。
        /// </summary>
        private static Task<string> GetTerminalOutputAsync(Dictionary<string, System.Text.Json.JsonElement> args)
        {
            string id = GetStringArg(args, "id");
            if (string.IsNullOrEmpty(id))
                return Task.FromResult("❌ get_terminal_output: 缺少 id 参数");

            // 简化实现：异步终端输出可通过 VS 输出窗口查看
            return Task.FromResult(
                $"📟 终端 ID: {id}\n" +
                "💡 提示：异步终端命令的输出请直接查看 VS 输出窗口或终端面板。\n" +
                "如果命令仍在运行中，请稍后重试。");
        }

        /// <summary>
        /// 解析文件路径（支持相对于工作区根目录的路径）。
        /// </summary>
        private static string ResolvePath(string filePath, string? workspaceRoot)
        {
            if (string.IsNullOrEmpty(filePath))
                return filePath;

            if (Path.IsPathRooted(filePath))
                return filePath;

            if (!string.IsNullOrEmpty(workspaceRoot))
                return Path.Combine(workspaceRoot, filePath);

            return Path.Combine(Directory.GetCurrentDirectory(), filePath);
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
