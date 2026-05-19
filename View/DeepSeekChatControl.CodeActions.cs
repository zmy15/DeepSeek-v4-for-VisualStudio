using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.ToolWindows;
using DeepSeek_v4_for_VisualStudio.Utils;
using EnvDTE;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 代码操作相关方法：一键写入文件、Diff 预览、代码提示应用。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Public Methods - Code Actions

        /// <summary>
        /// 将 AI 生成的代码一键写入指定文件，并在编辑器中显示 diff 预览。
        /// </summary>
        /// <param name="code">要写入的代码</param>
        /// <param name="filePath">目标文件路径（可选，为空则使用当前活动文档）</param>
        public async Task WriteCodeToFileAsync(string code, string? filePath = null)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                StatusLabel.Text = "⚠️ 没有代码可写入";
                Logger.Info("[CodeAction] WriteCodeToFileAsync: 代码为空，跳过");
                return;
            }

            Logger.Info($"[CodeAction] WriteCodeToFileAsync: 开始写入, 代码长度={code.Length}, filePath={filePath ?? "(auto-detect)"}");

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                string? targetPath;
                string? oldContent = null;

                // ── 当 filePath 已提供时，直接使用该路径写入，不回退到 DTE ──
                if (!string.IsNullOrEmpty(filePath))
                {
                    targetPath = filePath;
                    if (File.Exists(targetPath))
                    {
                        oldContent = File.ReadAllText(targetPath);
                    }
                    else
                    {
                        Logger.Info($"[CodeAction] 目标文件不存在于磁盘，将创建: {targetPath}");
                    }

                    Logger.Info($"[CodeAction] 直接写入到: {targetPath}");
                    await PerformWriteAsync(targetPath, code, oldContent);
                    return;
                }

                // ── filePath 为空：尝试多种方式获取活动文档路径 ──
                targetPath = GetActiveDocumentPath();

                if (!string.IsNullOrEmpty(targetPath))
                {
                    oldContent = ReadActiveDocumentContent();

                    Logger.Info($"[CodeAction] 直接写入到: {targetPath}");
                    await PerformWriteAsync(targetPath, code, oldContent);
                }
                else
                {
                    StatusLabel.Text = "⚠️ 请先打开目标文件，或指定文件路径";
                    Logger.Info("[CodeAction] 未找到活动文档，无法写入");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"写入文件失败: {ex.Message}", ex);
                StatusLabel.Text = $"❌ 写入失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 确认并执行代码写入（在用户查看 diff 后调用）。
        /// </summary>
        public async Task ConfirmWriteCodeAsync(string code, string filePath)
        {
            Logger.Info($"[CodeAction] ConfirmWriteCodeAsync: 确认写入 {filePath}, 代码长度={code.Length}");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                string? oldContent = null;
                if (File.Exists(filePath))
                {
                    oldContent = File.ReadAllText(filePath);
                }

                await PerformWriteAsync(filePath, code, oldContent);
            }
            catch (Exception ex)
            {
                Logger.Error($"确认写入失败: {ex.Message}", ex);
                StatusLabel.Text = $"❌ 写入失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 获取当前活动文档的完整信息。
        /// </summary>
        public (string? filePath, string? content, string? language) GetActiveDocumentInfo()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                // ── 方案1：DTE ActiveDocument ──
                var dte = (DTE)Package.GetGlobalService(typeof(DTE));
                var doc = dte?.ActiveDocument;
                if (doc != null)
                {
                    string filePath = doc.FullName;
                    string language = doc.Language;
                    var textDoc = (TextDocument)doc.Object("TextDocument");
                    string content = textDoc.StartPoint.CreateEditPoint().GetText(textDoc.EndPoint);
                    return (filePath, content, language);
                }

                // ── 方案2：IVsTextManager.GetActiveView ──
                var textManager = (IVsTextManager)Package.GetGlobalService(typeof(SVsTextManager));
                if (textManager != null)
                {
                    textManager.GetActiveView(1, null, out IVsTextView vsTextView);
                    if (vsTextView != null)
                    {
                        var editorAdapter = GetEditorAdapter();
                        if (editorAdapter != null)
                        {
                            IWpfTextView? wpfView = editorAdapter.GetWpfTextView(vsTextView);
                            if (wpfView != null)
                            {
                                string filePath = string.Empty;
                                if (wpfView.TextDataModel.DocumentBuffer.Properties.TryGetProperty(
                                    typeof(ITextDocument), out ITextDocument textDocument))
                                {
                                    filePath = textDocument.FilePath;
                                }
                                string content = wpfView.TextSnapshot.GetText();
                                return (filePath, content, null);
                            }
                        }
                    }
                }

                return (null, null, null);
            }
            catch (Exception ex)
            {
                Logger.Error($"获取活动文档信息失败: {ex.Message}", ex);
                return (null, null, null);
            }
        }

        /// <summary>
        /// 获取当前选中的代码。
        /// </summary>
        public string? GetSelectedCode()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                // ── 方案1：DTE ActiveDocument.TextSelection ──
                var dte = (DTE)Package.GetGlobalService(typeof(DTE));
                var doc = dte?.ActiveDocument;
                if (doc != null)
                {
                    var textDoc = (TextDocument)doc.Object("TextDocument");
                    var selection = textDoc.Selection as TextSelection;
                    if (selection != null && !selection.IsEmpty)
                    {
                        return selection.Text;
                    }
                }

                // ── 方案2：IVsTextManager 获取 WPF 视图的选中内容 ──
                var textManager = (IVsTextManager)Package.GetGlobalService(typeof(SVsTextManager));
                if (textManager != null)
                {
                    textManager.GetActiveView(1, null, out IVsTextView vsTextView);
                    if (vsTextView != null)
                    {
                        var editorAdapter = GetEditorAdapter();
                        if (editorAdapter != null)
                        {
                            IWpfTextView? wpfView = editorAdapter.GetWpfTextView(vsTextView);
                            if (wpfView != null && !wpfView.Selection.SelectedSpans[0].IsEmpty)
                            {
                                return wpfView.Selection.SelectedSpans[0].GetText();
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"获取选中代码失败: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 在编辑器中替换选中代码（或全部内容），并触发编辑器内 diff 预览。
        /// </summary>
        public async Task ReplaceCodeInEditorAsync(string newCode, bool replaceAll = false)
        {
            Logger.Info($"[CodeAction] ReplaceCodeInEditorAsync: replaceAll={replaceAll}, 代码长度={newCode?.Length ?? 0}");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                string? oldContent = null;
                IWpfTextView? wpfView = null;

                // ── 首先尝试获取 IWpfTextView（用于 diff 预览）──
                var textManager = (IVsTextManager)Package.GetGlobalService(typeof(SVsTextManager));
                if (textManager != null)
                {
                    textManager.GetActiveView(1, null, out IVsTextView vsTextView);
                    if (vsTextView != null)
                    {
                        var editorAdapter = GetEditorAdapter();
                        if (editorAdapter != null)
                        {
                            wpfView = editorAdapter.GetWpfTextView(vsTextView);
                        }
                    }
                }

                // ── 捕获修改前的内容 ──
                if (wpfView != null)
                {
                    NormalizedSnapshotSpanCollection selection = wpfView.Selection.SelectedSpans;
                    if (selection.Count > 0 && !selection[0].IsEmpty && !replaceAll)
                    {
                        oldContent = selection[0].GetText();
                    }
                    else
                    {
                        oldContent = wpfView.TextSnapshot.GetText();
                    }
                }

                // ── 方案1：DTE ActiveDocument ──
                var dte = (DTE)Package.GetGlobalService(typeof(DTE));
                var doc = dte?.ActiveDocument;
                if (doc != null)
                {
                    var textDoc = (TextDocument)doc.Object("TextDocument");
                    var selection = textDoc.Selection as TextSelection;

                    if (oldContent == null)
                    {
                        oldContent = selection != null && !selection.IsEmpty && !replaceAll
                            ? selection.Text
                            : textDoc.StartPoint.CreateEditPoint().GetText(textDoc.EndPoint);
                    }

                    if (selection != null && !selection.IsEmpty && !replaceAll)
                    {
                        selection.Text = newCode;
                        Logger.Info("[CodeAction] 已替换编辑器选中内容（DTE）");
                    }
                    else
                    {
                        var editPoint = textDoc.StartPoint.CreateEditPoint();
                        editPoint.ReplaceText(textDoc.EndPoint, newCode, 0);
                        Logger.Info("[CodeAction] 已替换编辑器全部内容（DTE）");
                    }

                    // ── 触发编辑器内 diff 预览 ──
                    if (wpfView != null && !string.IsNullOrEmpty(oldContent)
                        && (_options == null || _options.ShowDiffMarkersInEditor))
                    {
                        EditorDiffMarkerService.Instance.BeginDiffPreview(wpfView, oldContent, newCode);
                        StatusLabel.Text = "📊 预览中 — 点击「确认变更」保留或「撤销」回退";
                    }
                    else
                    {
                        StatusLabel.Text = "✅ 代码已写入文件";
                    }

                    // ── 记录文件变更历史（用于后续重试回退）──
                    RecordManualCodeChange(oldContent, newCode, GetActiveDocumentPath());

                    Logger.Info(LocalizationService.Instance["write.appliedToEditor"]);
                    return;
                }

                // ── 方案2：IVsTextManager.GetActiveView ──
                if (wpfView != null)
                {
                    ITextBuffer textBuffer = wpfView.TextBuffer;
                    NormalizedSnapshotSpanCollection selection = wpfView.Selection.SelectedSpans;

                    using (ITextEdit edit = textBuffer.CreateEdit())
                    {
                        if (selection.Count > 0 && !selection[0].IsEmpty && !replaceAll)
                        {
                            edit.Replace(selection[0], newCode);
                        }
                        else
                        {
                            edit.Replace(new SnapshotSpan(wpfView.TextSnapshot, 0, wpfView.TextSnapshot.Length), newCode);
                        }
                        edit.Apply();
                    }

                    // ── 触发编辑器内 diff 预览 ──
                    if (!string.IsNullOrEmpty(oldContent)
                        && (_options == null || _options.ShowDiffMarkersInEditor))
                    {
                        EditorDiffMarkerService.Instance.BeginDiffPreview(wpfView, oldContent, newCode);
                        StatusLabel.Text = "📊 预览中 — 点击「确认变更」保留或「撤销」回退";
                    }
                    else
                    {
                        StatusLabel.Text = "✅ 代码已写入文件";
                    }

                    // ── 记录文件变更历史（用于后续重试回退）──
                    RecordManualCodeChange(oldContent, newCode, GetActiveDocumentPath());

                    Logger.Info("[CodeAction] 已替换编辑器内容（IVsTextManager）");
                    return;
                }

                StatusLabel.Text = "⚠️ 没有打开的文档";
            }
            catch (Exception ex)
            {
                Logger.Error($"替换代码失败: {ex.Message}", ex);
                StatusLabel.Text = $"❌ 写入失败: {ex.Message}";
            }
        }

        #endregion

        #region Private Methods - Code Actions

        /// <summary>
        /// 通过 MEF 容器获取 <see cref="IVsEditorAdaptersFactoryService"/>。
        /// 该接口是 MEF 导出组件，无法通过 GetService 获取，必须走 IComponentModel。
        /// </summary>
        private static IVsEditorAdaptersFactoryService? GetEditorAdapter()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            return componentModel?.DefaultExportProvider.GetExport<IVsEditorAdaptersFactoryService>()?.Value;
        }

        /// <summary>
        /// 获取当前活动文档的完整路径。优先使用 DTE ActiveDocument，
        /// 若不可用则通过 IVsTextManager 获取活动文本视图的文件路径。
        /// </summary>
        /// <returns>活动文档的完整路径，若无活动文档则返回 null。</returns>
        private string? GetActiveDocumentPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                // ── 方案1：DTE ActiveDocument ──
                var dte = (DTE)Package.GetGlobalService(typeof(DTE));
                var doc = dte?.ActiveDocument;
                if (doc != null)
                {
                    Logger.Info($"[CodeAction] 通过 DTE.ActiveDocument 获取路径: {doc.FullName}");
                    return doc.FullName;
                }

                // ── 方案2：IVsTextManager.GetActiveView ──
                var textManager = (IVsTextManager)Package.GetGlobalService(typeof(SVsTextManager));
                if (textManager != null)
                {
                    textManager.GetActiveView(1, null, out IVsTextView vsTextView);
                    if (vsTextView != null)
                    {
                        var editorAdapter = GetEditorAdapter();
                        if (editorAdapter != null)
                        {
                            IWpfTextView? wpfView = editorAdapter.GetWpfTextView(vsTextView);
                            if (wpfView != null &&
                                wpfView.TextDataModel.DocumentBuffer.Properties.TryGetProperty(
                                    typeof(ITextDocument), out ITextDocument textDocument))
                            {
                                Logger.Info($"[CodeAction] 通过 IVsTextManager 获取路径: {textDocument.FilePath}");
                                return textDocument.FilePath;
                            }
                        }
                    }
                }

                Logger.Info("[CodeAction] 无法获取活动文档路径（DTE 和 IVsTextManager 均失败）");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"[CodeAction] GetActiveDocumentPath 异常: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 记录手动代码操作（一键写入/替换）到文件变更历史，
        /// 以便后续重试时能够回退这些修改。
        /// </summary>
        /// <param name="oldContent">修改前的原始内容</param>
        /// <param name="newContent">修改后的新内容</param>
        /// <param name="filePath">目标文件路径（null 则跳过）</param>
        private void RecordManualCodeChange(string? oldContent, string newContent, string? filePath)
        {
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(oldContent))
                return;
            if (oldContent == newContent)
                return;

            try
            {
                int oldLineCount = oldContent.Split('\n').Length;
                int newLineCount = newContent.Split('\n').Length;
                int linesAdded = Math.Max(0, newLineCount - oldLineCount);
                int linesRemoved = Math.Max(0, oldLineCount - newLineCount);

                int userMsgIndex = GetLastUserMessageIndex();
                if (userMsgIndex < 0) return;

                var change = new FileChangeSummary
                {
                    FilePath = filePath,
                    LinesAdded = linesAdded,
                    LinesRemoved = linesRemoved,
                    BriefDescription = $"手动应用代码到 {Path.GetFileName(filePath)}",
                    OriginalContent = oldContent,
                    NewContent = newContent,
                };

                RecordFileChangesForTurn(userMsgIndex, new List<FileChangeSummary> { change });
                Logger.Info($"[CodeAction] 已记录手动代码变更: {Path.GetFileName(filePath)} (+{linesAdded} -{linesRemoved})");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[CodeAction] 记录手动代码变更失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取消息列表中最后一个用户消息的索引。
        /// </summary>
        private int GetLastUserMessageIndex()
        {
            lock (_lock)
            {
                for (int i = _messages.Count - 1; i >= 0; i--)
                {
                    if (_messages[i].Role == "user")
                        return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// 读取当前活动文档的完整文本内容。
        /// 优先通过 DTE ActiveDocument 读取，备用通过 IVsTextManager 读取。
        /// </summary>
        /// <returns>活动文档的文本内容，若无活动文档则返回 null。</returns>
        private string? ReadActiveDocumentContent()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                // ── 方案1：DTE ActiveDocument ──
                var dte = (DTE)Package.GetGlobalService(typeof(DTE));
                var doc = dte?.ActiveDocument;
                if (doc != null)
                {
                    var textDoc = (TextDocument)doc.Object("TextDocument");
                    return textDoc.StartPoint.CreateEditPoint().GetText(textDoc.EndPoint);
                }

                // ── 方案2：IVsTextManager.GetActiveView ──
                var textManager = (IVsTextManager)Package.GetGlobalService(typeof(SVsTextManager));
                if (textManager != null)
                {
                    textManager.GetActiveView(1, null, out IVsTextView vsTextView);
                    if (vsTextView != null)
                    {
                        var editorAdapter = GetEditorAdapter();
                        if (editorAdapter != null)
                        {
                            IWpfTextView? wpfView = editorAdapter.GetWpfTextView(vsTextView);
                            if (wpfView != null)
                            {
                                return wpfView.TextSnapshot.GetText();
                            }
                        }
                    }
                }

                Logger.Info("[CodeAction] 无法读取活动文档内容（DTE 和 IVsTextManager 均失败）");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"[CodeAction] ReadActiveDocumentContent 异常: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 执行实际的写入操作。如果目标文件在编辑器中已打开，使用编辑器内 diff 预览；
        /// 否则直接写入文件。
        /// </summary>
        private async Task PerformWriteAsync(string filePath, string code, string? oldContent)
        {
            Logger.Info($"[CodeAction] PerformWriteAsync: 写入 {filePath}, 代码长度={code?.Length ?? 0}, 原内容长度={oldContent?.Length ?? 0}");

            try
            {
                // ── 检查文件是否已在编辑器中打开 ──
                IWpfTextView? openView = await FindOpenTextViewForFileAsync(filePath);

                if (openView != null && !string.IsNullOrEmpty(oldContent)
                    && (_options == null || _options.ShowDiffMarkersInEditor))
                {
                    // ── 文件已打开 → 使用编辑器内 diff 预览 ──
                    EditorDiffMarkerService.Instance.BeginDiffPreview(openView, oldContent, code);

                    int addLines = diffLinesAdd(oldContent, code);
                    int delLines = diffLinesDel(oldContent, code);
                    string fileName = Path.GetFileName(filePath);
                    StatusLabel.Text = $"📊 预览中: {fileName}" +
                        (addLines > 0 || delLines > 0 ? $" (+{addLines} -{delLines} 行变化)" : "");
                    Logger.Info($"[CodeAction] 编辑器内 diff 预览已激活: {filePath}");
                }
                else
                {
                    // ── 文件未打开 → 直接写入，并注册待处理 diff ──
                    string? error = await TerminalWindowHelper.WriteCodeToFileAsync(filePath, code ?? string.Empty);

                    if (error != null)
                    {
                        Logger.Error($"[CodeAction] 写入失败: {error}");
                        StatusLabel.Text = $"❌ 写入失败: {error}";
                        return;
                    }

                    // 注册待处理 diff（用户稍后打开文件时自动激活预览）
                    if (!string.IsNullOrEmpty(oldContent)
                        && (_options == null || _options.ShowDiffMarkersInEditor))
                    {
                        EditorDiffMarkerService.Instance.RegisterPendingDiff(filePath, oldContent, code);
                    }

                    int addLines = diffLinesAdd(oldContent, code);
                    int delLines = diffLinesDel(oldContent, code);
                    string fileName = Path.GetFileName(filePath);
                    StatusLabel.Text = $"✅ 已写入 {fileName}" +
                        (addLines > 0 || delLines > 0 ? $" (+{addLines} -{delLines} 行变化)" : "") +
                        ((_options == null || _options.ShowDiffMarkersInEditor) && (addLines > 0 || delLines > 0)
                            ? " — 打开文件后可预览变更" : "");
                    Logger.Info($"[CodeAction] 写入完成: {filePath}, +{addLines} -{delLines} 行变化");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[CodeAction] 执行写入失败: {ex.Message}", ex);
                StatusLabel.Text = $"❌ 写入失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 查找指定文件路径对应的已打开 <see cref="IWpfTextView"/>。
        /// 通过运行文档表 (RDT) + <see cref="IVsEditorAdaptersFactoryService"/> 获取。
        /// </summary>
        private static async Task<IWpfTextView?> FindOpenTextViewForFileAsync(string filePath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var editorAdapter = GetEditorAdapter();
                if (editorAdapter == null)
                    return null;

                // 通过 RDT 查找已打开的文档
                var rdt = (Microsoft.VisualStudio.Shell.Interop.IVsRunningDocumentTable?)
                    Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsRunningDocumentTable));

                if (rdt == null)
                    return null;

                IEnumRunningDocuments? enumDocs;
                int hr = rdt.GetRunningDocumentsEnum(out enumDocs);
                if (hr != Microsoft.VisualStudio.VSConstants.S_OK || enumDocs == null)
                    return null;

                uint[] cookieArray = new uint[1];
                uint fetched;

                while (enumDocs.Next(1, cookieArray, out fetched) == Microsoft.VisualStudio.VSConstants.S_OK && fetched == 1)
                {
                    uint cookie = cookieArray[0];
                    uint flags;
                    uint readLocks;
                    uint editLocks;
                    string? docPath;
                    Microsoft.VisualStudio.Shell.Interop.IVsHierarchy? hierarchy;
                    uint itemId;
                    IntPtr docDataPtr;

                    hr = rdt.GetDocumentInfo(cookie,
                        out flags, out readLocks, out editLocks,
                        out docPath, out hierarchy, out itemId, out docDataPtr);

                    if (hr == Microsoft.VisualStudio.VSConstants.S_OK && docPath != null &&
                        string.Equals(docPath, filePath, StringComparison.OrdinalIgnoreCase) &&
                        docDataPtr != IntPtr.Zero)
                    {
                        var vsTextBuffer = System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(docDataPtr) as IVsTextBuffer;
                        if (vsTextBuffer != null)
                        {
                            var textBuffer = editorAdapter.GetDataBuffer(vsTextBuffer);
                            if (textBuffer != null)
                            {
                                // 尝试获取该 buffer 关联的 IWpfTextView
                                // 通过 ITextBuffer.Properties 中存储的视图引用获取
                                if (textBuffer.Properties.TryGetProperty(typeof(IWpfTextView), out IWpfTextView? wpfView))
                                {
                                    return wpfView;
                                }

                                // 回退：通过 IVsTextManager 查找视图
                                var textManager = (IVsTextManager)Package.GetGlobalService(typeof(SVsTextManager));
                                if (textManager != null)
                                {
                                    textManager.GetActiveView(1, null, out IVsTextView vsTextView);
                                    if (vsTextView != null)
                                    {
                                        var bufferAdapter = editorAdapter.GetDataBuffer(vsTextView as IVsTextBuffer);
                                        if (bufferAdapter == textBuffer)
                                        {
                                            return editorAdapter.GetWpfTextView(vsTextView);
                                        }
                                    }
                                }
                            }
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[CodeAction] FindOpenTextViewForFileAsync 异常: {ex.Message}");
            }

            return null;
        }

        private int diffLinesAdd(string? oldContent, string code)
        {
            if (string.IsNullOrEmpty(oldContent)) return code.Split('\n').Length;
            var diff = CodeDiffService.ComputeDiff(oldContent, code);
            return diff.Count(d => d.Type == DiffLineType.Added);
        }

        private int diffLinesDel(string? oldContent, string code)
        {
            if (string.IsNullOrEmpty(oldContent)) return 0;
            var diff = CodeDiffService.ComputeDiff(oldContent, code);
            return diff.Count(d => d.Type == DiffLineType.Deleted);
        }

        #endregion

        #region Inline Code Completion (IntelliSense)

        /// <summary>
        /// AI 代码提示：根据当前上下文请求代码建议。
        /// 在用户输入暂停时触发，显示建议的代码片段。
        /// </summary>
        /// <param name="contextCode">当前编辑器的上下文代码</param>
        /// <param name="cursorPosition">光标位置</param>
        /// <returns>AI 建议的代码片段</returns>
        public async Task<string?> GetCodeSuggestionAsync(string contextCode, int cursorPosition = -1)
        {
            if (_apiService == null || string.IsNullOrWhiteSpace(contextCode))
            {
                Logger.Info($"[CodeSuggestion] 跳过: apiService={_apiService != null}, contextLen={contextCode?.Length ?? 0}");
                return null;
            }

            Logger.Info($"[CodeSuggestion] 请求代码建议, 上下文长度={contextCode.Length}, 光标位置={cursorPosition}");

            try
            {
                string prompt = BuildCodeSuggestionPrompt(contextCode, cursorPosition);

                var messages = new List<Models.ChatApiMessage>
                {
                    new Models.ChatApiMessage
                    {
                        Role = "system",
                        Content = "你是一个代码补全助手。只返回要补全的代码片段，不要解释，不要Markdown标记。直接返回纯代码。补全要简洁、准确、符合上下文。"
                    },
                    new Models.ChatApiMessage
                    {
                        Role = "user",
                        Content = prompt
                    }
                };

                var result = new StringBuilder();
                var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));

                await foreach (var chunk in _apiService.ChatStreamAsync(messages, null, cts.Token))
                {
                    result.Append(chunk);
                }

                string suggestion = result.ToString().Trim();
                if (string.IsNullOrWhiteSpace(suggestion) || suggestion.Length < 2)
                {
                    Logger.Info("[CodeSuggestion] AI 未返回有效建议");
                    return null;
                }

                // 清理可能残留的 Markdown 标记
                suggestion = suggestion
                    .Replace("```", "")
                    .Trim();

                Logger.Info($"[CodeSuggestion] 获得建议, 长度={suggestion.Length}");
                return suggestion;
            }
            catch (Exception ex)
            {
                Logger.Info($"[CodeSuggestion] 获取失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 构建代码补全提示词。
        /// </summary>
        private string BuildCodeSuggestionPrompt(string contextCode, int cursorPosition)
        {
            string prompt;
            if (cursorPosition > 0 && cursorPosition < contextCode.Length)
            {
                string before = contextCode.Substring(0, cursorPosition);
                string after = contextCode.Substring(cursorPosition);
                prompt = $"根据上下文补全光标处的代码。\n\n```\n{before}<CURSOR>{after}\n```\n\n只返回 <CURSOR> 位置应插入的代码。";
            }
            else
            {
                prompt = $"根据上下文补全代码。\n\n```\n{contextCode}\n```\n\n只返回要追加的代码片段。";
            }
            Logger.Info($"[CodeSuggestion] 构建提示词, 长度={prompt.Length}");
            return prompt;
        }

        #endregion
    }
}
