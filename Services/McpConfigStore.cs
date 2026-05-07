using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// MCP 服务器配置的持久化存储。
    /// 配置文件路径: %LocalAppData%\DeepSeekVS\mcp_servers.json
    /// 
    /// 支持 Claude Desktop / VS Code / 自定义格式的透明读写。
    /// </summary>
    public static class McpConfigStore
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekVS");

        private static readonly string ConfigFile = Path.Combine(ConfigDir, "mcp_servers.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>PP-OCRv5 默认配置（URL/Token 为空，需用户填写）</summary>
        public static readonly List<McpServerConfig> DefaultConfigs = new()
        {
            new McpServerConfig
            {
                Name = "PP-OCRv5",
                Command = "uvx",
                Args = "--from paddleocr-mcp paddleocr_mcp",
                Enabled = false,
                Env = new Dictionary<string, string>
                {
                    ["PADDLEOCR_MCP_PIPELINE"] = "OCR",
                    ["PADDLEOCR_MCP_PPOCR_SOURCE"] = "aistudio",
                    ["PADDLEOCR_MCP_SERVER_URL"] = "",
                    ["PADDLEOCR_MCP_AISTUDIO_ACCESS_TOKEN"] = "",
                }
            }
        };

        /// <summary>
        /// 加载 MCP 服务器配置列表。
        /// 如果文件不存在，自动创建默认配置。
        /// </summary>
        public static List<McpServerConfig> Load()
        {
            try
            {
                if (!File.Exists(ConfigFile))
                {
                    // 首次使用：写入默认配置
                    Save(DefaultConfigs);
                    return new List<McpServerConfig>(DefaultConfigs);
                }

                var json = File.ReadAllText(ConfigFile);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<McpServerConfig>(DefaultConfigs);

                // 使用 McpConfigParser 解析（支持多格式）
                var configs = McpConfigParser.Parse(json);
                return configs.Count > 0 ? configs : new List<McpServerConfig>(DefaultConfigs);
            }
            catch (Exception)
            {
                return new List<McpServerConfig>(DefaultConfigs);
            }
        }

        /// <summary>
        /// 保存 MCP 服务器配置列表（Claude Desktop 兼容格式）。
        /// </summary>
        public static void Save(List<McpServerConfig> configs)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);

                // 序列化为 Claude Desktop 兼容格式
                var dict = new Dictionary<string, McpServerConfig>();
                foreach (var s in configs)
                {
                    if (!string.IsNullOrEmpty(s.Name))
                        dict[s.Name] = s;
                }

                var wrapper = new { mcpServers = dict };
                var json = JsonSerializer.Serialize(wrapper, JsonOptions);
                File.WriteAllText(ConfigFile, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MCP Store] 保存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查配置文件是否存在。
        /// </summary>
        public static bool Exists() => File.Exists(ConfigFile);
    }
}
