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
        private readonly IMemoryService? _memoryService;

        // ── 文件读取缓存：同一会话内相同路径只从磁盘读取一次 ──
        // 缓存条目包含内容 + 上次读取轮次，支持基于轮数的缓存过期
        private readonly ConcurrentDictionary<string, FileReadCacheEntry> _fileReadCache = new(StringComparer.OrdinalIgnoreCase);

        private int _currentRound;
        private int _roundThreshold = 5;

        /// <summary>
        /// 当前 API 请求的轮次号（由 Agent 工具循环设置）。
        /// 0 表示不在工具循环中，此时缓存永不过期（仅磁盘变更触发重读）。
        /// 设置时会自动同步到 ReadFileTool 实例。
        /// </summary>
        public int CurrentRound
        {
            get => _currentRound;
            set
            {
                _currentRound = value;
                SyncRoundToReadFileTool();
            }
        }

        /// <summary>
        /// 文件读取缓存的轮数阈值。当 CurrentRound - LastReadRound >= 此值时，
        /// 即使文件内容未变更也允许 AI 重新读取文件以刷新上下文。
        /// 默认 5 轮。设置时会自动同步到 ReadFileTool 实例。
        /// </summary>
        public int RoundThreshold
        {
            get => _roundThreshold;
            set
            {
                _roundThreshold = value;
                SyncRoundToReadFileTool();
            }
        }

        /// <summary>
        /// 将 CurrentRound 和 RoundThreshold 同步到 ReadFileTool 实例。
        /// </summary>
        private void SyncRoundToReadFileTool()
        {
            if (_tools.TryGetValue("read_file", out var tool) && tool is ReadFileTool rft)
            {
                rft.CurrentRound = _currentRound;
                rft.RoundThreshold = _roundThreshold;
            }
        }

        // ── 工具注册表 ──
        private readonly Dictionary<string, BuiltInToolBase> _tools = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// RunSubagent 工具的处理委托（由 BaseAgent 在执行前注入）。
        /// 桥接 BuiltInToolService 和 Agent 的 ExploreAgent 引用。
        /// </summary>
        public Func<BuiltInTools.ExplorationContext, Task<string>>? ExploreHandler { get; set; }

        /// <summary>
        /// 当前会话 ID，供 MemoryTool 等需要会话上下文的工具使用。
        /// 由 ChatControl 在会话切换/创建时设置。
        /// </summary>
        public string? CurrentSessionId { get; set; }

        public BuiltInToolService(
            McpManagerService? mcpManager = null,
            WebSearchService? webSearchService = null,
            IBuildService? buildService = null,
            IMemoryService? memoryService = null)
        {
            _mcpManager = mcpManager;
            _webSearchService = webSearchService;
            _buildService = buildService;
            _memoryService = memoryService;

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

            // 记忆工具
            if (_memoryService != null)
            {
                Register(new MemoryTool(_memoryService,
                    () => CurrentSessionId,
                    () => null)); // solutionPath 回退值；实际由 ExecuteAsync 的 workspaceRoot 参数传入
            }

            // 子代理委派工具
            Register(new RunSubagentTool(ctx =>
            {
                // 委托给 ExploreHandler（由 BaseAgent 在执行前注入）
                if (ExploreHandler != null)
                    return ExploreHandler(ctx);
                return Task.FromResult("❌ runSubagent: ExploreAgent 未注入。请确保 AgentDispatcher 已正确初始化。");
            }));
        }

        private void Register(BuiltInToolBase tool)
        {
            _tools[tool.Name] = tool;
        }

        #endregion

        #region Cache Management

        /// <summary>
        /// 获取文件读取缓存的快照（仅内容，用于 Agent 步骤间上下文传递）。
        /// </summary>
        public Dictionary<string, string> GetFileReadCacheSnapshot()
        {
            var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _fileReadCache)
                snapshot[kvp.Key] = kvp.Value.FullContent;
            return snapshot;
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
        /// 创建所有内置工具的轻量实例（无外部依赖），作为定义的单一数据源。
        /// </summary>
        private static BuiltInToolBase[] CreateAllDisplayTools()
        {
            return new BuiltInToolBase[]
            {
                new ListDirTool(),
                new ReadFileTool(new ConcurrentDictionary<string, FileReadCacheEntry>()),
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
        }

        /// <summary>
        /// 获取所有内置工作区工具的定义列表（静态方法，向后兼容）。
        /// </summary>
        public static List<ToolDefinition> GetBuiltInToolDefinitions()
        {
            return CreateAllDisplayTools().Select(t => t.GetDefinition()).ToList();
        }

        /// <summary>
        /// 根据 Agent 白名单获取过滤后的工具定义。
        /// 合并内置工具和 MCP 外部工具。同名时 MCP 工具优先（覆盖内置）。
        /// </summary>
        public List<ToolDefinition> GetFilteredToolDefinitions(List<string>? allowedTools)
        {
            var definitions = new List<ToolDefinition>();

            // ── 1. 先收集 MCP 外部工具（同名时优先 MCP）──
            List<ToolDefinition> mcpDefs = new();
            if (_mcpManager != null && _mcpManager.AllTools.Count > 0)
            {
                mcpDefs = _mcpManager.GetFilteredToolDefinitions(allowedTools);
                definitions.AddRange(mcpDefs);
            }

            // ── 2. 添加内置工具（按白名单过滤，排除与 MCP 同名的）──
            var mcpNames = new HashSet<string>(mcpDefs.Select(d => d.Function.Name), StringComparer.OrdinalIgnoreCase);
            var builtInDefs = _tools.Values.Select(t => t.GetDefinition()).ToList();
            if (allowedTools != null && allowedTools.Count > 0)
            {
                var whitelist = new HashSet<string>(allowedTools, StringComparer.OrdinalIgnoreCase);
                builtInDefs = builtInDefs
                    .Where(d => whitelist.Contains(d.Function.Name) && !mcpNames.Contains(d.Function.Name))
                    .ToList();
            }
            else
            {
                builtInDefs = builtInDefs.Where(d => !mcpNames.Contains(d.Function.Name)).ToList();
            }
            definitions.AddRange(builtInDefs);

            if (allowedTools != null)
            {
                Logger.Info($"[BuiltInTool] 工具定义: {definitions.Count} 个 (MCP: {mcpDefs.Count}, 内置: {builtInDefs.Count})"
                    + (mcpNames.Count > 0 ? $", MCP 覆盖内置: [{string.Join(", ", mcpNames)}]" : ""));
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
                    or "run_in_terminal" or "get_terminal_output" or "VisualStudio_askQuestions"
                    or "runSubagent" or "memory" => true,
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
        /// 获取用于显示文本的轻量工具实例（委托 CreateAllDisplayTools）。
        /// </summary>
        private static BuiltInToolBase? GetToolInstanceForDisplay(string toolName)
        {
            return CreateAllDisplayTools().FirstOrDefault(t =>
                string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 为 MCP 外部工具生成显示文本。
        /// OCR 工具额外提示参数格式。
        /// </summary>
        private static string GetMcpToolCallDisplayText(string toolName, Dictionary<string, JsonElement> args)
        {
            string filePath = GetStringArg(args, "filePath") ?? GetStringArg(args, "path") ?? GetStringArg(args, "directory");
            string query = GetStringArg(args, "query") ?? GetStringArg(args, "pattern") ?? GetStringArg(args, "search");
            string url = GetStringArg(args, "url");
            string inputData = GetStringArg(args, "input_data") ?? GetStringArg(args, "input") ?? GetStringArg(args, "image");

            // ── OCR 工具：显示参数格式提示 ──
            bool isOcrTool = toolName.IndexOf("ocr", StringComparison.OrdinalIgnoreCase) >= 0
                          || toolName.IndexOf("recognize_text", StringComparison.OrdinalIgnoreCase) >= 0;
            string ocrHint = "";
            if (isOcrTool)
            {
                string paramSummary = !string.IsNullOrEmpty(inputData)
                    ? (inputData.Length > 60 ? inputData.Substring(0, 60) + "…" : inputData)
                    : "（未提供 input_data）";
                ocrHint = $" | 📋 OCR 参数: input_data={paramSummary}";
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                string fname = Path.GetFileName(filePath);
                return $"🔧 调用 `{toolName}` → `{fname}`{ocrHint}";
            }
            if (!string.IsNullOrEmpty(url))
                return $"🔧 调用 `{toolName}` → `{TruncateText(url, 50)}`{ocrHint}";
            if (!string.IsNullOrEmpty(query))
                return $"🔧 调用 `{toolName}` → `{TruncateText(query, 50)}`{ocrHint}";
            if (!string.IsNullOrEmpty(inputData))
                return $"🔧 调用 `{toolName}` → `{TruncateText(inputData, 50)}`{ocrHint}";

            return $"🔧 调用工具 `{toolName}`{ocrHint}";
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
