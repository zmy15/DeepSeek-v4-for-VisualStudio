using System;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    public static partial class ChatHtmlService
    {
        #region CSS & CDN Constants

        private const string HighlightJsCdnScript = "https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/highlight.min.js";
        private const string HighlightJsCdnStyleDark = "https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/styles/github-dark.min.css";

        private const string PageCss = @"*{box-sizing:border-box;margin:0;padding:0}
body{background-color:#1e1e1e;color:#cccccc;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;font-size:13px;line-height:1.6;padding:0;overflow-wrap:break-word;word-wrap:break-word;scroll-behavior:smooth}
#chat-container{padding:12px 12px 30px 12px;max-width:100%;margin:0}
h1,h2,h3,h4,h5,h6{color:#e0e0e0;margin:16px 0 8px;font-weight:600}h1{font-size:1.3em}h2{font-size:1.15em}h3{font-size:1.05em}
p{margin:6px 0}a{color:#4fc1ff;text-decoration:none}a:hover{text-decoration:underline}strong,b{color:#e8e8e8;font-weight:600}
code{background:#333;color:#f48771;padding:1px 6px;border-radius:3px;font-family:'Cascadia Code','Fira Code',Consolas,monospace;font-size:0.9em}
pre{background:#252526;border:1px solid #3c3c3c;border-radius:8px;padding:32px 14px 12px 14px;margin:10px 0;overflow-x:auto;overflow-y:auto;max-height:480px;font-size:0.88em;line-height:1.5;position:relative}
pre code{background:transparent;color:#d4d4d4;padding:0;font-size:inherit;white-space:pre;display:block}
ul,ol{padding-left:22px;margin:6px 0}li{margin:2px 0}blockquote{border-left:3px solid #4fc1ff;padding:6px 12px;margin:8px 0;background:#2a2a2a;color:#aaa}
table{border-collapse:collapse;margin:8px 0;width:100%}th,td{border:1px solid #444;padding:6px 10px;text-align:left}th{background:#333;color:#e0e0e0;font-weight:600}hr{border:none;border-top:1px solid #444;margin:12px 0}
.code-lang{position:absolute;top:6px;left:14px;color:#9cdcfe;font-size:10px;font-family:'Segoe UI',sans-serif;text-transform:uppercase}
.copy-btn{position:absolute;top:4px;right:8px;background:#3c3c3c;color:#ccc;border:1px solid #555;border-radius:4px;padding:2px 10px;font-size:11px;cursor:pointer;font-family:'Segoe UI',sans-serif;z-index:1;transition:all .15s}.copy-btn:hover{background:#505050;color:#fff}
.msg-wrapper{display:flex;gap:12px;margin-bottom:24px;padding:0 4px}.msg-wrapper.user{justify-content:flex-end;align-items:center;gap:6px}
.msg-avatar{width:28px;height:28px;border-radius:50%;display:flex;align-items:center;justify-content:center;font-size:13px;flex-shrink:0}.msg-avatar.ai{background:#4ec9b0;color:#1e1e1e}.msg-avatar.user{background:#569cd6;color:#fff}
.msg-bubble{max-width:85%;min-width:0}.msg-bubble.ai{flex:1;min-width:0}
.msg-role-label{font-size:11px;font-weight:600;margin-bottom:4px;color:#999;text-transform:uppercase;letter-spacing:1px}.msg-role-label.user{align-self:flex-end;margin-bottom:6px;color:#569cd6;text-align:left}.msg-role-label.ai{color:#4ec9b0}
.msg-content{font-size:13px;line-height:1.65}.msg-content p:first-child{margin-top:0}.msg-content p:last-child{margin-bottom:0}
.msg-wrapper.user .msg-content{background:#264f78;border-radius:12px 12px 4px 12px;padding:10px 14px;color:#d4d4d4}
.msg-wrapper.user .msg-content pre{background:#1e3a5a;border-color:#2d5a8a}
.msg-wrapper.ai .msg-content{background:#2a2a2a;border:1px solid #555555;border-radius:4px 12px 12px 12px;padding:10px 14px;color:#d4d4d4}
.msg-wrapper.ai .msg-content pre{background:#1e1e1e;border-color:#333}
.agent-route-badge{display:inline-block;background:#3a2a5a;color:#c8a0f0;font-size:10px;font-weight:700;padding:2px 8px;border-radius:4px;margin-bottom:6px;letter-spacing:0.5px;text-transform:uppercase}
.reasoning-panel{margin:8px 0;border:1px solid #3a3a5a;border-radius:8px;background:#1e1e2e;overflow:hidden}
.reasoning-panel summary{cursor:pointer;padding:8px 14px;color:#9b9bd4;font-size:12px;font-weight:600;background:#252540;user-select:none;list-style:none}
.reasoning-panel summary::-webkit-details-marker{display:none}
.reasoning-panel .reasoning-content{padding:10px 14px;color:#8a8ab4;font-size:12px;font-style:italic;line-height:1.5;white-space:pre-wrap;max-height:300px;overflow-y:auto}
.search-results-card{margin:8px 0 12px;border:1px solid #3a5a8a;border-radius:8px;background:#1a2636}
.search-results-card summary{cursor:pointer;padding:8px 14px;color:#7eb8e0;font-size:12px;font-weight:600;background:#253545;list-style:none}
.search-results-card summary::-webkit-details-marker{display:none}
.search-results-card .search-result-item{padding:8px 14px;border-bottom:1px solid #2a3a4a}.search-results-card .search-result-item:last-child{border-bottom:none}
.search-results-card .search-result-title{color:#6cafd9;font-size:12px;font-weight:600;display:block}
.search-results-card .search-result-url{color:#608b4e;font-size:10px;display:block;word-break:break-all}
.search-results-card .search-result-snippet{color:#a0a0a0;font-size:11px;line-height:1.4}
.agent-plan{margin:8px 0;border:1px solid #3a5a3a;border-radius:8px;background:#1a2e1a;padding:10px 14px}
.agent-plan-header{display:flex;align-items:center;gap:12px}.agent-plan-title{color:#7ec87e;font-size:12px;font-weight:600}
.agent-plan-progress{display:flex;align-items:center;gap:8px;flex:1}.step-counter{color:#aaa;font-size:11px}
.agent-plan-progress-bar{flex:1;height:4px;background:#333;border-radius:2px}
.agent-plan-progress-bar-fill{height:100%;background:#4ec9b0;border-radius:2px;transition:width .3s}
.agent-step-node{display:flex;gap:8px;margin:6px 0}.agent-step-bullet-wrap{display:flex;flex-direction:column;align-items:center;width:20px;flex-shrink:0}
.agent-step-bullet{width:16px;height:16px;border-radius:50%;font-size:10px;display:flex;align-items:center;justify-content:center;font-weight:bold}
.agent-step-bullet.completed{background:#4ec9b0;color:#1e1e1e}.agent-step-bullet.in-progress{background:#e0c060;color:#1e1e1e;animation:pulse 1.5s infinite}
.agent-step-bullet.failed{background:#f48771;color:#1e1e1e}.agent-step-bullet.pending{background:#333;color:#aaa}
.agent-step-line{width:2px;flex:1;background:#333;min-height:12px;margin:2px 0}.agent-step-line.done{background:#4ec9b0}.agent-step-line.active{background:#e0c060}
.agent-step-content{flex:1;min-width:0}.agent-step-title{font-size:12px;color:#ccc}.agent-step-title-row{display:flex;align-items:center;gap:6px}
.agent-step-tag{font-size:9px;padding:1px 5px;border-radius:3px}.agent-step-tag.code{background:#264f78;color:#9cdcfe}
.agent-step-tag.build{background:#5a3a00;color:#e0c060}.agent-step-tag.analyze{background:#3a5a3a;color:#7ec87e}
.agent-step-summary{font-size:11px;color:#888;margin-top:2px}@keyframes pulse{0%,100%{opacity:1}50%{opacity:.5}}
.cache-stat-card{display:flex;align-items:center;gap:8px;padding:8px 12px;background:#2a2a2a;border:1px solid #3c3c3c;border-radius:6px;margin:10px 0;font-size:11px}
.cache-icon{font-size:14px}.cache-rate{font-weight:700;font-size:12px}.cache-rate.high{color:#6cd96c}.cache-rate.medium{color:#e0c060}.cache-rate.low{color:#f48771}
.cache-bar-wrap{flex:1;height:4px;background:#333;border-radius:2px}.cache-bar-fill{height:100%;border-radius:2px}
.cache-bar-fill.high{background:#6cd96c}.cache-bar-fill.medium{background:#e0c060}.cache-bar-fill.low{background:#f48771}.cache-detail{color:#888}
.msg-action-btn{display:inline-flex;align-items:center;gap:4px;background:transparent;border:none;color:#888;cursor:pointer;font-size:11px;padding:2px 6px;border-radius:3px;margin-top:4px;opacity:0;transition:opacity .15s}
.msg-wrapper:hover .msg-action-btn{opacity:1}.msg-action-btn:hover{background:#3c3c3c;color:#e0e0e0}.msg-action-btn.retry-btn:hover{color:#4fc1ff}.msg-action-btn.edit-btn:hover{color:#f48771}
.inline-edit-area{margin:4px 0}.inline-edit-area textarea{width:100%;min-height:80px;background:#1e1e1e;color:#d4d4d4;border:1px solid #4fc1ff;border-radius:6px;padding:8px 12px;font-size:13px;font-family:inherit;resize:vertical}
.edit-actions{display:flex;gap:8px;margin-top:6px}.inline-edit-btn-save{background:#0e639c;color:#fff;border:none;padding:6px 16px;border-radius:4px;cursor:pointer;font-size:12px}
.inline-edit-btn-save:hover{background:#1177bb}.inline-edit-btn-cancel{background:#3c3c3c;color:#ccc;border:1px solid #555;padding:6px 16px;border-radius:4px;cursor:pointer;font-size:12px}
@keyframes blink{0%,100%{opacity:1}50%{opacity:0}}.streaming-cursor{display:inline-block;width:1px;height:14px;background:#4fc1ff;margin-left:2px;animation:blink 1s infinite;vertical-align:text-bottom}
.branch-nav{display:flex;align-items:center;gap:6px;margin-top:6px;font-size:11px;color:#888}
.branch-nav-btn{background:transparent;border:1px solid #555;color:#aaa;cursor:pointer;font-size:11px;padding:2px 8px;border-radius:3px}
.branch-nav-btn:hover:not(:disabled){background:#3c3c3c;color:#e0e0e0}.branch-nav-btn:disabled{opacity:.3;cursor:default}.branch-nav-label{color:#aaa;min-width:40px;text-align:center}
::-webkit-scrollbar{width:8px;height:8px}::-webkit-scrollbar-track{background:#1e1e1e}::-webkit-scrollbar-thumb{background:#555;border-radius:4px}::-webkit-scrollbar-thumb:hover{background:#777}";

        #endregion
    }
}
