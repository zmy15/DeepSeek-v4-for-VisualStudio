using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// MCP 管理器服务，负责管理多个 MCP 服务器连接，
    /// 聚合所有工具，并提供统一的工具调用接口。
    /// 
    /// 同时负责将 MCP 工具转换为 DeepSeek API 的 tools 参数格式（兼容 OpenAI function calling）。
    /// </summary>
    public class McpManagerService : IMcpManagerService
    {
        private readonly List<IMcpClient> _clients = new();
        private readonly object _lock = new();
        private bool _isInitialized;

        /// <summary>
        /// 所有已加载的工具（来自所有服务器）
        /// </summary>
        public IReadOnlyList<McpTool> AllTools
        {
            get
            {
                lock (_lock)
                {
                    return _clients.SelectMany(c => c.Tools).ToList();
                }
            }
        }

        /// <summary>
        /// 不应暴露给 AI 模型的内部工具名模式列表。
        /// 这些工具由 VS 扩展内部调用（如 OCR），AI 模型不需要也不应该直接调用它们，
        /// 因为相关数据已通过其他方式（文件附件解析）注入到对话上下文中。
        /// </summary>
        private static readonly string[] InternalOnlyToolPatterns =
        {
            "ocr", "recognize_text", "paddle_ocr", "ocr_image", "image_to_text", "read_text"
        };

        /// <summary>
        /// 获取所有工具的 DeepSeek/OpenAI function calling 格式定义。
        /// 自动过滤仅内部使用的工具（如 OCR），避免 AI 模型错误调用。
        /// </summary>
        public List<ToolDefinition> GetToolDefinitions()
        {
            return GetFilteredToolDefinitions(null);
        }

        /// <summary>
        /// 获取经过白名单过滤的工具定义。
        /// 当 <paramref name="allowedTools"/> 不为 null/空时，仅返回名称在列表中的工具；
        /// 为 null 时返回所有非内部工具（等同于 <see cref="GetToolDefinitions()"/>）。
        /// </summary>
        /// <param name="allowedTools">允许的工具名称白名单。null 表示不过滤。</param>
        public List<ToolDefinition> GetFilteredToolDefinitions(List<string>? allowedTools)
        {
            var tools = AllTools;
            var definitions = new List<ToolDefinition>();

            // ── 构建白名单快速查找集合（仅当指定时）──
            HashSet<string>? whitelist = null;
            if (allowedTools != null && allowedTools.Count > 0)
            {
                whitelist = new HashSet<string>(allowedTools, StringComparer.OrdinalIgnoreCase);
            }

            foreach (var tool in tools)
            {
                // ── 过滤内部工具：OCR 已由扩展在发送前自动处理，AI 无需调用 ──
                bool isInternalOnly = InternalOnlyToolPatterns.Any(
                    pattern => tool.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);

                if (isInternalOnly)
                {
                    Logger.Info($"[MCP] 跳过内部工具 (不暴露给 AI): {tool.Name}");
                    continue;
                }

                // ── 白名单过滤：仅允许 Agent 声明的工具 ──
                if (whitelist != null && !whitelist.Contains(tool.Name))
                {
                    continue;
                }

                // 构建 OpenAI 兼容的 function 定义
                var func = new ToolFunction
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = ConvertToOpenAiParameters(tool.InputSchema)
                };

                definitions.Add(new ToolDefinition
                {
                    Type = "function",
                    Function = func
                });
            }

            if (whitelist != null)
            {
                Logger.Info($"[MCP] 白名单过滤: {definitions.Count}/{tools.Count} 个工具 (允许: [{string.Join(", ", allowedTools!)}])");
            }

            return definitions;
        }

        /// <summary>
        /// 根据工具名查找所属的 MCP 客户端
        /// </summary>
        private IMcpClient? FindClient(string toolName)
        {
            lock (_lock)
            {
                return _clients.FirstOrDefault(c => c.Tools.Any(t => t.Name == toolName));
            }
        }

        /// <summary>
        /// 初始化所有已启用的 MCP 服务器
        /// </summary>
        public async Task InitializeAsync(List<McpServerConfig> configs, CancellationToken cancellationToken = default)
        {
            if (_isInitialized) return;

            var enabledConfigs = configs.Where(c => c.Enabled).ToList();
            Logger.Info($"[MCP] 正在初始化 {enabledConfigs.Count} 个 MCP 服务器...");

            foreach (var config in enabledConfigs)
            {
                try
                {
                    IMcpClient client = config.Transport?.ToLowerInvariant() == "http"
                        ? new McpHttpClient(config)
                        : new McpStdioClient(config);

                    await client.ConnectAsync(cancellationToken);
                    lock (_lock) { _clients.Add(client); }
                    Logger.Info($"[MCP] 服务器 '{config.Name}' ({client.Transport}) 连接成功, {client.Tools.Count} 个工具");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MCP] 服务器 '{config.Name}' 连接失败: {ex.Message}", ex);
                }
            }

            _isInitialized = true;
            Logger.Info($"[MCP] 初始化完成, 共 {_clients.Count} 个服务器, {AllTools.Count} 个工具");
        }

        /// <summary>
        /// 调用指定的 MCP 工具
        /// </summary>
        public async Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
        {
            var client = FindClient(toolName);
            if (client == null)
            {
                return $"错误: 未找到工具 '{toolName}'";
            }

            try
            {
                // 解析参数 JSON
                Dictionary<string, object> arguments;
                try
                {
                    arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(argumentsJson)
                                ?? new Dictionary<string, object>();
                }
                catch (JsonException)
                {
                    arguments = new Dictionary<string, object>();
                }

                var result = await client.CallToolAsync(toolName, arguments, cancellationToken);

                if (result.IsError)
                {
                    var errorText = string.Join("\n", result.Content.Select(c => c.Text));
                    Logger.Error($"[MCP] 工具 '{toolName}' 返回错误: {errorText}");
                    return $"❌ 工具调用错误: {errorText}";
                }

                // 提取文本内容
                var textContents = result.Content
                    .Where(c => c.Type == "text")
                    .Select(c => c.Text);

                return string.Join("\n", textContents);
            }
            catch (Exception ex)
            {
                Logger.Error($"[MCP] 工具 '{toolName}' 调用异常: {ex.Message}", ex);
                return $"❌ 工具调用异常: {ex.Message}";
            }
        }

        /// <summary>
        /// 刷新所有服务器的工具列表
        /// </summary>
        public async Task RefreshAllToolsAsync(CancellationToken cancellationToken = default)
        {
            foreach (var client in _clients)
            {
                try
                {
                    await client.RefreshToolsAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MCP] 刷新 '{client.ServerName}' 工具列表失败: {ex.Message}");
                }
            }
        }

        #region 格式转换帮助方法

        /// <summary>
        /// 将 MCP JSON Schema 转换为 OpenAI function calling 的 parameters 格式。
        /// OpenAI 要求:
        /// {
        ///   "type": "object",
        ///   "properties": { ... },
        ///   "required": [...]
        /// }
        /// </summary>
        private static object ConvertToOpenAiParameters(McpInputSchema schema)
        {
            var properties = new Dictionary<string, object>();

            foreach (var kvp in schema.Properties)
            {
                var propDef = new Dictionary<string, object>
                {
                    ["type"] = MapJsonType(kvp.Value.Type),
                    ["description"] = kvp.Value.Description
                };

                if (kvp.Value.Enum != null && kvp.Value.Enum.Count > 0)
                {
                    propDef["enum"] = kvp.Value.Enum;
                }

                if (kvp.Value.Default != null)
                {
                    propDef["default"] = kvp.Value.Default;
                }

                properties[kvp.Key] = propDef;
            }

            var result = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = schema.Required ?? new List<string>()
            };

            return result;
        }

        /// <summary>
        /// 将 JSON Schema 类型映射到 OpenAI 期望的类型名。
        /// MCP 可能使用 "number"、"integer" 等，OpenAI 支持 "string"|"number"|"integer"|"boolean"|"object"|"array"|"null"
        /// </summary>
        private static string MapJsonType(string mcpType)
        {
            return mcpType switch
            {
                "number" => "number",
                "integer" => "integer",
                "boolean" => "boolean",
                "object" => "object",
                "array" => "array",
                "null" => "null",
                _ => "string"
            };
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var client in _clients)
                {
                    try { client.Dispose(); }
                    catch (Exception ex) { Logger.Info($"[MCP] 清理客户端异常: {ex.Message}"); }
                }
                _clients.Clear();
            }
            _isInitialized = false;
        }

        #endregion
    }
}
