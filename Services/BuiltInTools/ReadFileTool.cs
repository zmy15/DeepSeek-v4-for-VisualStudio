using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// read_file 工具 — 读取文件内容（支持行范围，带缓存）。
    /// </summary>
    public class ReadFileTool : BuiltInToolBase
    {
        private static LocalizationService L => LocalizationService.Instance;
        private readonly ConcurrentDictionary<string, string> _fileReadCache;

        public ReadFileTool(ConcurrentDictionary<string, string> fileReadCache)
        {
            _fileReadCache = fileReadCache ?? throw new ArgumentNullException(nameof(fileReadCache));
        }

        public override string Name => "read_file";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
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
                            filePath = new { type = "string", description = "要读取的文件的绝对路径（Windows 格式）" },
                            startLine = new { type = "integer", description = "起始行号（1-based），可选，默认为 1" },
                            endLine = new { type = "integer", description = "结束行号（1-based，包含），可选，默认读取到文件末尾" }
                        },
                        required = new[] { "filePath" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
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
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return "（无返回结果）";
            if (toolResult.StartsWith("❌")) return toolResult;

            var readLines = toolResult.Split('\n');
            string firstLine = readLines.Length > 0 ? readLines[0].Trim() : "";
            var lineCountMatch = System.Text.RegularExpressions.Regex.Match(
                firstLine, @"(?:共|total|总计)\s*(\d+)\s*(?:行|lines)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (lineCountMatch.Success)
                return $"✅ 读取完成 ({lineCountMatch.Groups[1].Value} 行)";
            return $"✅ 读取完成 ({readLines.Length} 行)";
        }

        public override Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");
            if (string.IsNullOrEmpty(filePath))
                return Task.FromResult("❌ read_file: 缺少 filePath 参数");

            // ── 缓存命中 ──
            if (_fileReadCache.TryGetValue(filePath, out string? cached))
                return Task.FromResult($"📄 [缓存] 文件: {filePath}\n\n{cached}");

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
                const int maxLinesToRead = 100000;
                int startLine = GetIntArg(args, "startLine", 1);
                int endLine = GetIntArg(args, "endLine", int.MaxValue);

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

                int actualEnd = Math.Min(endLine, totalLines);
                if (truncated)
                    actualEnd = Math.Min(actualEnd, maxLinesToRead);

                var resultBuilder = new StringBuilder();
                resultBuilder.AppendLine($"📄 文件: {filePath} (共 {totalLines} 行，显示 {startLine}-{actualEnd})");
                if (truncated)
                    resultBuilder.AppendLine($"> ⚠️ 文件过大（>{maxLinesToRead}行），仅读取了前 {maxLinesToRead} 行");
                resultBuilder.AppendLine();
                resultBuilder.Append(sb);

                string result = resultBuilder.ToString().TrimEnd();
                string rawContent = sb.ToString().TrimEnd();
                _fileReadCache.TryAdd(filePath, rawContent);

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                return Task.FromResult($"❌ 读取文件失败: {ex.Message}");
            }
        }
    }
}
