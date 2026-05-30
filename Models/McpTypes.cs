using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    // ========================================================================
    // MCP (Model Context Protocol) JSON-RPC 2.0 类型定义
    // 协议版本: 2025-11-25 (无状态架构)
    // 参考: https://modelcontextprotocol.io/specification/2025-11-25/
    // ========================================================================

    #region JSON-RPC 2.0 基础类型

    /// <summary>
    /// JSON-RPC 2.0 请求
    /// </summary>
    public class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("params")]
        public System.Text.Json.JsonElement? Params { get; set; }
    }

    /// <summary>
    /// JSON-RPC 2.0 响应（成功/失败）。
    /// Id 字段兼容 int 和 string 两种类型。
    /// </summary>
    public class JsonRpcResponse
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int Id { get; set; }

        [JsonPropertyName("result")]
        public System.Text.Json.JsonElement? Result { get; set; }

        [JsonPropertyName("error")]
        public JsonRpcError? Error { get; set; }
    }

    /// <summary>
    /// JSON-RPC 2.0 错误
    /// </summary>
    public class JsonRpcError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public System.Text.Json.JsonElement? Data { get; set; }
    }

    /// <summary>
    /// JSON-RPC 2.0 通知（无 id）
    /// </summary>
    public class JsonRpcNotification
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("params")]
        public System.Text.Json.JsonElement? Params { get; set; }
    }

    #endregion

    #region MCP 生命周期

    /// <summary>
    /// Initialize 请求参数
    /// </summary>
    public class InitializeParams
    {
        [JsonPropertyName("protocolVersion")]
        public string ProtocolVersion { get; set; } = "2025-11-25";

        [JsonPropertyName("capabilities")]
        public ClientCapabilities Capabilities { get; set; } = new();

        [JsonPropertyName("clientInfo")]
        public ImplementationInfo ClientInfo { get; set; } = new();
    }

    public class ClientCapabilities
    {
        [JsonPropertyName("roots")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CapabilityRoots? Roots { get; set; }

        [JsonPropertyName("sampling")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Sampling { get; set; }
    }

    public class CapabilityRoots
    {
        [JsonPropertyName("listChanged")]
        public bool ListChanged { get; set; }
    }

    public class ImplementationInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "DeepSeek-v4-for-VisualStudio";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.1.0";
    }

    /// <summary>
    /// Initialize 响应结果
    /// </summary>
    public class InitializeResult
    {
        [JsonPropertyName("protocolVersion")]
        public string ProtocolVersion { get; set; } = string.Empty;

        [JsonPropertyName("capabilities")]
        public ServerCapabilities Capabilities { get; set; } = new();

        [JsonPropertyName("serverInfo")]
        public ImplementationInfo ServerInfo { get; set; } = new();
    }

    public class ServerCapabilities
    {
        [JsonPropertyName("tools")]
        public ToolsCapability? Tools { get; set; }

        [JsonPropertyName("resources")]
        public ResourcesCapability? Resources { get; set; }

        [JsonPropertyName("prompts")]
        public PromptsCapability? Prompts { get; set; }

        [JsonPropertyName("tasks")]
        public TasksCapability? Tasks { get; set; }
    }

    /// <summary>
    /// 2025-11-25: 实验性异步任务能力
    /// </summary>
    public class TasksCapability
    {
        [JsonPropertyName("listChanged")]
        public bool ListChanged { get; set; }
    }

    public class ToolsCapability
    {
        [JsonPropertyName("listChanged")]
        public bool ListChanged { get; set; }
    }

    public class ResourcesCapability
    {
        [JsonPropertyName("subscribe")]
        public bool Subscribe { get; set; }

        [JsonPropertyName("listChanged")]
        public bool ListChanged { get; set; }
    }

    public class PromptsCapability
    {
        [JsonPropertyName("listChanged")]
        public bool ListChanged { get; set; }
    }

    #endregion

    #region MCP Tools

    /// <summary>
    /// tools/list 响应结果
    /// </summary>
    public class ToolsListResult
    {
        [JsonPropertyName("tools")]
        public List<McpTool> Tools { get; set; } = new();
    }

    /// <summary>
    /// MCP 工具定义
    /// </summary>
    public class McpTool
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("inputSchema")]
        public McpInputSchema InputSchema { get; set; } = new();
    }

    /// <summary>
    /// 工具输入参数 JSON Schema
    /// </summary>
    public class McpInputSchema
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "object";

        [JsonPropertyName("properties")]
        public Dictionary<string, McpPropertySchema> Properties { get; set; } = new();

        [JsonPropertyName("required")]
        public List<string> Required { get; set; } = new();
    }

    /// <summary>
    /// 单个属性的 JSON Schema
    /// </summary>
    public class McpPropertySchema
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "string";

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("enum")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Enum { get; set; }

        [JsonPropertyName("default")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Default { get; set; }
    }

    /// <summary>
    /// tools/call 请求参数
    /// </summary>
    public class ToolCallParams
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public Dictionary<string, object> Arguments { get; set; } = new();
    }

    /// <summary>
    /// tools/call 响应结果
    /// </summary>
    public class ToolCallResult
    {
        [JsonPropertyName("content")]
        public List<ToolContentItem> Content { get; set; } = new();

        [JsonPropertyName("isError")]
        public bool IsError { get; set; }
    }

    /// <summary>
    /// 工具调用返回的内容项
    /// </summary>
    public class ToolContentItem
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text"; // "text" | "image" | "resource"

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Data { get; set; }

        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }
    }

    #endregion

    #region MCP Resources

    public class ResourcesListResult
    {
        [JsonPropertyName("resources")]
        public List<McpResource> Resources { get; set; } = new();
    }

    public class McpResource
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }
    }

    public class ReadResourceParams
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;
    }

    public class ReadResourceResult
    {
        [JsonPropertyName("contents")]
        public List<ResourceContentItem> Contents { get; set; } = new();
    }

    public class ResourceContentItem
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;

        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }

        [JsonPropertyName("text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Text { get; set; }

        [JsonPropertyName("blob")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Blob { get; set; }
    }

    #endregion

    #region MCP 服务器配置

    /// <summary>
    /// 单个 MCP 服务器的持久化配置。
    /// 兼容 Claude Desktop / VS Code / 自定义三种 JSON 格式。
    /// 
    /// 字段说明：
    /// - "command" → Command
    /// - "args"   → Args（支持字符串 "a b c" 或数组 ["a","b","c"]，自动转换）
    /// - "env"    → Env（字典格式，Claude Desktop 兼容）
    /// </summary>
    public class McpServerConfig
    {
        /// <summary>服务器显示名称</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>传输类型: "stdio" 或 "http"</summary>
        public string Transport { get; set; } = "stdio";

        /// <summary>可执行文件路径（stdio 传输）</summary>
        public string Command { get; set; } = string.Empty;

        /// <summary>
        /// 命令行参数（兼容字符串和数组两种格式）。
        /// Claude Desktop 格式: "args": ["--from", "paddleocr-mcp", "paddleocr_mcp"]
        /// 自定义格式:       "args": "-y @anthropic/mcp-filesystem C:\\"
        /// </summary>
        [JsonConverter(typeof(StringOrArrayJsonConverter))]
        public string Args { get; set; } = string.Empty;

        /// <summary>HTTP URL（http 传输）</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>是否启用此服务器</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>环境变量，扁平格式: KEY1=VALUE1;KEY2=VALUE2（旧版兼容）</summary>
        public string Environment { get; set; } = string.Empty;

        /// <summary>环境变量，字典格式（Claude Desktop / VS Code 兼容）</summary>
        public Dictionary<string, string>? Env { get; set; }

        /// <summary>
        /// 获取统一的环境变量字典（合并多来源）。
        /// 优先级: Env 字典 > Environment 扁平字符串
        /// </summary>
        public Dictionary<string, string> GetResolvedEnvVars()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 先解析扁平字符串格式
            if (!string.IsNullOrWhiteSpace(Environment))
            {
                foreach (var pair in Environment.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var eqIndex = pair.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        var key = pair.Substring(0, eqIndex).Trim();
                        var value = pair.Substring(eqIndex + 1).Trim();
                        result[key] = value;
                    }
                }
            }

            // 后覆盖字典格式（优先级更高）
            if (Env != null)
            {
                foreach (var kvp in Env)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key))
                        result[kvp.Key] = kvp.Value ?? string.Empty;
                }
            }

            return result;
        }

        /// <summary>
        /// 获取统一的命令行参数字符串。
        /// Args 字段已通过 StringOrArrayJsonConverter 统一为字符串格式。
        /// </summary>
        public string GetResolvedArgs()
        {
            return Args ?? string.Empty;
        }
    }

    #endregion

    #region 配置解析（多格式兼容）

    /// <summary>
    /// Claude Desktop 格式的 MCP 配置根对象。
    /// {"mcpServers": { "serverName": { "command": "...", "args": [...], "env": {...} } }}
    /// </summary>
    public class ClaudeMcpConfig
    {
        [JsonPropertyName("mcpServers")]
        public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
    }

    /// <summary>
    /// VS Code / Cursor 格式的 MCP 配置根对象。
    /// {"mcpServers": { "serverName": { ... } }}
    /// 与 Claude Desktop 格式相同。
    /// </summary>
    public class VSCodeMcpConfig
    {
        [JsonPropertyName("mcpServers")]
        public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
    }

    /// <summary>
    /// 简化数组格式的 MCP 配置。
    /// [{"Name": "server1", "Command": "npx", ...}, ...]
    /// </summary>
    public class SimpleMcpConfigList
    {
        public List<McpServerConfig> Servers { get; set; } = new();
    }

    /// <summary>
    /// 多格式 MCP 配置解析器。
    /// 支持三种格式：
    /// 1. Claude Desktop / VS Code 格式: {"mcpServers": { "name": {...}, ... }}
    /// 2. 简化数组格式: [ { "Name": "...", "Command": "...", ... }, ... ]
    /// 3. 键值对象格式: { "serverName": { "command": "...", ... } }
    /// </summary>
    public static class McpConfigParser
    {
        /// <summary>
        /// 从 JSON 字符串解析 MCP 服务器配置列表。
        /// 自动检测格式：
        /// - 以 [ 开头 → 数组格式
        /// - 包含 "mcpServers" → Claude Desktop / VS Code 格式
        /// - 以 { 开头 → 键值对象格式
        /// </summary>
        public static List<McpServerConfig> Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<McpServerConfig>();

            json = json.Trim();

            List<McpServerConfig> result;

            // ── 格式 1: Claude Desktop / VS Code {"mcpServers": {...}} ──
            if (json.StartsWith("{") && json.Contains("\"mcpServers\""))
            {
                result = ParseClaudeFormat(json);
            }
            // ── 格式 2: 数组 [{...}, ...] ──
            else if (json.StartsWith("["))
            {
                result = ParseArrayFormat(json);
            }
            // ── 格式 3: 键值对象 {"serverName": {...}} ──
            else if (json.StartsWith("{"))
            {
                result = ParseKeyValueFormat(json);
            }
            else
            {
                return new List<McpServerConfig>();
            }

            // ── 智能传输检测：有 URL 无 Command → 自动切换为 HTTP 传输 ──
            foreach (var config in result)
            {
                NormalizeTransport(config);
            }

            return result;
        }

        /// <summary>
        /// 智能检测传输类型。
        /// 如果配置了 URL 但没有指定 Command（且 Transport 仍为默认 stdio），
        /// 自动切换为 HTTP 传输。
        /// 适用于 Claude Desktop 等只填 url 字段的简化配置。
        /// </summary>
        private static void NormalizeTransport(McpServerConfig config)
        {
            bool hasUrl = !string.IsNullOrWhiteSpace(config.Url);
            bool hasNoCommand = string.IsNullOrWhiteSpace(config.Command);
            bool isDefaultTransport = string.IsNullOrEmpty(config.Transport) ||
                                      config.Transport.Equals("stdio", StringComparison.OrdinalIgnoreCase);

            if (hasUrl && hasNoCommand && isDefaultTransport)
            {
                config.Transport = "http";
            }
        }

        private static List<McpServerConfig> ParseClaudeFormat(string json)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                };

                var config = JsonSerializer.Deserialize<ClaudeMcpConfig>(json, options);
                if (config?.McpServers == null) return new List<McpServerConfig>();

                // 把服务名注入到每个配置的 Name 字段
                foreach (var kvp in config.McpServers)
                {
                    if (string.IsNullOrEmpty(kvp.Value.Name))
                        kvp.Value.Name = kvp.Key;
                }

                return config.McpServers.Values.ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MCP Parser] Claude 格式解析失败: {ex.Message}");
                return new List<McpServerConfig>();
            }
        }

        private static List<McpServerConfig> ParseArrayFormat(string json)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                };

                var servers = JsonSerializer.Deserialize<List<McpServerConfig>>(json, options);
                return servers ?? new List<McpServerConfig>();
            }
            catch
            {
                return new List<McpServerConfig>();
            }
        }

        private static List<McpServerConfig> ParseKeyValueFormat(string json)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                };

                var dict = JsonSerializer.Deserialize<Dictionary<string, McpServerConfig>>(json, options);
                if (dict == null) return new List<McpServerConfig>();

                foreach (var kvp in dict)
                {
                    if (string.IsNullOrEmpty(kvp.Value.Name))
                        kvp.Value.Name = kvp.Key;
                }

                return dict.Values.ToList();
            }
            catch
            {
                return new List<McpServerConfig>();
            }
        }

        /// <summary>
        /// 将配置列表序列化为 Claude Desktop 兼容的 JSON 格式。
        /// </summary>
        public static string SerializeToClaudeFormat(List<McpServerConfig> servers)
        {
            var dict = new Dictionary<string, McpServerConfig>();
            foreach (var s in servers)
            {
                dict[s.Name] = s;
            }

            var wrapper = new ClaudeMcpConfig { McpServers = dict };
            return JsonSerializer.Serialize(wrapper, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
        }
    }

    #endregion

    #region JSON 转换器

    /// <summary>
    /// 灵活整数转换器：兼容 JSON 中的数字和字符串格式。
    /// JSON-RPC 2.0 规范允许 id 为 int 或 string。
    /// </summary>
    public class FlexibleIntConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return reader.GetInt32();
            if (reader.TokenType == JsonTokenType.String)
            {
                var str = reader.GetString();
                return int.TryParse(str, out var val) ? val : 0;
            }
            return 0;
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }

    /// <summary>
    /// 自定义 JSON 转换器：支持将 JSON 字符串或数组统一反序列化为字符串。
    /// 
    /// 用于 Args 字段：
    /// - 输入为字符串 "a b c" → 原样保留
    /// - 输入为数组 ["--from", "paddleocr-mcp", "paddleocr_mcp"] → 拼接为 "--from paddleocr-mcp paddleocr_mcp"
    /// 
    /// 序列化时始终输出为字符串格式。
    /// </summary>
    public class StringOrArrayJsonConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString() ?? string.Empty;
            }

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var items = new List<string>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                        break;
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        items.Add(reader.GetString() ?? string.Empty);
                    }
                }

                // 拼接时对含空格的参数加引号
                return string.Join(" ", items.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            }

            // 其他类型 → 跳过并返回空
            reader.Skip();
            return string.Empty;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }

    #endregion
}
