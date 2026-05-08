using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace DeepSeek_v4_for_VisualStudio.ToolWindows
{
    /// <summary>
    /// 工具辅助类：将 AI 生成的代码写入活动文档，并为代码变更生成差异对比视图。
    /// </summary>
    public static class TerminalWindowHelper
    {
        #region Public Methods

        /// <summary>
        /// 使用 VS SDK 文本缓冲区 API 将完整代码写入指定文件。
        /// 相较于 File.WriteAllText，此方法正确集成 VS 编辑器基础结构：
        /// - 如果文件已在编辑器中打开，修改会纳入撤销历史（Ctrl+Z）
        /// - 如果文件未打开，使用不可见编辑器在后台加载、修改并保存，不会弹出新标签页
        /// - 自动处理文件编码（UTF-8 with BOM for .cs/.vb 等）
        /// 
        /// 返回 null 表示成功；返回错误字符串表示失败原因。
        /// 
        /// API 参考：
        /// - IVsInvisibleEditorManager: https://learn.microsoft.com/visualstudio/extensibility/using-the-invisible-editor
        /// - IVsTextLines / ITextBuffer: https://learn.microsoft.com/visualstudio/extensibility/inside-the-editor
        /// </summary>
        /// <param name="filePath">目标文件的完整路径。</param>
        /// <param name="newContent">要写入的新内容（完整文件内容）。</param>
        public static async Task<string?> WriteCodeToFileAsync(string filePath, string newContent)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return "File path is empty.";
            if (newContent == null)
                newContent = string.Empty;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                // ── 确保目录和文件存在 ──
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                bool fileExisted = File.Exists(filePath);
                if (!fileExisted)
                {
                    await Task.Run(() => File.WriteAllText(filePath, string.Empty, System.Text.Encoding.UTF8));
                    Logger.Info($"[WriteCode] 创建新文件: {Path.GetFileName(filePath)}");
                }

                // ── 获取 editorAdapter（整个方法共用）──
                var componentModel = (IComponentModel)ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel));
                IVsEditorAdaptersFactoryService? editorAdapter = componentModel?.DefaultExportProvider
                    .GetExport<IVsEditorAdaptersFactoryService>()?.Value;

                // ── 方案一：文件已在 VS 编辑器中打开 → 直接操作其 ITextBuffer ──
                if (editorAdapter != null)
                {
                    ITextBuffer? targetBuffer = FindOpenTextBufferForFile(filePath, editorAdapter);
                    if (targetBuffer != null)
                    {
                        using (var edit = targetBuffer.CreateEdit())
                        {
                            var snapshot = targetBuffer.CurrentSnapshot;
                            if (snapshot.Length > 0)
                                edit.Replace(0, snapshot.Length, newContent);
                            else
                                edit.Insert(0, newContent);
                            edit.Apply();
                        }

                        if (targetBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDoc))
                        {
                            await Task.Run(() => textDoc.Save());
                        }

                        Logger.Info($"[WriteCode] ✅ 通过 ITextBuffer 写入已打开文件: {Path.GetFileName(filePath)}");
                        return null;
                    }
                }

                // ── 方案二：文件未打开 → 使用 IVsInvisibleEditor 后台加载/修改/保存 ──
                var invisibleEditorMgr = (IVsInvisibleEditorManager)
                    ServiceProvider.GlobalProvider.GetService(typeof(SVsInvisibleEditorManager));

                if (invisibleEditorMgr != null)
                {
                    IVsInvisibleEditor? invisibleEditor;
                    int hr = invisibleEditorMgr.RegisterInvisibleEditor(
                        filePath,
                        pProject: null,
                        dwFlags: (uint)_EDITORREGFLAGS.RIEF_ENABLECACHING,
                        pFactory: null,
                        ppEditor: out invisibleEditor);

                    if (hr == VSConstants.S_OK && invisibleEditor != null)
                    {
                        // 优先通过 ITextBuffer 适配器操作（支持 Undo）
                        var vsTextBuffer = invisibleEditor as IVsTextBuffer;
                        if (vsTextBuffer != null && editorAdapter != null)
                        {
                            var textBuffer = editorAdapter.GetDataBuffer(vsTextBuffer);
                            if (textBuffer != null)
                            {
                                using (var edit = textBuffer.CreateEdit())
                                {
                                    var snapshot = textBuffer.CurrentSnapshot;
                                    if (snapshot.Length > 0)
                                        edit.Replace(0, snapshot.Length, newContent);
                                    else
                                        edit.Insert(0, newContent);
                                    edit.Apply();
                                }

                                if (textBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDoc))
                                {
                                    await Task.Run(() => textDoc.Save());
                                }

                                Logger.Info($"[WriteCode] ✅ 通过 IVsInvisibleEditor+ITextBuffer 写入: {Path.GetFileName(filePath)}");
                                return null;
                            }
                        }

                        // 回退：通过 IVsTextLines 操作
                        var textLines = invisibleEditor as IVsTextLines;
                        if (textLines != null)
                        {
                            int lineCount;
                            int lastLineLen;
                            textLines.GetLastLineIndex(out lineCount, out lastLineLen);
                            int lastLineLength;
                            textLines.GetLengthOfLine(lineCount, out lastLineLength);

                            IntPtr pNewText = Marshal.StringToCoTaskMemUni(newContent);
                            try
                            {
                                textLines.ReplaceLines(
                                    0, 0,
                                    lineCount, lastLineLength,
                                    pNewText, newContent.Length,
                                    new TextSpan[1]);
                            }
                            finally
                            {
                                Marshal.FreeCoTaskMem(pNewText);
                            }

                            var persistDoc = invisibleEditor as IVsPersistDocData;
                            if (persistDoc != null)
                            {
                                persistDoc.SaveDocData(
                                    VSSAVEFLAGS.VSSAVE_Save,
                                    out string savePath,
                                    out int saveCanceled);
                            }

                            Logger.Info($"[WriteCode] ✅ 通过 IVsInvisibleEditor+IVsTextLines 写入: {Path.GetFileName(filePath)}");
                            return null;
                        }
                    }
                    else
                    {
                        Logger.Warn($"[WriteCode] RegisterInvisibleEditor 失败: hr={hr}");
                    }
                }

                // ── 回退方案：所有 VS SDK API 都不可用时 ──
                Logger.Warn($"[WriteCode] VS SDK 写入不可用，回退到 File.WriteAllText: {Path.GetFileName(filePath)}");
                await Task.Run(() => File.WriteAllText(filePath, newContent, System.Text.Encoding.UTF8));
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"[WriteCode] 写入失败: {ex.Message}", ex);
                return $"Failed to write file: {ex.Message}";
            }
        }

        /// <summary>
        /// 在已打开的文本缓冲区中查找匹配文件路径的 ITextBuffer。
        /// </summary>
        private static ITextBuffer? FindOpenTextBufferForFile(string filePath, IVsEditorAdaptersFactoryService editorAdapter)
        {
            try
            {
                // 通过 RDT 查找已打开的文档
                var rdt = (IVsRunningDocumentTable?)ServiceProvider.GlobalProvider.GetService(typeof(SVsRunningDocumentTable));
                if (rdt == null) return null;

                IEnumRunningDocuments? enumDocs;
                int hr = rdt.GetRunningDocumentsEnum(out enumDocs);
                if (hr != VSConstants.S_OK || enumDocs == null) return null;

                uint[] cookieArray = new uint[1];
                uint fetched;
                while (enumDocs.Next(1, cookieArray, out fetched) == VSConstants.S_OK && fetched == 1)
                {
                    uint cookie = cookieArray[0];
                    uint flags;
                    uint readLocks;
                    uint editLocks;
                    string? docPath;
                    IVsHierarchy? hierarchy;
                    uint itemId;
                    IntPtr docDataPtr;

                    hr = rdt.GetDocumentInfo(cookie,
                        out flags, out readLocks, out editLocks,
                        out docPath, out hierarchy, out itemId, out docDataPtr);

                    if (hr == VSConstants.S_OK && docPath != null &&
                        string.Equals(docPath, filePath, StringComparison.OrdinalIgnoreCase) &&
                        docDataPtr != IntPtr.Zero)
                    {
                        // 尝试获取 ITextBuffer
                        var vsTextBuffer = Marshal.GetObjectForIUnknown(docDataPtr) as IVsTextBuffer;
                        if (vsTextBuffer != null)
                        {
                            var textBuffer = editorAdapter.GetDataBuffer(vsTextBuffer);
                            if (textBuffer != null)
                                return textBuffer;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[WriteCode] FindOpenTextBuffer 异常: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 将代码写入 VS 当前活动文档：有选中文本则替换，无选中则在光标处插入。
        /// 使用 ITextEdit 确保整个操作为一次撤销。
        /// 返回 null 表示成功；返回错误字符串表示失败原因（由调用方决定如何展示）。
        /// </summary>
        /// <param name="code">要插入或替换的代码。</param>
        public static async Task<string?> ApplyCodeToActiveDocumentAsync(string code)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    return null; // 空代码，静默跳过
                }

                DocumentView? docView = await GetActiveDocumentViewAsync();

                if (docView == null)
                {
                    Logger.Warn("[ApplyCode] 没有活动文档，无法写入代码");
                    return "No active document is open to apply the code.";
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                ITextBuffer textBuffer = docView.TextView.TextBuffer;
                NormalizedSnapshotSpanCollection selection = docView.TextView.Selection.SelectedSpans;

                using (ITextEdit edit = textBuffer.CreateEdit())
                {
                    if (selection.Count > 0 && !selection[0].IsEmpty)
                    {
                        edit.Replace(selection[0], code);
                    }
                    else
                    {
                        int caretPosition = docView.TextView.Caret.Position.BufferPosition.Position;
                        edit.Insert(caretPosition, code);
                    }

                    edit.Apply();
                }

                Logger.Info("[ApplyCode] 代码已成功写入活动文档");
                return null; // 成功
            }
            catch (Exception ex)
            {
                Logger.Error($"[ApplyCode] Failed: {ex.Message}", ex);
                return $"Failed to apply the code: {ex.Message}";
            }
        }

        /// <summary>
        /// 应用 AI 生成的代码并在确认前展示与原始代码的差异对比（+/-）。
        /// 返回 null 表示成功或用户取消；返回错误字符串表示失败原因。
        /// </summary>
        /// <param name="newCode">AI 生成的新代码。</param>
        /// <param name="originalCode">修改前的原始选中代码。</param>
        /// <param name="filePath">文件路径，用于差异视图的上下文。</param>
        public static async Task<string?> ApplyCodeWithDiffAsync(string newCode, string originalCode, string filePath = "")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(newCode))
                {
                    return null;
                }

                DocumentView? docView = await GetActiveDocumentViewAsync();

                if (docView == null)
                {
                    Logger.Warn("[ApplyCodeWithDiff] 没有活动文档");
                    return "No active document is open to apply the code.";
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Generate diff content
                string diffContent = CodeDiffHelper.GenerateUnifiedDiff(originalCode, newCode, filePath);

                // Show diff in a message dialog (can be upgraded to a proper tool window later)
                bool hasChanges = !string.IsNullOrWhiteSpace(diffContent) &&
                                  diffContent != "No changes detected.";

                if (hasChanges)
                {
                    // Ask user to confirm the change
                    MessageBoxResult result = MessageBox.Show(
                        $"The following changes will be applied:\n\n{diffContent}\n\nApply these changes?",
                        "DeepSeek Chat - Code Diff",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                    {
                        return null; // 用户取消
                    }
                }

                // Apply the code
                ITextBuffer textBuffer = docView.TextView.TextBuffer;
                NormalizedSnapshotSpanCollection selection = docView.TextView.Selection.SelectedSpans;

                using (ITextEdit edit = textBuffer.CreateEdit())
                {
                    if (selection.Count > 0 && !selection[0].IsEmpty)
                    {
                        edit.Replace(selection[0], newCode);
                    }
                    else
                    {
                        int caretPosition = docView.TextView.Caret.Position.BufferPosition.Position;
                        edit.Insert(caretPosition, newCode);
                    }

                    edit.Apply();
                }

                Logger.Info("[ApplyCodeWithDiff] 代码已成功应用");
                return null; // 成功
            }
            catch (Exception ex)
            {
                Logger.Error($"[ApplyCodeWithDiff] Failed: {ex.Message}", ex);
                return $"Failed to apply the code: {ex.Message}";
            }
        }

        /// <summary>
        /// 捕获当前活动文档的原始内容（修改前）。
        /// 优先返回选中文本，无选中时返回全文。
        /// </summary>
        /// <returns>包含原始代码和文件路径的元组，无活动文档时返回 null。</returns>
        public static async Task<(string OriginalCode, string FilePath)?> CaptureOriginalCodeAsync()
        {
            try
            {
                DocumentView? docView = await GetActiveDocumentViewAsync();

                if (docView == null)
                {
                    return null;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                string originalCode;
                NormalizedSnapshotSpanCollection selection = docView.TextView.Selection.SelectedSpans;

                if (selection.Count > 0 && !selection[0].IsEmpty)
                {
                    originalCode = selection[0].GetText();
                }
                else
                {
                    originalCode = docView.TextView.TextBuffer.CurrentSnapshot.GetText();
                }

                string filePath = docView.FilePath ?? string.Empty;

                return (originalCode, filePath);
            }
            catch (Exception ex)
            {
                Logger.Error($"[CaptureOriginalCode] Failed: {ex.Message}", ex);
                return null;
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 使用 VS SDK API 获取当前活动的文档视图。
        /// </summary>
        private static async Task<DocumentView?> GetActiveDocumentViewAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                // Use the VS SDK to get the active text view
                var textManager = (Microsoft.VisualStudio.TextManager.Interop.IVsTextManager)
                    ServiceProvider.GlobalProvider.GetService(typeof(Microsoft.VisualStudio.TextManager.Interop.SVsTextManager));

                if (textManager == null)
                {
                    return null;
                }

                textManager.GetActiveView(1, null, out Microsoft.VisualStudio.TextManager.Interop.IVsTextView vsTextView);

                if (vsTextView == null)
                {
                    return null;
                }

                // Get the WPF text view via MEF (IVsEditorAdaptersFactoryService is a MEF export, not a VS service)
                var componentModel = (IComponentModel)ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel));
                var editorAdapter = componentModel?.DefaultExportProvider.GetExport<IVsEditorAdaptersFactoryService>()?.Value;

                if (editorAdapter == null)
                {
                    return null;
                }

                IWpfTextView? wpfView = editorAdapter.GetWpfTextView(vsTextView);

                if (wpfView == null)
                {
                    return null;
                }

                // Get file path
                string filePath = string.Empty;
                if (wpfView.TextDataModel.DocumentBuffer.Properties.TryGetProperty(
                    typeof(ITextDocument), out ITextDocument textDocument))
                {
                    filePath = textDocument.FilePath;
                }

                return new DocumentView(wpfView, filePath);
            }
            catch (Exception ex)
            {
                Logger.Error($"[GetActiveDocumentView] Failed: {ex.Message}", ex);
                return null;
            }
        }

        #endregion

        #region Nested Types

        /// <summary>
        /// Simple wrapper for an active document's text view and file path.
        /// </summary>
        private class DocumentView
        {
            public IWpfTextView TextView { get; }
            public string FilePath { get; }

            public DocumentView(IWpfTextView textView, string filePath)
            {
                TextView = textView;
                FilePath = filePath;
            }
        }

        #endregion
    }
}
