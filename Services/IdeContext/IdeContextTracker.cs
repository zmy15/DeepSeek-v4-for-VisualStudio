using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.TextManager.Interop;

namespace DeepSeek_v4_for_VisualStudio.Services.IdeContext
{
    /// <summary>
    /// IDE 实时态追踪器（P1-A）。
    ///
    /// 设计（报告 §7-§10）：
    /// - 每次用户消息发送前由 View 在 <b>UI 线程</b>调用一次 <see cref="CaptureFromActiveView"/>，
    ///   从当前活动的 IWpfTextView 提取文件/光标/选区/符号/当前文件 squiggle 诊断，
    ///   生成不可变 <see cref="IdeContextSnapshot"/>；Agent 每轮只读取 Current，不重复扫描 VS。
    /// - 捕获失败时置空（fail-closed）：宁可缺上下文，不给过期上下文误导模型。
    /// - 深度查询职责归 get_errors / read_file 工具，本类只做"快速定位"。
    /// </summary>
    public sealed class IdeContextTracker
    {
        private const int MaxDiagnosticsToCollect = 50;

        /// <summary>调试器局部变量捕获上限（展示层再截取前 12 项，保留 "+N more" 语义）。</summary>
        private const int MaxLocalsToCollect = 16;

        private volatile IdeContextSnapshot? _current;

        /// <summary>最近一次捕获的快照（null = 尚未捕获或上次捕获失败）。</summary>
        public IdeContextSnapshot? Current => _current;

        /// <summary>清空快照。</summary>
        public void Clear() => _current = null;

