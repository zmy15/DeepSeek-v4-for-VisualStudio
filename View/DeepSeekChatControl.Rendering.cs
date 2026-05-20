using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// WebView2 渲染相关方法：增量/全量页面刷新、流式更新、HTML 构建。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        #region Private Methods - Rendering

        /// <summary>
        /// 增量更新浏览器内容。
        /// 对标 ucChat.UpdateBrowser()：首次使用 NavigateToString，
        /// 后续通过 ExecuteScriptAsync 调用 window.__appendMessageHtml 增量追加。
        /// </summary>
        #pragma warning disable VSTHRD100 // async void 模式用于浏览器更新（fire-and-forget），异常已在方法内处理
        private async void UpdateBrowser()
        {
            if (ChatWebView.CoreWebView2 == null)
                return;

            try
            {
                string allMessages = _messagesHtml.ToString();

                // ── 增量更新路径 ──
                if (_browserInitialized && allMessages.Length > _lastRenderedMessagesLength)
                {
                    string delta = allMessages.Substring(_lastRenderedMessagesLength);
                    string jsFragment = System.Text.Json.JsonSerializer.Serialize(delta);

                    try
                    {
                        string script = $"window.__appendMessageHtml({jsFragment});";
                        await ChatWebView.CoreWebView2.ExecuteScriptAsync(script);
                        _lastRenderedMessagesLength = allMessages.Length;
                        return;
                    }
                    catch
                    {
                        // 增量更新失败时回退到全量刷新
                    }
                }

                // ── 全量刷新路径 ──
                string html = ChatHtmlService.BuildInitialPage(_messages);
                ChatWebView.CoreWebView2.NavigateToString(html);
                _browserInitialized = true;
                _lastRenderedMessagesLength = allMessages.Length;
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] UpdateBrowser 异常: {ex.Message}", ex);
            }
        }
        #pragma warning restore VSTHRD100

        /// <summary>
        /// 构建消息 HTML 片段并追加到 _messagesHtml，然后更新浏览器。
        /// </summary>
        private void AddMessagesHtml(string role, string content, string? reasoningContent = null, List<FileParseResult>? attachedFiles = null, int messageIndex = -1, bool isHtml = false)
        {
            // 自动推断消息索引
            if (messageIndex < 0)
                messageIndex = _messages.Count - 1;

            if (role == "user")
            {
                _messagesHtml.Append(ChatHtmlService.BuildUserMessageHtml(content, attachedFiles, messageIndex));
            }
            else
            {
                var tempMsg = new ChatMessage
                {
                    Role = "assistant",
                    Content = content,
                    ReasoningContent = reasoningContent ?? string.Empty,
                    IsStreaming = false,
                    IsHtml = isHtml,
                };
                _messagesHtml.Append(ChatHtmlService.BuildAssistantMessageHtml(tempMsg, _messages.Count - 1));
            }
        }

        /// <summary>
        /// CoreWebView2 初始化完成回调。
        /// </summary>
        private void ChatWebView_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                Logger.Info("[Render] CoreWebView2InitializationCompleted: 成功");
                ChatWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                ChatWebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;

                // 构建初始 HTML 内容
                RebuildMessagesHtml();
                _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    UpdateBrowser();
                });
            }
            else
            {
                Logger.Error($"[Render] CoreWebView2 初始化失败: {e.InitializationException?.Message}", e.InitializationException);
                _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    StatusLabel.Text = $"WebView2 初始化失败: {e.InitializationException?.Message}";
                });
            }
        }

        /// <summary>
        /// 根据 _messages 列表重建 _messagesHtml。
        /// </summary>
        private void RebuildMessagesHtml()
        {
            _messagesHtml.Clear();
            for (int i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i];
                if (msg.Role == "user")
                {
                    _messagesHtml.Append(ChatHtmlService.BuildUserMessageHtml(
                        msg.Content ?? string.Empty,
                        msg.AttachedFiles.Count > 0 ? msg.AttachedFiles : null,
                        i));
                    // ── 编辑产生的分支导航（在用户气泡下方）──
                    if (msg.SiblingCount > 1)
                        _messagesHtml.Append(ChatHtmlService.BuildBranchNavHtml(msg, i));
                }
                else
                {
                    _messagesHtml.Append(ChatHtmlService.BuildAssistantMessageHtml(msg, i));
                }
            }
            _lastRenderedMessagesLength = 0;
        }

        /// <summary>
        /// 通过 JS 增量更新流式消息的 DOM 内容（旧版 ExecuteScriptAsync 路径，保留兼容）。
        /// </summary>
        private async Task UpdateStreamingMessageAsync(int messageIndex, string content, string reasoningContent, bool isComplete)
        {
            if (ChatWebView.CoreWebView2 == null) return;

            try
            {
                string js = ChatHtmlService.BuildStreamingUpdateJs(messageIndex, content, reasoningContent, isComplete);
                await ChatWebView.CoreWebView2.ExecuteScriptAsync(js);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] UpdateStreamingMessage 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 通过 PostWebMessageAsString 非阻塞推送流式更新（高性能路径）。
        /// PostWebMessageAsString 不等待 JS 执行完成，不阻塞 UI 线程。
        /// JS 侧通过 requestAnimationFrame 批量处理 DOM 更新。
        /// </summary>
        private void PostStreamingUpdate(int messageIndex, string content, string reasoningContent, bool isComplete, string? statusText = null)
        {
            if (ChatWebView.CoreWebView2 == null) return;

            try
            {
                string json = ChatHtmlService.BuildStreamUpdateJson(messageIndex, content, reasoningContent, isComplete, statusText);
                ChatWebView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] PostStreamingUpdate 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 通过 PostWebMessageAsString 非阻塞推送流式完成（含 Markdown 渲染 HTML）。
        /// </summary>
        private void PostStreamEnd(int messageIndex, string fullContent, string reasoningContent, string? extraFooterHtml = null)
        {
            if (ChatWebView.CoreWebView2 == null) return;

            try
            {
                string json = ChatHtmlService.BuildStreamEndJson(messageIndex, fullContent, reasoningContent, extraFooterHtml);
                ChatWebView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] PostStreamEnd 异常: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 通过 PostWebMessageAsString 非阻塞更新状态栏文本。
        /// </summary>
        private void PostStatusUpdate(string statusText)
        {
            if (ChatWebView.CoreWebView2 == null) return;

            try
            {
                string json = ChatHtmlService.BuildStatusUpdateJson(statusText);
                ChatWebView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Render] PostStatusUpdate 异常: {ex.Message}", ex);
            }
        }

        // ── 批处理流式更新已在 DeepSeekChatControl.xaml.cs 中实现（BatchStreamingUpdate 方法）──

        #endregion
    }
}
