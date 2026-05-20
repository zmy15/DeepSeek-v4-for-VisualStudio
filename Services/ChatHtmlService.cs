using DeepSeek_v4_for_VisualStudio.Models;
using Markdig;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 将聊天消息列表构建为 HTML 页面，用于 WebView2 (Chromium) 渲染。
    /// 支持增量渲染：初始全页 NavigateToString + 后续 ExecuteScriptAsync 增量追加，
    /// 消除流式输出时的全页刷新闪烁。
    /// </summary>
    public static partial class ChatHtmlService
    {
        #region Constants

        /// <summary>
        /// i18n 便捷访问器。
        /// </summary>
        private static LocalizationService L => LocalizationService.Instance;

        /// <summary>
        /// Markdig 解析管道：启用高级扩展，禁用原生 HTML（防 XSS）。
        /// </summary>
        private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();


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
                {
                    AppendUserMessageHtml(sb, msg.Content ?? string.Empty, msg.AttachedFiles, i);
                    // ── 编辑产生的分支导航（在用户气泡下方）──
                    if (msg.SiblingCount > 1 && msg.ForkReason == "edit")
                        sb.Append(BuildBranchNavHtml(msg, i));
                }
                else if (msg.Role == "assistant")
                {
                    AppendAssistantMessageHtml(sb, msg, i);
                }
            }

            return WrapFullPage(sb.ToString(), hasStreamingMessage: true);
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
            return BuildStreamingUpdateJs(messageIndex, streamingContent, reasoningContent, isComplete, null);
        }

        /// <summary>
        /// 构建流式更新 JS（含内嵌状态栏批量更新，减少 ExecuteScriptAsync 调用次数）。
        /// </summary>
        public static string BuildStreamingUpdateJs(int messageIndex, string streamingContent, string reasoningContent, bool isComplete,
            string? statusText)
        {
            string escapedContent = EscapeJsString(streamingContent ?? string.Empty);
            string escapedReasoning = EscapeJsString(reasoningContent ?? string.Empty);
            string escapedStatus = EscapeJsString(statusText ?? "");

            return $@"
(function(){{
    var container=document.getElementById('msg-body-{messageIndex}');
    var reasoningPanel=document.getElementById('reasoning-{messageIndex}');
    var reasoningBody=document.getElementById('reasoning-body-{messageIndex}');
    var cursor=document.getElementById('cursor-{messageIndex}');

    if(container){{
        var text={escapedContent};
        var textNode=container._textNode;
        if(!textNode){{
            textNode=document.createTextNode('');
            container._textNode=textNode;
            container.textContent='';
            container.appendChild(textNode);
        }}
        textNode.textContent=text;
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

    // ── 合并状态栏更新（避免额外的 ExecuteScriptAsync）──
    var st=document.getElementById('status-text');
    if(st&&{escapedStatus}.length>0)st.textContent={escapedStatus};

    window.__scrollToBottom('smooth');
}})();";
        }

        #region 高性能流式消息（PostWebMessageAsString 非阻塞通道）

        /// <summary>
        /// 构建流式增量更新的 JSON 消息（用于 PostWebMessageAsString）。
        /// 短键名减少序列化开销：i=msgIndex, c=content, r=reasoning, f=isFinished, s=status
        /// </summary>
        public static string BuildStreamUpdateJson(int messageIndex, string streamingContent,
            string reasoningContent, bool isComplete, string? statusText = null)
        {
            // 使用手动拼接 JSON 避免 System.Text.Json 的分配开销（高频调用场景）
            var sb = new StringBuilder(256);
            sb.Append("{\"type\":\"stream\",\"i\":");
            sb.Append(messageIndex);
            sb.Append(",\"c\":");
            AppendJsonString(sb, streamingContent ?? string.Empty);
            if (!string.IsNullOrEmpty(reasoningContent))
            {
                sb.Append(",\"r\":");
                AppendJsonString(sb, reasoningContent);
            }
            if (isComplete)
                sb.Append(",\"f\":true");
            if (!string.IsNullOrEmpty(statusText))
            {
                sb.Append(",\"s\":");
                AppendJsonString(sb, statusText);
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 构建流式最终完成的 JSON 消息（含 Markdown 渲染 HTML）。
        /// 注意：Markdown 渲染在 C# 侧完成（Markdig），JS 侧直接 innerHTML。
        /// </summary>
        public static string BuildStreamEndJson(int messageIndex, string fullContent,
            string reasoningContent, string? extraFooterHtml = null)
        {
            string bodyHtml = RenderMarkdownToHtml(fullContent ?? string.Empty);
            string reasoningHtml = string.IsNullOrWhiteSpace(reasoningContent)
                ? string.Empty
                : RenderReasoningContentHtml(reasoningContent);

            var sb = new StringBuilder(512);
            sb.Append("{\"type\":\"streamEnd\",\"i\":");
            sb.Append(messageIndex);
            sb.Append(",\"html\":");
            AppendJsonString(sb, bodyHtml);
            if (!string.IsNullOrEmpty(reasoningHtml))
            {
                sb.Append(",\"reasoningHtml\":");
                AppendJsonString(sb, reasoningHtml);
            }
            if (!string.IsNullOrEmpty(extraFooterHtml))
            {
                sb.Append(",\"footerHtml\":");
                AppendJsonString(sb, extraFooterHtml);
            }
            // 本地化按钮文本
            sb.Append(",\"retryLabel\":");
            AppendJsonString(sb, L["chat.html.retryButton"]);
            sb.Append(",\"retryTitle\":");
            AppendJsonString(sb, L["chat.html.retryButtonTitle"]);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 构建仅状态栏更新的 JSON 消息。
        /// </summary>
        public static string BuildStatusUpdateJson(string statusText)
        {
            return $"{{\"type\":\"streamStatus\",\"text\":{JsonSerializer.Serialize(statusText)}}}";
        }

        /// <summary>
        /// 向 StringBuilder 追加 JSON 字符串值（手动转义，避免分配）。
        /// </summary>
        private static void AppendJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append($"\\u{(int)c:X4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        #endregion

        /// <summary>
        /// 构建流式完成后替换为完整 Markdown 渲染的 JS 脚本。
        /// </summary>
        public static string BuildFinalRenderJs(int messageIndex, string fullContent, string reasoningContent)
        {
            return BuildFinalRenderJs(messageIndex, fullContent, reasoningContent, null);
        }

        /// <summary>
        /// 构建流式完成后替换为完整 Markdown 渲染的 JS 脚本（带额外尾部 HTML）。
        /// extraFooterHtml 会以原始 HTML 形式注入到正文后面（不经过 Markdown 渲染），
        /// 用于 &lt;details&gt; 折叠面板等需要原生 HTML 的元素。
        /// </summary>
        public static string BuildFinalRenderJs(int messageIndex, string fullContent, string reasoningContent, string? extraFooterHtml)
        {
            // 在 C# 侧完成 Markdown → HTML 渲染
            string bodyHtml = RenderMarkdownToHtml(fullContent ?? string.Empty);
            string escapedBody = EscapeJsString(bodyHtml);

            string footerJs = string.IsNullOrWhiteSpace(extraFooterHtml)
                ? "''"
                : EscapeJsString(extraFooterHtml);

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
        // ── 注入额外尾部 HTML（如执行过程 &lt;details&gt; 面板）──
        var footerHtml={footerJs};
        if(footerHtml.length>0){{
            var footerDiv=document.createElement('div');
            footerDiv.innerHTML=footerHtml;
            container.appendChild(footerDiv);
        }}
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
            retryBtn.textContent='{EscapeJsString(L["chat.html.retryButton"])}';
            retryBtn.title='{EscapeJsString(L["chat.html.retryButtonTitle"])}';
            retryBtn.onclick=function(){{window.__retryMessage({messageIndex});}};
            var msgBody=document.getElementById('msg-body-{messageIndex}');
            if(msgBody) msgBody.parentNode.insertBefore(retryBtn,msgBody.nextSibling);
        }}
    }}

    // 重新为代码块添加按钮和语言标签
    if(msgDiv) decorateCodeBlocks(msgDiv);

    window.__scrollToBottom('smooth');
}})();";
        }

        /// <summary>
        /// 构建 Handoff 按钮的 JavaScript 注入脚本。
        /// 在消息底部渲染一个"▶ 开始实现"按钮，点击后触发 Agent Handoff。
        /// </summary>
        /// <param name="messageIndex">要附加按钮的消息索引</param>
        /// <param name="targetAgent">目标 Agent 类型（如 "Edit"）</param>
        /// <param name="label">按钮显示文本</param>
        /// <returns>JavaScript 脚本字符串</returns>
        public static string BuildHandoffButtonJs(int messageIndex, string targetAgent, string label)
        {
            string escapedLabel = EscapeJsString(label);
            string escapedTarget = EscapeJsString(targetAgent);
            return $@"
(function(){{
    var msgDiv=document.getElementById('msg-{messageIndex}');
    if(!msgDiv) return;
    var existingBtn=document.getElementById('handoff-btn-{messageIndex}');
    if(existingBtn) return;

    var btn=document.createElement('button');
    btn.id='handoff-btn-{messageIndex}';
    btn.className='msg-action-btn handoff-btn';
    btn.textContent='▶ {escapedLabel}';
    btn.title='{EscapeJsString(L["chat.html.handoffButtonTitle"])}';
    btn.style.cssText='background:#28a745;color:#fff;border:none;padding:8px 20px;border-radius:6px;cursor:pointer;font-size:14px;margin:10px 0;font-weight:600;';
    btn.onmouseover=function(){{this.style.background='#218838';}};
    btn.onmouseout=function(){{this.style.background='#28a745';}};
    btn.onclick=function(){{window.__executeHandoff('{escapedTarget}','{escapedLabel}');}};

    var msgBody=document.getElementById('msg-body-{messageIndex}');
    if(msgBody) msgBody.parentNode.insertBefore(btn,msgBody.nextSibling);
}})();";
        }

        /// <summary>
        /// 构建内嵌状态栏更新 JS（替代 WPF TextBlock，消除布局卡顿）。
        /// </summary>
        /// <param name="statusText">状态文本，空字符串则清空</param>
        /// <param name="agentBadgeText">Agent 模式徽章文本，空字符串则隐藏</param>
        /// <param name="agentBadgeClass">Agent 模式 CSS 类名（plan/edit/explore）</param>
        public static string BuildStatusUpdateJs(string statusText, string agentBadgeText = "", string agentBadgeClass = "")
        {
            string escapedStatus = EscapeJsString(statusText ?? "");
            string escapedBadgeText = EscapeJsString(agentBadgeText ?? "");
            string escapedBadgeClass = EscapeJsString(agentBadgeClass ?? "");

            return $@"
(function(){{
    var s=document.getElementById('status-text');
    if(s)s.textContent={escapedStatus};
    var b=document.getElementById('agent-badge');
    if(b){{
        if({escapedBadgeText}.length>0){{
            b.textContent={escapedBadgeText};
            b.className='agent-badge {escapedBadgeClass}';
            b.style.display='inline-block';
        }}else{{
            b.style.display='none';
        }}
    }}
}})();";
        }

        /// <summary>
        /// 构建 Cache 命中率统计卡片 HTML。
        /// 用于注入到 AI 回复底部，展示本轮请求的 Prompt Cache 命中情况。
        /// </summary>
        /// <param name="hitTokens">缓存命中 token 数</param>
        /// <param name="missTokens">缓存未命中 token 数</param>
        /// <param name="promptTokens">Prompt 总 token 数</param>
        /// <param name="completionTokens">Completion 总 token 数</param>
        /// <param name="roundCount">多轮工具调用的轮次数（≥1）</param>
        /// <returns>Cache 统计卡片的 HTML，若无数据则返回空字符串</returns>
        public static string BuildCacheHitFooterHtml(long hitTokens, long missTokens, long promptTokens, long completionTokens, int roundCount = 1)
        {
            long cacheable = hitTokens + missTokens;
            if (cacheable == 0) return string.Empty;

            double rate = (double)hitTokens / cacheable;
            string level = rate >= 0.95 ? "high" : rate >= 0.50 ? "medium" : "low";
            string icon = rate >= 0.95 ? "🟢" : rate >= 0.50 ? "🟡" : "🔴";

            // 命中率百分比
            string rateText = $"{rate * 100:F1}%";

            string roundInfo = roundCount > 1 ? $" · {roundCount} {L["chat.html.cacheRound"]}" : "";

            var sb = new StringBuilder();
            sb.Append("<div class='cache-stat-card'>");
            sb.Append($"<span class='cache-icon'>{icon}</span>");
            sb.Append($"<span class='cache-rate {level}'>{rateText}</span>");
            sb.Append("<div class='cache-bar-wrap'>");
            sb.Append($"<div class='cache-bar-fill {level}' style='width:{rate * 100:F0}%'></div>");
            sb.Append("</div>");
            sb.Append("<span class='cache-detail'>");
            sb.Append($"<span>{hitTokens:N0}</span> {L["chat.html.cacheHit"]} / <span>{missTokens:N0}</span> {L["chat.html.cacheMiss"]}");
            sb.Append($" · {L["chat.html.promptTokens"]} <span>{promptTokens:N0}</span> · {L["chat.html.completionTokens"]} <span>{completionTokens:N0}</span>");
            sb.Append(roundInfo);
            sb.Append("</span>");
            sb.Append("</div>");

            return sb.ToString();
        }

        /// <summary>
        /// 构建联网搜索结果的 HTML 卡片（可折叠）。
        /// 用于在 AI 回复之前展示搜索到的网页结果。
        /// </summary>
        /// <param name="results">搜索结果列表</param>
        /// <param name="providerName">搜索提供商名称（如 "百度搜索"、"DuckDuckGo"）</param>
        /// <returns>搜索结果卡片的 HTML 字符串</returns>
        public static string BuildSearchResultsHtml(IReadOnlyList<WebSearchResult> results, string? providerName = null)
        {
            if (results == null || results.Count == 0)
                return string.Empty;

            string label = providerName ?? L["chat.html.webSearchLabel"];

            var sb = new StringBuilder();
            sb.Append("<details class='search-results-card' open='true'>");
            sb.Append($"<summary>🌐 {label} ({results.Count} {L["chat.html.webSearchResults"]})</summary>");

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
    window.__scrollToBottom('smooth');
}})();";
        }

        /// <summary>
        /// 构建内联编辑区域的 JS 脚本。
        /// 在用户消息气泡中用 textarea + 保存/取消按钮替换正文区域。
        /// </summary>
        public static string BuildInlineEditJs(int messageIndex, string originalContent)
        {
            // 对文本进行 HTML 编码后嵌入 JS 字符串（防止 </textarea> 等标签注入）
            string safeText = System.Net.WebUtility.HtmlEncode(originalContent ?? string.Empty);
            string escapedText = EscapeJsString(safeText);

            // 编辑按钮的 onclick 中需要 JSON 字符串化新文本
            string escapedOnclickText = EscapeJsString(originalContent ?? string.Empty);

            return $@"
(function(){{
    var msgBody=document.getElementById('msg-body-{messageIndex}');
    if(!msgBody)return;

    // 隐藏原有的编辑按钮
    var editBtn=document.getElementById('edit-btn-{messageIndex}');
    if(editBtn)editBtn.style.display='none';

    // 移除已有的内联编辑区
    var existing=document.getElementById('inline-edit-{messageIndex}');
    if(existing)existing.remove();

    // 创建内联编辑区
    var editArea=document.createElement('div');
    editArea.id='inline-edit-{messageIndex}';
    editArea.className='inline-edit-area';

    var ta=document.createElement('textarea');
    ta.id='inline-textarea-{messageIndex}';
    ta.textContent={escapedText};
    editArea.appendChild(ta);

    var actions=document.createElement('div');
    actions.className='edit-actions';

    var saveBtn=document.createElement('button');
    saveBtn.className='inline-edit-btn-save';
    saveBtn.textContent='✅ 确认修改';
    saveBtn.onclick=function(){{
        var textEl=document.getElementById('inline-textarea-{messageIndex}');
        var val=textEl?textEl.value:'';
        window.__editMessageConfirm({messageIndex},val);
    }};
    actions.appendChild(saveBtn);

    var cancelBtn=document.createElement('button');
    cancelBtn.className='inline-edit-btn-cancel';
    cancelBtn.textContent='❌ 取消';
    cancelBtn.onclick=function(){{window.__editMessageCancel({messageIndex});}};
    actions.appendChild(cancelBtn);

    editArea.appendChild(actions);

    // 用编辑区替换正文内容
    msgBody.innerHTML='';
    msgBody.appendChild(editArea);

    // 自动聚焦 textarea 并选中全部文字
    setTimeout(function(){{
        var taEl=document.getElementById('inline-textarea-{messageIndex}');
        if(taEl){{taEl.focus();taEl.select();}}
    }},50);

    // Esc 键取消编辑
    document.addEventListener('keydown',function handler(e){{
        if(e.key==='Escape'){{
            var taEl=document.getElementById('inline-textarea-{messageIndex}');
            if(taEl && document.activeElement===taEl){{
                window.__editMessageCancel({messageIndex});
                document.removeEventListener('keydown',handler);
            }}
        }}
    }});

    window.__scrollToBottom('smooth');
}})();";
        }

        /// <summary>
        /// 构建恢复用户消息正文的 JS 脚本（取消内联编辑时使用）。
        /// </summary>
        public static string BuildRestoreMessageJs(int messageIndex, string originalContent)
        {
            string escapedContent = EscapeJsString(originalContent);

            return $@"
(function(){{
    var msgBody=document.getElementById('msg-body-{messageIndex}');
    if(!msgBody)return;

    // 移除内联编辑区
    var editArea=document.getElementById('inline-edit-{messageIndex}');
    if(editArea)editArea.remove();

    // 恢复原始正文
    var text={escapedContent};
    var html=text.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/\n/g,'<br>');
    msgBody.innerHTML=html;

    // 恢复编辑按钮
    var editBtn=document.getElementById('edit-btn-{messageIndex}');
    if(editBtn)editBtn.style.display='';
}})();";
        }

        #endregion

        #region Private Methods - Message HTML Builders

        private static void AppendUserMessageHtml(StringBuilder sb, string content, List<FileParseResult>? attachedFiles = null, int messageIndex = -1)
        {
            // ── 编辑按钮（仅在有索引时渲染） ──
            string editBtnHtml = messageIndex >= 0
                ? $"<button id='edit-btn-{messageIndex}' class='msg-action-btn edit-btn' onclick='window.__editMessage({messageIndex})' title='{L["chat.html.editButtonTitle"]}'>✏️</button>"
                : "";

            // ── 文件附件 𠅂
            string fileBlocksHtml = BuildFileAttachmentHtml(attachedFiles);

            string escaped = System.Net.WebUtility.HtmlEncode((content ?? string.Empty).Trim());
            string body = escaped.Replace("\n", "<br>");

            // ── 左对齐，简洁气泡
            sb.Append("<div class='msg-wrapper user'>");
            sb.Append("<div class='msg-avatar user'>👤</div>");
            sb.Append("<div class='msg-bubble user'>");
            sb.Append($"<div class='msg-role-label user'>You</div>");
            sb.Append(fileBlocksHtml);
            sb.Append($"<div class='msg-content' id='msg-body-{messageIndex}'>{body}</div>");
            sb.Append(editBtnHtml);
            sb.Append("</div>");
            sb.Append("</div>");
        }

        /// <summary>
        /// 构建文件附件 HTML（提取为独立方法，供用户消息和增量追加复用）。
        /// </summary>
        private static string BuildFileAttachmentHtml(List<FileParseResult>? attachedFiles)
        {
            if (attachedFiles == null || attachedFiles.Count == 0)
                return string.Empty;

            var blocks = new StringBuilder();
            blocks.Append("<div style='margin-bottom:6px;text-align:left'>");
            foreach (var file in attachedFiles)
            {
                string escapedFileName = System.Net.WebUtility.HtmlEncode(file.FileName);
                if (!file.Success || string.IsNullOrEmpty(file.Content))
                {
                    string errorMsg = System.Net.WebUtility.HtmlEncode(file.Error ?? "解析失败");
                    blocks.Append("<div style='display:inline-block;background:#5c1a1a;color:#e07878;padding:2px 8px;border-radius:3px;font-size:10px;margin:2px'>📎 ");
                    blocks.Append(escapedFileName).Append(" — ").Append(errorMsg);
                    blocks.Append("</div>");
                    continue;
                }

                bool isImage = IsImageExtension(file.FileExtension);
                bool isPdf = string.Equals(file.FileExtension, ".pdf", StringComparison.OrdinalIgnoreCase);
                string lang = isImage ? string.Empty : GetLanguageFromExtension(file.FileExtension);
                string borderColor = isImage ? "#6b3fa0" : isPdf ? "#8b4513" : "#3a5a3a";
                string bgColor = isImage ? "#1a1a2e" : isPdf ? "#1e150a" : "#1a2e1a";
                string summaryColor = isImage ? "#b98eff" : isPdf ? "#d4a76a" : "#7ec87e";
                string icon = isImage ? "🖼️" : isPdf ? "📄" : "📎";
                string tag = isImage ? "OCR" : isPdf ? "PDF" : (file.Truncated ? "已截断" : "");

                string escapedContent = System.Net.WebUtility.HtmlEncode(
                    (file.Truncated && file.TruncationNote != null
                        ? file.TruncationNote + "\n\n" + file.Content
                        : file.Content) ?? string.Empty);

                blocks.Append($"<details class='file-attachment' style='margin-bottom:3px;border:1px solid {borderColor};border-radius:4px;background:{bgColor};overflow:hidden'>");
                blocks.Append($"<summary style='cursor:pointer;padding:3px 8px;color:{summaryColor};font-size:11px;font-weight:600;list-style:none'>{icon} {escapedFileName}");
                if (!string.IsNullOrEmpty(tag))
                    blocks.Append($" <span style='color:#c8a84e;font-size:9px'>({tag})</span>");
                blocks.Append("</summary>");
                blocks.Append("<div style='padding:4px 8px;max-height:300px;overflow-y:auto'><pre style='margin:0;background:transparent;border:none;font-size:10px;line-height:1.3;max-height:280px'>");
                if (!string.IsNullOrEmpty(lang))
                    blocks.Append($"<code class='language-{lang}'>");
                blocks.Append(escapedContent);
                if (!string.IsNullOrEmpty(lang))
                    blocks.Append("</code>");
                blocks.Append("</pre></div></details>");
            }
            blocks.Append("</div>");
            return blocks.ToString();
        }

        /// <summary>
        /// 构建分支导航 HTML（根据 ForkReason 决定放在用户/助手气泡下）。
        /// </summary>
        private static string BuildBranchNavHtml(ChatMessage msg, int msgIndex)
        {
            if (msg == null || msg.SiblingCount <= 1) return "";

            int curIdx = msg.SiblingIndex;
            bool isFirst = curIdx <= 1;
            bool isLast = curIdx >= msg.SiblingCount;
            string nodeId = System.Net.WebUtility.HtmlEncode(msg.NodeId ?? "");

            return
                $"<div class='branch-nav'>" +
                $"<button class='branch-nav-btn' onclick='window.__navigateBranch(\"{nodeId}\",-1)' title='上一个分支' {(isFirst ? "disabled" : "")}>◀</button>" +
                $"<span class='branch-nav-label'>分支 {curIdx}/{msg.SiblingCount}</span>" +
                $"<button class='branch-nav-btn' onclick='window.__navigateBranch(\"{nodeId}\",1)' title='下一个分支' {(isLast ? "disabled" : "")}>▶</button>" +
                $"</div>";
        }

        private static void AppendAssistantMessageHtml(StringBuilder sb, ChatMessage msg, int idx)
        {
            string bodyHtml;
            bool isStreaming = msg.IsStreaming;

            if (!string.IsNullOrEmpty(msg.Content))
            {
                if (msg.IsHtml)
                    bodyHtml = msg.Content;
                else
                    bodyHtml = RenderMarkdownToHtml(msg.Content);
            }
            else if (isStreaming)
            {
                bodyHtml = "<span style='color:#888;font-style:italic'>Thinking…</span>";
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
                ? " <span style='color:#4fc1ff;font-size:10px'>● ● ●</span>" : "";

            string retryBtnHtml = !isStreaming && !msg.IsHtml
                ? $"<button id='retry-btn-{idx}' class='msg-action-btn retry-btn' onclick='window.__retryMessage({idx})' title='{L["chat.html.retryButtonTitle"]}'>↻</button>"
                : "";

            string branchNavHtml = BuildBranchNavHtml(msg, idx);

            // Copilot Chat 风格：左对齐，AI 标签
            sb.Append($"<div id='msg-{idx}' class='msg-wrapper ai'>");
            sb.Append("<div class='msg-avatar ai'>D</div>");
            sb.Append("<div class='msg-bubble ai'>");
            sb.Append($"<div class='msg-role-label ai'>DeepSeek{streamingDots}</div>");
            sb.Append(reasoningHtml);
            sb.Append($"<div class='msg-content' id='msg-body-{idx}'>{bodyHtml}</div>");
            sb.Append(streamingCursor);
            sb.Append(retryBtnHtml);
            sb.Append(branchNavHtml);
            sb.Append("</div>");
            sb.Append("</div>");
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
                "<summary>" + L["chat.html.thinkingTitle"] + "</summary>" +
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
                        $"<details class='reasoning-panel' open='true'><summary>{L["chat.html.thinkingTitle"]}</summary><div class='reasoning-content'>{thinkBody}</div></details>";
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

        private static string WrapFullPage(string messagesHtml, bool hasStreamingMessage)
        {
            string autoScrollJs = hasStreamingMessage ? BuildAutoScrollJs() : "";

return "<!DOCTYPE html><html lang='zh-CN'><head><meta charset='UTF-8'>" +
       "<meta name='viewport' content='width=device-width,initial-scale=1'>" +
       "<style>" + PageCss + "</style>" +
       // CSS 也改为非阻塞加载
       "<link rel='stylesheet' href='" + HighlightJsCdnStyleDark + 
       "' media='none' onload=\"if(this.media!=='all')this.media='all'\" />" +
       "</head><body>" +
       "<div id='chat-container'>" + messagesHtml + "</div>" +
       "<div id='status-bar'><span id='status-text'></span><span id='agent-badge' class='agent-badge' style='display:none'></span></div>" +
       "<script>" +
       // 动态创建 script 标签，异步加载 highlight.js
       "var hljsScript=document.createElement('script');" +
       "hljsScript.src='" + HighlightJsCdnScript + "';" +
       "hljsScript.onload=function(){" +
       "  window.decorateCodeBlocks(document.getElementById('chat-container'));" +
       "};" +
       "document.head.appendChild(hljsScript);" +
       BuildDecorateCodeBlocksJsFunction() +
       BuildShiftScrollJs() +
       autoScrollJs +
       BuildAppendMessageJsFunction() +
       BuildRetryEditJsFunctions() +
       // ── 页面就绪信号 ──
       "window.__pageReady=true;" +
       "if(window.chrome?.webview)window.chrome.webview.postMessage('__pageReady__');" +
       "setTimeout(function(){window.__scrollToBottom('auto');},100);" +
       "</script></body></html>";
        }

        /// <summary>
        /// 构建 Agent 步骤流程管线 HTML（垂直管线布局）。
        /// 每个步骤是一个节点（编号圆圈 + 连接线），实时展示执行进度。
        /// </summary>
        public static string BuildAgentPlanHtml(AgentTaskPlan plan)
        {
            string pid = plan.PlanId;
            int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
            int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
            int total = plan.Steps.Count;
            int progressPercent = total > 0 ? (completed + failed) * 100 / total : 0;

            var sb = new StringBuilder();
            sb.Append($"<div class='agent-plan' id='agent-plan-{pid}'>");

            // ── 头部：标题 + 进度条 ──
            sb.Append("<div class='agent-plan-header'>");
            sb.Append($"<div class='agent-plan-title'>Coding Agent — {EscapeHtml(plan.Title)}</div>");
            sb.Append("<div class='agent-plan-progress'>");
            sb.Append($"<span class='step-counter' id='agent-header-counter-{pid}'>{completed}/{total} 步</span>");
            sb.Append("<span class='agent-plan-progress-bar'>");
            sb.Append($"<span class='agent-plan-progress-bar-fill' id='agent-progress-fill-{pid}' style='width:{progressPercent}%'></span>");
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

                string tagHtml = "";
                if (IsCodeWritingStepName(step.Title))
                    tagHtml = "<span class='agent-step-tag code'>代码</span>";
                else if (IsBuildOrRunStepName(step.Title))
                    tagHtml = "<span class='agent-step-tag build'>构建</span>";
                else if (IsAnalyzeStepName(step.Title))
                    tagHtml = "<span class='agent-step-tag analyze'>分析</span>";

                string lineClass = step.Status == AgentStepStatus.Completed ? "done"
                    : step.Status == AgentStepStatus.InProgress ? "active"
                    : "";

                sb.Append($"<div class='agent-step-node' id='agent-step-node-{pid}-{step.Index}'>");

                sb.Append("<div class='agent-step-bullet-wrap'>");
                sb.Append($"<div class='agent-step-bullet {statusClass}' id='agent-bullet-{pid}-{step.Index}'>{bulletText}</div>");
                if (!isLast)
                    sb.Append($"<div class='agent-step-line {lineClass}' id='agent-line-{pid}-{step.Index}'></div>");
                sb.Append("</div>");

                sb.Append("<div class='agent-step-content'>");
                sb.Append("<div class='agent-step-title-row'>");
                sb.Append($"<span class='agent-step-title {statusClass}' id='agent-title-{pid}-{step.Index}'>{EscapeHtml(step.Title)}</span>");
                if (!string.IsNullOrEmpty(tagHtml))
                    sb.Append(tagHtml);
                sb.Append("</div>");

                string summaryDisplay = string.IsNullOrEmpty(step.ResultSummary) ? "none" : "block";
                sb.Append($"<div class='agent-step-summary' id='agent-summary-{pid}-{step.Index}' style='display:{summaryDisplay}'>");
                if (!string.IsNullOrEmpty(step.ResultSummary))
                    sb.Append(EscapeHtml(step.ResultSummary!));
                sb.Append("</div>");

                sb.Append("</div></div>");
            }

            // ── 底部状态栏 ──
            sb.Append("<div class='agent-plan-footer'>");
            sb.Append($"<span class='elapsed' id='agent-elapsed-{pid}'></span>");
            sb.Append($"<span class='step-counter' id='agent-step-counter-{pid}'>{completed}/{total} 步完成</span>");
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
    window.__scrollToBottom('smooth');
}})();";
        }

        /// <summary>
        /// 构建文件删除确认 UI 的 JS 脚本（在聊天底部注入文件列表 + 确认/取消按钮）。
        /// </summary>
        /// <param name="request">权限请求，其 ActionType 应为 "file_delete"，FilePaths 包含待删除文件路径列表</param>
        public static string BuildFileDeleteConfirmationJs(AgentPermissionRequest request)
        {
            string escapedRequestId = EscapeJsString(request.RequestId);

            // 构建文件列表 HTML（在 C# 侧完成，避免 JS 字符串嵌套转义）
            var fileItemsHtml = new StringBuilder();
            if (request.FilePaths != null && request.FilePaths.Count > 0)
            {
                foreach (string filePath in request.FilePaths)
                {
                    string fileName = System.IO.Path.GetFileName(filePath);
                    fileItemsHtml.Append("<div class=\"file-item\">");
                    fileItemsHtml.Append("<span class=\"file-icon\">📄</span>");
                    fileItemsHtml.Append("<span class=\"file-path\" title=\"");
                    fileItemsHtml.Append(EscapeHtml(filePath));
                    fileItemsHtml.Append("\">");
                    fileItemsHtml.Append(EscapeHtml(fileName));
                    fileItemsHtml.Append("</span></div>");
                }
            }

            // 构建完整的卡片 innerHTML
            string cardInnerHtml =
                "<div class=\"file-delete-card-header\">" +
                "<span class=\"icon\">🗑️</span>" +
                "<span class=\"title\">确认删除文件</span>" +
                "</div>" +
                "<div class=\"file-delete-card-body\">" +
                "<div class=\"warning-text\">⚠️ 即将删除以下项目文件，此操作将通过 Visual Studio 项目系统执行（如文件已加入项目，也将从项目中移除）：</div>" +
                "<div class=\"file-list\">" +
                fileItemsHtml.ToString() +
                "</div>" +
                "<div class=\"warning-text\" style=\"color:#E07878;font-weight:600\">此操作不可撤销！是否继续？</div>" +
                "</div>" +
                "<div class=\"file-delete-card-footer\">" +
                $"<button class=\"file-delete-btn-confirm\" onclick=\"window.__fileDeleteConfirm('{EscapeHtmlAttribute(request.RequestId)}')\">✅ 确认删除</button>" +
                $"<button class=\"file-delete-btn-cancel\" onclick=\"window.__fileDeleteCancel('{EscapeHtmlAttribute(request.RequestId)}')\">❌ 取消</button>" +
                "</div>";

            string escapedInnerHtml = EscapeJsString(cardInnerHtml);

            return $@"
(function(){{
    var existing=document.getElementById('file-delete-confirm');
    if(existing)existing.remove();

    var div=document.createElement('div');
    div.id='file-delete-confirm';
    div.className='file-delete-card';
    div.innerHTML={escapedInnerHtml};

    var container=document.getElementById('chat-container');
    if(container)container.appendChild(div);
    window.__scrollToBottom('smooth');
}})();";
        }

        /// <summary>
        /// 构建终端命令审批 UI 的 JS 脚本（在聊天底部注入命令详情 + 允许/跳过按钮）。
        /// </summary>
        /// <param name="request">权限请求，其 ActionType 应为 "terminal_command"，
        /// Title 为"运行 pwsh 命令?"，Command 为实际命令，FilePaths[0] 为命令说明。</param>
        public static string BuildTerminalApprovalJs(AgentPermissionRequest request)
        {
            string escapedRequestId = EscapeJsString(request.RequestId);
            string escapedTitle = EscapeJsString(request.Title);
            string escapedCommand = EscapeJsString(request.Command);
            string explanation = (request.FilePaths != null && request.FilePaths.Count > 0)
                ? request.FilePaths[0] : string.Empty;
            string escapedExplanation = EscapeJsString(explanation);

            string cardInnerHtml =
                "<div class=\"terminal-approval-card-header\">" +
                "<span class=\"icon\">🖥️</span>" +
                "<span class=\"title\">" + EscapeHtml(request.Title) + "</span>" +
                "</div>" +
                "<div class=\"terminal-approval-card-body\">" +
                "<div class=\"warning-text\">⚠️ AI 请求在终端中执行以下命令，请确认后继续：</div>" +
                "<div class=\"cmd-block\">" + EscapeHtml(request.Command) + "</div>" +
                (!string.IsNullOrEmpty(explanation)
                    ? "<div class=\"cmd-explanation\">📝 " + EscapeHtml(explanation) + "</div>"
                    : "") +
                "<div class=\"warning-text\" style=\"color:#CEA85C;font-weight:600\">此命令将修改系统状态，是否允许执行？</div>" +
                "</div>" +
                "<div class=\"terminal-approval-card-footer\">" +
                $"<button class=\"terminal-approval-btn-allow\" onclick=\"window.__terminalApprove('{EscapeHtmlAttribute(request.RequestId)}')\">✅ 允许</button>" +
                $"<button class=\"terminal-approval-btn-skip\" onclick=\"window.__terminalSkip('{EscapeHtmlAttribute(request.RequestId)}')\">⏭️ 跳过</button>" +
                "</div>";

            string escapedInnerHtml = EscapeJsString(cardInnerHtml);

            return $@"
(function(){{
    var existing=document.getElementById('terminal-approval');
    if(existing)existing.remove();

    var div=document.createElement('div');
    div.id='terminal-approval';
    div.className='terminal-approval-card';
    div.innerHTML={escapedInnerHtml};

    var container=document.getElementById('chat-container');
    if(container)container.appendChild(div);
    window.__scrollToBottom('smooth');
}})();";
        }

        /// <summary>
        /// 转义 HTML 属性值中的引号字符。
        /// </summary>
        private static string EscapeHtmlAttribute(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("'", "&#39;")
                        .Replace("<", "&lt;").Replace(">", "&gt;");
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
        /// 构建 Agent 任务流程底部面板的创建/更新 JS。
        /// 如果面板不存在则创建，存在则更新内容。
        /// 面板固定在聊天底部，包含步骤管线、文件变更、日志摘要。
        /// </summary>
        public static string BuildAgentTaskPanelCreateJs(AgentTaskPlan plan)
        {
            string pid = plan.PlanId;
            string escapedTitle = EscapeJsString(plan.Title);
            string planHtml = BuildAgentPlanHtml(plan);
            // 移除 agent-plan 自带的外层 div 包装，只保留内部内容（因为面板有自己的 wrapper）
            // 保留完整的 agent-plan div，在 CSS 中已去掉其 margin/border
            string escapedPlanHtml = EscapeJsString(planHtml);
            int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
            int total = plan.Steps.Count;

            return $@"
(function(){{
    // ── 清除同一 PlanId 的旧面板（若存在则更新），同时移除其他旧任务面板避免堆积 ──
    var existing=document.getElementById('agent-task-panel-{pid}');
    if(existing){{
        // 更新面板内容
        var body=document.getElementById('agent-task-body-{pid}');
        if(body)body.innerHTML={escapedPlanHtml};
        var prog=document.getElementById('agent-task-progress-{pid}');
        if(prog)prog.textContent='{completed}/{total} 步';
        return;
    }}

    // 移除所有旧的 agent-task-panel（不同 PlanId 的残留面板）
    var oldPanels=document.querySelectorAll('[id^=""agent-task-panel-""]');
    for(var i=0;i<oldPanels.length;i++){{var op=oldPanels[i];if(op&&op.parentNode)op.parentNode.removeChild(op);}}

    // 创建新面板
    var panel=document.createElement('div');
    panel.id='agent-task-panel-{pid}';
    panel.className='agent-task-panel';
    panel.innerHTML=
        '<div class=""agent-task-panel-header"" onclick=""var p=document.getElementById(\'agent-task-panel-{pid}\');if(p)p.classList.toggle(\'collapsed\')"">'+
        '<span class=""task-icon"">🤖</span>'+
        '<span class=""task-title"">Coding Agent — '+{escapedTitle}+'</span>'+
        '<span class=""task-progress"" id=""agent-task-progress-{pid}"">{completed}/{total} 步</span>'+
        '<button class=""task-close"" id=""agent-task-close-{pid}"" onclick=""(function(e){{e.stopPropagation();var p=document.getElementById(\'agent-task-panel-{pid}\');if(p&&p.parentNode)p.parentNode.removeChild(p);}})(event);return false;"" title=""关闭面板"">✕</button>'+
        '</div>'+
        '<div class=""agent-task-panel-body"" id=""agent-task-body-{pid}"">'+{escapedPlanHtml}+'</div>';

    var container=document.getElementById('chat-container');
    if(container)container.appendChild(panel);
    window.__scrollToBottom('smooth');
}})();";
        }

        /// <summary>
        /// 构建更新任务面板步骤进度的 JS（仅更新步骤状态，不重建整个面板）。
        /// </summary>
        public static string BuildAgentTaskPanelUpdateJs(AgentTaskPlan plan)
        {
            string pid = plan.PlanId;
            int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
            int total = plan.Steps.Count;

            var sb = new StringBuilder();
            sb.Append("(function(){");

            // 更新进度文本
            sb.Append($"var prog=document.getElementById('agent-task-progress-{pid}');");
            sb.Append($"if(prog)prog.textContent='{completed}/{total} 步';");

            // 复用现有的步骤进度更新逻辑
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

                sb.Append($"var b=document.getElementById('agent-bullet-{pid}-{step.Index}');");
                sb.Append($"if(b){{b.className='agent-step-bullet {statusClass}';b.textContent='{bulletText}';}}");

                sb.Append($"var t=document.getElementById('agent-title-{pid}-{step.Index}');");
                sb.Append($"if(t){{t.className='agent-step-title {statusClass}';}}");

                sb.Append($"var s=document.getElementById('agent-summary-{pid}-{step.Index}');");
                if (!string.IsNullOrEmpty(step.ResultSummary))
                {
                    sb.Append($"if(s){{s.style.display='block';s.textContent={EscapeJsString(step.ResultSummary!)};}}");
                }
                else
                {
                    sb.Append("if(s){s.style.display='none';}");
                }

                string lineClass = step.Status == AgentStepStatus.Completed ? "done"
                    : step.Status == AgentStepStatus.InProgress ? "active"
                    : "";
                sb.Append($"var l=document.getElementById('agent-line-{pid}-{step.Index}');");
                sb.Append($"if(l){{l.className='agent-step-line {lineClass}';}}");
            }

            // 更新进度条
            int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
            int progressPercent = total > 0 ? (completed + failed) * 100 / total : 0;
            sb.Append($"var pf=document.getElementById('agent-progress-fill-{pid}');");
            sb.Append($"if(pf){{pf.style.width='{progressPercent}%';}}");

            // 更新头部计数器
            sb.Append($"var hc=document.getElementById('agent-header-counter-{pid}');");
            sb.Append($"if(hc){{hc.textContent='{completed}/{total} 步';}}");

            // 更新底部计数器
            sb.Append($"var pc=document.getElementById('agent-step-counter-{pid}');");
            string counterText = failed > 0
                ? $"{completed}/{total} 步完成，{failed} 步失败"
                : $"{completed}/{total} 步完成";
            sb.Append($"if(pc){{pc.textContent='{EscapeJsString(counterText)}';}}");

            sb.Append("})();");
            return sb.ToString();
        }

        /// <summary>
        /// 构建任务完成后更新面板为完成状态的 JS（显示完成标记和关闭按钮高亮）。
        /// </summary>
        public static string BuildAgentTaskPanelCompleteJs(AgentTaskPlan plan)
        {
            string pid = plan.PlanId;
            int completed = plan.Steps.Count(s => s.Status == AgentStepStatus.Completed);
            int failed = plan.Steps.Count(s => s.Status == AgentStepStatus.Failed);
            int total = plan.Steps.Count;
            string statusIcon = plan.IsCancelled ? "⚠️" : (failed > 0 ? "⚠️" : "✅");
            string statusColor = plan.IsCancelled ? "#E07878" : (failed > 0 ? "#C8A84E" : "#4EC9B0");
            string statusText = plan.IsCancelled ? "已取消" : (failed > 0 ? $"{completed}/{total} 步成功，{failed} 步失败" : $"{completed}/{total} 步全部成功");
            string escapedStatusIcon = EscapeJsString(statusIcon);
            string escapedStatusText = EscapeJsString(statusText);

            return $@"
(function(){{
    var panel=document.getElementById('agent-task-panel-{pid}');
    if(!panel)return;

    // 更新标题为完成状态
    var title=panel.querySelector('.task-title');
    if(title){{title.textContent={escapedStatusIcon}+' '+{escapedStatusText};title.style.color='{statusColor}';}}

    // 更新进度
    var prog=document.getElementById('agent-task-progress-{pid}');
    if(prog)prog.textContent='{completed}/{total} 步';

    // 高亮关闭按钮
    var closeBtn=document.getElementById('agent-task-close-{pid}');
    if(closeBtn){{closeBtn.style.background='#3C1A1A';closeBtn.style.color='#E07878';closeBtn.style.borderColor='#6A3A3A';closeBtn.title='关闭任务面板';}}

    // 更新进度条
    var pf=document.getElementById('agent-progress-fill-{pid}');
    if(pf)pf.style.width='100%';

    // 滚动到底部
    window.__scrollToBottom('smooth');
}})();";
        }
    }
}

