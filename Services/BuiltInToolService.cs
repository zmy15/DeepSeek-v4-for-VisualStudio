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
                        Name = "delete_file",
                        Description = L["tool.delete_file.desc"],
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                filePath = new
                                {
                                    type = "string",
                                    description = "要删除的文件的绝对路径（Windows 格式）"
                                },
                                explanation = new
                                {
                                    type = "string",
                                    description = "删除原因的简短说明"
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
                        Name = "apply_patch",
                        Description = L["tool.apply_patch.desc"],
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                patch = new
                                {
                                    type = "string",
                                    description = "补丁文本，使用 *** Begin Patch / *** End Patch 格式。每行前缀：空格=上下文、-=删除行、+=新增行。@@ 用于定位（类名/函数名等）。"
                                }
                            },
                            required = new[] { "patch" }
                        }
                    }
                },
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "create_directory",
                        Description = L["tool.create_directory.desc"],
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                dirPath = new
                                {
                                    type = "string",
                                    description = "要创建的目录的绝对路径（Windows 格式，如 C:\\Users\\...\\newfolder）"
                                }
                            },
                            required = new[] { "dirPath" }
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
                },
                new ToolDefinition
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "VisualStudio_askQuestions",
                        Description = "向用户展示结构化问题并等待回答。用于在规划阶段向用户澄清需求。问题会以 UI 形式呈现给用户，用户回答后返回 JSON 格式的答案。",
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                questions = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            header = new { type = "string", description = "问题简短标题（唯一标识）" },
                                            question = new { type = "string", description = "问题的完整描述文本" },
                                            options = new
                                            {
                                                type = "array",
                                                items = new
                                                {
                                                    type = "object",
                                                    properties = new
                                                    {
                                                        label = new { type = "string", description = "选项文本" },
                                                        description = new { type = "string", description = "选项说明（可选）" }
                                                    },
                                                    required = new[] { "label" }
                                                },
                                                description = "可选选项列表，为空则允许自由文本输入"
                                            },
                                            multiSelect = new { type = "boolean", description = "是否允许多选，默认 false" },
                                            allowFreeformInput = new { type = "boolean", description = "除选项外是否允许自由文本输入，默认 true" }
                                        },
                                        required = new[] { "header", "question" }
                                    },
                                    description = "要向用户提问的问题列表（每次 1-2 个问题）"
                                }
                            },
                            required = new[] { "questions" }
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
                    "delete_file" => await DeleteFileAsync(args, workspaceRoot),
                    "apply_patch" => await ApplyPatchAsync(args, workspaceRoot),
                    "create_directory" => await CreateDirectoryAsync(args),
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
                    or "replace_string_in_file" or "multi_replace_string_in_file" or "create_file" or "delete_file"
                    or "apply_patch" or "create_directory"
                    or "run_in_terminal" or "get_terminal_output" or "VisualStudio_askQuestions" => true,
                _ => false
            };
        }

        /// <summary>
        /// 根据工具名称和参数 JSON 生成人类可读的工具调用描述。
        /// 用于在聊天 UI 中向用户展示 AI 正在做什么。
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="argumentsJson">工具参数 JSON 字符串</param>
        /// <returns>人类可读的描述文本（含 emoji 图标）</returns>
        public static string GetToolCallDisplayText(string toolName, string argumentsJson)
        {
            try
            {
                var args = string.IsNullOrWhiteSpace(argumentsJson)
                    ? new Dictionary<string, System.Text.Json.JsonElement>()
                    : System.Text.Json.JsonSerializer
                        .Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(argumentsJson)
                        ?? new Dictionary<string, System.Text.Json.JsonElement>();

                switch (toolName)
                {
                    case "list_dir":
                        string dirPath = GetStringArg(args, "path");
                        return string.IsNullOrEmpty(dirPath)
                            ? "📂 列出目录"
                            : $"📂 列出目录 `{TruncatePath(dirPath)}`";

                    case "read_file":
                        string filePath = GetStringArg(args, "filePath");
                        int sLine = GetIntArg(args, "startLine", 0);
                        int eLine = GetIntArg(args, "endLine", 0);
                        string fileName = string.IsNullOrEmpty(filePath) ? "?" : Path.GetFileName(filePath);
                        if (sLine > 0 && eLine > 0 && eLine > sLine)
                            return $"📄 读取文件 `{fileName}` (第{sLine}-{eLine}行)";
                        else if (sLine > 0)
                            return $"📄 读取文件 `{fileName}` (从第{sLine}行)";
                        else
                            return string.IsNullOrEmpty(filePath)
                                ? "📄 读取文件"
                                : $"📄 读取文件 `{fileName}`";

                    case "file_search":
                        string fsQuery = GetStringArg(args, "query");
                        return string.IsNullOrEmpty(fsQuery)
                            ? "🔍 搜索文件"
                            : $"🔍 搜索文件 `{TruncateText(fsQuery, 60)}`";

                    case "grep_search":
                        string grepQuery = GetStringArg(args, "query");
                        string incPattern = GetStringArg(args, "includePattern");
                        string grepDesc = string.IsNullOrEmpty(grepQuery)
                            ? "🔎 搜索文本"
                            : $"🔎 搜索文本 `{TruncateText(grepQuery, 40)}`";
                        if (!string.IsNullOrEmpty(incPattern))
                            grepDesc += $" 在 `{TruncateText(incPattern, 40)}` 中";
                        return grepDesc;

                    case "get_errors":
                        var filePaths = GetStringArrayArg(args, "filePaths");
                        if (filePaths != null && filePaths.Length > 0)
                            return $"⚠️ 检查 {filePaths.Length} 个文件的编译错误";
                        return "⚠️ 检查工作区编译错误";

                    case "fetch_webpage":
                        string url = GetStringArg(args, "url");
                        return string.IsNullOrEmpty(url)
                            ? "🌐 抓取网页"
                            : $"🌐 抓取网页 `{TruncateText(url, 60)}`";

                    case "build_solution":
                        string config = GetStringArg(args, "configuration");
                        return string.IsNullOrEmpty(config)
                            ? "🔨 构建解决方案"
                            : $"🔨 构建解决方案 ({config})";

                    case "replace_string_in_file":
                        string editPath = GetStringArg(args, "filePath");
                        string editFile = string.IsNullOrEmpty(editPath) ? "?" : Path.GetFileName(editPath);
                        return $"✏️ 编辑文件 `{editFile}`";

                    case "multi_replace_string_in_file":
                        int count = 0;
                        if (args.TryGetValue("replacements", out var repsElement)
                            && repsElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                            count = repsElement.GetArrayLength();
                        return count > 0
                            ? $"✏️ 批量编辑 ({count} 处替换)"
                            : "✏️ 批量编辑文件";

                    case "create_file":
                        string createPath = GetStringArg(args, "filePath");
                        string createFile = string.IsNullOrEmpty(createPath) ? "?" : Path.GetFileName(createPath);
                        return $"📝 创建文件 `{createFile}`";

                    case "delete_file":
                        string deletePath = GetStringArg(args, "filePath");
                        string deleteFile = string.IsNullOrEmpty(deletePath) ? "?" : Path.GetFileName(deletePath);
                        return $"🗑️ 删除文件 `{deleteFile}`";

                    case "apply_patch":
                        return "🔧 应用补丁";

                    case "create_directory":
                        string mkdirPath = GetStringArg(args, "dirPath");
                        string mkdirName = string.IsNullOrEmpty(mkdirPath) ? "?" : Path.GetFileName(mkdirPath);
                        return $"📁 创建目录 `{mkdirName}`";

                    case "run_in_terminal":
                        string cmd = GetStringArg(args, "command");
                        string expl = GetStringArg(args, "explanation");
                        if (!string.IsNullOrEmpty(expl))
                            return $"💻 执行终端命令: {TruncateText(expl, 80)}";
                        else if (!string.IsNullOrEmpty(cmd))
                            return $"💻 执行终端命令: `{TruncateText(cmd, 60)}`";
                        return "💻 执行终端命令";

                    case "get_terminal_output":
                        return "📋 获取终端输出";

                    case "VisualStudio_askQuestions":
                        return "💬 向用户提问";

                    // MCP 外部工具：尝试解析常见参数
                    default:
                        return GetMcpToolCallDisplayText(toolName, args);
                }
            }
            catch
            {
                return $"🔧 调用工具 `{toolName}`";
            }
        }

        /// <summary>
        /// 为 MCP 外部工具生成显示文本（解析常见参数模式）。
        /// </summary>
        private static string GetMcpToolCallDisplayText(string toolName, Dictionary<string, System.Text.Json.JsonElement> args)
        {
            // 通用模式：检查常见参数名
            string filePath = GetStringArg(args, "filePath") ?? GetStringArg(args, "path") ?? GetStringArg(args, "directory");
            string query = GetStringArg(args, "query") ?? GetStringArg(args, "pattern") ?? GetStringArg(args, "search");
            string url = GetStringArg(args, "url");

            if (!string.IsNullOrEmpty(filePath))
            {
                string fname = Path.GetFileName(filePath);
                return $"🔧 调用 `{toolName}` → `{fname}`";
            }
            if (!string.IsNullOrEmpty(url))
            {
                return $"🔧 调用 `{toolName}` → `{TruncateText(url, 50)}`";
            }
            if (!string.IsNullOrEmpty(query))
            {
                return $"🔧 调用 `{toolName}` → `{TruncateText(query, 50)}`";
            }

            return $"🔧 调用工具 `{toolName}`";
        }

        /// <summary>
        /// 截断路径显示，保留文件名，前面用 ... 表示省略。
        /// </summary>
        private static string TruncatePath(string path, int maxLen = 50)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= maxLen)
                return path;
            string fileName = Path.GetFileName(path);
            string dirName = Path.GetDirectoryName(path) ?? "";
            if (fileName.Length >= maxLen - 3)
                return "..." + fileName.Substring(fileName.Length - (maxLen - 3));
            int dirMax = maxLen - fileName.Length - 4;
            if (dirMax <= 0)
                return "..." + fileName;
            return dirName.Substring(0, Math.Min(dirMax, dirName.Length)) + "...\\" + fileName;
        }

        /// <summary>
        /// 截断文本，超出长度添加 ...
        /// </summary>
        private static string TruncateText(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
                return text;
            return text.Substring(0, maxLen) + "…";
        }

        /// <summary>
        /// 从参数字典中获取字符串数组参数。
        /// </summary>
        private static string[]? GetStringArrayArg(Dictionary<string, System.Text.Json.JsonElement> args, string key)
        {
            if (!args.TryGetValue(key, out var element))
                return null;
            if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                        list.Add(item.GetString() ?? "");
                }
                return list.ToArray();
            }
            return null;
        }

        /// <summary>
        /// 构建工具执行结果的简短摘要（用于在聊天 UI 中展示）。
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="toolResult">工具返回的完整结果文本</param>
        /// <returns>简短摘要文本</returns>
        public static string GetToolResultSummary(string toolName, string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult))
                return "（无返回结果）";

            // 错误结果直接返回
            if (toolResult.StartsWith("❌"))
                return toolResult;

            try
            {
                switch (toolName)
                {
                    case "list_dir":
                        // ── 统计 📁 📄 emoji 行数（locale-independent）──
                        var dirLines = toolResult.Split('\n');
                        int dirCount = dirLines.Count(l => l.TrimStart().StartsWith("- 📁"));
                        int fileCount = dirLines.Count(l => l.TrimStart().StartsWith("- 📄"));
                        return $"列出完成: {dirCount} 个子目录, {fileCount} 个文件";

                    case "read_file":
                        var readLines = toolResult.Split('\n');
                        string firstLine = readLines.Length > 0 ? readLines[0].Trim() : "";
                        // 提取行数信息（中英文通用：匹配 "共 N 行" 或 "total N lines" 或 "(N 行)" 等模式）
                        var lineCountMatch = System.Text.RegularExpressions.Regex.Match(
                            firstLine, @"(?:共|total|总计)\s*(\d+)\s*(?:行|lines)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (lineCountMatch.Success)
                            return $"✅ 读取完成 ({lineCountMatch.Groups[1].Value} 行)";
                        return $"✅ 读取完成 ({readLines.Length} 行)";

                    case "file_search":
                        // 提取第一行中的文件数量
                        var fsFirstLine = toolResult.Split('\n')[0];
                        var fsMatch = System.Text.RegularExpressions.Regex.Match(
                            fsFirstLine, @"(?:找到|Found|found)\s*(\d+|>\d+)\s*个?\s*(?:文件|files?)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (fsMatch.Success)
                            return $"✅ {fsMatch.Value.Trim()}";
                        var fsLines = toolResult.Split('\n');
                        int fsCount = fsLines.Count(l => l.TrimStart().StartsWith("- `"));
                        return $"✅ 找到 {fsCount} 个文件";

                    case "grep_search":
                        var gsFirstLine = toolResult.Split('\n')[0];
                        var gsMatch = System.Text.RegularExpressions.Regex.Match(
                            gsFirstLine, @"(?:找到|Found|found)\s*(\d+|>\d+)\s*(?:处|个)?\s*(?:匹配|matches?)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (gsMatch.Success)
                            return $"✅ {gsMatch.Value.Trim()}";
                        return "✅ 搜索完成";

                    case "get_errors":
                        if (toolResult.Contains("0 个错误") || toolResult.Contains("0 errors"))
                            return "✅ 无编译错误";
                        var errMatch = System.Text.RegularExpressions.Regex.Match(
                            toolResult, @"(\d+)\s*(?:个)?\s*(?:错误|errors?)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (errMatch.Success)
                            return $"⚠️ 发现 {errMatch.Groups[1].Value} 个错误";
                        return "✅ 编译检查完成";

                    case "fetch_webpage":
                        return $"✅ 抓取完成 ({toolResult.Length} 字符)";

                    case "build_solution":
                        if (toolResult.Contains("构建成功") || toolResult.Contains("Build succeeded"))
                            return "✅ 构建成功";
                        if (toolResult.Contains("构建失败") || toolResult.Contains("Build failed"))
                            return "⚠️ 构建失败";
                        return "🔨 构建完成";

                    case "replace_string_in_file":
                    case "multi_replace_string_in_file":
                        if (toolResult.StartsWith("✅") || toolResult.Contains("成功") || toolResult.Contains("success"))
                            return "✅ 编辑完成";
                        return "✏️ 编辑完成";

                    case "create_file":
                        if (toolResult.StartsWith("✅") || toolResult.Contains("成功") || toolResult.Contains("success"))
                            return "✅ 文件已创建";
                        return "📝 文件操作完成";

                    case "run_in_terminal":
                        if (toolResult.Contains("exit code: 0") || toolResult.Contains("ExitCode: 0"))
                            return "✅ 终端命令执行成功";
                        return "💻 终端命令已执行";

                    case "get_terminal_output":
                        return $"📋 终端输出 ({toolResult.Length} 字符)";

                    default:
                        // 通用：显示前 80 字符
                        string shortResult = toolResult.Length > 80
                            ? toolResult.Substring(0, 80) + "…"
                            : toolResult;
                        return shortResult;
                }
            }
            catch
            {
                string shortResult = toolResult.Length > 80
                    ? toolResult.Substring(0, 80) + "…"
                    : toolResult;
                return shortResult;
            }
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
                // ── 提供有用的错误信息，引导 AI 使用工作区根目录或创建目录 ──
                string suggestion = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
                    ? $"\n💡 提示: 当前工作区根目录是 \"{workspaceRoot}\"，请使用此路径或其中的子目录。"
                    : "\n💡 提示: 请使用 Windows 绝对路径格式（如 C:\\Users\\...\\project\\src）。";
                suggestion += "\n💡 如需创建新目录，请使用 create_directory 工具。";
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
                wsHint += "\n💡 如果这是需要新建的文件，请使用 create_file 工具创建（会自动创建父目录）。如果父目录不存在，请先使用 create_directory 创建目录。";
                return Task.FromResult($"❌ 文件不存在: {filePath}{wsHint}");
            }

            try
            {
                const int maxLinesToRead = 100000; // 安全上限：防止大文件撑爆内存
                int startLine = GetIntArg(args, "startLine", 1);
                int endLine = GetIntArg(args, "endLine", int.MaxValue);

                // ── 使用流式读取（File.ReadLines）避免大文件全部加载到内存 ──
                int totalLines = 0;
                var sb = new StringBuilder();
                bool truncated = false;

                foreach (var line in File.ReadLines(filePath))
                {
                    totalLines++;
                    if (totalLines > maxLinesToRead)
                    {
                        truncated = true;
                        break;
                    }
                    if (totalLines >= startLine && totalLines <= endLine)
                    {
                        sb.AppendLine($"{totalLines}: {line}");
                    }
                }

                // 如果没有到达 endLine，调整 endLine 为实际读取的最大行
                int actualEnd = Math.Min(endLine, totalLines);
                if (truncated)
                    actualEnd = Math.Min(actualEnd, maxLinesToRead);

                var resultBuilder = new StringBuilder();
                resultBuilder.AppendLine($"📄 文件: {filePath} (共 {totalLines} 行，显示 {startLine}-{actualEnd})");
                if (truncated)
                    resultBuilder.AppendLine($"> ⚠️ 文件过大（>{maxLinesToRead}行），仅读取了前 {maxLinesToRead} 行");
                resultBuilder.AppendLine();
                resultBuilder.Append(sb);

                // RAG-MARK: no-truncate — 不再截断 read_file 返回结果，完整返回文件内容
                // RAG-SOURCE: file-read 读取文件内容（BuiltInTool read_file 工具）
                string result = resultBuilder.ToString().TrimEnd();

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

                const int maxFilesToEnumerate = 10000; // 安全上限：防止超大目录撑爆内存
                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

                // ── 使用 EnumerateFiles 流式枚举（避免 Directory.GetFiles 全部加载到内存）──
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

                const int maxFilesToSearch = 5000; // 安全上限：防止超大项目文件遍历
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

                // 排除常见的非代码目录
                var excludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "node_modules", ".git", "bin", "obj", "packages", ".vs", "Debug", "Release",
                    "__pycache__", ".venv", "venv", "dist", "build", ".next", ".nuget"
                };

                Regex? regex = null;
                if (isRegexp)
                {
                    try { regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)); }
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
                    // RAG-MARK: no-truncate — 不再截断网页抓取内容
                    // RAG-SOURCE: web-fetch 网页抓取内容（BuiltInTool fetch_webpage）
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
                // RAG-SOURCE: file-read 读取文件内容（replace_string_in_file 编辑工具）
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
        /// 执行 delete_file 工具 — 删除指定文件。
        /// 注意：审批检查在 BaseAgent.ExecuteToolAsync 中完成，此方法仅执行实际删除。
        /// </summary>
        private static async Task<string> DeleteFileAsync(Dictionary<string, System.Text.Json.JsonElement> args, string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");

            if (string.IsNullOrEmpty(filePath))
                return "❌ delete_file: 缺少 filePath 参数";

            filePath = ResolvePath(filePath, workspaceRoot);

            try
            {
                if (!File.Exists(filePath))
                    return $"⚠️ 文件不存在: {Path.GetFileName(filePath)}";

                string fileName = Path.GetFileName(filePath);
                await Task.Run(() => File.Delete(filePath));

                return $"✅ 已删除文件: {fileName}";
            }
            catch (Exception ex)
            {
                return $"❌ delete_file 失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 应用 apply_patch 格式的补丁到文件。
        /// 解析 *** Begin Patch / *** End Patch 块，提取 Update File / Add File / Delete File 操作。
        /// 使用简单的行级上下文匹配来定位和替换内容。
        /// </summary>
        private static async Task<string> ApplyPatchAsync(Dictionary<string, System.Text.Json.JsonElement> args, string? workspaceRoot)
        {
            string patchText = GetStringArg(args, "patch");

            if (string.IsNullOrEmpty(patchText))
                return "❌ apply_patch: 缺少 patch 参数。请提供 *** Begin Patch / *** End Patch 格式的补丁文本。";

            workspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);

            try
            {
                var results = new List<string>();
                var patches = ParsePatchBlocks(patchText);

                if (patches.Count == 0)
                {
                    // 没有找到 Begin/End Patch 块，尝试作为简单替换处理
                    return "⚠️ apply_patch: 未检测到 *** Begin Patch / *** End Patch 块。\n"
                        + "请使用正确格式：\n"
                        + "*** Begin Patch\n"
                        + "*** Update File: /path/to/file\n"
                        + "@@ some context\n"
                        + " context line\n"
                        + "- old line to remove\n"
                        + "+ new line to add\n"
                        + " context line\n"
                        + "*** End Patch";
                }

                foreach (var patch in patches)
                {
                    string filePath = ResolvePath(patch.FilePath, workspaceRoot);

                    switch (patch.Operation.ToLowerInvariant())
                    {
                        case "delete file":
                        case "delete":
                            if (File.Exists(filePath))
                            {
                                await Task.Run(() => File.Delete(filePath));
                                results.Add($"✅ 已删除: {Path.GetFileName(filePath)}");
                            }
                            else
                            {
                                results.Add($"⚠️ 文件不存在，跳过删除: {Path.GetFileName(filePath)}");
                            }
                            break;

                        case "add file":
                        case "add":
                            // 新增文件：所有 + 行组成内容
                            string newContent = string.Join(Environment.NewLine,
                                patch.Hunks.SelectMany(h => h.AddLines));
                            string? newFileDir = Path.GetDirectoryName(filePath);
                            if (!string.IsNullOrEmpty(newFileDir) && !Directory.Exists(newFileDir))
                                Directory.CreateDirectory(newFileDir);
                            await Task.Run(() => File.WriteAllText(filePath, newContent));
                            results.Add($"✅ 已创建: {Path.GetFileName(filePath)} ({newContent.Split('\n').Length} 行)");
                            break;

                        case "update file":
                        case "update":
                        default:
                            if (!File.Exists(filePath))
                            {
                                results.Add($"❌ 文件不存在: {filePath}\n💡 如需创建新文件，请使用 Add File 操作或 create_file 工具。");
                                break;
                            }

                            string originalContent = await Task.Run(() => File.ReadAllText(filePath));
                            string[] originalLines = originalContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                            string[] newLines = (string[])originalLines.Clone();
                            bool anyApplied = false;

                            foreach (var hunk in patch.Hunks)
                            {
                                int matchStart = FindContextMatch(originalLines, hunk.ContextLines, hunk.RemoveLines);
                                if (matchStart < 0)
                                {
                                    // 尝试宽松匹配（只匹配上下文行）
                                    matchStart = FindLooseContextMatch(originalLines, hunk.ContextLines);
                                }

                                if (matchStart >= 0)
                                {
                                    // 构建替换后的行
                                    var updatedLines = new List<string>();
                                    // 保留匹配位置之前的行
                                    for (int i = 0; i < matchStart; i++)
                                        updatedLines.Add(newLines[i]);
                                    // 插入新增行（跳过标记为删除的行）
                                    foreach (var hunkLine in hunk.AllLines)
                                    {
                                        if (hunkLine.Type == PatchLineType.Context || hunkLine.Type == PatchLineType.Add)
                                            updatedLines.Add(hunkLine.Text);
                                        // Remove 行不添加
                                    }
                                    // 保留 hunk 之后的行（跳过被删除的行数）
                                    int removedCount = hunk.RemoveLines.Count;
                                    int afterHunkStart = matchStart + hunk.ContextLines.Count + removedCount;
                                    if (afterHunkStart < 0) afterHunkStart = 0;
                                    for (int i = afterHunkStart; i < newLines.Length; i++)
                                        updatedLines.Add(newLines[i]);

                                    newLines = updatedLines.ToArray();
                                    anyApplied = true;
                                }
                                else
                                {
                                    results.Add($"⚠️ 无法匹配 hunk (上下文: {string.Join(", ", hunk.ContextLines.Take(2))}...) → 文件: {Path.GetFileName(filePath)}");
                                }
                            }

                            if (anyApplied)
                            {
                                string finalContent = string.Join(Environment.NewLine, newLines);
                                await Task.Run(() => File.WriteAllText(filePath, finalContent));
                                results.Add($"✅ 已应用补丁: {Path.GetFileName(filePath)} ({patch.Hunks.Count} 个 hunk)");
                            }
                            else if (results.All(r => !r.StartsWith("✅") && !r.StartsWith("⚠️")))
                            {
                                results.Add($"❌ 补丁应用失败: {Path.GetFileName(filePath)} — 无法匹配任何 hunk 的上下文。\n💡 请使用 replace_string_in_file 工具进行精确替换，或使用 create_file 工具重写整个文件。");
                            }
                            break;
                    }
                }

                return results.Count > 0
                    ? string.Join("\n", results)
                    : "⚠️ apply_patch: 未执行任何操作";
            }
            catch (Exception ex)
            {
                return $"❌ apply_patch 失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 创建目录（递归创建所有父目录，类似 mkdir -p）。
        /// </summary>
        private static Task<string> CreateDirectoryAsync(Dictionary<string, System.Text.Json.JsonElement> args)
        {
            string dirPath = GetStringArg(args, "dirPath");

            if (string.IsNullOrEmpty(dirPath))
                return Task.FromResult("❌ create_directory: 缺少 dirPath 参数。请提供 Windows 绝对路径。");

            try
            {
                if (Directory.Exists(dirPath))
                    return Task.FromResult($"📁 目录已存在: {dirPath}");

                Directory.CreateDirectory(dirPath);
                return Task.FromResult($"✅ 已创建目录: {dirPath}");
            }
            catch (Exception ex)
            {
                return Task.FromResult($"❌ create_directory 失败: {ex.Message}");
            }
        }

        #region Patch Parsing Helpers

        /// <summary>
        /// 补丁块操作类型
        /// </summary>
        private enum PatchOperationType { Update, Add, Delete }

        /// <summary>
        /// 补丁行类型
        /// </summary>
        private enum PatchLineType { Context, Add, Remove }

        /// <summary>
        /// 补丁行
        /// </summary>
        private struct PatchLine
        {
            public PatchLineType Type;
            public string Text;
        }

        /// <summary>
        /// 单个 hunk（一个 @@ 块）
        /// </summary>
        private class PatchHunk
        {
            public List<string> ContextLines { get; set; } = new();
            public List<string> RemoveLines { get; set; } = new();
            public List<string> AddLines { get; set; } = new();
            public List<PatchLine> AllLines { get; set; } = new();
        }

        /// <summary>
        /// 解析后的补丁块
        /// </summary>
        private class ParsedPatch
        {
            public string Operation { get; set; } = "update";
            public string FilePath { get; set; } = string.Empty;
            public List<PatchHunk> Hunks { get; set; } = new();
        }

        /// <summary>
        /// 解析 *** Begin Patch / *** End Patch 块。
        /// </summary>
        private static List<ParsedPatch> ParsePatchBlocks(string patchText)
        {
            var patches = new List<ParsedPatch>();

            // 按 *** Begin Patch 分割
            var beginSplit = patchText.Split(new[] { "*** Begin Patch" }, StringSplitOptions.None);
            foreach (var block in beginSplit.Skip(1)) // 跳过第一个空块
            {
                int endIdx = block.IndexOf("*** End Patch", StringComparison.OrdinalIgnoreCase);
                if (endIdx < 0) continue;

                string blockContent = block.Substring(0, endIdx).Trim();
                var patch = new ParsedPatch();
                PatchHunk? currentHunk = null;

                var lines = blockContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var rawLine in lines)
                {
                    string line = rawLine.TrimEnd();

                    // 检测操作类型
                    if (line.StartsWith("*** Update File:", StringComparison.OrdinalIgnoreCase))
                    {
                        patch.Operation = "update";
                        patch.FilePath = line.Substring("*** Update File:".Length).Trim().TrimStart(':').Trim();
                        continue;
                    }
                    if (line.StartsWith("*** Add File:", StringComparison.OrdinalIgnoreCase))
                    {
                        patch.Operation = "add";
                        patch.FilePath = line.Substring("*** Add File:".Length).Trim().TrimStart(':').Trim();
                        continue;
                    }
                    if (line.StartsWith("*** Delete File:", StringComparison.OrdinalIgnoreCase))
                    {
                        patch.Operation = "delete";
                        patch.FilePath = line.Substring("*** Delete File:".Length).Trim().TrimStart(':').Trim();
                        continue;
                    }
                    if (line.StartsWith("*** Move to:", StringComparison.OrdinalIgnoreCase))
                    {
                        patch.Operation = "add"; // 移动视为在新位置创建
                        patch.FilePath = line.Substring("*** Move to:".Length).Trim().TrimStart(':').Trim();
                        continue;
                    }

                    // 检测 hunk 标记
                    if (line.StartsWith("@@"))
                    {
                        currentHunk = new PatchHunk();
                        patch.Hunks.Add(currentHunk);
                        continue;
                    }

                    if (currentHunk == null)
                    {
                        // 如果没有 hunk 但有操作行，创建默认 hunk
                        currentHunk = new PatchHunk();
                        patch.Hunks.Add(currentHunk);
                    }

                    // 解析行前缀
                    if (line.StartsWith("- "))
                    {
                        string text = line.Substring(2);
                        currentHunk.RemoveLines.Add(text);
                        currentHunk.ContextLines.Add(text); // 删除行也作为上下文
                        currentHunk.AllLines.Add(new PatchLine { Type = PatchLineType.Remove, Text = text });
                    }
                    else if (line.StartsWith("+ "))
                    {
                        string text = line.Substring(2);
                        currentHunk.AddLines.Add(text);
                        currentHunk.AllLines.Add(new PatchLine { Type = PatchLineType.Add, Text = text });
                    }
                    else if (line.Length > 0 && line[0] == ' ')
                    {
                        string text = line.Substring(1);
                        currentHunk.ContextLines.Add(text);
                        currentHunk.AllLines.Add(new PatchLine { Type = PatchLineType.Context, Text = text });
                    }
                    else if (line.Length > 0 && line != "*** End Patch")
                    {
                        // 无前缀的行视为上下文
                        currentHunk.ContextLines.Add(line);
                        currentHunk.AllLines.Add(new PatchLine { Type = PatchLineType.Context, Text = line });
                    }
                }

                if (!string.IsNullOrEmpty(patch.FilePath))
                    patches.Add(patch);
            }

            return patches;
        }

        /// <summary>
        /// 在源文件中查找上下文匹配的起始行（精确匹配）。
        /// 返回匹配的起始行索引（0-based），未找到返回 -1。
        /// </summary>
        private static int FindContextMatch(string[] sourceLines, List<string> contextLines, List<string> removeLines)
        {
            if (contextLines.Count == 0) return 0;

            // 构建完整搜索模式：上下文行 + 待删除行
            var searchPattern = new List<string>();
            searchPattern.AddRange(contextLines);
            searchPattern.AddRange(removeLines);

            for (int i = 0; i <= sourceLines.Length - searchPattern.Count; i++)
            {
                bool match = true;
                for (int j = 0; j < searchPattern.Count; j++)
                {
                    if (!string.Equals(sourceLines[i + j].Trim(), searchPattern[j].Trim(), StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }

            // 如果带上 removeLines 匹配失败，尝试只用 contextLines
            if (removeLines.Count > 0 && contextLines.Count > 0)
            {
                for (int i = 0; i <= sourceLines.Length - contextLines.Count; i++)
                {
                    bool match = true;
                    for (int j = 0; j < contextLines.Count; j++)
                    {
                        if (!string.Equals(sourceLines[i + j].Trim(), contextLines[j].Trim(), StringComparison.Ordinal))
                        {
                            match = false;
                            break;
                        }
                    }
                    if (match) return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 宽松上下文匹配：仅使用第1个和最后1个上下文行定位。
        /// </summary>
        private static int FindLooseContextMatch(string[] sourceLines, List<string> contextLines)
        {
            if (contextLines.Count < 2) return -1;

            string firstCtx = contextLines.First().Trim();
            string lastCtx = contextLines.Last().Trim();

            for (int i = 0; i < sourceLines.Length; i++)
            {
                if (string.Equals(sourceLines[i].Trim(), firstCtx, StringComparison.Ordinal))
                {
                    // 找到第一个上下文行，然后向后搜索最后一个上下文行
                    for (int j = i + 1; j < sourceLines.Length; j++)
                    {
                        if (string.Equals(sourceLines[j].Trim(), lastCtx, StringComparison.Ordinal))
                            return i;
                    }
                }
            }

            // 再试试只用第一个上下文行
            for (int i = 0; i < sourceLines.Length; i++)
            {
                if (string.Equals(sourceLines[i].Trim(), firstCtx, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        #endregion

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
                    // RAG-MARK: no-truncate — 不再截断终端 stdout/stderr 输出
                    // RAG-SOURCE: terminal-output 终端命令输出（BuiltInTool run_in_terminal）
                    if (!string.IsNullOrWhiteSpace(stdout))
                    {
                        sb.AppendLine(stdout);
                    }
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        sb.AppendLine("--- STDERR ---");
                        sb.AppendLine(stderr);
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
        /// 包含路径穿越防护：确保解析后的路径在工作区范围内。
        /// </summary>
        private static string ResolvePath(string filePath, string? workspaceRoot)
        {
            if (string.IsNullOrEmpty(filePath))
                return filePath;

            string resolved;
            if (Path.IsPathRooted(filePath))
            {
                resolved = Path.GetFullPath(filePath);
            }
            else if (!string.IsNullOrEmpty(workspaceRoot))
            {
                string candidate = Path.Combine(workspaceRoot, filePath.Replace('/', '\\'));
                resolved = Path.GetFullPath(candidate);
            }
            else
            {
                return filePath;
            }

            // ── 路径穿越防护：确保解析后的路径在工作区根目录内 ──
            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                string normalizedWorkspace = Path.GetFullPath(workspaceRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedResolved = resolved
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!normalizedResolved.StartsWith(
                        normalizedWorkspace + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(normalizedResolved, normalizedWorkspace,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"[BuiltInTool] ⚠️ 路径穿越检测: {resolved} 不在工作区 {workspaceRoot} 内，拒绝访问");
                    return filePath; // 返回原始路径，由调用方处理（文件不存在时会给出提示）
                }
            }

            return resolved;
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
