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
                            filePath = new { type = "string", description = LocalizationService.Instance["tool.readFile.param.filePath"] },
                            startLine = new { type = "integer", description = LocalizationService.Instance["tool.readFile.param.startLine"] },
                            endLine = new { type = "integer", description = LocalizationService.Instance["tool.readFile.param.endLine"] }
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
                return LocalizationService.Instance.Format("tool.readFile.displayTextRange", fileName, sLine, eLine);
            else if (sLine > 0)
                return LocalizationService.Instance.Format("tool.readFile.displayTextFrom", fileName, sLine);
            else
                return string.IsNullOrEmpty(filePath)
                    ? LocalizationService.Instance["tool.readFile.readingFile"]
                    : LocalizationService.Instance.Format("tool.readFile.readingFileName", fileName);
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("❌")) return toolResult;

            var readLines = toolResult.Split('\n');
            string firstLine = readLines.Length > 0 ? readLines[0].Trim() : "";
            var lineCountMatch = System.Text.RegularExpressions.Regex.Match(
                firstLine, @"(?:共|total|总计)\s*(\d+)\s*(?:行|lines)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (lineCountMatch.Success)
                return LocalizationService.Instance.Format("tool.readFile.readCompleteLines", lineCountMatch.Groups[1].Value);
            return LocalizationService.Instance.Format("tool.readFile.readCompleteLines", readLines.Length);
        }

        public override Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string filePath = GetStringArg(args, "filePath");
            if (string.IsNullOrEmpty(filePath))
                return Task.FromResult(LocalizationService.Instance["tool.readFile.missingParam"]);

            int reqStartLine = GetIntArg(args, "startLine", 1);
            int reqEndLine = GetIntArg(args, "endLine", int.MaxValue);

            // ── 缓存命中 ──
            if (_fileReadCache.TryGetValue(filePath, out FileReadCacheEntry cachedEntry))
            {
                string cachedFull = cachedEntry.FullContent;
                int lastRound = cachedEntry.LastReadRound;
                bool fileChanged = false;

                try
                {
                    if (File.Exists(filePath))
                    {
                        string currentContent = File.ReadAllText(filePath);
                        if (currentContent != cachedFull)
                        {
                            fileChanged = true;
                            // 文件已变更 → 更新 FullContent，清空已读范围
                            _fileReadCache.TryUpdate(filePath,
                                new FileReadCacheEntry
                                {
                                    FullContent = currentContent,
                                    ReadRanges = null,
                                    LastReadRound = lastRound
                                },
                                cachedEntry);
                            cachedFull = currentContent;
                        }
                    }
                    else
                    {
                        _fileReadCache.TryRemove(filePath, out _);
                        return Task.FromResult(LocalizationService.Instance.Format("tool.readFile.fileNotFound", filePath));
                    }
                }
                catch { /* 读取失败时不阻塞，使用缓存内容 */ }

                if (!fileChanged)
                {
                    // ── 轮数阈值检查：过期后清空已读范围，允许重新读取 ──
                    // 如果 lastRound > CurrentRound，说明轮次计数器已重置（新会话），
                    // 缓存条目来自旧会话，应视为过期并立即允许重读
                    int roundsSinceLastRead;
                    bool staleCache = CurrentRound > 0 && lastRound > 0 && lastRound > CurrentRound;
                    if (CurrentRound > 0 && lastRound > 0 && lastRound <= CurrentRound)
                        roundsSinceLastRead = CurrentRound - lastRound;
                    else if (staleCache)
                        roundsSinceLastRead = int.MaxValue; // 旧会话缓存，强制过期
                    else
                        roundsSinceLastRead = 0;
                    if (RoundThreshold > 0 && roundsSinceLastRead >= RoundThreshold)
                    {
                        // 轮数过期 → 清空已读范围，允许重读
                        int totalLines = cachedFull.Count(c => c == '\n') + 1;
                        int actualEnd = Math.Min(reqEndLine, totalLines);
                        string freshContent = FormatLinesFromFullContent(cachedFull, reqStartLine, actualEnd);
                        _fileReadCache.TryUpdate(filePath,
                            new FileReadCacheEntry
                            {
                                FullContent = cachedFull,
                                ReadRanges = new List<(int, int)> { (reqStartLine, actualEnd) },
                                LastReadRound = CurrentRound
                            },
                            cachedEntry);
                        var fname = Path.GetFileName(filePath);
                        return Task.FromResult(staleCache
                            ? LocalizationService.Instance.Format("tool.readFile.cacheExpiredStale", fname, filePath, totalLines, reqStartLine, actualEnd, freshContent)
                            : LocalizationService.Instance.Format("tool.readFile.cacheExpiredRound", roundsSinceLastRead, RoundThreshold, fname, filePath, totalLines, reqStartLine, actualEnd, freshContent));
                    }

                    // ── 行范围覆盖检查 ──
                    int cachedTotalLines = cachedFull.Count(c => c == '\n') + 1;
                    int reqActualEnd = Math.Min(reqEndLine, cachedTotalLines);
                    if (cachedEntry.IsRangeCovered(reqStartLine, reqActualEnd))
                    {
                        // 请求范围已被之前读取覆盖 → 拦截重复读取
                        int cachedLineCount = cachedTotalLines;
                        int cachedCharCount = cachedFull.Length;
                        string roundHint = CurrentRound > 0 && lastRound > 0 && lastRound <= CurrentRound
                            ? $"  (距今 {roundsSinceLastRead} 轮，还需 {Math.Max(0, RoundThreshold - roundsSinceLastRead)} 轮后可重读)"
                            : "";
                        return Task.FromResult(
                            LocalizationService.Instance.Format("tool.readFile.cachedWarningFull", Path.GetFileName(filePath), cachedLineCount, cachedCharCount, reqStartLine, reqActualEnd, roundHint));
                    }

                    // 请求范围未被覆盖 → 从缓存中提取新范围，更新已读范围
                    string rangedContent = FormatLinesFromFullContent(cachedFull, reqStartLine, reqActualEnd);
                    var updatedEntry = cachedEntry;
                    updatedEntry.AddRange(reqStartLine, reqActualEnd);
                    updatedEntry.LastReadRound = CurrentRound;
                    _fileReadCache.TryUpdate(filePath, updatedEntry, cachedEntry);
                    return Task.FromResult(
                        LocalizationService.Instance.Format("tool.readFile.cachedContent", filePath, cachedTotalLines, reqStartLine, reqActualEnd, rangedContent));
                }

                // 文件已变更 → 返回完整最新内容（已读范围已清空，重新开始追踪）
                int changedTotalLines = cachedFull.Count(c => c == '\n') + 1;
                int changedActualEnd = Math.Min(reqEndLine, changedTotalLines);
                string changedContent = FormatLinesFromFullContent(cachedFull, reqStartLine, changedActualEnd);
                _fileReadCache.TryUpdate(filePath,
                    new FileReadCacheEntry
                    {
                        FullContent = cachedFull,
                        ReadRanges = new List<(int, int)> { (reqStartLine, changedActualEnd) },
                        LastReadRound = CurrentRound
                    },
                    cachedEntry);
                return Task.FromResult(
                    LocalizationService.Instance.Format("tool.readFile.fileChanged", Path.GetFileName(filePath), filePath, changedTotalLines, reqStartLine, changedActualEnd, changedContent));
            }

            // ── 首次读取（缓存未命中）──
            if (!File.Exists(filePath))
            {
                string wsHint = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
                    ? "\n" + LocalizationService.Instance.Format("tool.readFile.workspaceHint", workspaceRoot)
                    : "";
                wsHint += "\n" + LocalizationService.Instance["tool.readFile.newFileHint"];
                return Task.FromResult(LocalizationService.Instance.Format("tool.readFile.fileNotFound", filePath) + wsHint);
            }

            try
            {
                const int maxLinesToRead = 100000;

                // 读取完整文件内容用于缓存
                string fullContent = File.ReadAllText(filePath);
                int totalLines = fullContent.Count(c => c == '\n') + 1;
                bool truncated = totalLines > maxLinesToRead;

                int actualEnd = Math.Min(reqEndLine, totalLines);
                if (truncated)
                    actualEnd = Math.Min(actualEnd, maxLinesToRead);

                // 提取请求行范围（带行号前缀）
                string rangedContent = FormatLinesFromFullContent(fullContent, reqStartLine, actualEnd);

                var resultBuilder = new StringBuilder();
                resultBuilder.AppendLine(LocalizationService.Instance.Format("tool.readFile.fileHeader", filePath, totalLines, reqStartLine, actualEnd));
                if (truncated)
                    resultBuilder.AppendLine(LocalizationService.Instance.Format("tool.readFile.fileTooLarge", maxLinesToRead));
                resultBuilder.AppendLine();
                resultBuilder.Append(rangedContent);

                string result = resultBuilder.ToString().TrimEnd();

                // 缓存：存储完整文件内容（截断后）+ 已读范围
                string contentToCache = truncated
                    ? string.Join("\n", File.ReadLines(filePath).Take(maxLinesToRead))
                    : fullContent;
                _fileReadCache.TryAdd(filePath,
                    new FileReadCacheEntry
                    {
                        FullContent = contentToCache,
                        ReadRanges = new List<(int, int)> { (reqStartLine, actualEnd) },
                        LastReadRound = CurrentRound
                    });

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                return Task.FromResult(LocalizationService.Instance.Format("tool.readFile.failed", ex.Message));
            }
        }

        /// <summary>
        /// 从完整文件内容中提取指定行范围，并添加行号前缀。
        /// </summary>
        private static string FormatLinesFromFullContent(string fullContent, int startLine, int endLine)
        {
            var lines = fullContent.Split('\n');
            var sb = new StringBuilder();
            int maxLine = Math.Min(endLine, lines.Length);
            for (int i = startLine - 1; i < maxLine; i++)
            {
                // 去掉 \r 尾巴
                string line = lines[i].TrimEnd('\r');
                sb.AppendLine($"{i + 1}: {line}");
            }
            return sb.ToString().TrimEnd();
        }
    }
}
