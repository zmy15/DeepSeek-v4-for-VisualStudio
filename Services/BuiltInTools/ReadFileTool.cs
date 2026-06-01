using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private readonly ConcurrentDictionary<string, FileReadCacheEntry> _fileReadCache;

        /// <summary>
        /// 当前 API 请求轮次号（由 BuiltInToolService 同步设置）。
        /// </summary>
        public int CurrentRound { get; set; }

        /// <summary>
        /// 缓存轮数阈值：经过此轮数后允许重新读取文件。
        /// </summary>
        public int RoundThreshold { get; set; } = 10;

        public ReadFileTool(ConcurrentDictionary<string, FileReadCacheEntry> fileReadCache)
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

            // ── 缓存命中 ── 比较磁盘文件是否已变更 ──
            if (_fileReadCache.TryGetValue(filePath, out FileReadCacheEntry cachedEntry))
            {
                string cached = cachedEntry.Content;
                int lastRound = cachedEntry.LastReadRound;
                bool fileChanged = false;
                try
                {
                    if (File.Exists(filePath))
                    {
                        string currentContent = File.ReadAllText(filePath);
                        if (currentContent != cached)
                        {
                            fileChanged = true;
                            // 更新缓存为最新内容，保持原轮次不变
                            _fileReadCache.TryUpdate(filePath,
                                new FileReadCacheEntry { Content = currentContent, LastReadRound = lastRound },
                                cachedEntry);
                            cached = currentContent;
                        }
                    }
                    else
                    {
                        // 文件已被删除，清除缓存
                        _fileReadCache.TryRemove(filePath, out _);
                        return Task.FromResult($"❌ 文件不存在: {filePath}");
                    }
                }
                catch { /* 读取失败时不阻塞，返回缓存摘要 */ }

                if (!fileChanged)
                {
                    // ── 轮数阈值检查：经过足够轮数后允许重新读取 ──
                    int roundsSinceLastRead = CurrentRound > 0 && lastRound > 0
                        ? CurrentRound - lastRound
                        : 0;
                    if (RoundThreshold > 0 && roundsSinceLastRead >= RoundThreshold)
                    {
                        // 轮数已达标 → 允许重新读取，更新轮次
                        _fileReadCache.TryUpdate(filePath,
                            new FileReadCacheEntry { Content = cached, LastReadRound = CurrentRound },
                            cachedEntry);
                        int cachedLineCount = cached.Count(c => c == '\n') + 1;
                        return Task.FromResult(
                            $"🔄 [轮数过期，允许重读] 文件 `{Path.GetFileName(filePath)}` 上次读取距今 {roundsSinceLastRead} 轮（阈值: {RoundThreshold} 轮），以下是最新内容：\n\n📄 文件: {filePath}\n\n{cached}");
                    }

                    // 文件未变更且轮数未达标 → 返回极简摘要，严禁重复消费 token
                    int cachedLineCount2 = cached.Count(c => c == '\n') + 1;
                    int cachedCharCount2 = cached.Length;
                    string roundHint = CurrentRound > 0 && lastRound > 0
                        ? $"  (距今 {roundsSinceLastRead} 轮，还需 {RoundThreshold - roundsSinceLastRead} 轮后可重读)"
                        : "";
                    return Task.FromResult(
                        $"⚡ [已缓存，请勿重复读取] 文件 `{Path.GetFileName(filePath)}`（{cachedLineCount2} 行，{cachedCharCount2} 字符）已在之前的 read_file 调用中完整读取。{roundHint}" +
                        $"\n\n💡 你已拥有此文件的全部内容，请直接基于已有内容进行分析，**无需再次调用 read_file** 读取此文件。");
                }

                // 文件已变更 → 允许重读，返回完整最新内容，更新轮次
                _fileReadCache.TryUpdate(filePath,
                    new FileReadCacheEntry { Content = cached, LastReadRound = CurrentRound },
                    cachedEntry);
                return Task.FromResult(
                    $"🔄 [文件已变更，重新读取] 文件 `{Path.GetFileName(filePath)}` 自上次读取后已被修改，以下是最新内容：\n\n📄 文件: {filePath}\n\n{cached}");
            }

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
                _fileReadCache.TryAdd(filePath,
                    new FileReadCacheEntry { Content = rawContent, LastReadRound = CurrentRound });

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                return Task.FromResult($"❌ 读取文件失败: {ex.Message}");
            }
        }
    }
}
