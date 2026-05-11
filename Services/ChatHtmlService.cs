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

/* ── Agent 步骤流程管线样式 ── */
.agent-plan{border:1px solid #3A5A8A;border-radius:10px;background:linear-gradient(180deg,#1A2436 0%,#1A1E2E 100%);padding:16px;margin:6px 0;position:relative;overflow:hidden}
.agent-plan::before{content:'';position:absolute;top:0;left:0;right:0;height:2px;background:linear-gradient(90deg,#3A5A8A,#6CAFD9,#3A5A8A);opacity:.6}
.agent-plan-header{display:flex;align-items:center;justify-content:space-between;margin-bottom:12px;flex-wrap:wrap;gap:8px}
.agent-plan-title{color:#7EB8E0;font-size:14px;font-weight:700;display:flex;align-items:center;gap:6px}
.agent-plan-progress{display:flex;align-items:center;gap:8px;font-size:11px;color:#888}
.agent-plan-progress-bar{width:120px;height:4px;background:#2D2D2D;border-radius:2px;overflow:hidden}
.agent-plan-progress-bar-fill{height:100%;border-radius:2px;transition:width .5s ease;background:linear-gradient(90deg,#4EC9B0,#6CAFD9)}

/* ── 步骤管线节点 ── */
.agent-step-node{position:relative;display:flex;align-items:flex-start;gap:10px;padding:0 0 0 0;min-height:36px}
.agent-step-node:last-child .agent-step-line{display:none}
.agent-step-bullet-wrap{display:flex;flex-direction:column;align-items:center;flex-shrink:0;width:28px}
.agent-step-bullet{width:28px;height:28px;border-radius:50%;display:flex;align-items:center;justify-content:center;font-size:11px;font-weight:700;border:2px solid #444;background:#252526;color:#888;transition:all .35s ease;position:relative;z-index:1;flex-shrink:0}
.agent-step-bullet.pending{background:#252526;border-color:#444;color:#666}
.agent-step-bullet.in-progress{background:#1A2E3E;border-color:#6CAFD9;color:#6CAFD9;box-shadow:0 0 10px rgba(108,175,217,.35);animation:stepPulse 2s ease-in-out infinite}
.agent-step-bullet.completed{background:#1A2E1A;border-color:#4EC9B0;color:#4EC9B0;box-shadow:0 0 6px rgba(78,201,176,.25)}
.agent-step-bullet.failed{background:#2E1A1A;border-color:#E07878;color:#E07878;box-shadow:0 0 6px rgba(224,120,120,.25)}
.agent-step-bullet.skipped{background:#252526;border-color:#555;color:#666}
.agent-step-bullet.waiting{background:#2E2A1A;border-color:#C8A84E;color:#C8A84E;box-shadow:0 0 8px rgba(200,168,78,.3);animation:stepPulse 1.5s ease-in-out infinite}
.agent-step-line{width:2px;flex-grow:1;min-height:12px;background:#333;margin:2px 0;transition:background .5s ease}
.agent-step-line.active{background:linear-gradient(180deg,#6CAFD9,#333)}
.agent-step-line.done{background:#3A5A3A}

/* ── 步骤内容 ── */
.agent-step-content{flex:1;padding:2px 0 6px 0;min-width:0}
.agent-step-title-row{display:flex;align-items:center;gap:6px;flex-wrap:wrap}
.agent-step-title{font-size:12.5px;font-weight:600;line-height:1.4}
.agent-step-title.pending{color:#888}
.agent-step-title.in-progress{color:#D4D4D4}
.agent-step-title.completed{color:#4EC9B0}
.agent-step-title.failed{color:#E07878}
.agent-step-title.skipped{color:#666}
.agent-step-title.waiting{color:#C8A84E}
.agent-step-summary{font-size:11px;color:#707070;margin-top:2px;line-height:1.4;word-break:break-word}
.agent-step-detail{margin-top:6px;padding:8px 10px;background:#121A24;border-radius:4px;border-left:2px solid #444;font-size:11px;color:#8A9AB0;white-space:pre-wrap;max-height:180px;overflow-y:auto;display:none;line-height:1.5;transition:border-color .35s ease}
.agent-step-detail.show{display:block}
.agent-step-detail.in-progress{border-left-color:#6CAFD9}
.agent-step-detail.completed{border-left-color:#4EC9B0}
.agent-step-detail.failed{border-left-color:#E07878}

/* ── 步骤标签（构建/运行/代码/分析） ── */
.agent-step-tag{display:inline-block;font-size:9px;padding:1px 5px;border-radius:3px;font-weight:600;text-transform:uppercase;letter-spacing:.5px;flex-shrink:0}
.agent-step-tag.code{background:#1A2E3E;color:#6CAFD9;border:1px solid #2A4A6A}
.agent-step-tag.build{background:#2E2A1A;color:#C8A84E;border:1px solid #4A3A1A}
.agent-step-tag.analyze{background:#1A262E;color:#7EB8E0;border:1px solid #2A4A5A}
.agent-step-tag.verify{background:#1A2E2A;color:#4EC9B0;border:1px solid #2A4A3A}

/* ── 步骤动画 ── */
@keyframes stepPulse{
    0%,100%{box-shadow:0 0 6px rgba(108,175,217,.25)}
    50%{box-shadow:0 0 16px rgba(108,175,217,.5)}
}
@keyframes stepSlideIn{
    from{opacity:0;transform:translateX(-8px)}
    to{opacity:1;transform:translateX(0)}
}
.agent-step-node{animation:stepSlideIn .3s ease-out}

/* ── Agent 计划底部操作栏 ── */
.agent-plan-footer{display:flex;align-items:center;gap:8px;margin-top:10px;padding-top:10px;border-top:1px solid #2A2A3A;font-size:11px;color:#666}
.agent-plan-footer .elapsed{color:#555}
.agent-plan-footer .step-counter{color:#888}
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

    // ── 注入重试按钮（流式完成/停止后始终显示）──
    if(msgDiv){{
        var existingRetry=document.getElementById('retry-btn-{messageIndex}');
        if(!existingRetry){{
            var retryBtn=document.createElement('button');
            retryBtn.id='retry-btn-{messageIndex}';
            retryBtn.className='msg-action-btn retry-btn';
            retryBtn.textContent='🔄 重试';
            retryBtn.title='重新生成回答';
            retryBtn.onclick=function(){{window.__retryMessage({messageIndex});}};
            var msgBody=document.getElementById('msg-body-{messageIndex}');
            if(msgBody) msgBody.parentNode.insertBefore(retryBtn,msgBody.nextSibling);
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

            // ── 重试按钮（非流式、非预渲染 HTML 消息才显示） ──
            string retryBtnHtml = !isStreaming && !msg.IsHtml
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
        /// 将 Markdown 文本渲染为 HTML（使用 Markdig）。公开方法，供外部调用。
        /// </summary>
        public static string RenderMarkdownToHtml(string markdown)
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
        /// 构建 Agent 步骤流程管线 HTML（垂直管线布局）。
        /// 每个步骤是一个节点（编号圆圈 + 连接线），实时展示执行进度。
        /// </summary>
        public static string BuildAgentPlanHtml(AgentTaskPlan plan)
        {
            int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
            int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
            int total = plan.Steps.Count;
            int progressPercent = total > 0 ? (completed + failed) * 100 / total : 0;

            var sb = new StringBuilder();
            sb.Append("<div class='agent-plan'>");

            // ── 头部：标题 + 进度条 ──
            sb.Append("<div class='agent-plan-header'>");
            sb.Append($"<div class='agent-plan-title'>🤖 Coding Agent — {EscapeHtml(plan.Title)}</div>");
            sb.Append("<div class='agent-plan-progress'>");
            sb.Append($"<span class='step-counter'>{completed}/{total} 步</span>");
            sb.Append("<span class='agent-plan-progress-bar'>");
            sb.Append($"<span class='agent-plan-progress-bar-fill' id='agent-progress-fill' style='width:{progressPercent}%'></span>");
            sb.Append("</span></div></div>");

            // ── 步骤管线节点 ──
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                bool isLast = i == plan.Steps.Count - 1;

                string statusClass = step.Status switch
                {
                    AgentStepStatus.Completed => "completed",
                    AgentStepStatus.InProgress => "in-progress",
                    AgentStepStatus.Failed => "failed",
                    AgentStepStatus.Skipped => "skipped",
                    AgentStepStatus.WaitingApproval => "waiting",
                    _ => "pending",
                };

                string bulletText = step.Status switch
                {
                    AgentStepStatus.Completed => "✓",
                    AgentStepStatus.InProgress => "●",
                    AgentStepStatus.Failed => "✗",
                    AgentStepStatus.Skipped => "—",
                    AgentStepStatus.WaitingApproval => "?",
                    _ => step.Index.ToString(),
                };

                // 步骤类型标签
                string tagHtml = "";
                if (IsCodeWritingStepName(step.Title))
                    tagHtml = "<span class='agent-step-tag code'>代码</span>";
                else if (IsBuildOrRunStepName(step.Title))
                    tagHtml = "<span class='agent-step-tag build'>构建</span>";
                else if (IsAnalyzeStepName(step.Title))
                    tagHtml = "<span class='agent-step-tag analyze'>分析</span>";

                // 连接线的样式
                string lineClass = step.Status == AgentStepStatus.Completed ? "done"
                    : step.Status == AgentStepStatus.InProgress ? "active"
                    : "";

                sb.Append($"<div class='agent-step-node' id='agent-step-node-{step.Index}'>");

                // ── 编号圆圈 + 连接线 ──
                sb.Append("<div class='agent-step-bullet-wrap'>");
                sb.Append($"<div class='agent-step-bullet {statusClass}' id='agent-bullet-{step.Index}'>{bulletText}</div>");
                if (!isLast)
                    sb.Append($"<div class='agent-step-line {lineClass}' id='agent-line-{step.Index}'></div>");
                sb.Append("</div>");

                // ── 步骤内容 ──
                sb.Append("<div class='agent-step-content'>");
                sb.Append("<div class='agent-step-title-row'>");
                sb.Append($"<span class='agent-step-title {statusClass}' id='agent-title-{step.Index}'>{EscapeHtml(step.Title)}</span>");
                if (!string.IsNullOrEmpty(tagHtml))
                    sb.Append(tagHtml);
                sb.Append("</div>");

                // 结果摘要
                string summaryDisplay = string.IsNullOrEmpty(step.ResultSummary) ? "none" : "block";
                sb.Append($"<div class='agent-step-summary' id='agent-summary-{step.Index}' style='display:{summaryDisplay}'>");
                if (!string.IsNullOrEmpty(step.ResultSummary))
                    sb.Append(EscapeHtml(step.ResultSummary!));
                sb.Append("</div>");

                // 详情区（AI 响应，可折叠）
                bool hasDetail = !string.IsNullOrEmpty(step.AiResponse);
                string detailClass = hasDetail ? "agent-step-detail show" : "agent-step-detail";
                if (hasDetail && statusClass is "in-progress" or "completed" or "failed")
                    detailClass += $" {statusClass}";
                string detailContent = hasDetail
                    ? EscapeHtml(step.AiResponse!.Length > 600 ? step.AiResponse.Substring(0, 600) + "…" : step.AiResponse)
                    : "";
                sb.Append($"<div class='{detailClass}' id='agent-detail-{step.Index}'>{detailContent}</div>");

                sb.Append("</div></div>");
            }

            // ── 底部状态栏 ──
            sb.Append("<div class='agent-plan-footer'>");
            sb.Append($"<span class='elapsed' id='agent-elapsed'></span>");
            sb.Append($"<span class='step-counter' id='agent-step-counter'>{completed}/{total} 步完成</span>");
            if (failed > 0)
                sb.Append($"<span style='color:#E07878'>⚠ {failed} 步失败</span>");
            sb.Append("</div>");

            sb.Append("</div>");
            return sb.ToString();
        }

        /// <summary>
        /// 判断步骤标题是否属于代码编写类型。
        /// </summary>
        private static bool IsCodeWritingStepName(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            return title.Contains("创建") || title.Contains("修改") || title.Contains("编写")
                || title.Contains("实现") || title.Contains("添加") || title.Contains("重构")
                || title.Contains("Create") || title.Contains("Modify") || title.Contains("Implement")
                || title.Contains("Add") || title.Contains("Refactor") || title.Contains("更新")
                || title.Contains("删除") || title.Contains("生成") || title.Contains("修复");
        }

        /// <summary>
        /// 判断步骤标题是否属于构建/运行类型。
        /// </summary>
        private static bool IsBuildOrRunStepName(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            return title.Contains("构建") || title.Contains("编译") || title.Contains("运行")
                || title.Contains("测试") || title.Contains("验证") || title.Contains("Build")
                || title.Contains("Compile") || title.Contains("Run") || title.Contains("Test")
                || title.Contains("Verify") || title.Contains("检查") || title.Contains("lint");
        }

        /// <summary>
        /// 判断步骤标题是否属于分析类型。
        /// </summary>
        private static bool IsAnalyzeStepName(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            return title.Contains("分析") || title.Contains("研究") || title.Contains("探索")
                || title.Contains("理解") || title.Contains("Analyze") || title.Contains("Research")
                || title.Contains("Explore") || title.Contains("Understand") || title.Contains("检查");
        }

        /// <summary>
        /// 构建 Agent 步骤流程管线进度更新 JS（增量更新 DOM，不重建 HTML）。
        /// 更新每个步骤节点的圆形编号、标题颜色、连接线、摘要和详情区。
        /// </summary>
        public static string BuildAgentProgressUpdateJs(AgentTaskPlan plan)
        {
            int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
            int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
            int total = plan.Steps.Count;
            int progressPercent = total > 0 ? (completed + failed) * 100 / total : 0;

            var sb = new StringBuilder();
            sb.Append("(function(){");

            foreach (var step in plan.Steps)
            {
                string statusClass = step.Status switch
                {
                    AgentStepStatus.Completed => "completed",
                    AgentStepStatus.InProgress => "in-progress",
                    AgentStepStatus.Failed => "failed",
                    AgentStepStatus.Skipped => "skipped",
                    AgentStepStatus.WaitingApproval => "waiting",
                    _ => "pending",
                };

                string bulletText = step.Status switch
                {
                    AgentStepStatus.Completed => "✓",
                    AgentStepStatus.InProgress => "●",
                    AgentStepStatus.Failed => "✗",
                    AgentStepStatus.Skipped => "—",
                    AgentStepStatus.WaitingApproval => "?",
                    _ => step.Index.ToString(),
                };

                // ── 更新步骤圆形编号 ──
                sb.Append($"var b=document.getElementById('agent-bullet-{step.Index}');");
                sb.Append($"if(b){{b.className='agent-step-bullet {statusClass}';b.textContent='{bulletText}';}}");

                // ── 更新步骤标题 ──
                sb.Append($"var t=document.getElementById('agent-title-{step.Index}');");
                sb.Append($"if(t){{t.className='agent-step-title {statusClass}';}}");

                // ── 更新步骤摘要 ──
                sb.Append($"var s=document.getElementById('agent-summary-{step.Index}');");
                if (!string.IsNullOrEmpty(step.ResultSummary))
                {
                    sb.Append($"if(s){{s.style.display='block';s.textContent={EscapeJsString(step.ResultSummary!)};}}");
                }
                else
                {
                    sb.Append("if(s){s.style.display='none';}");
                }

                // ── 更新步骤详情区 ──
                sb.Append($"var d=document.getElementById('agent-detail-{step.Index}');");
                if (!string.IsNullOrEmpty(step.AiResponse))
                {
                    string detailText = step.AiResponse!.Length > 600
                        ? step.AiResponse.Substring(0, 600) + "…"
                        : step.AiResponse;
                    sb.Append($"if(d){{d.className='agent-step-detail show {statusClass}';");
                    sb.Append($"d.textContent={EscapeJsString(detailText)};}}");
                }
                else
                {
                    sb.Append($"if(d){{d.className='agent-step-detail';d.textContent='';}}");
                }

                // ── 更新连接线 ──
                string lineClass = step.Status == AgentStepStatus.Completed ? "done"
                    : step.Status == AgentStepStatus.InProgress ? "active"
                    : "";
                sb.Append($"var l=document.getElementById('agent-line-{step.Index}');");
                sb.Append($"if(l){{l.className='agent-step-line {lineClass}';}}");
            }

            // ── 更新进度条 ──
            sb.Append($"var pf=document.getElementById('agent-progress-fill');");
            sb.Append($"if(pf){{pf.style.width='{progressPercent}%';}}");

            // ── 更新进度计数器 ──
            sb.Append($"var pc=document.getElementById('agent-step-counter');");
            string counterText = failed > 0
                ? $"{completed}/{total} 步完成，{failed} 步失败"
                : $"{completed}/{total} 步完成";
            sb.Append($"if(pc){{pc.textContent='{EscapeJsString(counterText)}';}}");

            // ── 自动滚动到底部 ──
            sb.Append("window.scrollTo({top:document.body.scrollHeight,behavior:'smooth'});");
            sb.Append("})();");
            return sb.ToString();
        }

        /// <summary>
        /// 构建 Agent 任务完成后的变更摘要 HTML（与流程管线风格一致）。
        /// </summary>
        public static string BuildAgentSummaryHtml(AgentTaskPlan plan)
        {
            int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
            int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
            int total = plan.Steps.Count;

            var sb = new StringBuilder();

            if (plan.IsCancelled)
            {
                sb.Append("<div class='agent-plan' style='border-color:#6A3A3A'>");
                sb.Append("<div class='agent-plan-header'>");
                sb.Append("<div class='agent-plan-title' style='color:#E07878'>⚠️ 任务已取消</div>");
                sb.Append($"<div class='agent-plan-progress'><span class='step-counter'>{completed}/{total} 步</span></div>");
                sb.Append("</div>");
            }
            else
            {
                string statusColor = failed > 0 ? "#E07878" : "#4EC9B0";
                string statusIcon = failed > 0 ? "⚠️" : "✅";
                string borderColor = failed > 0 ? "#6A3A3A" : "#3A5A3A";

                sb.Append($"<div class='agent-plan' style='border-color:{borderColor}'>");
                sb.Append("<div class='agent-plan-header'>");
                sb.Append($"<div class='agent-plan-title' style='color:{statusColor}'>{statusIcon} 任务完成 — {completed}/{total} 步成功" +
                    (failed > 0 ? $"，{failed} 步失败" : "") + "</div>");
                sb.Append("</div>");

                // ── 逐步骤结果（管线节点紧凑版） ──
                for (int i = 0; i < plan.Steps.Count; i++)
                {
                    var step = plan.Steps[i];
                    bool isLast = i == plan.Steps.Count - 1;

                    string statusClass = step.Status switch
                    {
                        AgentStepStatus.Completed => "completed",
                        AgentStepStatus.Failed => "failed",
                        AgentStepStatus.Skipped => "skipped",
                        _ => "pending",
                    };

                    string bulletText = step.Status switch
                    {
                        AgentStepStatus.Completed => "✓",
                        AgentStepStatus.Failed => "✗",
                        AgentStepStatus.Skipped => "—",
                        _ => "○",
                    };

                    string lineClass = step.Status == AgentStepStatus.Completed ? "done" : "";

                    sb.Append("<div class='agent-step-node'>");
                    sb.Append("<div class='agent-step-bullet-wrap'>");
                    sb.Append($"<div class='agent-step-bullet {statusClass}'>{bulletText}</div>");
                    if (!isLast)
                        sb.Append($"<div class='agent-step-line {lineClass}'></div>");
                    sb.Append("</div>");
                    sb.Append("<div class='agent-step-content'>");
                    sb.Append("<div class='agent-step-title-row'>");
                    sb.Append($"<span class='agent-step-title {statusClass}'>{EscapeHtml(step.Title)}</span>");
                    sb.Append("</div>");
                    if (!string.IsNullOrEmpty(step.ResultSummary))
                        sb.Append($"<div class='agent-step-summary'>{EscapeHtml(step.ResultSummary!)}</div>");
                    sb.Append("</div></div>");
                }

                // ── 文件变更表（合并相同文件的多条记录） ──
                if (plan.ChangedFiles.Count > 0)
                {
                    // ── 按文件路径合并：相同文件合并行数，拼接描述 ──
                    var mergedFiles = plan.ChangedFiles
                        .GroupBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new FileChangeSummary
                        {
                            FilePath = g.Key,
                            LinesAdded = g.Sum(c => c.LinesAdded),
                            LinesRemoved = g.Sum(c => c.LinesRemoved),
                            BriefDescription = string.Join("; ", g.Select(c => c.BriefDescription).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct()),
                        })
                        .ToList();

                    sb.Append("<div style='margin-top:10px;padding-top:8px;border-top:1px solid #2A3A2A'>");
                    sb.Append($"<div style='color:#4EC9B0;font-size:12px;font-weight:600;margin-bottom:6px'>📁 文件变更 ({mergedFiles.Count} 个文件)</div>");
                    sb.Append("<table style='width:100%;border-collapse:collapse;font-size:11px'>");
                    sb.Append("<tr style='color:#888'><th style='text-align:left;padding:2px 8px'>文件</th><th style='text-align:right;padding:2px 8px'>+</th><th style='text-align:right;padding:2px 8px'>-</th><th style='text-align:left;padding:2px 8px'>描述</th></tr>");

                    foreach (var change in mergedFiles)
                    {
                        string fileName = System.IO.Path.GetFileName(change.FilePath);
                        sb.Append("<tr>");
                        sb.Append($"<td style='padding:2px 8px;color:#D4D4D4'>{EscapeHtml(fileName)}</td>");
                        sb.Append($"<td style='padding:2px 8px;text-align:right;color:#4EC9B0'>+{change.LinesAdded}</td>");
                        sb.Append($"<td style='padding:2px 8px;text-align:right;color:#E07878'>-{change.LinesRemoved}</td>");
                        sb.Append($"<td style='padding:2px 8px;color:#888;font-size:10px'>{EscapeHtml(change.BriefDescription ?? "")}</td>");
                        sb.Append("</tr>");
                    }

                    sb.Append("</table></div>");
                }
                else if (completed == total)
                {
                    sb.Append("<div style='color:#888;font-size:11px;margin-top:8px;padding-top:8px;border-top:1px solid #2A2A3A'>ℹ️ 所有步骤已完成，未产生文件变更（分析类任务无需修改文件）</div>");
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

