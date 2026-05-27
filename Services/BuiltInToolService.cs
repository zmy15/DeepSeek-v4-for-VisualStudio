using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 内置工作区工具服务 — 为 Agent 提供无需外部 MCP 服务器的本地工具。
    /// 
    /// 作为工具注册中心，管理所有内置工具实例。
    /// 每个工具的定义和实现位于 Services/BuiltInTools/ 目录下的独立文件中。
    /// </summary>
    public class BuiltInToolService : IBuiltInToolService
    {
        private readonly McpManagerService? _mcpManager;
        private readonly WebSearchService? _webSearchService;
        private readonly IBuildService? _buildService;

        // ── 文件读取缓存：同一会话内相同路径只从磁盘读取一次 ──
        private readonly ConcurrentDictionary<string, string> _fileReadCache = new(StringComparer.OrdinalIgnoreCase);

        // ── 工具注册表 ──
        private readonly Dictionary<string, BuiltInToolBase> _tools = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// i18n 便捷访问器。
        /// </summary>
        private static LocalizationService L => LocalizationService.Instance;

        public BuiltInToolService(McpManagerService? mcpManager = null, WebSearchService? webSearchService = null, IBuildService? buildService = null)
        {
            _mcpManager = mcpManager;
            _webSearchService = webSearchService;
            _buildService = buildService;

            // ── 注册所有内置工具 ──
            RegisterAllTools();
        }

        #region Tool Registry

        /// <summary>
        /// 注册所有内置工具实例。
        /// 新增工具只需在此方法中添加一行注册代码。
        /// </summary>
        private void RegisterAllTools()
        {
            // 只读探索工具
            Register(new ListDirTool());
            Register(new ReadFileTool(_fileReadCache));
            Register(new FileSearchTool());
            Register(new GrepSearchTool());
            Register(new GetErrorsTool(_buildService));
            Register(new FetchWebpageTool(_webSearchService));

            // 构建工具
            Register(new BuildSolutionTool(_buildService));

            // 编辑工具
            Register(new ReplaceStringInFileTool());
            Register(new MultiReplaceStringInFileTool());
            Register(new CreateFileTool());
            Register(new DeleteFileTool());
            Register(new ApplyPatchTool());
            Register(new CreateDirectoryTool());

            // 终端工具
            Register(new RunInTerminalTool());
            Register(new GetTerminalOutputTool());

            // 交互工具
            Register(new AskQuestionsTool());
        }

        private void Register(BuiltInToolBase tool)
        {
            _tools[tool.Name] = tool;
        }

        #endregion

        #region Cache Management

        /// <summary>
        /// 获取文件读取缓存的快照（用于 Agent 步骤间上下文传递）。
        /// </summary>
        public Dictionary<string, string> GetFileReadCacheSnapshot()
        {
            return new Dictionary<string, string>(_fileReadCache, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 使指定文件的读取缓存失效。
        /// </summary>
        public void InvalidateFileReadCache(string filePath)
        {
            _fileReadCache.TryRemove(filePath, out _);
        }

        /// <summary>
        /// 批量使文件读取缓存失效。
        /// </summary>
        public void InvalidateFileReadCache(IEnumerable<string> filePaths)
        {
            foreach (var path in filePaths)
                _fileReadCache.TryRemove(path, out _);
        }

        #endregion

        #region Tool Definitions

        /// <summary>
        /// 获取所有内置工作区工具的定义列表（静态方法，向后兼容）。
        /// </summary>
        public static List<ToolDefinition> GetBuiltInToolDefinitions()
        {
            var tools = new BuiltInToolBase[]
            {
                new ListDirTool(),
                new ReadFileTool(new ConcurrentDictionary<string, string>()),
                new FileSearchTool(),
                new GrepSearchTool(),
                new GetErrorsTool(),
                new FetchWebpageTool(),
                new BuildSolutionTool(),
                new ReplaceStringInFileTool(),
                new MultiReplaceStringInFileTool(),
                new CreateFileTool(),
                new DeleteFileTool(),
                new ApplyPatchTool(),
                new CreateDirectoryTool(),
                new RunInTerminalTool(),
                new GetTerminalOutputTool(),
                new AskQuestionsTool(),
            };

            return tools.Select(t => t.GetDefinition()).ToList();
        }

        /// <summary>
        /// 根据 Agent 白名单获取过滤后的工具定义。
        /// 合并内置工具和 MCP 外部工具。
        /// </summary>
        public List<ToolDefinition> GetFilteredToolDefinitions(List<string>? allowedTools)
        {
            var definitions = new List<ToolDefinition>();

            // ── 1. 添加内置工具（按白名单过滤）──
            var builtInDefs = _tools.Values.Select(t => t.GetDefinition()).ToList();
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
                if (!_tools.TryGetValue(toolName, out var tool))
                    return null; // 不是内置工具，交由 MCP 处理

                var args = JsonSerializer
                    .Deserialize<Dictionary<string, JsonElement>>(argumentsJson)
                    ?? new Dictionary<string, JsonElement>();

                return await tool.ExecuteAsync(args, workspaceRoot);
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

        #endregion

        #region Display Text & Result Summary (Static Helpers)

        /// <summary>
        /// 根据工具名称和参数 JSON 生成人类可读的工具调用描述。
        /// </summary>
        public static string GetToolCallDisplayText(string toolName, string argumentsJson)
        {
            try
            {
                var args = string.IsNullOrWhiteSpace(argumentsJson)
                    ? new Dictionary<string, JsonElement>()
                    : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson)
                        ?? new Dictionary<string, JsonElement>();

                var tool = GetToolInstanceForDisplay(toolName);
                if (tool != null)
                    return tool.GetDisplayText(args);

                return GetMcpToolCallDisplayText(toolName, args);
            }
            catch
            {
                return $"🔧 调用工具 `{toolName}`";
            }
        }

        /// <summary>
        /// 构建工具执行结果的简短摘要（用于在聊天 UI 中展示）。
        /// </summary>
        public static string GetToolResultSummary(string toolName, string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult))
                return "（无返回结果）";

            if (toolResult.StartsWith("❌") || toolResult.StartsWith("⛔"))
                return toolResult;

            try
            {
                var tool = GetToolInstanceForDisplay(toolName);
                if (tool != null)
                    return tool.GetResultSummary(toolResult);

                string shortResult = toolResult.Length > 80
                    ? toolResult.Substring(0, 80) + "…"
                    : toolResult;
                return shortResult;
            }
            catch
            {
                string shortResult = toolResult.Length > 80
                    ? toolResult.Substring(0, 80) + "…"
                    : toolResult;
                return shortResult;
            }
        }

        /// <summary>
        /// 获取用于显示文本的轻量工具实例（无状态依赖，可安全临时创建）。
        /// </summary>
        private static BuiltInToolBase? GetToolInstanceForDisplay(string toolName)
        {
            return toolName switch
            {
                "list_dir" => new ListDirTool(),
                "read_file" => new ReadFileTool(new ConcurrentDictionary<string, string>()),
                "file_search" => new FileSearchTool(),
                "grep_search" => new GrepSearchTool(),
                "get_errors" => new GetErrorsTool(),
                "fetch_webpage" => new FetchWebpageTool(),
                "build_solution" => new BuildSolutionTool(),
                "replace_string_in_file" => new ReplaceStringInFileTool(),
                "multi_replace_string_in_file" => new MultiReplaceStringInFileTool(),
                "create_file" => new CreateFileTool(),
                "delete_file" => new DeleteFileTool(),
                "apply_patch" => new ApplyPatchTool(),
                "create_directory" => new CreateDirectoryTool(),
                "run_in_terminal" => new RunInTerminalTool(),
                "get_terminal_output" => new GetTerminalOutputTool(),
                "VisualStudio_askQuestions" => new AskQuestionsTool(),
                _ => null
            };
        }

        /// <summary>
        /// 为 MCP 外部工具生成显示文本。
        /// </summary>
        private static string GetMcpToolCallDisplayText(string toolName, Dictionary<string, JsonElement> args)
        {
            string filePath = GetStringArg(args, "filePath") ?? GetStringArg(args, "path") ?? GetStringArg(args, "directory");
            string query = GetStringArg(args, "query") ?? GetStringArg(args, "pattern") ?? GetStringArg(args, "search");
            string url = GetStringArg(args, "url");

            if (!string.IsNullOrEmpty(filePath))
            {
                string fname = Path.GetFileName(filePath);
                return $"🔧 调用 `{toolName}` → `{fname}`";
            }
            if (!string.IsNullOrEmpty(url))
                return $"🔧 调用 `{toolName}` → `{TruncateText(url, 50)}`";
            if (!string.IsNullOrEmpty(query))
                return $"🔧 调用 `{toolName}` → `{TruncateText(query, 50)}`";

            return $"🔧 调用工具 `{toolName}`";
        }

        #endregion

        #region Static Arg Helpers

        private static string GetStringArg(Dictionary<string, JsonElement> args, string key)
        {
            if (args.TryGetValue(key, out var element))
            {
                if (element.ValueKind == JsonValueKind.String)
                    return element.GetString() ?? string.Empty;
                if (element.ValueKind == JsonValueKind.Null)
                    return string.Empty;
                return element.ToString();
            }
            return string.Empty;
        }

        private static string TruncateText(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
                return text;
            return text.Substring(0, maxLen) + "…";
        }

        #endregion
    }
}
