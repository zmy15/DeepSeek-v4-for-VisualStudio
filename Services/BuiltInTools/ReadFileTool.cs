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
    /// 
    /// v1.1.10: 内置分页上限，默认 200 行/16KB，硬上限 500 行。
    /// 大文件返回结构化标记以支持分页续读（参考 CodeWhale read_file 设计）。
    /// </summary>
    public class ReadFileTool : BuiltInToolBase
    {
        private readonly ConcurrentDictionary<string, FileReadCacheEntry> _fileReadCache;

        /// <summary>
        /// 活跃文件追踪器（可选注入，用于 Working Set 追踪）。
        /// </summary>
        public IActiveFileTracker? ActiveFileTracker { get; set; }

        /// <summary>
        /// 当前 API 请求轮次号（由 BuiltInToolService 同步设置）。
        /// </summary>
        public int CurrentRound { get; set; }

        /// <summary>
        /// 缓存轮数阈值：经过此轮数后允许重新读取文件。
        /// </summary>
        public int RoundThreshold { get; set; } = 10;

        // ── 分页常量（参考 CodeWhale file.rs）──
        /// <summary>默认最大返回行数（无 startLine/endLine 参数时）</summary>
        public const int DefaultMaxLines = 200;
        /// <summary>硬上限行数（endLine 参数夹紧到此值）</summary>
        public const int HardMaxLines = 500;
        /// <summary>可见内容字节上限（UTF-8，含行号前缀）</summary>
        public const int MaxVisibleBytes = 16 * 1024;
        /// <summary>小文件快速通道：行数阈值</summary>
        public const int SmallFileLinesThreshold = 200;
        /// <summary>小文件快速通道：字节阈值</summary>
        public const int SmallFileBytesThreshold = 16 * 1024;

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
                            endLine = new { type = "integer", description = LocalizationService.Instance["tool.readFile.param.endLine"] + $" (clamped to startLine+{HardMaxLines} max)" },
                            maxLines = new { type = "integer", description = LocalizationService.Instance["tool.readFile.param.maxLines"] ?? $"Max lines to return (default {DefaultMaxLines}, max {HardMaxLines})" }
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
                        // ── 请求范围已被缓存覆盖 → 直接从缓存返回内容，避免重复磁盘读取 ──
                        //     修复：之前返回 "已缓存，请勿重复读取" 警告，导致不同 Explore 子代理
                        //     （串行执行时）无法获取前一个子代理已读取的文件内容。
                        //     现在直接返回缓存中的实际内容，带 cached="true" 标记，
                        //     既避免磁盘 I/O，又确保后续子代理能获得所需数据。
                        string cachedRangedContent = FormatLinesFromFullContent(cachedFull, reqStartLine, reqActualEnd);
                        int cacheShownStart = reqStartLine;
                        int cacheShownEnd = reqActualEnd;
                        bool cacheTruncated = cacheShownEnd < cachedTotalLines;
                        string roundHint = CurrentRound > 0 && lastRound > 0 && lastRound <= CurrentRound
                            ? $" (from cache, {roundsSinceLastRead} rounds ago)"
                            : " (from cache)";
                        var cachedSb = new StringBuilder();
                        cachedSb.AppendLine($"<file path=\"{filePath}\" total_lines=\"{cachedTotalLines}\" shown_lines=\"{cacheShownStart}-{cacheShownEnd}\" truncated=\"{cacheTruncated.ToString().ToLowerInvariant()}\" cached=\"true\" cache_note=\"{roundHint}\">");
                        cachedSb.Append(cachedRangedContent);
                        if (cacheTruncated)
                            cachedSb.AppendLine($"\n[TRUNCATED] Showing lines {cacheShownStart}-{cacheShownEnd} of {cachedTotalLines}. To continue, call read_file with path=\"{filePath}\" start_line={cacheShownEnd + 1}");
                        cachedSb.Append("</file>");
                        return Task.FromResult(cachedSb.ToString());
                    }

                    // 请求范围未被覆盖 → 从缓存中提取新范围，更新已读范围
                    string rangedContent = FormatLinesFromFullContent(cachedFull, reqStartLine, reqActualEnd);
                    var updatedEntry = cachedEntry;
                    updatedEntry.AddRange(reqStartLine, reqActualEnd);
                    updatedEntry.LastReadRound = CurrentRound;
                    _fileReadCache.TryUpdate(filePath, updatedEntry, cachedEntry);

                    // 注册活跃文件访问
                    ActiveFileTracker?.ObserveRead(filePath, Name, CurrentRound);

                    // 构建结构化返回格式（缓存命中 + 新范围）
                    int shownStart = reqStartLine;
                    int shownEnd = reqActualEnd;
                    bool truncated = shownEnd < cachedTotalLines;
                    var sb = new StringBuilder();
                    sb.AppendLine($"<file path=\"{filePath}\" total_lines=\"{cachedTotalLines}\" shown_lines=\"{shownStart}-{shownEnd}\" truncated=\"{truncated.ToString().ToLowerInvariant()}\" cached=\"true\">");
                    sb.Append(rangedContent);
                    if (truncated)
                        sb.AppendLine($"\n[TRUNCATED] Showing lines {shownStart}-{shownEnd} of {cachedTotalLines}. To continue, call read_file with path=\"{filePath}\" start_line={shownEnd + 1}");
                    sb.Append("</file>");
                    return Task.FromResult(sb.ToString());
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

                // 注册活跃文件访问
                ActiveFileTracker?.ObserveRead(filePath, Name, CurrentRound);

                int cShownEnd = changedActualEnd;
                bool cTruncated = cShownEnd < changedTotalLines;
                var csb = new StringBuilder();
                csb.AppendLine($"<file path=\"{filePath}\" total_lines=\"{changedTotalLines}\" shown_lines=\"{reqStartLine}-{cShownEnd}\" truncated=\"{cTruncated.ToString().ToLowerInvariant()}\" changed=\"true\">");
                csb.Append(changedContent);
                if (cTruncated)
                    csb.AppendLine($"\n[TRUNCATED] File changed. Showing lines {reqStartLine}-{cShownEnd} of {changedTotalLines}.");
                csb.Append("</file>");
                return Task.FromResult(csb.ToString());
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
                // 读取完整文件内容用于缓存
                string fullContent = File.ReadAllText(filePath);
                int totalLines = fullContent.Count(c => c == '\n') + 1;
                int totalBytes = Encoding.UTF8.GetByteCount(fullContent);

                // ── 小文件快速通道：无显式范围参数且文件在阈值内 → 原样返回，不包装 ──
                bool hasExplicitRange = args.ContainsKey("startLine") || args.ContainsKey("endLine");
                if (!hasExplicitRange && totalLines <= SmallFileLinesThreshold && totalBytes <= SmallFileBytesThreshold)
                {
                    // 注册活跃文件访问
                    ActiveFileTracker?.ObserveRead(filePath, Name, CurrentRound);

                    // 缓存完整内容
                    _fileReadCache.TryAdd(filePath,
                        new FileReadCacheEntry
                        {
                            FullContent = fullContent,
                            ReadRanges = new List<(int, int)> { (1, totalLines) },
                            LastReadRound = CurrentRound
                        });

                    return Task.FromResult(fullContent);
                }

                // ── 大文件 / 显式范围 → 分页模式 ──
                int maxLines = GetIntArg(args, "maxLines", DefaultMaxLines);
                maxLines = Math.Max(1, Math.Min(maxLines, HardMaxLines));

                // 计算实际显示范围
                int shownStart = reqStartLine;
                int shownEnd = Math.Min(reqStartLine + maxLines - 1, totalLines);

                if (shownStart > totalLines)
                {
                    // 起始行超出文件范围 → 空内容哨兵
                    return Task.FromResult(
                        $"<file path=\"{filePath}\" total_lines=\"{totalLines}\" shown_lines=\"none\" truncated=\"false\">\n" +
                        $"\n[NO CONTENT] start_line {shownStart} is beyond total_lines {totalLines}.\n" +
                        $"</file>");
                }

                // 提取行范围内容（带行号前缀）
                string rangedContent = FormatLinesFromFullContent(fullContent, shownStart, shownEnd);

                // 字节截断检查
                int rangedBytes = Encoding.UTF8.GetByteCount(rangedContent);
                bool truncatedByBytes = rangedBytes > MaxVisibleBytes;
                if (truncatedByBytes)
                {
                    // UTF-8 安全截断
                    string truncatedContent = TruncateUtf8Safe(rangedContent, MaxVisibleBytes);
                    rangedContent = truncatedContent;
                }

                bool truncatedByLines = shownEnd < totalLines;
                bool truncated = truncatedByLines || truncatedByBytes;
                int nextStart = shownEnd + 1;

                // ── 构建结构化返回格式（参考 CodeWhale）──
                var resultBuilder = new StringBuilder();

                // 文件元数据行
                resultBuilder.Append($"<file path=\"{filePath}\" total_lines=\"{totalLines}\" shown_lines=\"{shownStart}-{shownEnd}\" truncated=\"{truncated.ToString().ToLowerInvariant()}\"");
                if (truncatedByLines)
                    resultBuilder.Append($" next_start_line=\"{nextStart}\"");
                resultBuilder.AppendLine(">");

                // 文件内容
                resultBuilder.Append(rangedContent);

                // 截断提示
                if (truncatedByLines)
                {
                    resultBuilder.AppendLine();
                    resultBuilder.Append($"[TRUNCATED] Showing lines {shownStart}-{shownEnd} of {totalLines}. ");
                    resultBuilder.Append($"To continue, call read_file with path=\"{filePath}\" start_line={nextStart} max_lines={maxLines}");
                    resultBuilder.AppendLine();
                }
                if (truncatedByBytes)
                {
                    resultBuilder.AppendLine();
                    resultBuilder.AppendLine($"[TRUNCATED] The selected range exceeded {MaxVisibleBytes / 1024}KB. Continue with a smaller max_lines value.");
                }

                resultBuilder.Append("</file>");

                string result = resultBuilder.ToString();

                // ── 缓存完整内容 + 已读范围 ──
                // 注意：缓存存储的是完整文件内容（无行号前缀），而非分页内容
                _fileReadCache.TryAdd(filePath,
                    new FileReadCacheEntry
                    {
                        FullContent = fullContent,
                        ReadRanges = new List<(int, int)> { (shownStart, shownEnd) },
                        LastReadRound = CurrentRound
                    });

                // 注册活跃文件访问
                ActiveFileTracker?.ObserveRead(filePath, Name, CurrentRound);

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
                sb.AppendLine($"{i +1,6}│ {line}");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// UTF-8 安全的字符串截断：确保不会在代理对或组合字符中间切断。
        /// </summary>
        /// <param name="text">原始文本</param>
        /// <param name="maxBytes">最大字节数</param>
        /// <returns>截断后的文本</returns>
        private static string TruncateUtf8Safe(string text, int maxBytes)
        {
            int byteCount = 0;
            int charIndex = 0;
            foreach (char c in text)
            {
                int charBytes = Encoding.UTF8.GetByteCount(new[] { c });
                if (byteCount + charBytes > maxBytes)
                    break;
                byteCount += charBytes;
                charIndex++;
            }
            return text.Substring(0, charIndex);
        }
    }
}
