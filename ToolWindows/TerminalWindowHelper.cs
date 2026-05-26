using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
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

namespace DeepSeek_v4_for_VisualStudio.ToolWindows
{
    /// <summary>
    /// 工具辅助类：将 AI 生成的代码写入活动文档，并为代码变更生成差异对比视图。
    /// </summary>
    public static class TerminalWindowHelper
    {
        /// <summary>
        /// 全局开关：设为 true 时抑制所有 diff 预览。
        /// EditAgent 在批量修改期间设为 true，流程结束时再统一显示一次最终 diff。
        /// </summary>
        public static bool SuppressDiffPreview { get; set; }

        /// <summary>
        /// 在流程结束时统一显示一次最终 diff 预览。
        /// 比较原始内容和最终内容，在编辑器中激活差异标记。
        /// </summary>
        /// <param name="oldContent">修改前的内容（新建文件传空字符串）</param>
        /// <param name="newContent">修改后的最终内容</param>
        /// <param name="filePath">文件完整路径</param>
        public static async Task ShowFinalDiffAsync(string oldContent, string newContent, string filePath)
        {
            if (oldContent == newContent) return;

            bool showDiff = DeepSeekOptionsPage.Instance?.ShowDiffMarkersInEditor ?? true;
            if (!showDiff) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var wpfView = FindWpfTextViewForFile(filePath);
                if (wpfView != null)
                {
                    EditorDiffMarkerService.Instance.BeginDiffPreview(wpfView, oldContent, newContent);
                    Logger.Info($"[WriteCode] 最终 diff 预览已激活: {Path.GetFileName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[WriteCode] 激活最终 diff 预览失败: {ex.Message}");
            }
        }

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

            // ── 代码内容合法性检测：防止 AI 写入描述/注释替代实际代码 ──
            if (!string.IsNullOrEmpty(newContent)
                && !Utils.CodeContentValidator.IsProbablySourceCode(filePath, newContent))
            {
                string lang = Utils.CodeContentValidator.GetLanguageDescription(filePath);
                string msg = $"❌ 文件写入被拒绝: `{Path.GetFileName(filePath)}` 的内容不像是合法的 {lang} 源代码。" +
                    "\n请写入实际可编译的代码，严禁用自然语言描述、TODO 注释、文档摘要或功能说明替代代码。";
                Logger.Warn($"[WriteCode] {msg}");
                return msg;
            }

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
                    // 创建空文件（调用方应在此时已将文件加入 VS 项目，以便后续 VS SDK 路径可用）
                    await Task.Run(() => File.WriteAllText(filePath, string.Empty, System.Text.Encoding.UTF8));
                    Logger.Info($"[WriteCode] 创建新文件: {Path.GetFileName(filePath)}");
                }

                // ── 获取 editorAdapter（整个方法共用）──
                var componentModel = (IComponentModel)ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel));
                IVsEditorAdaptersFactoryService? editorAdapter = componentModel?.DefaultExportProvider
                    .GetExport<IVsEditorAdaptersFactoryService>()?.Value;

                if (editorAdapter == null && componentModel != null)
                {
                    Logger.Warn("[WriteCode] IComponentModel 可用但 IVsEditorAdaptersFactoryService 获取失败");
                }
                else if (componentModel == null)
                {
                    Logger.Warn("[WriteCode] IComponentModel 不可用（ServiceProvider.GlobalProvider 可能尚未完全初始化）");
                }

                // ── 方案一：文件已在 VS 编辑器中打开 → 直接操作其 ITextBuffer ──
                if (editorAdapter != null)
                {
                    ITextBuffer? targetBuffer = FindOpenTextBufferForFile(filePath, editorAdapter);
                    if (targetBuffer != null)
                    {
                        // 读取旧内容（用于 diff 预览）
                        string oldContent = targetBuffer.CurrentSnapshot.GetText();

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
                            textDoc.Save();
                        }

                        // 触发编辑器内 diff 预览
                        TryBeginDiffPreview(oldContent, newContent, filePath);

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
                                string oldContent = textBuffer.CurrentSnapshot.GetText();

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
                                    textDoc.Save();
                                }

                                // 文件未在编辑器中打开，注册待处理 diff
                                TryRegisterPendingDiff(oldContent, newContent, filePath);

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
        /// 对已打开的编辑器文件尝试触发 diff 预览（红绿行标记 + 确认/撤销工具栏）。
        /// </summary>
        private static void TryBeginDiffPreview(string oldContent, string newContent, string filePath)
        {
            if (SuppressDiffPreview) return;

            // 空旧内容表示新建文件，仍应显示 diff（全部为新增行）
            if (oldContent == newContent)
                return;

            bool showDiff = DeepSeekOptionsPage.Instance?.ShowDiffMarkersInEditor ?? true;
            if (!showDiff)
                return;

            try
            {
                var wpfView = FindWpfTextViewForFile(filePath);
                if (wpfView != null)
                {
                    EditorDiffMarkerService.Instance.BeginDiffPreview(wpfView, oldContent, newContent);
                    Logger.Info($"[WriteCode] 编辑器内 diff 预览已激活: {Path.GetFileName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[WriteCode] 激活 diff 预览失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 通过枚举 VS 所有打开的文档窗口，查找指定文件路径对应的 <see cref="IWpfTextView"/>。
        /// </summary>
        private static IWpfTextView? FindWpfTextViewForFile(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var uiShell = (IVsUIShell?)ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell));
                if (uiShell == null) return null;

                uiShell.GetDocumentWindowEnum(out IEnumWindowFrames? enumFrames);
                if (enumFrames == null) return null;

                var componentModel = (IComponentModel)ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel));
                var editorAdapter = componentModel?.DefaultExportProvider
                    .GetExport<IVsEditorAdaptersFactoryService>()?.Value;
                if (editorAdapter == null) return null;

                IVsWindowFrame[] frames = new IVsWindowFrame[1];

                while (enumFrames.Next(1, frames, out uint fetched) == VSConstants.S_OK && fetched == 1)
                {
                    var frame = frames[0];

                    // 获取文档的完整路径
                    object pathObj;
                    frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out pathObj);
                    string? framePath = pathObj as string;

                    if (string.IsNullOrEmpty(framePath) ||
                        !string.Equals(framePath, filePath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // 路径匹配 → 获取 IVsTextView
                    var vsTextView = VsShellUtilities.GetTextView(frame);
                    if (vsTextView != null)
                    {
                        var wpfView = editorAdapter.GetWpfTextView(vsTextView);
                        if (wpfView != null)
                            return wpfView;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[WriteCode] 查找 IWpfTextView 失败: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 对未在编辑器中打开的文件注册待处理 diff，用户稍后打开文件时自动激活预览。
        /// </summary>
        private static void TryRegisterPendingDiff(string oldContent, string newContent, string filePath)
        {
            if (SuppressDiffPreview) return;

            if (string.IsNullOrEmpty(oldContent) || oldContent == newContent)
                return;

            bool showDiff = DeepSeekOptionsPage.Instance?.ShowDiffMarkersInEditor ?? true;
            if (!showDiff)
                return;

            try
            {
                EditorDiffMarkerService.Instance.RegisterPendingDiff(filePath, oldContent, newContent);
                Logger.Info($"[WriteCode] 已注册待处理 diff: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[WriteCode] 注册待处理 diff 失败: {ex.Message}");
            }
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
