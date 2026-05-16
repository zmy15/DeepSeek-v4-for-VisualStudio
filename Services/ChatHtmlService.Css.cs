using System;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// CSS 常量与 CDN 资源引用 —— ChatHtmlService 的分部类。
    /// </summary>
    public static partial class ChatHtmlService
    {
        #region CSS & CDN Constants

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
pre{background-color:#252526;border-radius:6px;padding:28px 12px 10px 12px;margin:8px 0;overflow-x:auto;overflow-y:auto;max-height:500px;font-size:0.9em;line-height:1.5;position:relative}
pre code{background:transparent;color:#D4D4D4;padding:0;font-size:inherit;white-space:pre;display:block}
ul,ol{padding-left:24px;margin:6px 0}
li{margin:2px 0}
blockquote{border-left:3px solid #6CAFD9;padding:6px 12px;margin:8px 0;background-color:#252526;color:#A0A0A0}
table{border-collapse:collapse;margin:8px 0}
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
/* ── 分支导航（树状分叉，替代旧版本导航）── */
.branch-nav{display:flex;align-items:center;gap:6px;margin-top:6px;font-size:11px;color:#888;user-select:none}
.branch-nav-btn{background:transparent;border:1px solid #555;color:#AAA;cursor:pointer;font-size:11px;padding:2px 8px;border-radius:3px;transition:all .15s}
.branch-nav-btn:hover:not(:disabled){background:#3C3C3C;color:#D4D4D4;border-color:#888}
.branch-nav-btn:disabled{opacity:.3;cursor:default}
.branch-nav-label{color:#AAA;min-width:40px;text-align:center;font-weight:500}

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

/* ── 文件删除确认卡片 ── */
.file-delete-card{margin:8px 0;border:1px solid #D16969;border-radius:8px;background:#2E1A1A;overflow:hidden;animation:fadeIn .3s}
.file-delete-card-header{padding:8px 12px;background:#3E1A1A;border-bottom:1px solid #5A2A2A;display:flex;align-items:center;gap:8px}
.file-delete-card-header .icon{font-size:16px}
.file-delete-card-header .title{color:#E07878;font-size:13px;font-weight:700}
.file-delete-card-body{padding:10px 12px}
.file-delete-card-body .warning-text{color:#D4A0A0;font-size:12px;margin-bottom:8px;line-height:1.5}
.file-delete-card-body .file-list{max-height:200px;overflow-y:auto;margin-bottom:10px}
.file-delete-card-body .file-item{display:flex;align-items:center;gap:6px;padding:3px 0;color:#D4A0A0;font-size:11px;font-family:'Cascadia Code',Consolas,monospace}
.file-delete-card-body .file-item .file-icon{color:#E07878;flex-shrink:0}
.file-delete-card-body .file-item .file-path{word-break:break-all}
.file-delete-card-footer{display:flex;gap:8px;padding:8px 12px;border-top:1px solid #3A1A1A}
.file-delete-card-footer button{padding:5px 18px;border-radius:4px;cursor:pointer;font-size:12px;font-weight:600;transition:all .15s;border:none}
.file-delete-btn-confirm{background:#8B2020;color:#FFC0C0;border:1px solid #C04040 !important}
.file-delete-btn-confirm:hover{background:#A03030;color:#FFE0E0}
.file-delete-btn-cancel{background:#3C3C3C;color:#CCC;border:1px solid #555 !important}
.file-delete-btn-cancel:hover{background:#4A4A4A;color:#FFF}

/* ── Agent 规划过程流式日志面板 ── */
.agent-log-panel{margin:4px 0 8px 0;border:1px solid #2A3A5A;border-radius:6px;background:#121A24;overflow:hidden;animation:fadeIn .3s}
.agent-log-panel-header{padding:4px 10px;background:#1A2636;border-bottom:1px solid #2A3A5A;display:flex;align-items:center;gap:6px;cursor:pointer}
.agent-log-panel-header .log-icon{font-size:13px}
.agent-log-panel-header .log-title{color:#7EB8E0;font-size:11px;font-weight:600}
.agent-log-panel-header .log-count{color:#5A7A9A;font-size:10px}
.agent-log-panel-body{padding:4px 0;max-height:300px;overflow-y:auto;font-size:11px;font-family:'Cascadia Code',Consolas,monospace;line-height:1.5}
.agent-log-panel-body .log-line{padding:1px 10px;color:#6A8AAA;white-space:pre-wrap;word-break:break-word;animation:logSlideIn .2s ease-out}
.agent-log-panel-body .log-line.warn{color:#C8A84E}
.agent-log-panel-body .log-line.error{color:#E07878}
.agent-log-panel-body .log-line.info{color:#6A8AAA}
.agent-log-panel-body .log-line.success{color:#4EC9B0}
@keyframes logSlideIn{from{opacity:0;transform:translateX(-6px)}to{opacity:1;transform:translateX(0)}}

/* ── 实时文件变更通知条 ── */
.agent-file-notify{display:flex;align-items:center;gap:6px;padding:3px 10px;margin:2px 0;border-radius:4px;font-size:11px;font-family:'Cascadia Code',Consolas,monospace;animation:logSlideIn .25s ease-out}
.agent-file-notify.modify{background:#1A2E1A;color:#4EC9B0;border-left:2px solid #4EC9B0}
.agent-file-notify.create{background:#1A2E2E;color:#6CAFD9;border-left:2px solid #6CAFD9}
.agent-file-notify.delete{background:#2E1A1A;color:#E07878;border-left:2px solid #E07878}

/* ── Agent 任务流程底部面板（替代独立计划消息，固定在聊天底部）── */
.agent-task-panel{margin:8px 0 4px 0;border:1px solid #3A5A8A;border-radius:8px;background:linear-gradient(180deg,#1A2436 0%,#1A1E2E 100%);overflow:hidden;animation:taskSlideUp .35s ease-out}
.agent-task-panel.collapsed .agent-task-panel-body{display:none}
.agent-task-panel-header{display:flex;align-items:center;gap:8px;padding:8px 12px;background:#1A2636;border-bottom:1px solid #2A3A5A;cursor:pointer;user-select:none;flex-wrap:wrap}
.agent-task-panel-header .task-icon{font-size:14px}
.agent-task-panel-header .task-title{color:#7EB8E0;font-size:12px;font-weight:600;flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.agent-task-panel-header .task-progress{color:#888;font-size:11px;white-space:nowrap}
.agent-task-panel-header .task-close{background:transparent;border:1px solid #555;color:#888;cursor:pointer;font-size:14px;width:22px;height:22px;border-radius:3px;display:flex;align-items:center;justify-content:center;transition:all .15s;padding:0;line-height:1;flex-shrink:0}
.agent-task-panel-header .task-close:hover{background:#3C1A1A;color:#E07878;border-color:#6A3A3A}
.agent-task-panel-body{padding:10px 12px;max-height:320px;overflow-y:auto;font-size:12px;line-height:1.5}
.agent-task-panel-body .task-empty{color:#666;font-size:11px;font-style:italic;padding:8px 0}
/* 面板内复用 agent-plan 的步骤样式，但不显示 plan 的 margin/border */
.agent-task-panel-body .agent-plan{border:none;background:transparent;padding:0;margin:0}
.agent-task-panel-body .agent-plan::before{display:none}
@keyframes taskSlideUp{from{opacity:0;transform:translateY(12px)}to{opacity:1;transform:translateY(0)}}

/* ── 内联编辑区域 ── */
.inline-edit-area{margin:4px 0;padding:8px;background:#1A1A2E;border:1px solid #3A3A6A;border-radius:6px;animation:fadeIn .2s}
.inline-edit-area textarea{width:100%;min-height:60px;background:#12121E;color:#D4D4D4;border:1px solid #444;border-radius:4px;padding:8px;font-family:'Cascadia Code',Consolas,monospace;font-size:12px;line-height:1.5;resize:vertical}
.inline-edit-area .edit-actions{display:flex;gap:6px;margin-top:6px}
.inline-edit-area .edit-actions button{padding:4px 14px;border-radius:4px;cursor:pointer;font-size:12px;border:none}
.inline-edit-btn-save{background:#1A3A1A;color:#4EC9B0;border:1px solid #3A6A3A}
.inline-edit-btn-save:hover{background:#2A5A2A;color:#7EFFC0}
.inline-edit-btn-cancel{background:#3C3C3C;color:#CCC;border:1px solid #555}
.inline-edit-btn-cancel:hover{background:#4A4A4A;color:#FFF}
/* ── Cache 命中率统计卡片 ── */
.cache-stat-card{display:flex;align-items:center;gap:10px;margin-top:10px;padding:8px 12px;border-radius:6px;font-size:11px;font-family:'Segoe UI',system-ui,sans-serif;flex-wrap:wrap;border:1px solid #333;background:#252526}
.cache-stat-card .cache-icon{font-size:15px;flex-shrink:0}
.cache-stat-card .cache-main{display:flex;align-items:center;gap:8px;flex-wrap:wrap;flex:1;min-width:0}
.cache-stat-card .cache-rate{font-size:15px;font-weight:700;flex-shrink:0}
.cache-stat-card .cache-rate.high{color:#4EC9B0}
.cache-stat-card .cache-rate.medium{color:#C8A84E}
.cache-stat-card .cache-rate.low{color:#E07878}
.cache-stat-card .cache-detail{color:#888;line-height:1.5}
.cache-stat-card .cache-detail span{color:#AAA;font-weight:600}
.cache-stat-card .cache-bar-wrap{width:80px;height:4px;background:#3C3C3C;border-radius:2px;overflow:hidden;flex-shrink:0}
.cache-stat-card .cache-bar-fill{height:100%;border-radius:2px;transition:width .5s ease}
.cache-stat-card .cache-bar-fill.high{background:linear-gradient(90deg,#4EC9B0,#3A8A7A)}
.cache-stat-card .cache-bar-fill.medium{background:linear-gradient(90deg,#C8A84E,#8A7A3A)}
.cache-stat-card .cache-bar-fill.low{background:linear-gradient(90deg,#E07878,#8A3A3A)}
.cache-stat-card .cache-saved{color:#666;font-size:10px;white-space:nowrap}
";

private const string AiAvatarHtml = "<span class='avatar avatar-ai'>AI</span>";
        private const string UserAvatarHtml = "<span class='avatar avatar-user'>U</span>";

        #endregion
    }
}
