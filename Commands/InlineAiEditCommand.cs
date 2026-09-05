using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using DeepSeek_v4_for_VisualStudio.View.InlineEdit;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Document;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace DeepSeek_v4_for_VisualStudio.Commands
{
    /// <summary>
    /// 编辑器 Inline Edit 命令（P1-B，Ctrl+I / 右键菜单）。
    ///
    /// 流程（报告 §12-§14）：选区 → 指令条 → LLM 单次调用（非 Agent）→ 复用
    /// InlineDiffSession preview-commit 管线 → 用户 Accept/Reject。
    /// 不重新发明 Patch；失败时指令条原地重试；Esc 可随时取消。
    /// </summary>
    internal sealed class InlineAiEditCommand
    {
        public const int CommandId = 0x0102;

        /// <summary>命令集 GUID（与 VSCommandTable.vsct 的 guidDeepSeekCmdSet 一致）。</summary>
        public static readonly Guid CommandSet = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

        private readonly DeepSeek_v4_for_VisualStudioPackage _package;

        private InlineAiEditCommand(DeepSeek_v4_for_VisualStudioPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandId = new CommandID(CommandSet, CommandId);
            commandService.AddCommand(new MenuCommand(Execute, menuCommandId));
        }

        public static InlineAiEditCommand? Instance { get; private set; }

        public static async Task InitializeAsync(DeepSeek_v4_for_VisualStudioPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var rawService = await package.GetServiceAsync(typeof(IMenuCommandService));
            var commandService = rawService as OleMenuCommandService;
            Instance = new InlineAiEditCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await RunCoreAsync(_package.DisposalToken);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[InlineEdit] 执行失败: {ex.Message}", ex);
                }
            });
        }

        // ────────────────────────── 主流程 ──────────────────────────

        private async Task RunCoreAsync(CancellationToken packageCt)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(packageCt);
            await _package.LoadPersistedOptionsAsync();

            // ── 选区捕获（序号 15）──
            var view = TryGetActiveWpfTextView();
            if (view == null) return;

            var initialSel = view.Selection.SelectedSpans;
            if (initialSel.Count == 0 || initialSel[0].IsEmpty)
            {
                Toast(LocalizationService.Instance["inlineEdit.noSelection"]);
                return;
            }

            var options = Settings.DeepSeekOptionsPage.Instance;
            if (string.IsNullOrWhiteSpace(options?.ApiKey))
            {
                Toast(LocalizationService.Instance["inlineEdit.noApiKey"]);
                return;
            }

            string filePath = "(untitled)";
            if (view.TextDataModel.DocumentBuffer.Properties.TryGetProperty(
                    typeof(ITextDocument), out ITextDocument doc))
            {
                filePath = doc.FilePath ?? filePath;
            }
            string langTag = IdeContextSnapshot.GetFenceLanguage(filePath);

            var anchor = ComputeAnchorScreen(view, initialSel[0]);
            var service = new Services.InlineEdit.InlineEditService(
                CompositionRoot.GetService<IDeepSeekApiService>());

            // ── 指令条（序号 16/19）──
            var bar = new View.InlineEdit.InlineEditBarWindow(anchor, view.VisualElement);
            InlineEditCommandFilter? commandFilter = TryGetTextViewAdapter(view) is { } textViewAdapter
                ? new InlineEditCommandFilter(bar, textViewAdapter)
                : null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(packageCt);
            bar.CancelRequested += () => cts.Cancel();

            try
            {
                bar.ShowBar();

                while (true)
                {
                    // ── 等待提交或关闭 ──
                    var submitTask = bar.WaitForSubmitAsync();
                    var closeTask = bar.WaitForCloseAsync();
                    var done = await Task.WhenAny(submitTask, closeTask);

                    if (done == closeTask || cts.IsCancellationRequested)
                        return;   // Esc / 失焦 / 包关闭 → 取消（序号 19）

                    string instruction = submitTask.Result?.Trim() ?? string.Empty;
                    bar.ResetSubmit();          // 复位以支持原地重试（序号 18）
                    if (instruction.Length == 0) continue;

                    // ── 提交时重新捕获基线（输入期间用户可能已改动编辑器）──
                    var spans = view.Selection.SelectedSpans;
                    if (spans.Count == 0 || spans[0].IsEmpty)
                    {
                        Toast(LocalizationService.Instance["inlineEdit.noSelection"]);
                        return;
                    }
                    SnapshotSpan span = spans[0];
                    string fullText = view.TextSnapshot.GetText();
                    string oldText = span.GetText();

                    var request = new Services.InlineEdit.InlineEditRequest
                    {
                        FilePath = filePath,
                        FenceLanguage = langTag,
                        UserInstruction = instruction,
                        SelectedText = oldText,
                        BeforeContext = SliceContextBefore(fullText, span.Start.Position),
                        AfterContext = SliceContextAfter(fullText, span.End.Position),
                    };

                    // ── LLM 单次调用（非 Agent，后台执行）──
                    bar.SetBusy(LocalizationService.Instance["inlineEdit.generating"]);
                    var result = await Task.Run(() => service.RewriteAsync(request, cts.Token));

                    if (result.WasCancelled || cts.IsCancellationRequested)
                    {
                        bar.CloseGracefully();
                        return;
                    }

                    if (!result.Success)
                    {
                        bar.SetError(string.Format(
                            LocalizationService.Instance["inlineEdit.failed"], result.Error));
                        continue;   // 错误留在指令条上，可改写指令后重试
                    }

                    // ── 接入现有预览管线（序号 17/18：Accept/Reject 由 Diff 宿主提供）──
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);

                    var session = EditorDiffMarkerService.Instance.CreateInlineDiffPreview(
                        view,
                        oldText,
                        result.Replacement!,
                        new List<ProposedTextChange>
                        {
                            new ProposedTextChange
                            {
                                Offset = span.Start.Position,
                                Length = span.Length,
                                NewText = result.Replacement!,
                                MatchedText = oldText,
                            },
                        });

                    bar.CloseGracefully();

                    if (session == null)
                        Logger.Warn($"[InlineEdit] 预览会话创建失败: {System.IO.Path.GetFileName(filePath)}（可能已有活跃 Session 或内容无变化）");
                    else
                        Logger.Info($"[InlineEdit] 预览已激活: {System.IO.Path.GetFileName(filePath)} ({oldText.Length} → {result.Replacement!.Length} chars)");
                    return;
                }
            }
            finally
            {
                commandFilter?.Dispose();
                try { bar.Close(); } catch { /* 已关闭 */ }
            }
        }

        // ────────────────────────── 辅助方法 ──────────────────────────

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

        private static IVsTextView? TryGetTextViewAdapter(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var componentModel = (IComponentModel?)Package.GetGlobalService(typeof(SComponentModel));
            var adapter = componentModel?.DefaultExportProvider
                .GetExport<IVsEditorAdaptersFactoryService>()?.Value;
            return adapter?.GetViewAdapter(view);
        }

        /// <summary>计算指令条锚点（选区起始行左上角，物理像素坐标；DPI 换算由窗口负责）。</summary>
        private static Point ComputeAnchorScreen(IWpfTextView view, SnapshotSpan span)
        {
            try
            {
                var line = view.TextViewLines.GetTextViewLineContainingBufferPosition(span.Start);
                double x = line != null ? line.Left : 40;
                double y = line != null ? line.Top : 40;
                return view.VisualElement.PointToScreen(new Point(x, y));
            }
            catch
            {
                try { return view.VisualElement.PointToScreen(new Point(40, 40)); }
                catch { return new Point(200, 200); }
            }
        }

        /// <summary>截取选区前文上下文（最多 60 行 / 3000 字符）。</summary>
        private static string SliceContextBefore(string fullText, int selectionStart)
        {
            const int maxLines = Services.InlineEdit.InlineEditService.MaxContextLines;
            const int maxChars = Services.InlineEdit.InlineEditService.MaxContextChars;

            int begin = selectionStart;
            int count = 0;
            while (count < maxLines && begin > 0 && selectionStart - begin < maxChars)
            {
                int nl = fullText.LastIndexOf('\n', begin - 1);
                if (nl < 0) { begin = 0; break; }
                begin = nl + 1;
                count++;
            }
            if (selectionStart - begin > maxChars)
                begin = selectionStart - maxChars;

            return selectionStart > begin
                ? fullText.Substring(begin, selectionStart - begin).TrimEnd()
                : string.Empty;
        }

        /// <summary>截取选区后文上下文（最多 60 行 / 3000 字符）。</summary>
        private static string SliceContextAfter(string fullText, int selectionEnd)
        {
            const int maxLines = Services.InlineEdit.InlineEditService.MaxContextLines;
            const int maxChars = Services.InlineEdit.InlineEditService.MaxContextChars;

            int end = selectionEnd;
            int count = 0;
            while (count < maxLines && end < fullText.Length && end - selectionEnd < maxChars)
            {
                int nl = fullText.IndexOf('\n', end);
                if (nl < 0) { end = fullText.Length; break; }
                end = nl + 1;
                count++;
            }
            if (end - selectionEnd > maxChars)
                end = selectionEnd + maxChars;

            return end > selectionEnd
                ? fullText.Substring(selectionEnd, Math.Min(end, fullText.Length) - selectionEnd).TrimStart()
                : string.Empty;
        }

        private void Toast(string message)
        {
            try
            {
                if (CompositionRoot.IsBuilt)
                    CompositionRoot.GetService<ToastNotificationService>().Show("DeepSeek AI Edit", message);
                else
                    Logger.Warn($"[InlineEdit] {message}");
            }
            catch
            {
                Logger.Warn($"[InlineEdit] {message}");
            }
        }
    }
}
