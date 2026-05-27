using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// 内置工具的抽象基类。
    /// 每个内置工具继承此类，实现自己的定义、执行逻辑和显示文本。
    /// </summary>
    public abstract class BuiltInToolBase
    {
        /// <summary>工具名称（与 Agent AllowedTools 白名单一致）</summary>
        public abstract string Name { get; }

        /// <summary>获取工具的 OpenAI Function Calling 格式定义</summary>
        public abstract ToolDefinition GetDefinition();

        /// <summary>执行工具，返回结果文本</summary>
        public abstract Task<string> ExecuteAsync(
            Dictionary<string, JsonElement> args, string? workspaceRoot);

        /// <summary>生成人类可读的工具调用描述（用于聊天 UI）</summary>
        public abstract string GetDisplayText(Dictionary<string, JsonElement> args);

        /// <summary>生成工具执行结果的简短摘要（用于聊天 UI）</summary>
        public abstract string GetResultSummary(string toolResult);

        #region Shared Helpers

        /// <summary>
        /// 规范化工作区根目录：如果是文件路径则取其目录。
        /// </summary>
        protected static string? NormalizeWorkspaceRoot(string? workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot))
                return null;

            try
            {
                if (File.Exists(workspaceRoot))
                    return Path.GetDirectoryName(workspaceRoot);
                if (Directory.Exists(workspaceRoot))
                    return workspaceRoot;
            }
            catch { }

            return workspaceRoot;
        }

        /// <summary>
        /// 解析文件路径（支持相对于工作区根目录的路径）。
        /// 包含路径穿越防护。
        /// </summary>
        protected static string ResolvePath(string filePath, string? workspaceRoot)
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

            // ── 路径穿越防护 ──
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
                    System.Diagnostics.Debug.WriteLine(
                        $"[BuiltInTool] ⚠️ 路径穿越检测: {resolved} 不在工作区 {workspaceRoot} 内，拒绝访问");
                    return filePath;
                }
            }

            return resolved;
        }

        protected static string GetStringArg(Dictionary<string, JsonElement> args, string key)
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

        protected static int GetIntArg(Dictionary<string, JsonElement> args, string key, int defaultValue)
        {
            if (args.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.Number)
            {
                try { return element.GetInt32(); }
                catch { return defaultValue; }
            }
            return defaultValue;
        }

        protected static bool GetBoolArg(Dictionary<string, JsonElement> args, string key)
        {
            if (args.TryGetValue(key, out var element))
            {
                if (element.ValueKind == JsonValueKind.True) return true;
                if (element.ValueKind == JsonValueKind.False) return false;
            }
            return false;
        }

        protected static bool GetBoolArg(Dictionary<string, JsonElement> args, string key, bool defaultValue)
        {
            if (args.TryGetValue(key, out var element))
            {
                if (element.ValueKind == JsonValueKind.True) return true;
                if (element.ValueKind == JsonValueKind.False) return false;
            }
            return defaultValue;
        }

        protected static string[]? GetStringArrayArg(Dictionary<string, JsonElement> args, string key)
        {
            if (!args.TryGetValue(key, out var element))
                return null;
            if (element.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        list.Add(item.GetString() ?? "");
                }
                return list.ToArray();
            }
            return null;
        }

        /// <summary>截断路径显示，保留文件名</summary>
        protected static string TruncatePath(string path, int maxLen = 50)
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

        /// <summary>截断文本</summary>
        protected static string TruncateText(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
                return text;
            return text.Substring(0, maxLen) + "…";
        }

        #endregion
    }
}