        /// <summary>从当前活动编辑器视图捕获快照。必须在 UI 线程调用。</summary>
        public void CaptureFromActiveView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                _current = CaptureCore();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[IdeContext] 捕获失败: {ex.Message}");
                _current = null;
            }
        }

        // ────────────────────────── 内部实现 ──────────────────────────

        private static IdeContextSnapshot? CaptureCore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var s = new IdeContextSnapshot();

            // ── 编辑器上下文（可选：非文本视图/无活动视图时跳过）──
            var view = TryGetActiveWpfTextView();
            if (view != null && view.TextSnapshot.Length > 0)
            {
                // ── 文件路径 ──
                if (view.TextDataModel.DocumentBuffer.Properties.TryGetProperty(
                        typeof(ITextDocument), out ITextDocument doc))
                {
                    // VS 可能返回未保存的临时 buffer（FilePath 是“Temp.txt”但磁盘上不存在）。
                    // fail-closed：不存在时丢弃整个编辑器快照，避免注入误导性文件名/光标。
                    if (!string.IsNullOrWhiteSpace(doc.FilePath) && !File.Exists(doc.FilePath))
                    {
                        Logger.Warn($"[IdeContext] 跳过不存在的活动文件: {doc.FilePath}");
                        view = null!;
                    }
                    else
                    {
                        s.FilePath = doc.FilePath;
                    }
                }

                if (view == null)
                    return CaptureDebuggerOnlySnapshot();

                // ── 光标 ──
                var caretPos = view.Caret.Position.BufferPosition;
                var containingLine = caretPos.GetContainingLine();
                s.CursorLine = containingLine.LineNumber + 1;
                s.CursorColumn = caretPos.Position - containingLine.Start.Position + 1;

                // ── 选区 ──
                var selectedSpans = view.Selection.SelectedSpans;
                if (selectedSpans.Count > 0 && !selectedSpans[0].IsEmpty)
                {
                    var span = selectedSpans[0];
                    s.SelectionText = span.GetText();
                    s.SelectionStartLine = span.Start.GetContainingLine().LineNumber + 1;
                    s.SelectionEndLine = span.End.GetContainingLine().LineNumber + 1;
                }

                // ── 符号启发式（光标处标识符；非语义解析，够用于"快速定位"）──
                string lineText = containingLine.GetText();
                s.SymbolAtCursor = IdeContextSnapshot.ExtractIdentifierAt(lineText, s.CursorColumn - 1);
                if (!string.IsNullOrWhiteSpace(s.SymbolAtCursor))
                    s.SymbolLineText = lineText.Trim();

                // ── 当前文件 squiggle 诊断 ──
                var diags = CollectBufferDiagnostics(view);
                if (diags != null)
                {
                    foreach (var d in diags)
                        s.Diagnostics.Add(d);
                }
            }

            // ── 调试器断点快照（独立于编辑器视图；未中断时返回 null 不占注入）──
            s.DebuggerFrame = TryCaptureDebuggerFrame();

            s.CapturedAt = DateTime.Now;

            return s.HasContent ? s : null;
        }

        private static IdeContextSnapshot? CaptureDebuggerOnlySnapshot()
        {
            var snapshot = new IdeContextSnapshot
            {
                DebuggerFrame = TryCaptureDebuggerFrame(),
                CapturedAt = DateTime.Now,
            };
            return snapshot.HasContent ? snapshot : null;
        }

        /// <summary>获取当前活动视图（与 CodeActions 的 IVsTextManager 方案一致）。</summary>
        private static IWpfTextView? TryGetActiveWpfTextView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var textManager = (IVsTextManager?)Package.GetGlobalService(typeof(SVsTextManager));
            if (textManager == null) return null;

            textManager.GetActiveView(1, null, out IVsTextView vsTextView);
            if (vsTextView == null) return null;

            var componentModel = (IComponentModel?)Package.GetGlobalService(typeof(SComponentModel));
            var adapter = componentModel?.DefaultExportProvider
                .GetExport<IVsEditorAdaptersFactoryService>()?.Value;

            return adapter?.GetWpfTextView(vsTextView);
        }

        /// <summary>
        /// 捕获调试器断点暂停态（当前栈帧 + 局部变量，只读、有界）。
        /// 仅在断点中断态(IsBroken)时返回数据；其余情况（运行中/设计态/服务缺失）返回 null。
        /// 局部变量逐项容错：求值失败以占位符呈现，不阻断整体捕获。
        /// 注意：Expression.Value 对属性求值可能执行用户代码 —— 以只读诊断为目的、
        /// 有界截断，与 VS 自身"局部变量"窗口行为一致。
        /// </summary>
        private static IdeDebuggerFrame? TryCaptureDebuggerFrame()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var dbg = dte?.Debugger;
            if (dbg == null || dbg.CurrentMode != EnvDTE.dbgDebugMode.dbgBreakMode) return null;

            var frame = dbg.CurrentStackFrame;
            if (frame == null) return null;

            var result = new IdeDebuggerFrame
            {
                Function = Truncate(frame.FunctionName ?? "", IdeContextSnapshot.MaxDebuggerFunctionLength),
            };

            // EnvDTE.StackFrame 不含源位置：断点命中时 VS 会跳转到对应文档，
            // 以活动文档 + 光标行作为位置来源（取不到则留空）。
            try
            {
                var doc = dte.ActiveDocument;
                if (doc != null)
                {
                    result.File = doc.FullName;
                    if (doc.Selection is EnvDTE.TextSelection sel)
                        result.Line = sel.ActivePoint.Line;
                }
            }
            catch { /* 位置信息缺失不影响帧内容 */ }

            int collected = 0;
            foreach (EnvDTE.Expression e in frame.Locals)
            {
                if (collected >= MaxLocalsToCollect) break;
                try
                {
                    result.Locals.Add(new IdeDebuggerValue
                    {
                        Name = Truncate(e.Name ?? "?", IdeContextSnapshot.MaxLocalNameLength),
                        Value = Truncate(e.Value ?? "<unavailable>", IdeContextSnapshot.MaxLocalValueLength),
                    });
                    collected++;
                }
                catch (Exception ex)
                {
                    result.Locals.Add(new IdeDebuggerValue { Name = Truncate(e.Name ?? "?", 60), Value = $"<error: {ex.Message}>" });
                    collected++;
                }
            }

            return result.HasContent ? result : null;

            static string Truncate(string v, int max)
                => string.IsNullOrEmpty(v) ? v : (v.Length <= max ? v : v.Substring(0, max) + "…");
        }

        /// <summary>
        /// 收集当前 buffer 的 squiggle 诊断（错误优先展示由格式化器负责排序）。
        /// 任何失败返回 null —— 诊断缺失不影响其余上下文注入。
        /// </summary>
        private static List<IdeDiagnosticItem>? CollectBufferDiagnostics(IWpfTextView view)        {
            try
            {
                var componentModel = (IComponentModel?)Package.GetGlobalService(typeof(SComponentModel));
                var factory = componentModel?.DefaultExportProvider
                    .GetExport<IErrorProviderFactory>()?.Value;
                if (factory == null) return null;

                SimpleTagger<ErrorTag> tagger = factory.GetErrorTagger(view.TextBuffer);
                var fullSpan = new SnapshotSpan(view.TextSnapshot, 0, view.TextSnapshot.Length);

                var result = new List<IdeDiagnosticItem>();
                foreach (var mapping in tagger.GetTags(new NormalizedSnapshotSpanCollection(fullSpan)))
                {
                    if (result.Count >= MaxDiagnosticsToCollect) break;

                    string errorType = mapping.Tag.ErrorType ?? string.Empty;
                    string severity =
                        errorType.Contains("error", StringComparison.OrdinalIgnoreCase) ? "error"
                        : errorType.Contains("warn", StringComparison.OrdinalIgnoreCase) ? "warning"
                        : "";

                    if (severity.Length == 0) continue;

                    int lineNo = 0;
                    // SimpleTagger.GetTags 返回的 Span 已是快照解析后的 SnapshotSpan
                    lineNo = mapping.Span.Start.GetContainingLine().LineNumber + 1;

                    string message = (mapping.Tag.ToolTipContent?.ToString() ?? "")
                        .Replace('\r', ' ').Replace('\n', ' ').Trim();

                    result.Add(new IdeDiagnosticItem { Severity = severity, Line = lineNo, Message = message });
                }
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[IdeContext] 收集诊断失败: {ex.Message}");
                return null;
            }
        }
    }
}
