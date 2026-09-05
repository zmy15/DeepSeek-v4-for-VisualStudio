using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// 单条诊断摘要（P1-A：仅当前文件的编辑器 squiggle，深度查询仍走 get_errors 工具）。
    /// </summary>
    public sealed class IdeDiagnosticItem
    {
        /// <summary>"error" 或 "warning"</summary>
        public string Severity { get; set; } = "error";

        /// <summary>行号（1-based）</summary>
        public int Line { get; set; }

        /// <summary>消息（已截断）</summary>
        public string? Message { get; set; }
    }

    /// <summary>
    /// 调试器局部值条目（只读展示；取值经 EnvDTE 求值，逐项容错）。
    /// </summary>
    public sealed class IdeDebuggerValue
    {
        public string? Name { get; set; }

        /// <summary>求值结果文本（已截断；不可求值时为占位符）</summary>
        public string? Value { get; set; }
    }

    /// <summary>
    /// 调试器断点暂停态快照（当前栈帧级别，只读、有界）。
    /// null = 调试器未处于中断状态或捕获失败。
    /// </summary>
    public sealed class IdeDebuggerFrame
    {
        /// <summary>当前栈帧函数名</summary>
        public string? Function { get; set; }

        /// <summary>源文件路径（可空：动态代码/优化帧可能缺失）</summary>
        public string? File { get; set; }

        /// <summary>行号（1-based；未知为 0）</summary>
        public int Line { get; set; }

        /// <summary>栈帧局部变量（已截断至捕获上限）</summary>
        public List<IdeDebuggerValue> Locals { get; } = new();

        public bool HasContent =>
            !string.IsNullOrWhiteSpace(Function) || !string.IsNullOrWhiteSpace(File) || Locals.Count > 0;
    }

    /// <summary>
    /// IDE 实时态快照（P1-A）。
    /// 每次用户发送消息时由 IdeContextTracker 在 UI 线程捕获一次，
    /// 经 <see cref="ToPromptBlock"/> 格式化后注入 volatile 块 —— 不触碰稳定前缀，保护 Prefix Cache。
    ///
    /// 职责边界（报告 §10）：IDE Context = 快速定位；完整错误查询 = get_errors 工具。
    /// </summary>
    public sealed class IdeContextSnapshot
    {
        /// <summary>活动文档绝对路径（null = 无活动文本视图）</summary>
        public string? FilePath { get; set; }

        /// <summary>当前选区文本（null = 无选区）</summary>
        public string? SelectionText { get; set; }

        /// <summary>选区起始行（1-based；无选区为 0）</summary>
        public int SelectionStartLine { get; set; }

        /// <summary>选区结束行（1-based；无选区为 0）</summary>
        public int SelectionEndLine { get; set; }

        /// <summary>光标行（1-based）</summary>
        public int CursorLine { get; set; }

        /// <summary>光标列（1-based）</summary>
        public int CursorColumn { get; set; }

        /// <summary>光标处标识符（启发式提取，非语义解析）</summary>
        public string? SymbolAtCursor { get; set; }

        /// <summary>光标所在行原文（截断后）</summary>
        public string? SymbolLineText { get; set; }

        /// <summary>当前文件诊断摘要（squiggle 级别）</summary>
        public List<IdeDiagnosticItem> Diagnostics { get; } = new();

        /// <summary>
        /// 调试器断点暂停态快照（null = 未中断）。
        /// 断点命中时即便无编辑器视图也可独立构成注入内容。
        /// </summary>
        public IdeDebuggerFrame? DebuggerFrame { get; set; }

        /// <summary>捕获时间</summary>
        public DateTime CapturedAt { get; set; }

        public bool HasSelection => !string.IsNullOrWhiteSpace(SelectionText);
        public int ErrorCount => Diagnostics.Count(d => d.Severity == "error");
        public int WarningCount => Diagnostics.Count(d => d.Severity == "warning");
        public bool HasDebuggerFrame => DebuggerFrame != null;

        /// <summary>是否有任何值得注入的内容。</summary>
        public bool HasContent =>
            !string.IsNullOrWhiteSpace(FilePath)
            || HasSelection
            || !string.IsNullOrWhiteSpace(SymbolAtCursor)
            || HasDebuggerFrame;

        // ───────────────────── 格式化常量（测试可见性友好） ─────────────────────

        internal const int MaxSelectionLines = 40;
        internal const int MaxSelectionChars = 2000;
        internal const int MaxSymbolLineLength = 200;
        internal const int MaxDiagnosticsInBlock = 6;
        internal const int MaxDiagnosticMessageLength = 120;
        internal const int MaxLocalsInBlock = 12;
        internal const int MaxLocalNameLength = 60;
        internal const int MaxLocalValueLength = 160;
        internal const int MaxDebuggerFunctionLength = 120;

        /// <summary>
        /// 格式化为注入 volatile 块的 Markdown 文本。
        /// 无任何内容时返回 null（调用方应跳过注入）。
        /// </summary>
        /// <param name="workspaceRoot">工作区根目录；提供时将文件路径转为相对路径显示</param>
        public string? ToPromptBlock(string? workspaceRoot = null)
        {
            if (!HasContent) return null;

            var sb = new StringBuilder(512);
            sb.AppendLine("[IDE Context]");

            // ── 活动文件 ──
            if (!string.IsNullOrWhiteSpace(FilePath))
                sb.Append("Active File: ").AppendLine(GetDisplayPath(FilePath!, workspaceRoot));

            // ── 光标与符号 ──
            if (CursorLine > 0)
                sb.Append("Cursor: line ").Append(CursorLine).Append(", col ").Append(CursorColumn).AppendLine();
            if (!string.IsNullOrWhiteSpace(SymbolAtCursor))
            {
                sb.Append("Symbol: ").Append(SymbolAtCursor).AppendLine();
                if (!string.IsNullOrWhiteSpace(SymbolLineText))
                    sb.Append("Current Line: ").AppendLine(TruncateInline(SymbolLineText!, MaxSymbolLineLength));
            }

            // ── 选区 ──
            if (HasSelection)
                AppendSelection(sb);

            // ── 诊断摘要 ──
            AppendDiagnostics(sb);

            // ── 调试器断点快照 ──
            AppendDebuggerFrame(sb);

            return sb.ToString().TrimEnd();
        }

        private void AppendSelection(StringBuilder sb)
        {
            string text = SelectionText!;
            string fence = GetFenceLanguage(FilePath);

            sb.Append("Selection (").Append(
                    SelectionStartLine == SelectionEndLine
                        ? $"line {SelectionStartLine}"
                        : $"lines {SelectionStartLine}-{SelectionEndLine}")
                .AppendLine("):");

            var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            bool truncatedByLines = lines.Length > MaxSelectionLines;
            if (truncatedByLines)
                lines = lines.Take(MaxSelectionLines).ToArray();

            sb.Append("```").Append(fence).AppendLine();
            int used = 0;
            foreach (var l in lines)
            {
                int cost = l.Length + 1;
                if (used + cost > MaxSelectionChars)
                {
                    sb.AppendLine("…");
                    truncatedByLines = true;
                    break;
                }
                sb.AppendLine(l);
                used += cost;
            }
            sb.AppendLine("```");

            if (truncatedByLines)
                sb.AppendLine("(selection truncated)");
        }

        private void AppendDiagnostics(StringBuilder sb)
        {
            if (Diagnostics.Count == 0) return;

            sb.Append("Diagnostics: ")
              .Append(ErrorCount).Append(ErrorCount == 1 ? " error" : " errors")
              .Append(" / ")
              .Append(WarningCount).Append(WarningCount == 1 ? " warning" : " warnings")
              .AppendLine();

            foreach (var d in Diagnostics
                         .OrderByDescending(d => d.Severity == "error" ? 1 : 0) // 错误优先（稳定排序）
                         .Take(MaxDiagnosticsInBlock))
            {
                sb.Append("- ").Append(d.Severity).Append(" line ").Append(d.Line).Append(": ")
                  .AppendLine(TruncateInline(d.Message ?? "", MaxDiagnosticMessageLength));
            }
            if (Diagnostics.Count > MaxDiagnosticsInBlock)
                sb.Append("(+").Append(Diagnostics.Count - MaxDiagnosticsInBlock).AppendLine(" more)");
        }

        private void AppendDebuggerFrame(StringBuilder sb)
        {
            var f = DebuggerFrame;
            if (f == null) return;

            sb.AppendLine("Debugger: paused");
            if (!string.IsNullOrWhiteSpace(f.Function))
                sb.Append("Frame: ").AppendLine(TruncateInline(f.Function, MaxDebuggerFunctionLength));
            if (!string.IsNullOrWhiteSpace(f.File))
            {
                sb.Append("Location: ").Append(GetDisplayPath(f.File, null));
                if (f.Line > 0) sb.Append(':').Append(f.Line);
                sb.AppendLine();
            }

            if (f.Locals.Count == 0) return;

            sb.Append("Locals (").Append(f.Locals.Count).AppendLine("):");
            foreach (var v in f.Locals.Take(MaxLocalsInBlock))
            {
                sb.Append("- ")
                  .Append(TruncateInline(v.Name ?? "?", MaxLocalNameLength))
                  .Append(" = ")
                  .AppendLine(TruncateInline(v.Value ?? "<unavailable>", MaxLocalValueLength));
            }
            if (f.Locals.Count > MaxLocalsInBlock)
                sb.Append("(+").Append(f.Locals.Count - MaxLocalsInBlock).AppendLine(" more)");
        }

        // ───────────────────── 辅助方法 ─────────────────────

        private static string GetDisplayPath(string filePath, string? workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return filePath;
            string root = workspaceRoot.TrimEnd('\\', '/');
            if (filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && filePath.Length > root.Length + 1)
            {
                return filePath.Substring(root.Length + 1);
            }
            return filePath;
        }

        private static string TruncateInline(string value, int max)
        {
            string normalized = value.Trim();
            return normalized.Length <= max ? normalized : normalized.Substring(0, max) + "…";
        }

        /// <summary>按扩展名映射 Markdown 代码围栏语言标记。</summary>
        public static string GetFenceLanguage(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return "";
            string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".cs" => "csharp",
                ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".h" => "cpp",
                ".c" => "c",
                ".js" or ".mjs" => "javascript",
                ".ts" or ".tsx" => "typescript",
                ".py" => "python",
                ".java" => "java",
                ".go" => "go",
                ".rs" => "rust",
                ".md" => "markdown",
                ".json" => "json",
                ".xml" or ".xaml" => "xml",
                ".html" or ".htm" => "html",
                ".css" => "css",
                ".sql" => "sql",
                _ => "",
            };
        }

        // ───────────────────── 符号提取（P1-A Case B 验收核心） ─────────────────────

        internal const int MinSymbolLength = 2;

        /// <summary>
        /// 提取光标处标识符（字母/数字/下划线）。短于 <see cref="MinSymbolLength"/> 字符视为噪音返回 null。
        /// 词法启发式，非语义解析；列号为 0-based。
        /// </summary>
        public static string? ExtractIdentifierAt(string lineText, int columnIndex0Based)
        {
            if (string.IsNullOrEmpty(lineText)) return null;

            bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

            // net472 无 Math.Clamp，手动收敛
            int i = columnIndex0Based;
            if (i < 0) i = 0;
            if (i > lineText.Length - 1) i = lineText.Length - 1;

            if (!IsWord(lineText[i]))
            {
                // 光标可能停在词尾后一格：尝试前一位
                if (i > 0 && IsWord(lineText[i - 1])) i--;
                else return null;
            }

            int start = i;
            while (start > 0 && IsWord(lineText[start - 1])) start--;
            int end = i;
            while (end < lineText.Length - 1 && IsWord(lineText[end + 1])) end++;

            string word = lineText.Substring(start, end - start + 1);
            return word.Length >= MinSymbolLength ? word : null;
        }
    }
}
