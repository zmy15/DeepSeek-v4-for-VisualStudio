using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.EditTools
{
    /// <summary>
    /// VS 编辑器缓冲区应用器 — 将 TextEdit 应用到已打开的 VS 文档编辑器。
    /// 从 EditPatchService 的 #region VS Editor Integration 提取。
    /// 
    /// 参考: vscode-copilot-chat applyPatchTool.tsx (textEdit application)
    /// </summary>
    public static class EditBufferApplier
    {
        /// <summary>
        /// 统一写入入口（异步版）：将完整内容写入"已在编辑器中打开"的文档。
        ///
        /// 已打开的文档永远"通过 buffer 写、用编辑器自己的 Save 持久化"：
        /// 1. 在一个撤销事务中整体替换 buffer 内容（保留一步 Ctrl+Z）；
        /// 2. 通过 ITextDocument.Save() 持久化 —— 编辑器主动保存不会被 VS 当作外部更改，
        ///    buffer 保存后回到 clean，"文件已在磁盘上修改"弹窗的条件永远不成立。
        ///
        /// 未打开的文档不适用此入口（返回 false），调用方应回退 File.WriteAllText 裸写盘。
        /// </summary>
        /// <returns>true = 文档已打开且写入+保存成功；false = 未打开或失败，调用方回退磁盘写入。</returns>
        public static async Task<bool> TryWriteOpenDocumentAsync(string filePath, string fullContent)
        {
            if (string.IsNullOrWhiteSpace(filePath) || fullContent == null)
                return false;

            // 契约：任何失败都返回 false（调用方回退磁盘写入），绝不抛出中断调用链。
            // 无 VS 宿主环境（如单元测试进程）中 JoinableTaskFactory 不可用会抛 NRE，同样回退。
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BufferWriter] 切换 UI 线程失败（回退磁盘写入）: {Path.GetFileName(filePath)} — {ex.Message}");
                return false;
            }

            return WriteToOpenDocumentOnUIThread(filePath, fullContent);
        }

        /// <summary>
        /// 统一写入入口（同步版）：语义同 <see cref="TryWriteOpenDocumentAsync"/>。
        /// 供 StagedEditWorkspace 等同步路径以 Func&lt;string,string,bool&gt; 委托注入使用：
        /// 已在 UI 线程则直接执行；否则通过 JoinableTaskFactory 切换到 UI 线程。
        /// </summary>
        public static bool TryWriteOpenDocument(string filePath, string fullContent)
        {
            if (string.IsNullOrWhiteSpace(filePath) || fullContent == null)
                return false;

            if (ThreadHelper.CheckAccess())
                return WriteToOpenDocumentOnUIThread(filePath, fullContent);

            try
            {
                return ThreadHelper.JoinableTaskFactory.Run(
                    () => TryWriteOpenDocumentAsync(filePath, fullContent));
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BufferWriter] 切换 UI 线程写入失败: {Path.GetFileName(filePath)} — {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 若文件已在编辑器中打开，返回当前编辑器 buffer 内容；否则返回 null。
        /// 只读不保存，供 StagedEditWorkspace 在写入前捕获用户未保存内容作为撤销 Baseline。
        /// </summary>
        /// <returns>buffer 内容；文件未打开或读取失败时返回 null。</returns>
        public static string? TryGetOpenDocumentContent(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            if (ThreadHelper.CheckAccess())
                return GetOpenDocumentContentOnUIThread(filePath);

            try
            {
                return ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    return GetOpenDocumentContentOnUIThread(filePath);
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BufferWriter] 切换 UI 线程读取失败: {Path.GetFileName(filePath)} — {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// UI 线程上执行：buffer 整体替换 + 编辑器 Save。
        /// 任何异常都被捕获并返回 false（调用方回退磁盘写入），绝不部分生效后中断。
        /// </summary>
        private static bool WriteToOpenDocumentOnUIThread(string filePath, string fullContent)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var textBuffer = GetTextBufferForFile(filePath);
                if (textBuffer == null)
                    return false; // 未在编辑器中打开 → 调用方回退磁盘写入

                if (!textBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument? textDoc)
                    || textDoc == null)
                    return false;

                // 不做写入前的二次 Save。调用方已通过 TryGetOpenDocumentContent
                // 保留 dirty buffer 作为撤销 Baseline；这里只写最终内容并保存一次。
                ITextUndoTransaction? transaction = null;
                try
                {
                    var undoRegistry = GetUndoHistoryRegistry();
                    if (undoRegistry != null)
                    {
                        var history = undoRegistry.RegisterHistory(textBuffer);
                        transaction = history.CreateTransaction("AI Edit (write-through)");
                    }

                    using (ITextEdit edit = textBuffer.CreateEdit())
                    {
                        ITextSnapshot snapshot = textBuffer.CurrentSnapshot;
                        if (snapshot.Length > 0)
                            edit.Replace(0, snapshot.Length, fullContent);
                        else
                            edit.Insert(0, fullContent);
                        edit.Apply();
                    }

                    transaction?.Complete();
                }
                finally
                {
                    transaction?.Dispose();
                }

                // 编辑器自己的 Save：不会被当作外部更改，保存后 buffer 回到 clean
                textDoc.Save();

                Logger.Info($"[BufferWriter] 已通过编辑器 buffer 写入并保存: {Path.GetFileName(filePath)}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BufferWriter] 写入已打开文档失败: {Path.GetFileName(filePath)} — {ex.Message}");
                return false;
            }
        }

        /// <summary>UI 线程上执行：读取已打开文档的当前 buffer 内容。</summary>
        private static string? GetOpenDocumentContentOnUIThread(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var textBuffer = GetTextBufferForFile(filePath);
                if (textBuffer != null)
                {
                    return textBuffer.CurrentSnapshot.GetText();
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[BufferWriter] 读取已打开文档失败: {Path.GetFileName(filePath)} — {ex.Message}");
                return null;
            }
        }

        private static ITextUndoHistoryRegistry? GetUndoHistoryRegistry()
        {
            try
            {
                var componentModel = (IComponentModel?)
                    Package.GetGlobalService(typeof(SComponentModel));
                return componentModel?.DefaultExportProvider
                    .GetExport<ITextUndoHistoryRegistry>()?.Value;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 通过 VS 文本缓冲区将 TextEdit 应用到已打开的文件编辑器。
        /// 使用 ITextEdit 确保整个操作为一个撤销单元。
        /// </summary>
        public static async Task<bool> ApplyEditsToOpenDocumentAsync(
            string filePath, List<TextEditOperation> edits)
        {
            if (edits == null || edits.Count == 0) return true;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var textBuffer = GetTextBufferForFile(filePath);
                if (textBuffer == null)
                {
                    // 文件未在编辑器中打开，文件级操作已由调用方完成
                    return true;
                }

                using (ITextEdit edit = textBuffer.CreateEdit())
                {
                    foreach (var textEdit in edits)
                    {
                        var snapshot = textBuffer.CurrentSnapshot;
                        int startLine = Math.Min(textEdit.StartLine, snapshot.LineCount - 1);
                        int endLine = Math.Min(textEdit.EndLine, snapshot.LineCount - 1);

                        var startLineObj = snapshot.GetLineFromLineNumber(startLine);
                        var endLineObj = snapshot.GetLineFromLineNumber(endLine);

                        int startPos = startLineObj.Start.Position + Math.Min(textEdit.StartColumn,
                            startLineObj.Length);
                        int endPos = endLineObj.Start.Position + Math.Min(textEdit.EndColumn,
                            endLineObj.Length);

                        if (startPos < 0) startPos = 0;
                        if (endPos > snapshot.Length) endPos = snapshot.Length;
                        if (startPos > endPos) startPos = endPos;

                        Span span = new Span(startPos, endPos - startPos);
                        edit.Replace(span, textEdit.NewText);
                    }

                    edit.Apply();
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(LocalizationService.Instance.Format("tool.edit.buffer.applyFailed", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 获取文件的 ITextBuffer（如果文件在 VS 编辑器中打开）。
        /// 通过 IVsRunningDocumentTable 枚举打开文档，使用 IVsEditorAdaptersFactoryService 获取 buffer。
        /// </summary>
        private static ITextBuffer? GetTextBufferForFile(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var rdt = (IVsRunningDocumentTable?)
                    Package.GetGlobalService(typeof(SVsRunningDocumentTable));
                if (rdt == null) return null;

                if (rdt.GetRunningDocumentsEnum(out IEnumRunningDocuments? enumDocs) != VSConstants.S_OK
                    || enumDocs == null)
                    return null;

                var componentModel = (IComponentModel?)
                    Package.GetGlobalService(typeof(SComponentModel));
                var editorAdapter = componentModel?.DefaultExportProvider
                    .GetExport<IVsEditorAdaptersFactoryService>()?.Value;
                if (editorAdapter == null) return null;

                uint[] cookieArray = new uint[1];
                uint fetched;

                while (enumDocs.Next(1, cookieArray, out fetched) == VSConstants.S_OK && fetched == 1)
                {
                    uint cookie = cookieArray[0];

                    if (rdt.GetDocumentInfo(cookie,
                        out uint flags, out uint readLocks, out uint editLocks,
                        out string? docPath, out IVsHierarchy? hierarchy,
                        out uint itemId, out IntPtr docDataPtr) != VSConstants.S_OK)
                        continue;

                    if (docPath == null ||
                        !string.Equals(docPath, filePath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (docDataPtr == IntPtr.Zero) continue;

                    var vsTextBuffer = Marshal.GetObjectForIUnknown(docDataPtr) as IVsTextBuffer;
                    if (vsTextBuffer == null) continue;

                    var textBuffer = editorAdapter.GetDataBuffer(vsTextBuffer);
                    if (textBuffer != null)
                        return textBuffer;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(LocalizationService.Instance.Format("tool.edit.buffer.getFailed", ex.Message));
            }

            return null;
        }
    }
}
