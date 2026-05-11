using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 上下文压缩服务 — 当 Token 预算接近上限时，将早期消息压缩为精简摘要，
    /// 替代直接删除旧消息，保留关键信息不丢失。
    /// 
    /// 压缩策略：
    /// 1. 保留最近 N 轮完整消息（由 PreserveRecentTurns 控制）
    /// 2. 更早的轮次按批压缩为摘要文本
    /// 3. 压缩摘要以 system 消息形式注入到对话中
    /// 4. 支持多次渐进压缩（压缩后的摘要可被再次压缩）
    /// </summary>
    public class ContextCompressorService
    {
        private readonly CompressionConfig _config;
        private readonly List<CompressedTurnSummary> _compressedSummaries = new();
        private readonly Func<string, CancellationToken, Task<string>>? _summarizer;

        /// <summary>已压缩的轮次摘要列表</summary>
        public IReadOnlyList<CompressedTurnSummary> CompressedSummaries => _compressedSummaries;

        /// <summary>压缩配置</summary>
        public CompressionConfig Config => _config;

        /// <summary>
        /// 创建压缩服务实例。
        /// </summary>
        /// <param name="summarizer">
        /// 摘要生成函数，接收待压缩文本和取消令牌，返回压缩后的摘要。
        /// 如果为 null，则使用基于规则的本地提取（无 LLM 调用）。
        /// </param>
        /// <param name="config">压缩配置，为 null 时使用默认配置</param>
        public ContextCompressorService(
            Func<string, CancellationToken, Task<string>>? summarizer = null,
            CompressionConfig? config = null)
        {
            _summarizer = summarizer;
            _config = config ?? new CompressionConfig();
        }

        /// <summary>
        /// 计算压缩后的摘要 Token 总数。
        /// </summary>
        public int TotalCompressedTokens => _compressedSummaries.Sum(s => s.CompressedTokens);

        /// <summary>
        /// 获取所有压缩摘要的合并文本（用于注入到 system 消息）。
        /// </summary>
        public string GetCompressedContextText()
        {
            if (_compressedSummaries.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("[对话历史摘要]");
            sb.AppendLine("以下是早期对话的压缩摘要，包含了之前讨论的关键信息和决策：");
            sb.AppendLine();

            foreach (var summary in _compressedSummaries.OrderBy(s => s.FromTurn))
            {
                sb.AppendLine($"--- 第 {summary.FromTurn}-{summary.ToTurn} 轮摘要 ---");
                sb.AppendLine(summary.Summary);
                sb.AppendLine();
            }

            sb.AppendLine("[/对话历史摘要]");
            return sb.ToString();
        }

        /// <summary>
        /// 压缩指定轮次的对话消息。
        /// </summary>
        /// <param name="turnsToCompress">待压缩的上下文条目（由 ConversationContextManager 提供）</param>
        /// <param name="fromTurn">起始轮次号</param>
        /// <param name="toTurn">结束轮次号</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>压缩结果</returns>
        internal async Task<CompressedTurnSummary> CompressTurnsAsync(
            List<ConversationContextManager.ContextEntry> turnsToCompress,
            int fromTurn,
            int toTurn,
            CancellationToken cancellationToken = default)
        {
            if (turnsToCompress == null || turnsToCompress.Count == 0)
                return new CompressedTurnSummary
                {
                    FromTurn = fromTurn,
                    ToTurn = toTurn,
                    Summary = "(空对话)",
                };

            // 1. 拼接待压缩的对话文本
            string rawText = FormatTurnsForCompression(turnsToCompress);
            int originalTokens = ConversationContextManager.EstimateTokens(rawText);

            // 2. 生成摘要
            string summary;
            if (_summarizer != null)
            {
                // 使用 LLM 生成摘要
                string prompt = string.Format(_config.CompressionPrompt, rawText);
                summary = await _summarizer(prompt, cancellationToken);
            }
            else
            {
                // 基于规则的本地提取（无 LLM 调用）
                summary = ExtractLocalSummary(rawText, turnsToCompress);
            }

            int compressedTokens = ConversationContextManager.EstimateTokens(summary);

            var result = new CompressedTurnSummary
            {
                Summary = summary,
                FromTurn = fromTurn,
                ToTurn = toTurn,
                OriginalTokens = originalTokens,
                CompressedTokens = compressedTokens,
            };

            _compressedSummaries.Add(result);

            Logger.Info($"[ContextCompressor] 压缩第 {fromTurn}-{toTurn} 轮: " +
                $"{originalTokens} → {compressedTokens} tokens " +
                $"(压缩率 {result.CompressionRatio:P0})");

            return result;
        }

        /// <summary>
        /// 清空所有压缩摘要。
        /// </summary>
        public void Clear()
        {
            _compressedSummaries.Clear();
        }

        #region Private Methods

        /// <summary>
        /// 将上下文条目格式化为适合压缩的文本。
        /// </summary>
        private static string FormatTurnsForCompression(List<ConversationContextManager.ContextEntry> entries)
        {
            var sb = new StringBuilder();
            foreach (var entry in entries)
            {
                string roleLabel = entry.Role switch
                {
                    "user" => "用户",
                    "assistant" => "助手",
                    "tool" => "工具结果",
                    _ => entry.Role,
                };

                sb.AppendLine($"[{roleLabel}]");
                if (!string.IsNullOrEmpty(entry.Content))
                {
                    // 截断过长的单条消息，保留前 2000 字符用于压缩
                    string content = entry.Content!.Length > 2000
                        ? entry.Content.Substring(0, 2000) + "..."
                        : entry.Content;
                    sb.AppendLine(content);
                }
                if (!string.IsNullOrEmpty(entry.ReasoningContent))
                {
                    string reasoning = entry.ReasoningContent!.Length > 500
                        ? entry.ReasoningContent.Substring(0, 500) + "..."
                        : entry.ReasoningContent;
                    sb.AppendLine($"[思考过程: {reasoning}]");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>
        /// 基于规则的本地摘要提取（无 LLM 调用）。
        /// 提取用户问题、关键文件名、代码片段、错误信息等。
        /// </summary>
        private static string ExtractLocalSummary(string rawText, List<ConversationContextManager.ContextEntry> entries)
        {
            var sb = new StringBuilder();

            // 提取用户消息的核心内容
            var userMessages = entries
                .Where(e => e.Role == "user" && !string.IsNullOrEmpty(e.Content))
                .ToList();

            if (userMessages.Count > 0)
            {
                sb.AppendLine("用户问题：");
                foreach (var msg in userMessages)
                {
                    string content = msg.Content ?? "";
                    // 提取第一行（通常是主要问题）
                    int newlineIdx = content.IndexOf('\n');
                    string firstLine = newlineIdx > 0
                        ? content.Substring(0, newlineIdx).Trim()
                        : content.Trim();
                    if (firstLine.Length > 150)
                        firstLine = firstLine.Substring(0, 150) + "...";
                    sb.AppendLine($"  • {firstLine}");
                }
                sb.AppendLine();
            }

            // 提取文件路径引用
            var filePattern = new System.Text.RegularExpressions.Regex(
                @"(?:📄\s*)?([\w\-./\\]+\.(?:cs|py|js|ts|java|cpp|c|h|xml|json|yaml|yml|md|sql|html|css|txt))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var files = new HashSet<string>();
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Content)) continue;
                foreach (System.Text.RegularExpressions.Match m in filePattern.Matches(entry.Content))
                {
                    files.Add(m.Groups[1].Value);
                }
            }
            if (files.Count > 0)
            {
                sb.AppendLine("涉及文件：");
                foreach (var f in files.Take(10))
                    sb.AppendLine($"  • {f}");
                sb.AppendLine();
            }

            // 提取错误信息
            var errorPattern = new System.Text.RegularExpressions.Regex(
                @"(?:error|Error|ERROR|Exception|异常|错误)[:：]\s*(.+?)(?:\n|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var errors = new HashSet<string>();
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Content)) continue;
                foreach (System.Text.RegularExpressions.Match m in errorPattern.Matches(entry.Content))
                {
                    string err = m.Groups[1].Value.Trim();
                    if (err.Length > 100) err = err.Substring(0, 100) + "...";
                    errors.Add(err);
                }
            }
            if (errors.Count > 0)
            {
                sb.AppendLine("错误/异常：");
                foreach (var e in errors.Take(5))
                    sb.AppendLine($"  • {e}");
                sb.AppendLine();
            }

            // 提取助手回答的结论（最后一条助手消息的末尾）
            var lastAssistant = entries.LastOrDefault(e => e.Role == "assistant" && !string.IsNullOrEmpty(e.Content));
            if (lastAssistant != null)
            {
                string content = lastAssistant.Content ?? "";
                if (content.Length > 300)
                    content = content.Substring(content.Length - 300);
                sb.AppendLine("最后结论：");
                sb.AppendLine($"  {content.Trim()}");
            }

            return sb.ToString().Trim();
        }

        #endregion
    }
}
