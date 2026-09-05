using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Services.InlineEdit
{
    /// <summary>
    /// Inline Edit 请求（P1-B）。
    /// 非 Agent 直改路径：选区 + 指令 → LLM 单次调用 → 替换文本。
    /// </summary>
    public sealed class InlineEditRequest
    {
        public string FilePath { get; set; } = string.Empty;

        /// <summary>Markdown 代码围栏语言标记（如 "cpp"/"csharp"，用于提示模型语言）</summary>
        public string FenceLanguage { get; set; } = string.Empty;

        public string UserInstruction { get; set; } = string.Empty;

        public string SelectedText { get; set; } = string.Empty;

        public string BeforeContext { get; set; } = string.Empty;

        public string AfterContext { get; set; } = string.Empty;
    }

    /// <summary>Inline Edit 结果。</summary>
    public sealed class InlineEditResult
    {
        public bool Success { get; private init; }
        public string? Replacement { get; private init; }
        public string? Error { get; private init; }
        public bool WasCancelled { get; private init; }

        public static InlineEditResult Ok(string replacement) =>
            new() { Success = true, Replacement = replacement };

        public static InlineEditResult Fail(string error) =>
            new() { Success = false, Error = error };

        public static InlineEditResult Cancelled() =>
            new() { Success = false, WasCancelled = true };
    }

    /// <summary>
    /// Inline Edit 服务（P1-B，序号 14-19）：
    /// 选区 + 指令 → 单次 LLM 调用（不走 Agent 工具循环）→ 返回替换文本。
    /// 预览/提交复用现有 InlineDiffSession 管线（EditorDiffMarkerService），
    /// 本服务只负责"生成替换内容"这一件事。
    /// </summary>
    public sealed class InlineEditService
    {
        internal const int MaxContextLines = 60;
        internal const int MaxContextChars = 3000;
        internal const int MaxSelectionChars = 12000;

        private const string SystemPrompt =
            "You are a precise code editing assistant embedded in Visual Studio. " +
            "Rewrite ONLY the SELECTED CODE according to the user's instruction.\n" +
            "Rules:\n" +
            "1. Output the complete replacement code and nothing else - no markdown fences, no explanations, no prose.\n" +
            "2. Preserve the original language, indentation style and naming conventions.\n" +
            "3. Never include the surrounding context lines in your output.";

        private readonly IDeepSeekApiService _api;

        public InlineEditService(IDeepSeekApiService api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
        }

        /// <summary>执行一次选区重写。永不抛出异常（结果携带错误信息）。</summary>
        public async Task<InlineEditResult> RewriteAsync(InlineEditRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var messages = new List<ChatApiMessage>
                {
                    new ChatApiMessage { Role = "system", Content = SystemPrompt },
                    new ChatApiMessage { Role = "user", Content = BuildUserPrompt(request) },
                };

                string raw = await _api.CompleteAsync(messages, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                string replacement = ExtractReplacement(raw);

                if (string.IsNullOrWhiteSpace(replacement))
                    return InlineEditResult.Fail(LocalizationService.Instance["inlineEdit.emptyResponse"]);

                if (string.Equals(replacement.TrimEnd(), request.SelectedText?.TrimEnd(), StringComparison.Ordinal))
                    return InlineEditResult.Fail(LocalizationService.Instance["inlineEdit.noChange"]);

                return InlineEditResult.Ok(replacement);
            }
            catch (OperationCanceledException)
            {
                return InlineEditResult.Cancelled();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[InlineEdit] 生成失败: {ex.Message}");
                return InlineEditResult.Fail(ex.Message);
            }
        }

        // ────────────────────────── 提示词构建 ──────────────────────────

        private static string BuildUserPrompt(InlineEditRequest r)
        {
            var sb = new System.Text.StringBuilder(1024);
            sb.Append("File: ").AppendLine(string.IsNullOrEmpty(r.FilePath) ? "(untitled)" : r.FilePath);
            if (!string.IsNullOrEmpty(r.FenceLanguage))
                sb.Append("Language: ").AppendLine(r.FenceLanguage);
            sb.AppendLine();

            sb.AppendLine("[Code before selection] (context only - do NOT include in output)");
            sb.AppendLine(r.BeforeContext.Length > 0 ? r.BeforeContext : "(none)");
            sb.AppendLine();
            sb.AppendLine("[SELECTED CODE - rewrite this]");
            sb.AppendLine(TruncateMiddle(r.SelectedText ?? "", MaxSelectionChars));
            sb.AppendLine();
            sb.AppendLine("[Code after selection] (context only - do NOT include in output)");
            sb.AppendLine(r.AfterContext.Length > 0 ? r.AfterContext : "(none)");
            sb.AppendLine();
            sb.Append("Instruction: ").AppendLine(r.UserInstruction);
            return sb.ToString();
        }

        private static string TruncateMiddle(string value, int maxChars)
        {
            if (value.Length <= maxChars) return value;
            int head = maxChars * 2 / 3;
            int tail = maxChars - head;
            return value.Substring(0, head) + "\n… (truncated) …\n" + value.Substring(value.Length - tail);
        }

        // ────────────────────────── 输出解析 ──────────────────────────

        /// <summary>
        /// 剥离模型输出中的 markdown 围栏；无围栏时返回原文（Trim 后）。
        /// </summary>
        internal static string ExtractReplacement(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string t = raw.Trim();

            int open = t.IndexOf("```", StringComparison.Ordinal);
            if (open >= 0)
            {
                int bodyStart = t.IndexOf('\n', open);
                if (bodyStart >= 0 && bodyStart + 1 <= t.Length)
                {
                    int close = t.IndexOf("```", bodyStart + 1);
                    string body = close > bodyStart
                        ? t.Substring(bodyStart + 1, close - bodyStart - 1)
                        : t.Substring(bodyStart + 1);
                    return body.Trim();
                }
            }
            return t;
        }
    }
}
