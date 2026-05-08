using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.ToolWindows;
using DeepSeek_v4_for_VisualStudio.Utils;
using EnvDTE;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
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
        /// 将 AI 生成的代码一键写入指定文件。
        /// </summary>
        /// <param name="code">要写入的代码</param>
        /// <param name="filePath">目标文件路径（可选，为空则使用当前活动文档）</param>
        /// <param name="showDiff">写入前是否显示 diff 预览</param>
        public async Task WriteCodeToFileAsync(string code, string? filePath = null, bool showDiff = true)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                StatusLabel.Text = "⚠️ 没有代码可写入";
                Logger.Info("[CodeAction] WriteCodeToFileAsync: 代码为空，跳过");
                return;
            }

            Logger.Info($"[CodeAction] WriteCodeToFileAsync: 开始写入, 代码长度={code.Length}, filePath={filePath ?? "(auto-detect)"}, showDiff={showDiff}");

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
                        // 文件不存在：可能是新文件或路径仅作为标识，仍然尝试写入
                        Logger.Info($"[CodeAction] 目标文件不存在于磁盘，将创建: {targetPath}");
                    }

                    // 显示 diff 预览（仅当原内容存在时）
                    if (showDiff && !string.IsNullOrEmpty(oldContent))
                    {
                        StatusLabel.Text = "正在生成差异预览…";
                        Logger.Info($"[CodeAction] 目标文件已存在 ({targetPath}), 生成 diff 预览");
                        string diffHtml = await GenerateDiffHtmlAsync(oldContent, code, targetPath);
                        await ShowDiffInChatAsync(diffHtml);
                        return; // 等待用户确认后再写入
                    }

                    // 直接写入
                    Logger.Info($"[CodeAction] 直接写入到: {targetPath}");
                    await PerformWriteAsync(targetPath, code, oldContent);
                    return;
                }

                // ── filePath 为空：尝试多种方式获取活动文档路径 ──
                targetPath = GetActiveDocumentPath();

                if (!string.IsNullOrEmpty(targetPath))
                {
                    // 读取当前活动文档内容
                    oldContent = ReadActiveDocumentContent();

                    // 显示 diff 预览
                    if (showDiff && !string.IsNullOrEmpty(oldContent))
                    {
                        StatusLabel.Text = "正在生成差异预览…";
                        Logger.Info($"[CodeAction] 目标文件已存在 ({targetPath}), 生成 diff 预览");
                        string diffHtml = await GenerateDiffHtmlAsync(oldContent, code, targetPath);
                        await ShowDiffInChatAsync(diffHtml);
                        return; // 等待用户确认后再写入
                    }

                    // 直接写入
                    Logger.Info($"[CodeAction] 直接写入到: {targetPath}");
                    await PerformWriteAsync(targetPath, code, oldContent);
                }
                else
                {
                    // 没有活动文档，让用户选择文件
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
        /// 在编辑器中替换选中代码（或全部内容）。
        /// </summary>
        public async Task ReplaceCodeInEditorAsync(string newCode, bool replaceAll = false)
        {
            Logger.Info($"[CodeAction] ReplaceCodeInEditorAsync: replaceAll={replaceAll}, 代码长度={newCode?.Length ?? 0}");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                // ── 方案1：DTE ActiveDocument ──
                var dte = (DTE)Package.GetGlobalService(typeof(DTE));
                var doc = dte?.ActiveDocument;
                if (doc != null)
                {
                    var textDoc = (TextDocument)doc.Object("TextDocument");
                    var selection = textDoc.Selection as TextSelection;

                    if (selection != null && !selection.IsEmpty && !replaceAll)
                    {
                        selection.Text = newCode;
                        StatusLabel.Text = "✅ 代码已替换选中内容";
                        Logger.Info("[CodeAction] 已替换编辑器选中内容（DTE）");
                    }
                    else
                    {
                        var editPoint = textDoc.StartPoint.CreateEditPoint();
                        editPoint.ReplaceText(textDoc.EndPoint, newCode, 0);
                        StatusLabel.Text = "✅ 代码已写入文件";
                        Logger.Info("[CodeAction] 已替换编辑器全部内容（DTE）");
                    }

                    Logger.Info("代码已成功写入编辑器");
                    return;
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

                                StatusLabel.Text = "✅ 代码已写入文件";
                                Logger.Info("[CodeAction] 已替换编辑器内容（IVsTextManager）");
                                return;
                            }
                        }
                    }
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
        /// 生成 diff 的 HTML 内容。
        /// </summary>
        private Task<string> GenerateDiffHtmlAsync(string oldContent, string newContent, string filePath)
        {
            return Task.Run(() =>
            {
                var diffLines = CodeDiffService.ComputeDiff(oldContent, newContent);
                string fileName = Path.GetFileName(filePath);
                return CodeDiffService.BuildDiffHtml(diffLines, filePath, filePath);
            });
        }

        /// <summary>
        /// 在聊天窗口中显示 diff 预览。
        /// 通过 WebView2 消息将 diff HTML 注入到页面中。
        /// </summary>
        private async Task ShowDiffInChatAsync(string diffHtml)
        {
            if (ChatWebView.CoreWebView2 == null) return;

            try
            {
                string escapedHtml = System.Text.Json.JsonSerializer.Serialize(diffHtml);

                string js = $@"
(function() {{
    // 创建 diff 预览模态框
    var overlay = document.createElement('div');
    overlay.className = 'diff-overlay';
    overlay.id = 'diff-overlay';
    overlay.onclick = function(e) {{
        if (e.target === overlay) closeDiffPreview();
    }};

    var modal = document.createElement('div');
    modal.className = 'diff-modal';
    modal.innerHTML = {escapedHtml} + `
        <div class='diff-actions'>
            <button class='diff-btn-cancel' onclick='closeDiffPreview()'>取消</button>
            <button class='diff-btn-apply' onclick='confirmApplyCode()'>✅ 确认应用</button>
        </div>
    `;

    overlay.appendChild(modal);
    document.body.appendChild(overlay);

    // 滚动到 diff 区域
    setTimeout(function() {{
        modal.scrollIntoView({{ behavior: 'smooth', block: 'center' }});
    }}, 100);
}})();

function closeDiffPreview() {{
    var overlay = document.getElementById('diff-overlay');
    if (overlay) overlay.remove();
    window.chrome.webview.postMessage({{ type: 'diffCancelled' }});
}}

function confirmApplyCode() {{
    var overlay = document.getElementById('diff-overlay');
    if (overlay) overlay.remove();
    window.chrome.webview.postMessage({{ type: 'diffConfirmed' }});
}}";

                await ChatWebView.CoreWebView2.ExecuteScriptAsync(js);
                StatusLabel.Text = "📊 差异预览已显示 — 请确认或取消";
            }
            catch (Exception ex)
            {
                Logger.Error($"显示 diff 预览失败: {ex.Message}", ex);
                StatusLabel.Text = $"❌ Diff 预览失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 在聊天消息中直接嵌入 diff 视图（非弹窗模式）。
        /// </summary>
        private async Task ShowDiffInlineAsync(string diffHtml)
        {
            if (ChatWebView.CoreWebView2 == null) return;

            try
            {
                string escapedHtml = System.Text.Json.JsonSerializer.Serialize(diffHtml);

                string js = $@"
(function() {{
    var container = document.getElementById('chat-container');
    if (!container) return;

    var diffDiv = document.createElement('div');
    diffDiv.className = 'diff-inline-container';
    diffDiv.innerHTML = {escapedHtml} + `
        <div class='diff-actions'>
            <button class='diff-btn-cancel' onclick='this.parentElement.parentElement.remove()'>取消</button>
            <button class='diff-btn-apply' onclick='confirmInlineApply(this)'>✅ 应用变更</button>
        </div>
    `;
    container.appendChild(diffDiv);
    window.scrollTo({{ top: document.body.scrollHeight, behavior: 'smooth' }});
}})();

function confirmInlineApply(el) {{
    var code = el.getAttribute('data-code') || '';
    var filePath = el.getAttribute('data-file') || '';
    window.chrome.webview.postMessage({{
        type: 'confirmApply',
        code: code,
        filePath: filePath
    }});
    el.parentElement.parentElement.remove();
}}";

                await ChatWebView.CoreWebView2.ExecuteScriptAsync(js);
            }
            catch (Exception ex)
            {
                Logger.Error($"显示内联 diff 失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 执行实际的写入操作（使用 VS SDK API，集成撤销历史）。
        /// </summary>
        private async Task PerformWriteAsync(string filePath, string code, string? oldContent)
        {
            Logger.Info($"[CodeAction] PerformWriteAsync: 写入 {filePath}, 代码长度={code?.Length ?? 0}, 原内容长度={oldContent?.Length ?? 0}");

            try
            {
                string? error = await TerminalWindowHelper.WriteCodeToFileAsync(filePath, code ?? string.Empty);

                if (error != null)
                {
                    Logger.Error($"[CodeAction] 写入失败: {error}");
                    StatusLabel.Text = $"❌ 写入失败: {error}";
                    return;
                }

                int addLines = diffLinesAdd(oldContent, code);
                int delLines = diffLinesDel(oldContent, code);
                string fileName = Path.GetFileName(filePath);
                StatusLabel.Text = $"✅ 已写入 {fileName}" +
                    (addLines > 0 || delLines > 0 ? $" (+{addLines} -{delLines} 行变化)" : "");
                Logger.Info($"[CodeAction] 写入完成: {filePath}, +{addLines} -{delLines} 行变化");
            }
            catch (Exception ex)
            {
                Logger.Error($"[CodeAction] 执行写入失败: {ex.Message}", ex);
                StatusLabel.Text = $"❌ 写入失败: {ex.Message}";
            }
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
