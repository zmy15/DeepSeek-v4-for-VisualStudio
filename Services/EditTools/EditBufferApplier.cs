using DeepSeek_v4_for_VisualStudio.Models;
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
using System.Collections.Generic;
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
