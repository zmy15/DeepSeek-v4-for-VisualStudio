using DeepSeek_v4_for_VisualStudio.Models;
using Markdig;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 将聊天消息列表构建为 HTML 页面，用于 WebView2 (Chromium) 渲染。
    /// 支持增量渲染：初始全页 NavigateToString + 后续 ExecuteScriptAsync 增量追加，
    /// 消除流式输出时的全页刷新闪烁。
    /// </summary>
    public static class ChatHtmlService
    {
        #region Constants

        /// <summary>
        /// Markdig 解析管道：启用高级扩展，禁用原生 HTML（防 XSS）。
        /// </summary>
        private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();

        /// <summary>highlight.js CDN - 语法高亮脚本</summary>
        private const string HighlightJsCdnScript = "https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/highlight.min.js";

        /// <summary>highlight.js CDN - 暗色主题 CSS</summary>
        private const string HighlightJsCdnStyleDark = "https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/styles/github-dark.min.css";

        // ═══ 页面 CSS（暗色主题，与 Turbo 一致的现代风格） ═══
        private const string PageCss = @"
*{box-sizing:border-box;margin:0;padding:0}
body{background-color:#1E1E1E;color:#D4D4D4;font-family:'Segoe UI','Cascadia Code',Consolas,monospace;font-size:13px;line-height:1.6;padding:12px 16px;overflow-wrap:break-word;word-wrap:break-word}
h1,h2,h3,h4,h5,h6{color:#6CAFD9;margin:12px 0 6px;font-weight:600}
h1{font-size:1.4em;border-bottom:1px solid #444;padding-bottom:4px}
h2{font-size:1.25em;border-bottom:1px solid #444;padding-bottom:3px}
h3{font-size:1.1em}
p{margin:4px 0}
a{color:#6CAFD9;text-decoration:none}
a:hover{text-decoration:underline}
strong,b{color:#E8E8E8;font-weight:600}
em,i{font-style:italic;color:#C8C8C8}
code{background-color:#2D2D2D;color:#CE9178;padding:1px 5px;border-radius:3px;font-family:'Cascadia Code',Consolas,monospace;font-size:0.92em}
pre{background-color:#252526;border-radius:6px;padding:28px 12px 10px 12px;margin:8px 0;overflow-x:auto;font-size:0.9em;line-height:1.5;position:relative}
pre code{background:transparent;color:#D4D4D4;padding:0;font-size:inherit;white-space:pre;display:block}
ul,ol{padding-left:24px;margin:6px 0}
li{margin:2px 0}
blockquote{border-left:3px solid #6CAFD9;padding:6px 12px;margin:8px 0;background-color:#252526;color:#A0A0A0}
table.msg-table{border-collapse:collapse;margin:8px 0}
th,td{padding:6px 10px;text-align:left;border:none}
th{background:#2D2D2D;color:#E8E8E8;font-weight:600}
hr{border:none;border-top:1px solid #444;margin:12px 0}
img{max-width:100%}
.code-lang{position:absolute;top:4px;left:12px;color:#888;font-size:10px;font-family:'Segoe UI',sans-serif;text-transform:uppercase;letter-spacing:0.5px}
.copy-btn{position:absolute;top:4px;right:8px;background:#3C3C3C;color:#CCC;border:1px solid #555;border-radius:3px;padding:2px 8px;font-size:11px;cursor:pointer;font-family:'Segoe UI',sans-serif;z-index:1}
.copy-btn:hover{background:#4A4A4A;color:#FFF}
.copy-btn.copied{background:#1A3A1A;color:#4EC9B0}
.msg-ai{background:#2D2D2D;border-radius:8px;padding:10px 14px;color:#D4D4D4;font-size:13px;line-height:1.5}
.msg-user{background:#264F78;border-radius:8px;padding:10px 14px;color:#D4D4D4;font-size:13px;line-height:1.5}
/* ── 联网搜索结果卡片 ── */
.search-results-card{margin:6px 0 10px 0;border:1px solid #3A5A8A;border-radius:6px;background:#1A2636;overflow:hidden}
.search-results-card summary{cursor:pointer;padding:6px 12px;color:#7EB8E0;font-size:12px;font-weight:600;background:#253545;user-select:none;list-style:none}
.search-results-card summary::-webkit-details-marker{display:none}
.search-results-card summary::before{content:'🌐 ';margin-right:4px}
.search-results-card summary:hover{color:#A0D0F0}
.search-results-card .search-result-item{padding:6px 12px;border-bottom:1px solid #2A3A4A}
.search-results-card .search-result-item:last-child{border-bottom:none}
.search-results-card .search-result-title{color:#6CAFD9;font-size:12px;font-weight:600;text-decoration:none;display:block;margin-bottom:2px}
.search-results-card .search-result-title:hover{text-decoration:underline}
.search-results-card .search-result-url{color:#608B4E;font-size:10px;display:block;margin-bottom:2px;word-break:break-all}
.search-results-card .search-result-snippet{color:#A0A0A0;font-size:11px;line-height:1.4}
.search-results-card .search-result-date{color:#707070;font-size:10px;display:block;margin-top:2px}
.msg-header{font-weight:600;font-size:11px;margin-bottom:4px}
.msg-header-ai{color:#888}
.msg-header-user{color:#6CAFD9;text-align:right}
.msg-body{word-wrap:break-word;overflow-wrap:break-word}
.avatar{display:inline-flex;align-items:center;justify-content:center;width:32px;height:32px;border-radius:50%;font-weight:bold;font-size:14px;flex-shrink:0}
.avatar-ai{background:#4EC9B0;color:#1E1E1E}
.avatar-user{background:#569CD6;color:#fff}
/* ── 思考面板样式 ── */
.reasoning-panel{margin:6px 0;border:1px solid #3A3A6A;border-radius:6px;background:#1A1A2E;overflow:hidden}
.reasoning-panel summary{cursor:pointer;padding:6px 12px;color:#8A8AD4;font-size:12px;font-weight:600;background:#252545;user-select:none;list-style:none}
.reasoning-panel summary::-webkit-details-marker{display:none}
.reasoning-panel summary::before{content:'🧠 ';margin-right:4px}
.reasoning-panel summary:hover{color:#A0A0D0}
.reasoning-panel .reasoning-content{padding:8px 12px;color:#8A8AB4;font-size:12px;font-style:italic;line-height:1.5;white-space:pre-wrap;max-height:300px;overflow-y:auto}
/* ── 流式光标闪烁 ── */
@keyframes blink{0%,100%{opacity:1}50%{opacity:0}}
.streaming-cursor{display:inline-block;width:8px;height:15px;background:#6CAFD9;margin-left:2px;animation:blink 0.8s infinite;vertical-align:text-bottom}
::-webkit-scrollbar{width:8px;height:8px}
::-webkit-scrollbar-track{background:#1E1E1E}
::-webkit-scrollbar-thumb{background:#444;border-radius:4px}
::-webkit-scrollbar-thumb:hover{background:#555}

/* ── 操作按钮（重试/编辑） ── */
.msg-action-btn{display:inline-flex;align-items:center;gap:3px;background:transparent;border:1px solid #444;color:#888;cursor:pointer;font-size:11px;padding:2px 8px;border-radius:3px;margin-top:6px;transition:all .15s;opacity:0}
.msg-action-btn:hover{background:#3C3C3C;color:#D4D4D4;border-color:#666}
.msg-user:hover .msg-action-btn,.msg-ai:hover .msg-action-btn{opacity:1}
.msg-action-btn.retry-btn:hover{color:#6CAFD9;border-color:#6CAFD9}
.msg-action-btn.edit-btn:hover{color:#CE9178;border-color:#CE9178}

/* ── 版本导航栏 ── */
.version-nav{display:flex;align-items:center;gap:6px;margin-top:4px;font-size:11px;color:#888;user-select:none}
.version-nav-btn{background:transparent;border:1px solid #444;color:#888;cursor:pointer;font-size:11px;padding:1px 6px;border-radius:3px;transition:all .15s}
.version-nav-btn:hover:not(:disabled){background:#3C3C3C;color:#D4D4D4;border-color:#666}
.version-nav-btn:disabled{opacity:.3;cursor:default}
.version-nav-label{color:#888;min-width:30px;text-align:center}
.version-nav .sep{color:#555;margin:0 2px}
";

        private const string AiAvatarHtml = "<span class='avatar avatar-ai'>AI</span>";
        private const string UserAvatarHtml = "<span class='avatar avatar-user'>U</span>";

        #endregion

        #region Public Methods

        /// <summary>
        /// 构建初始完整 HTML 页面（用于首次 NavigateToString）。
        /// 包含所有消息 + 内嵌 JS 基础设施（增量追加、流式更新、自动滚动等）。
        /// </summary>
        public static string BuildInitialPage(IReadOnlyList<ChatMessage> messages)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < messages.Count; i++)
            {
                var msg = messages[i];
                if (msg.Role == "user")
                    AppendUserMessageHtml(sb, msg.Content ?? string.Empty, msg.AttachedFiles, i);
                else if (msg.Role == "assistant")
                    AppendAssistantMessageHtml(sb, msg, i);
            }

            return WrapFullPage(sb.ToString(), hasStreamingMessage: false);
        }

        /// <summary>
        /// 构建单条用户消息的 HTML 片段（用于增量追加）。
        /// </summary>
        public static string BuildUserMessageHtml(string content, List<FileParseResult>? attachedFiles = null, int messageIndex = -1)
        {
            var sb = new StringBuilder();
            AppendUserMessageHtml(sb, content, attachedFiles, messageIndex);
            return sb.ToString();
        }

        /// <summary>
        /// 构建单条 AI 消息的 HTML 片段（用于增量追加）。
        /// </summary>
        public static string BuildAssistantMessageHtml(ChatMessage msg, int index)
        {
            var sb = new StringBuilder();
            AppendAssistantMessageHtml(sb, msg, index);
            return sb.ToString();
        }

        /// <summary>
        /// 构建流式更新用的 JS 脚本：更新指定索引的 AI 消息内容和思考面板。
        /// 返回的 JS 字符串可直接传给 ExecuteScriptAsync。
        /// </summary>
        /// <param name="messageIndex">消息在列表中的索引（从0开始）。</param>
        /// <param name="streamingContent">当前流式累积的正文内容（纯文本）。</param>
        /// <param name="reasoningContent">当前流式累积的思考内容（纯文本），可为空。</param>
        /// <param name="isComplete">是否流式已完成（完成后移除光标）。</param>
        public static string BuildStreamingUpdateJs(int messageIndex, string streamingContent, string reasoningContent, bool isComplete)
        {
            string escapedContent = EscapeJsString(streamingContent ?? string.Empty);
            string escapedReasoning = EscapeJsString(reasoningContent ?? string.Empty);

            return $@"
(function(){{
    var container=document.getElementById('msg-body-{messageIndex}');
    var reasoningPanel=document.getElementById('reasoning-{messageIndex}');
    var reasoningBody=document.getElementById('reasoning-body-{messageIndex}');
    var cursor=document.getElementById('cursor-{messageIndex}');

    if(container){{
        // 流式文本：HTML 编码 + 换行转 &lt;br&gt;
        var text={escapedContent};
        var html=text.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/\n/g,'<br>');
        container.innerHTML=html;
    }}

    if(reasoningPanel && reasoningBody){{
        var rtext={escapedReasoning};
        if(rtext.length>0){{
            reasoningPanel.style.display='block';
            reasoningBody.textContent=rtext;
        }}else{{
            reasoningPanel.style.display='none';
        }}
    }}

    if(cursor){{
        cursor.style.display={(isComplete ? "'none'" : "'inline-block'")};
    }}

    // 自动滚动到底部
    window.scrollTo({{top:document.body.scrollHeight,behavior:'smooth'}});
}})();";
        }

        /// <summary>
        /// 构建流式完成后替换为完整 Markdown 渲染的 JS 脚本。
        /// </summary>
        public static string BuildFinalRenderJs(int messageIndex, string fullContent, string reasoningContent)
        {
            // 在 C# 侧完成 Markdown → HTML 渲染
            string bodyHtml = RenderMarkdownToHtml(fullContent ?? string.Empty);
            string escapedBody = EscapeJsString(bodyHtml);

            string reasoningHtml = string.IsNullOrWhiteSpace(reasoningContent)
                ? string.Empty
                : RenderReasoningContentHtml(reasoningContent);
            string escapedReasoningHtml = EscapeJsString(reasoningHtml);

            return $@"
(function(){{
    var container=document.getElementById('msg-body-{messageIndex}');
    var reasoningPanel=document.getElementById('reasoning-{messageIndex}');
    var reasoningBody=document.getElementById('reasoning-body-{messageIndex}');
    var cursor=document.getElementById('cursor-{messageIndex}');
    var msgDiv=document.getElementById('msg-{messageIndex}');

    if(container){{
        container.innerHTML={escapedBody};
    }}

    if(cursor) cursor.style.display='none';

    if(reasoningPanel && reasoningBody){{
        var rhtml={escapedReasoningHtml};
        if(rhtml.length>0){{
            reasoningPanel.style.display='block';
            reasoningBody.innerHTML=rhtml;
        }}else{{
            reasoningPanel.style.display='none';
        }}
    }}

    // 重新为代码块添加按钮和语言标签
    if(msgDiv) decorateCodeBlocks(msgDiv);

    window.scrollTo({{top:document.body.scrollHeight,behavior:'smooth'}});
}})();";
        }

        /// <summary>
        /// 获取用于嵌入初始页面的"装饰代码块"JS 函数定义。
        /// </summary>
        public static string GetDecorateCodeBlocksJsFunction()
        {
            return BuildCodeLangLabelsJs() + BuildCopyBtnJs();
        }

        /// <summary>
        /// 构建联网搜索结果的 HTML 卡片（可折叠）。
        /// 用于在 AI 回复之前展示搜索到的网页结果。
        /// </summary>
        /// <param name="results">搜索结果列表</param>
        /// <param name="providerName">搜索提供商名称（如 "百度搜索"、"DuckDuckGo"）</param>
        /// <returns>搜索结果卡片的 HTML 字符串</returns>
        public static string BuildSearchResultsHtml(IReadOnlyList<WebSearchResult> results, string providerName = "联网搜索")
        {
            if (results == null || results.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.Append("<details class='search-results-card' open='true'>");
            sb.Append($"<summary>🌐 {providerName} ({results.Count} 条结果)</summary>");

            foreach (var result in results)
            {
                string escapedTitle = System.Net.WebUtility.HtmlEncode(result.Title ?? string.Empty);
                string escapedUrl = System.Net.WebUtility.HtmlEncode(result.Url ?? string.Empty);
                string escapedSnippet = System.Net.WebUtility.HtmlEncode(result.Snippet ?? string.Empty);
                string escapedDate = System.Net.WebUtility.HtmlEncode(result.Date ?? string.Empty);

                sb.Append("<div class='search-result-item'>");
                sb.Append($"<a class='search-result-title' href='{escapedUrl}' target='_blank'>{escapedTitle}</a>");
                sb.Append($"<span class='search-result-url'>{escapedUrl}</span>");
                sb.Append($"<div class='search-result-snippet'>{escapedSnippet}</div>");
                if (!string.IsNullOrWhiteSpace(result.Date))
                    sb.Append($"<span class='search-result-date'>📅 {escapedDate}</span>");
                sb.Append("</div>");
            }

            sb.Append("</details>");
            return sb.ToString();
        }

        /// <summary>
        /// 构建用于 JS 注入的搜索结果卡片脚本。
        /// 将搜索结果卡片插入到指定 AI 消息的上方。
        /// </summary>
        public static string BuildSearchResultsInjectionJs(int messageIndex, IReadOnlyList<WebSearchResult> results, string providerName = "联网搜索")
        {
            string cardHtml = BuildSearchResultsHtml(results, providerName);
            string escapedCard = EscapeJsString(cardHtml);

            return $@"
(function(){{
    var msgDiv=document.getElementById('msg-{messageIndex}');
    if(!msgDiv)return;
    var existing=document.getElementById('search-card-{messageIndex}');
    if(existing)existing.remove();
    var temp=document.createElement('div');
    temp.id='search-card-{messageIndex}';
    temp.innerHTML={escapedCard};
    msgDiv.parentNode.insertBefore(temp,msgDiv);
    window.scrollTo({{top:document.body.scrollHeight,behavior:'smooth'}});
}})();";
        }

        #endregion

        #region Private Methods - Message HTML Builders

        private static void AppendUserMessageHtml(StringBuilder sb, string content, List<FileParseResult>? attachedFiles = null, int messageIndex = -1)
        {
            // ── 编辑按钮（仅在有索引时渲染） ──
            string editBtnHtml = messageIndex >= 0
                ? $"<button class='msg-action-btn edit-btn' onclick='window.__editMessage({messageIndex})' title='编辑此消息'>✏️ 编辑</button>"
                : "";

            // ── 文件附件：可折叠的 &lt;details&gt; 块 ──
            string fileBlocksHtml = string.Empty;
            if (attachedFiles != null && attachedFiles.Count > 0)
            {
                var blocks = new StringBuilder();
                blocks.Append("<div style='margin-bottom:8px;text-align:left'>");

                foreach (var file in attachedFiles)
                {
                    string escapedFileName = System.Net.WebUtility.HtmlEncode(file.FileName);

                    if (file.Success && !string.IsNullOrEmpty(file.Content))
                    {
                        // 判断是否为图像文件（OCR 结果）
                        bool isImage = IsImageExtension(file.FileExtension);
                        // 判断是否为 PDF 文件
                        bool isPdf = string.Equals(file.FileExtension, ".pdf", StringComparison.OrdinalIgnoreCase);

                        string lang = isImage ? string.Empty : GetLanguageFromExtension(file.FileExtension);
                        string escapedContent = System.Net.WebUtility.HtmlEncode(
                            (file.Truncated && file.TruncationNote != null
                                ? file.TruncationNote + "\n\n" + file.Content
                                : file.Content) ?? string.Empty);

                        // ── 图像文件：OCR 结果用紫色边框卡片展示 ──
                        if (isImage)
                        {
                            blocks.Append("<details class='file-attachment' style='margin-bottom:4px;border:1px solid #6B3FA0;border-radius:4px;background:#1A1A2E;overflow:hidden'>");
                            blocks.Append("<summary style='cursor:pointer;padding:4px 10px;color:#B98EFF;font-size:12px;font-weight:600;background:#252535;user-select:none;list-style:none'>");
                            blocks.Append("&#128247; ");
                            blocks.Append(escapedFileName);
                            blocks.Append(" <span style='color:#8A8AB4;font-size:10px'>(OCR 识别)</span>");
                        }
                        else if (isPdf)
                        {
                            blocks.Append("<details class='file-attachment' style='margin-bottom:4px;border:1px solid #8B4513;border-radius:4px;background:#1E150A;overflow:hidden'>");
                            blocks.Append("<summary style='cursor:pointer;padding:4px 10px;color:#D4A76A;font-size:12px;font-weight:600;background:#2B1D0E;user-select:none;list-style:none'>");
                            blocks.Append("&#128214; ");
                            blocks.Append(escapedFileName);
                        }
                        else
                        {
                            blocks.Append("<details class='file-attachment' style='margin-bottom:4px;border:1px solid #3A5A3A;border-radius:4px;background:#1A2E1A;overflow:hidden'>");
                            blocks.Append("<summary style='cursor:pointer;padding:4px 10px;color:#7EC87E;font-size:12px;font-weight:600;background:#253525;user-select:none;list-style:none'>");
                            blocks.Append("&#128206; ");
                            blocks.Append(escapedFileName);
                        }

                        if (file.Truncated)
                            blocks.Append(" <span style='color:#C8A84E;font-size:10px'>(已截断)</span>");
                        blocks.Append("</summary>");
                        blocks.Append("<div style='padding:6px 10px;max-height:400px;overflow-y:auto'>");
                        blocks.Append("<pre style='margin:0;background:#1A1E1A;font-size:11px;line-height:1.4;max-height:380px'><code");
                        if (!string.IsNullOrEmpty(lang))
                            blocks.Append(" class='language-" + lang + "'");
                        blocks.Append(">");
                        blocks.Append(escapedContent);
                        blocks.Append("</code></pre>");
                        blocks.Append("</div>");
                        blocks.Append("</details>");
                    }
                    else
                    {
                        // 文件解析失败 → 显示错误标签
                        string errorMsg = System.Net.WebUtility.HtmlEncode(file.Error ?? "解析失败");
                        blocks.Append("<div style='display:inline-block;background:#5C1A1A;color:#E07878;");
                        blocks.Append("padding:3px 10px;border-radius:3px;font-size:11px;margin-bottom:3px'>");
                        blocks.Append("&#128206; ");
                        blocks.Append(escapedFileName);
                        blocks.Append(" &mdash; ");
                        blocks.Append(errorMsg);
                        blocks.Append("</div>");
                    }
                }

                blocks.Append("</div>");
                fileBlocksHtml = blocks.ToString();
            }

            string escaped = System.Net.WebUtility.HtmlEncode((content ?? string.Empty).Trim());
            string body = escaped.Replace("\n", "<br>");

            sb.Append(
                "<div style='display:flex;justify-content:flex-end;margin-bottom:14px'>" +
                "<div style='max-width:85%;text-align:left'>" +
                fileBlocksHtml +
                "<div class='msg-user' style='display:inline-block;text-align:left'>" +
                "<div class='msg-body'>" + body + "</div>" +
                editBtnHtml +
                "</div>" +
                "</div>" +
                "<div style='margin-left:10px'>" + UserAvatarHtml + "</div>" +
                "</div>");
        }

        private static void AppendAssistantMessageHtml(StringBuilder sb, ChatMessage msg, int idx)
        {
            string bodyHtml;
            bool isStreaming = msg.IsStreaming;

            if (!string.IsNullOrEmpty(msg.Content))
            {
                // 已有内容：若为预渲染 HTML（如Agent计划），直接使用；否则走 Markdown 渲染
                if (msg.IsHtml)
                {
                    bodyHtml = msg.Content;
                }
                else
                {
                    bodyHtml = RenderMarkdownToHtml(msg.Content);
                }
            }
            else if (isStreaming)
            {
                // 流式中但尚内容：显示等待提示
                bodyHtml = "<span style='color:#888;font-style:italic'>思考中…</span>";
            }
            else
            {
                bodyHtml = string.Empty;
            }

            string reasoningHtml = RenderReasoningPanelHtml(msg.ReasoningContent, idx);

            string streamingCursor = isStreaming
                ? "<span class='streaming-cursor' id='cursor-" + idx + "'></span>"
                : "";

            string streamingDots = isStreaming
                ? " <span style='color:#6CAFD9;font-size:10px'>●●●</span>" : "";

            // ── 重试按钮（非流式消息才显示） ──
            string retryBtnHtml = !isStreaming
                ? $"<button class='msg-action-btn retry-btn' onclick='window.__retryMessage({idx})' title='重新生成回答'>🔄 重试</button>"
                : "";

            // ── 版本导航栏（多版本时显示） ──
            string versionNavHtml = "";
            if (!isStreaming && msg.TotalVersions > 1)
            {
                int curVer = msg.VersionIndex;
                versionNavHtml =
                    $"<div class='version-nav'>" +
                    $"<button class='version-nav-btn' onclick='window.__navigateVersion({idx},-1)' title='上一个版本' " + (curVer <= 1 ? "disabled" : "") + ">◀</button>" +
                    $"<span class='version-nav-label'>{curVer}/{msg.TotalVersions}</span>" +
                    $"<button class='version-nav-btn' onclick='window.__navigateVersion({idx},1)' title='下一个版本' " + (curVer >= msg.TotalVersions ? "disabled" : "") + ">▶</button>" +
                    $"</div>";
            }

            sb.Append(
                "<div id='msg-" + idx + "'>" +
                "<table cellpadding='0' cellspacing='0' border='0' width='100%' style='margin-bottom:14px'>" +
                "<tr>" +
                "<td width='36' valign='top'>" + AiAvatarHtml + "</td>" +
                "<td valign='top'><div class='msg-ai'>" +
                "<div class='msg-header msg-header-ai'>DeepSeek" + streamingDots + "</div>" +
                reasoningHtml +
                "<div class='msg-body' id='msg-body-" + idx + "'>" + bodyHtml + "</div>" +
                streamingCursor +
                retryBtnHtml +
                versionNavHtml +
                "</div></td></tr></table>" +
                "</div>");
        }

        /// <summary>
        /// 构建思考面板 HTML（含 details/summary 包装，带 ID）。
        /// </summary>
        private static string RenderReasoningPanelHtml(string reasoningContent, int idx)
        {
            bool hasContent = !string.IsNullOrWhiteSpace(reasoningContent);
            string displayStyle = hasContent ? "block" : "none";
            string body = hasContent ? RenderReasoningContentHtml(reasoningContent) : string.Empty;

            return
                "<details class='reasoning-panel' id='reasoning-" + idx + "' open='true' style='display:" + displayStyle + "'>" +
                "<summary>思考过程</summary>" +
                "<div class='reasoning-content' id='reasoning-body-" + idx + "'>" + body + "</div>" +
                "</details>";
        }

        /// <summary>
        /// 仅构建思考内容的 HTML（无面板包装），用于流式结束后的最终渲染 JS。
        /// </summary>
        private static string RenderReasoningContentHtml(string reasoningContent)
        {
            if (string.IsNullOrWhiteSpace(reasoningContent)) return string.Empty;
            string escaped = System.Net.WebUtility.HtmlEncode(reasoningContent);
            return escaped.Replace("\n", "<br>");
        }

        /// <summary>
        /// 根据文件扩展名获取 Markdown 代码块语言标识。
        /// </summary>
        private static string GetLanguageFromExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension)) return string.Empty;

            return extension.ToLowerInvariant() switch
            {
                ".py" => "python",
                ".cs" => "csharp",
                ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" => "cpp",
                ".java" => "java",
                ".js" or ".jsx" => "javascript",
                ".ts" or ".tsx" => "typescript",
                ".html" or ".htm" => "html",
                ".css" => "css",
                ".xml" or ".xaml" or ".axml" => "xml",
                ".json" => "json",
                ".yaml" or ".yml" => "yaml",
                ".md" => "markdown",
                ".sql" => "sql",
                ".php" => "php",
                ".rb" => "ruby",
                ".go" => "go",
                ".rs" => "rust",
                ".swift" => "swift",
                ".kt" => "kotlin",
                ".scala" => "scala",
                ".lua" => "lua",
                ".sh" or ".bash" => "bash",
                ".ps1" => "powershell",
                ".bat" => "batch",
                ".ini" or ".cfg" or ".conf" or ".editorconfig" => "ini",
                ".r" => "r",
                ".dockerfile" => "dockerfile",
                ".cmake" => "cmake",
                ".proto" => "protobuf",
                ".pdf" => "text",
                _ => string.Empty,
            };
        }

        /// <summary>
        /// 判断文件扩展名是否为图像格式（需要 OCR 处理）。
        /// </summary>
        private static bool IsImageExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension)) return false;

            return extension.ToLowerInvariant() switch
            {
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tiff" or ".tif" or ".webp" => true,
                _ => false,
            };
        }

        /// <summary>
        /// 将 Markdown 文本渲染为 HTML（使用 Markdig）。
        /// 对标 ucChat.AddMessagesHtml 中 AI 消息的处理逻辑。
        /// </summary>
        private static string RenderMarkdownToHtml(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return string.Empty;

            try
            {
                string htmlContent;

                // ── 处理 <think>...</think> 思考块 ──
                Match thinkMatch = Regex.Match(markdown,
                    @"^<think>(?<content>.*)</think>(?<answer>.*)$",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                if (!thinkMatch.Success)
                {
                    htmlContent = Markdown.ToHtml(markdown, MarkdownPipeline);
                }
                else
                {
                    string thinkBody = Markdown.ToHtml(thinkMatch.Groups["content"].Value, MarkdownPipeline);
                    string thinkBlock =
                        $"<details class='reasoning-panel' open='true'><summary>思考过程</summary><div class='reasoning-content'>{thinkBody}</div></details>";
                    string answerHtml = Markdown.ToHtml(thinkMatch.Groups["answer"].Value, MarkdownPipeline);
                    htmlContent = $"{thinkBlock}\n{answerHtml}";
                }

                // ── Mermaid 代码块后处理 ──
                htmlContent = Regex.Replace(htmlContent,
                    @"<pre\s+class=""mermaid[^""]*""[^>]*>(.*?)</pre>",
                    m => WrapMermaidCodeBlock(m.Groups[1].Value),
                    RegexOptions.Singleline);

                htmlContent = Regex.Replace(htmlContent,
                    @"<div class=""lang-mermaid[^""]*"">(.*?)</div>",
                    m => WrapMermaidCodeBlock(m.Groups[1].Value),
                    RegexOptions.Singleline);

                // ── 移除末尾多余的 <br /> ──
                if (htmlContent.EndsWith("<br />"))
                    htmlContent = htmlContent.Substring(0, htmlContent.Length - 6);

                // ── XSS 防护 ──
                htmlContent = htmlContent
                    .Replace("<script", "&lt;script")
                    .Replace("</script>", "&lt;/script&gt;");

                return htmlContent;
            }
            catch
            {
                return "<pre>" + System.Net.WebUtility.HtmlEncode(markdown) + "</pre>";
            }
        }

        private static string WrapMermaidCodeBlock(string inner)
        {
            int svgIdx = inner.IndexOf("<svg", StringComparison.Ordinal);
            if (svgIdx > 0) inner = inner.Substring(0, svgIdx).Trim();
            inner = System.Net.WebUtility.HtmlDecode(inner);
            string escaped = System.Net.WebUtility.HtmlEncode(inner);
            return $"<pre><code class=\"language-mermaid\">{escaped}</code></pre>";
        }

        #endregion

        #region Private Methods - Full Page & JS

        private static string WrapFullPage(string messagesHtml, bool hasStreamingMessage)
        {
            string autoScrollJs = hasStreamingMessage ? BuildAutoScrollJs() : "";

            return "<!DOCTYPE html><html lang='zh-CN'><head><meta charset='UTF-8'>" +
                   "<link rel='stylesheet' href='" + HighlightJsCdnStyleDark + "' />" +
                   "<style>" + PageCss + "</style>" +
                   "<script src='" + HighlightJsCdnScript + "'></script>" +
                   "</head><body><div id='chat-container'>" +
                   messagesHtml + "</div><script>" +
                   BuildDecorateCodeBlocksJsFunction() +
                   BuildDecorateAllCodeBlocksInvocation() +
                   BuildShiftScrollJs() +
                   autoScrollJs +
                   BuildAppendMessageJsFunction() +
                   BuildRetryEditJsFunctions() +
                   "setTimeout(function(){window.scrollTo(0,document.body.scrollHeight);},50);" +
                   "</script></body></html>";
        }

        /// <summary>
        /// 声明 decorateCodeBlocks 函数（语言标签 + highlight.js 语法高亮 + 复制/应用按钮）。
        /// 使用 highlight.js CDN 进行语法高亮
        /// </summary>
        private static string BuildDecorateCodeBlocksJsFunction()
        {
            return @"
window.decorateCodeBlocks=function(container){
    if(!container)return;
    var pres=container.querySelectorAll('pre:not(.mermaid-block)');
    pres.forEach(function(pre){
        if(pre.querySelector('.copy-btn'))return;
        var code=pre.querySelector('code');
        if(!code)return;
        var lang='';
        if(code.className){
            var m=code.className.match(/language-(\w+)/);
            if(m)lang=m[1];
        }
        if(lang){
            var label=document.createElement('span');
            label.className='code-lang';
            label.textContent=lang;
            pre.insertBefore(label,pre.firstChild);
        }
        // highlight.js 语法高亮
        if(window.hljs){
            try{window.hljs.highlightElement(code);}catch(e){}
        }
        // 复制按钮
        var copyBtn=document.createElement('button');
        copyBtn.className='copy-btn';
        copyBtn.textContent='📋 复制';
        copyBtn.title='复制代码到剪贴板';
        copyBtn.onclick=function(){
            var target=pre.querySelector('code')||pre;
            var text=target.innerText,ok=false;
            if(navigator.clipboard&&navigator.clipboard.writeText){
                navigator.clipboard.writeText(text);ok=true;
            }else{
                var ta=document.createElement('textarea');
                ta.value=text;ta.style.cssText='position:fixed;opacity:0';
                document.body.appendChild(ta);ta.select();
                try{document.execCommand('copy');ok=true;}catch(e){}
                document.body.removeChild(ta);
            }
            if(ok){copyBtn.textContent='✓ 已复制';copyBtn.classList.add('copied');}
            setTimeout(function(){copyBtn.textContent='📋 复制';copyBtn.classList.remove('copied');},1500);
        };
        pre.appendChild(copyBtn);
        // 应用按钮 - 直接写入编辑器
        var applyBtn=document.createElement('button');
        applyBtn.className='copy-btn';
        applyBtn.textContent='⚡ 写入';
        applyBtn.title='将代码写入当前活动文档';
        applyBtn.style.right='60px';
        applyBtn.onclick=function(){
            var target=pre.querySelector('code')||pre;
            var codeText=target.innerText;
            try{
                window.chrome.webview.postMessage(JSON.stringify({type:'applyCode',code:codeText}));
            }catch(e1){
                try{
                    window.external.notify(JSON.stringify({type:'applyCode',code:codeText}));
                }catch(e2){}
            }
            applyBtn.textContent='✓ 已写入';
            applyBtn.classList.add('copied');
            setTimeout(function(){applyBtn.textContent='⚡ 写入';applyBtn.classList.remove('copied');},1500);
        };
        pre.appendChild(applyBtn);
        // Diff 按钮 - 显示代码变更
        var diffBtn=document.createElement('button');
        diffBtn.className='copy-btn';
        diffBtn.textContent='📊 对比';
        diffBtn.title='显示原始代码和AI修改后的差异';
        diffBtn.style.right='122px';
        diffBtn.onclick=function(){
            var target=pre.querySelector('code')||pre;
            var codeText=target.innerText;
            try{
                window.chrome.webview.postMessage(JSON.stringify({type:'showDiff',code:codeText}));
            }catch(e1){
                try{
                    window.external.notify(JSON.stringify({type:'showDiff',code:codeText}));
                }catch(e2){}
            }
        };
        pre.appendChild(diffBtn);
    });
};
";
        }

        private static string BuildDecorateAllCodeBlocksInvocation()
        {
            return "window.decorateCodeBlocks(document.getElementById('chat-container'));";
        }

        private static string BuildCodeLangLabelsJs()
        {
            return @"
(function(){'use strict';
var pres=document.querySelectorAll('pre:not(.mermaid-block)');
pres.forEach(function(pre){
    var code=pre.querySelector('code');
    if(!code)return;
    var lang='';
    if(code.className){
        var m=code.className.match(/language-(\w+)/);
        if(m)lang=m[1];
    }
    if(lang){
        var label=document.createElement('span');
        label.className='code-lang';
        label.textContent=lang;
        pre.insertBefore(label,pre.firstChild);
    }
});
})();";
        }

        private static string BuildCopyBtnJs()
        {
            return @"
(function(){
var pres=document.querySelectorAll('pre:not(.mermaid-block)');
pres.forEach(function(pre){
    if(pre.querySelector('.copy-btn'))return;
    var copyBtn=document.createElement('button');
    copyBtn.className='copy-btn';
    copyBtn.textContent='📋 复制';
    copyBtn.title='复制代码到剪贴板';
    copyBtn.onclick=function(){
        var target=pre.querySelector('code')||pre;
        var text=target.innerText,ok=false;
        if(navigator.clipboard&&navigator.clipboard.writeText){
            navigator.clipboard.writeText(text);ok=true;
        }else{
            var ta=document.createElement('textarea');
            ta.value=text;ta.style.cssText='position:fixed;opacity:0';
            document.body.appendChild(ta);ta.select();
            try{document.execCommand('copy');ok=true;}catch(e){}
            document.body.removeChild(ta);
        }
        if(ok){copyBtn.textContent='✓ 已复制';copyBtn.classList.add('copied');}
        setTimeout(function(){copyBtn.textContent='📋 复制';copyBtn.classList.remove('copied');},1500);
    };
    pre.appendChild(copyBtn);
    var applyBtn=document.createElement('button');
    applyBtn.className='copy-btn';
    applyBtn.textContent='⚡ 写入';
    applyBtn.title='将代码写入当前活动文档';
    applyBtn.style.right='60px';
    applyBtn.onclick=function(){
        var target=pre.querySelector('code')||pre;
        var codeText=target.innerText;
        try{
            window.chrome.webview.postMessage(JSON.stringify({type:'applyCode',code:codeText}));
        }catch(e1){
            try{
                window.external.notify(JSON.stringify({type:'applyCode',code:codeText}));
            }catch(e2){}
        }
        applyBtn.textContent='✓ 已写入';
        applyBtn.classList.add('copied');
        setTimeout(function(){applyBtn.textContent='⚡ 写入';applyBtn.classList.remove('copied');},1500);
    };
    pre.appendChild(applyBtn);
    var diffBtn=document.createElement('button');
    diffBtn.className='copy-btn';
    diffBtn.textContent='📊 对比';
    diffBtn.title='显示原始代码和AI修改后的差异';
    diffBtn.style.right='122px';
    diffBtn.onclick=function(){
        var target=pre.querySelector('code')||pre;
        var codeText=target.innerText;
        try{
            window.chrome.webview.postMessage(JSON.stringify({type:'showDiff',code:codeText}));
        }catch(e1){
            try{
                window.external.notify(JSON.stringify({type:'showDiff',code:codeText}));
            }catch(e2){}
        }
    };
    pre.appendChild(diffBtn);
});
})();";
        }

        private static string BuildShiftScrollJs()
        {
            return @"
document.addEventListener('wheel',function(e){
    if(!e.shiftKey)return;
    var pre=e.target.closest('pre');
    if(!pre||pre.scrollWidth<=pre.clientWidth)return;
    pre.scrollLeft+=e.deltaY>0?80:-80;
    e.preventDefault();
},{passive:false});";
        }

        /// <summary>
        /// 流式自动滚动 JS（MutationObserver）。
        /// </summary>
        private static string BuildAutoScrollJs()
        {
            return @"
(function(){
var timer=null;
new MutationObserver(function(){
    if(timer)clearTimeout(timer);
    timer=setTimeout(function(){window.scrollTo({top:document.body.scrollHeight,behavior:'smooth'});},80);
}).observe(document.body,{childList:true,subtree:true,characterData:true});
})();";
        }

        /// <summary>
        /// 声明 window.__appendMessageHtml 函数，用于增量追加新消息到页面。
        /// </summary>
        private static string BuildAppendMessageJsFunction()
        {
            return @"
window.__appendMessageHtml=function(html){
    var container=document.getElementById('chat-container');
    if(!container)return;
    var temp=document.createElement('div');
    temp.innerHTML=html;
    while(temp.firstChild){
        container.appendChild(temp.firstChild);
    }
    window.decorateCodeBlocks(container);
    window.scrollTo({top:document.body.scrollHeight,behavior:'smooth'});
};";
        }

        /// <summary>
        /// 声明重试/编辑/版本导航的 JS 函数。
        /// 通过 window.chrome.webview.postMessage 与 C# 通信。
        /// </summary>
        private static string BuildRetryEditJsFunctions()
        {
            return @"
window.__sendToHost=function(msg){
    try{window.chrome.webview.postMessage(JSON.stringify(msg));}
    catch(e1){try{window.external.notify(JSON.stringify(msg));}catch(e2){}}
};
window.__retryMessage=function(msgIndex){
    window.__sendToHost({type:'retryMessage',messageIndex:msgIndex});
};
window.__editMessage=function(msgIndex){
    window.__sendToHost({type:'editMessage',messageIndex:msgIndex});
};
window.__navigateVersion=function(msgIndex,direction){
    window.__sendToHost({type:'navigateVersion',messageIndex:msgIndex,direction:direction});
};
window.__agentApprove=function(requestId){
    window.__sendToHost({type:'agentApprove',requestId:requestId,approved:true});
};
window.__agentDeny=function(requestId){
    window.__sendToHost({type:'agentApprove',requestId:requestId,approved:false});
};";
        }

        #endregion

        /// <summary>
        /// 构建 Agent 步骤计划 HTML。
        /// </summary>
        public static string BuildAgentPlanHtml(AgentTaskPlan plan)
        {
            var sb = new StringBuilder();
            sb.Append("<div class='agent-plan' style='border:1px solid #3A5A8A;border-radius:8px;background:#1A2636;padding:12px;margin:4px 0'>");
            sb.Append($"<div style='color:#7EB8E0;font-size:14px;font-weight:600;margin-bottom:8px'>🤖 Coding Agent — {EscapeHtml(plan.Title)}</div>");

            sb.Append("<div class='agent-steps'>");
            foreach (var step in plan.Steps)
            {
                string icon = step.Status == AgentStepStatus.Completed ? "✅"
                    : step.Status == AgentStepStatus.InProgress ? "🔄"
                    : step.Status == AgentStepStatus.Failed ? "❌"
                    : step.Status == AgentStepStatus.WaitingApproval ? "🔐"
                    : "⏳";

                string color = step.Status == AgentStepStatus.Completed ? "#4EC9B0"
                    : step.Status == AgentStepStatus.InProgress ? "#6CAFD9"
                    : step.Status == AgentStepStatus.Failed ? "#E07878"
                    : "#888";

                sb.Append($"<div class='agent-step' id='agent-step-{step.Index}' style='display:flex;align-items:center;gap:8px;padding:4px 0;color:{color};font-size:12px'>");
                sb.Append($"<span style='min-width:20px;text-align:center'>{icon}</span>");
                sb.Append($"<span>{EscapeHtml(step.Title)}</span>");
                if (!string.IsNullOrEmpty(step.ResultSummary))
                    sb.Append($" <span style='color:#888;font-size:11px'>— {EscapeHtml(step.ResultSummary)}</span>");
                sb.Append("</div>");

                // ── 步骤详情区（AI 响应内容，完成后展示） ──
                bool hasDetail = !string.IsNullOrEmpty(step.AiResponse);
                string detailDisplay = hasDetail ? "block" : "none";
                string detailContent = hasDetail
                    ? EscapeHtml(step.AiResponse!.Length > 500 ? step.AiResponse.Substring(0, 500) + "…" : step.AiResponse)
                    : "";
                sb.Append($"<div id='agent-detail-{step.Index}' style='display:{detailDisplay};margin:2px 0 8px 28px;padding:8px;background:#121A24;border-radius:4px;border-left:2px solid {color};font-size:11px;color:#A0B0C0;white-space:pre-wrap;max-height:200px;overflow-y:auto'>{detailContent}</div>");
            }
            sb.Append("</div></div>");
            return sb.ToString();
        }

        /// <summary>
        /// 构建 Agent 步骤进度更新的 JS 脚本。
        /// </summary>
        public static string BuildAgentProgressUpdateJs(AgentTaskPlan plan)
        {
            var sb = new StringBuilder();
            sb.Append("(function(){");
            foreach (var step in plan.Steps)
            {
                string icon = step.Status == AgentStepStatus.Completed ? "✅"
                    : step.Status == AgentStepStatus.InProgress ? "🔄"
                    : step.Status == AgentStepStatus.Failed ? "❌"
                    : step.Status == AgentStepStatus.WaitingApproval ? "🔐"
                    : "⏳";

                string color = step.Status == AgentStepStatus.Completed ? "#4EC9B0"
                    : step.Status == AgentStepStatus.InProgress ? "#6CAFD9"
                    : step.Status == AgentStepStatus.Failed ? "#E07878"
                    : "#888";

                string summary = step.ResultSummary != null
                    ? $" — {EscapeJsString(step.ResultSummary)}"
                    : "";

                sb.Append($"var s=document.getElementById('agent-step-{step.Index}');");
                sb.Append($"if(s){{s.style.color='{color}';");
                sb.Append($"s.innerHTML='<span style=\"min-width:20px;text-align:center\">{icon}</span>");
                sb.Append($"<span>{EscapeJsString(step.Title)}</span>");
                sb.Append($"<span style=\"color:#888;font-size:11px\">{summary}</span>';}}");

                // ── 更新步骤详情区 ──
                if (!string.IsNullOrEmpty(step.AiResponse))
                {
                    string detailText = step.AiResponse.Length > 500
                        ? step.AiResponse.Substring(0, 500) + "…"
                        : step.AiResponse;
                    sb.Append($"var d=document.getElementById('agent-detail-{step.Index}');");
                    sb.Append($"if(d){{d.style.display='block';d.style.borderLeftColor='{color}';");
                    sb.Append($"d.textContent={EscapeJsString(detailText)};}}");
                }
            }
            sb.Append("window.scrollTo({top:document.body.scrollHeight,behavior:'smooth'});");
            sb.Append("})();");
            return sb.ToString();
        }

        /// <summary>
        /// 构建 Agent 任务完成后的变更摘要 HTML。
        /// </summary>
        public static string BuildAgentSummaryHtml(AgentTaskPlan plan)
        {
            var sb = new StringBuilder();
            sb.Append("<div style='border:1px solid #3A5A3A;border-radius:8px;background:#1A2E1A;padding:12px;margin:4px 0'>");

            if (plan.IsCancelled)
            {
                sb.Append("<div style='color:#E07878;font-size:13px;font-weight:600'>⚠️ 任务已取消</div>");
            }
            else
            {
                // ── 步骤执行总结 ──
                int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
                int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
                string statusColor = failed > 0 ? "#E07878" : "#4EC9B0";
                string statusIcon = failed > 0 ? "⚠️" : "✅";
                sb.Append($"<div style='color:{statusColor};font-size:14px;font-weight:600;margin-bottom:8px'>{statusIcon} 任务完成 — {completed}/{plan.Steps.Count} 步成功" +
                    (failed > 0 ? $"，{failed} 步失败" : "") + "</div>");

                // ── 逐步骤结果 ──
                sb.Append("<div style='margin-bottom:8px'>");
                foreach (var step in plan.Steps)
                {
                    string stepIcon = step.Status == AgentStepStatus.Completed ? "✅"
                        : step.Status == AgentStepStatus.Failed ? "❌"
                        : step.Status == AgentStepStatus.Skipped ? "⏭"
                        : "⏳";
                    string stepColor = step.Status == AgentStepStatus.Completed ? "#4EC9B0"
                        : step.Status == AgentStepStatus.Failed ? "#E07878"
                        : "#888";

                    sb.Append($"<div style='display:flex;align-items:flex-start;gap:6px;padding:2px 0;font-size:12px'>");
                    sb.Append($"<span style='min-width:18px;color:{stepColor}'>{stepIcon}</span>");
                    sb.Append($"<span style='color:#D4D4D4;font-weight:500'>{EscapeHtml(step.Title)}</span>");
                    if (!string.IsNullOrEmpty(step.ResultSummary))
                        sb.Append($"<span style='color:#888'>— {EscapeHtml(step.ResultSummary)}</span>");
                    sb.Append("</div>");
                }
                sb.Append("</div>");

                // ── 文件变更表 ──
                if (plan.ChangedFiles.Count > 0)
                {
                    sb.Append("<div style='color:#4EC9B0;font-size:12px;font-weight:600;margin:8px 0 4px'>📁 文件变更</div>");
                    sb.Append("<table style='width:100%;border-collapse:collapse;font-size:11px'>");
                    sb.Append("<tr style='color:#888'><th style='text-align:left;padding:2px 8px'>文件</th><th style='text-align:right;padding:2px 8px'>+</th><th style='text-align:right;padding:2px 8px'>-</th></tr>");

                    foreach (var change in plan.ChangedFiles)
                    {
                        string fileName = System.IO.Path.GetFileName(change.FilePath);
                        sb.Append("<tr>");
                        sb.Append($"<td style='padding:2px 8px;color:#D4D4D4'>{EscapeHtml(fileName)}</td>");
                        sb.Append($"<td style='padding:2px 8px;text-align:right;color:#4EC9B0'>+{change.LinesAdded}</td>");
                        sb.Append($"<td style='padding:2px 8px;text-align:right;color:#E07878'>-{change.LinesRemoved}</td>");
                        sb.Append("</tr>");
                    }

                    sb.Append("</table>");
                }
                else if (plan.ChangedFiles.Count == 0 && completed == plan.Steps.Count)
                {
                    sb.Append("<div style='color:#888;font-size:12px;margin-top:6px'>ℹ️ 所有步骤已完成，未产生文件变更（分析类任务无需修改文件）</div>");
                }
            }

            sb.Append("</div>");
            return sb.ToString();
        }

        /// <summary>
        /// 构建权限请求 UI 的 JS 脚本（在聊天底部注入确认/拒绝按钮）。
        /// </summary>
        public static string BuildPermissionRequestJs(AgentPermissionRequest request)
        {
            string escapedTitle = EscapeJsString(request.Title);
            string escapedCommand = EscapeJsString(request.Command);
            string escapedRequestId = EscapeJsString(request.RequestId);

            return $@"
(function(){{
    // 移除已有的权限请求 UI
    var existing=document.getElementById('agent-permission');
    if(existing)existing.remove();

    var div=document.createElement('div');
    div.id='agent-permission';
    div.style.cssText='border:1px solid #C8A84E;border-radius:8px;background:#2E2A1A;padding:12px;margin:8px 0;animation:fadeIn .3s';

    div.innerHTML=
        '<div style=""color:#C8A84E;font-size:12px;font-weight:600;margin-bottom:6px"">🔐 Agent 请求权限</div>'+
        '<div style=""color:#D4D4D4;font-size:12px;margin-bottom:4px"">{escapedTitle}</div>'+
        '<pre style=""background:#1A1A0E;color:#C8C84E;padding:8px;border-radius:4px;font-size:11px;margin:4px 0;max-height:60px;overflow-y:auto"">{escapedCommand}</pre>'+
        '<div style=""display:flex;gap:8px;margin-top:8px"">'+
        '<button onclick=""window.__agentApprove(\'{escapedRequestId}\')"" style=""background:#1A3A1A;color:#4EC9B0;border:1px solid #3A6A3A;border-radius:4px;padding:4px 16px;cursor:pointer;font-size:12px"">✅ 允许</button>'+
        '<button onclick=""window.__agentDeny(\'{escapedRequestId}\')"" style=""background:#3A1A1A;color:#E07878;border:1px solid #6A3A3A;border-radius:4px;padding:4px 16px;cursor:pointer;font-size:12px"">❌ 拒绝</button>'+
        '</div>';

    var container=document.getElementById('chat-container');
    if(container)container.appendChild(div);
    window.scrollTo({{top:document.body.scrollHeight,behavior:'smooth'}});
}})();";
        }

        /// <summary>
        /// HTML 转义辅助方法。
        /// </summary>
        private static string EscapeHtml(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return System.Net.WebUtility.HtmlEncode(text);
        }

        /// <summary>
        /// 转义字符串用于嵌入 JS 字符串字面量。
        /// </summary>
        private static string EscapeJsString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            // 使用 JSON 序列化来安全转义
            return System.Text.Json.JsonSerializer.Serialize(s);
        }
    }
}

