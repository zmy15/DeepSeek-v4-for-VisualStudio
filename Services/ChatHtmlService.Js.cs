using System;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// JavaScript 生成方法 —— ChatHtmlService 的分部类。
    /// </summary>
    public static partial class ChatHtmlService
    {
        #region JavaScript Builders

        /// <summary>
        /// 声明 decorateCodeBlocks 函数（语言标签 + highlight.js 语法高亮 + 复制/应用按钮）。
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
        if(window.hljs){
            try{window.hljs.highlightElement(code);}catch(e){}
        }
        // Copy button
        var copyBtn=document.createElement('button');
        copyBtn.className='copy-btn';
        copyBtn.textContent='📋 Copy';
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
            if(ok){copyBtn.textContent='✓ Copied';copyBtn.style.background='#1a3a1a';copyBtn.style.color='#6cd96c';}
            setTimeout(function(){copyBtn.textContent='📋 Copy';copyBtn.style.background='';copyBtn.style.color='';},2000);
        };
        pre.appendChild(copyBtn);
    });
};";
        }

        private static string BuildDecorateAllCodeBlocksInvocation()
        {
            return "window.decorateCodeBlocks(document.getElementById('chat-container'));";
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
        /// 智能自动滚动 JS.
        /// 用户滚动离开底部 → 暂停自动滚动；
        /// 用户滚回底部附近 → 恢复自动滚动。
        /// 检测阈值: 距底部 80px 以内视为"在底部"。
        /// </summary>
        private static string BuildAutoScrollJs()
        {
            return @"
(function(){
var _autoScroll=true;
window._autoScroll=true;   // 暴露为全局，供其他 JS 模块读取
var SCROLL_THRESHOLD=80;
var timer=null;

// 定义全局智能滚动函数（所有 scrollToBottom 通过此函数）
window.__scrollToBottom=function(behavior){
    if(window._autoScroll===false)return;
    window.scrollTo({top:document.body.scrollHeight,behavior:behavior||'smooth'});
};

// 检测用户手动滚动
window.addEventListener('scroll',function(){
    var atBottom=(window.innerHeight+window.scrollY+SCROLL_THRESHOLD)>=document.body.scrollHeight;
    if(!atBottom&&_autoScroll){ _autoScroll=false; window._autoScroll=false; }
    else if(atBottom&&!_autoScroll){ _autoScroll=true; window._autoScroll=true; }
},{passive:true});

// 内容变更时若在底部则自动滚动（范围缩小到 #chat-container，防抖 120ms）
var chatContainer=document.getElementById('chat-container')||document.body;
new MutationObserver(function(){
    if(!_autoScroll)return;
    if(timer)clearTimeout(timer);
    timer=setTimeout(function(){ window.__scrollToBottom('smooth'); },120);
}).observe(chatContainer,{childList:true,subtree:true});
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
    // 使用 DocumentFragment 批量追加，减少回流次数
    var temp=document.createElement('div');
    temp.innerHTML=html;
    var fragment=document.createDocumentFragment();
    while(temp.firstChild){
        fragment.appendChild(temp.firstChild);
    }
    container.appendChild(fragment);
    // 流式追加期间跳过语法高亮（节省性能），由 BuildFinalRenderJs 在完成时统一处理
    if (typeof window.__scrollToBottom === 'function') {
        window.__scrollToBottom('smooth');
    }
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
window.__editMessageConfirm=function(msgIndex,newText){
    window.__sendToHost({type:'editMessageConfirm',messageIndex:msgIndex,text:newText});
};
window.__editMessageCancel=function(msgIndex){
    window.__sendToHost({type:'editMessageCancel',messageIndex:msgIndex});
};
window.__navigateVersion=function(msgIndex,direction){
    window.__sendToHost({type:'navigateVersion',messageIndex:msgIndex,direction:direction});
};
window.__navigateBranch=function(nodeId,direction){
    window.__sendToHost({type:'navigateBranch',nodeId:nodeId,direction:direction});
};
window.__agentApprove=function(requestId){
    window.__sendToHost({type:'agentApprove',requestId:requestId,approved:true});
};
window.__agentDeny=function(requestId){
    window.__sendToHost({type:'agentApprove',requestId:requestId,approved:false});
};
window.__fileDeleteConfirm=function(requestId){
    window.__sendToHost({type:'fileDeleteConfirm',requestId:requestId,confirmed:true});
};
window.__fileDeleteCancel=function(requestId){
    window.__sendToHost({type:'fileDeleteConfirm',requestId:requestId,confirmed:false});
};
window.__terminalApprove=function(requestId){
    window.__sendToHost({type:'terminalApprove',requestId:requestId});
};
window.__terminalSkip=function(requestId){
    window.__sendToHost({type:'terminalSkip',requestId:requestId});
};
window.__executeHandoff=function(targetAgent,label){
    window.__sendToHost({type:'executeHandoff',targetAgent:targetAgent,label:label});
};

// ═══════════════════════════════════════════════
//  流式更新批处理引擎（性能优化核心）
//  C# 通过 PostWebMessageAsString 推送 JSON，
//  JS 通过 requestAnimationFrame 批量应用 DOM 更新，
//  避免每次 ExecuteScriptAsync 阻塞 UI 线程。
// ═══════════════════════════════════════════════
(function(){
    var _streamBuf={};          // 消息索引 → 累积内容
    var _rafPending=false;      // 是否已有待处理的 rAF
    var _rafTimer=null;         // 兜底定时器（防止 rAF 不触发）

    // 监听来自 C# 的流式更新消息
    if(window.chrome&&window.chrome.webview){
        window.chrome.webview.addEventListener('message',function(e){
            try{
                var msg=typeof e.data==='string'?JSON.parse(e.data):e.data;
                if(msg.type==='stream'){
                    _streamBuf[msg.i]=msg;
                    if(!_rafPending){
                        _rafPending=true;
                        requestAnimationFrame(_flushStreamBuf);
                        // 兜底：250ms 后强制执行（防止 rAF 在某些场景不触发）
                        if(_rafTimer)clearTimeout(_rafTimer);
                        _rafTimer=setTimeout(function(){
                            if(_rafPending)_flushStreamBuf(0);
                        },250);
                    }
                }else if(msg.type==='streamEnd'){
                    // ★ 先取消待处理的 rAF 和定时器，防止 _flushStreamBuf 覆盖已渲染的 Markdown
                    if(_rafTimer){clearTimeout(_rafTimer);_rafTimer=null;}
                    _rafPending=false;
                    delete _streamBuf[msg.i];  // 清除可能残留的流式缓冲，避免 textNode 覆盖 innerHTML

                    // ★ 渲染 Markdown（独立于 _flushStreamBuf，不受其异常影响）
                    if (msg.html) {
                        var container = document.getElementById('msg-body-' + msg.i);
                        if (container) {
                            // 清理流式阶段残留的 _textNode 引用
                            container._textNode = null;
                            container.innerHTML = msg.html;
                            container.style.whiteSpace = '';  // 流式结束，恢复默认 whitespace 处理
                            var cursor = document.getElementById('cursor-' + msg.i);
                            if (cursor) cursor.style.display = 'none';
                            // 注入尾部 HTML
                            if(msg.footerHtml){
                                var footerDiv=document.createElement('div');
                                footerDiv.innerHTML=msg.footerHtml;
                                container.appendChild(footerDiv);
                            }
                            // 推理面板
                            if(msg.reasoningHtml){
                                var rp=document.getElementById('reasoning-'+msg.i);
                                var rb=document.getElementById('reasoning-body-'+msg.i);
                                if(rp&&rb){
                                    rp.style.display='block';
                                    rb.innerHTML=msg.reasoningHtml;
                                }
                            }
                            // 注入重试按钮
                            var msgDiv=document.getElementById('msg-'+msg.i);
                            if(msgDiv&&!document.getElementById('retry-btn-'+msg.i)){
                                var retryBtn=document.createElement('button');
                                retryBtn.id='retry-btn-'+msg.i;
                                retryBtn.className='msg-action-btn retry-btn';
                                retryBtn.textContent=msg.retryLabel||'Retry';
                                retryBtn.title=msg.retryTitle||'Retry this message';
                                retryBtn.onclick=function(){window.__retryMessage(msg.i);};
                                var msgBody=document.getElementById('msg-body-'+msg.i);
                                if(msgBody)msgBody.parentNode.insertBefore(retryBtn,msgBody.nextSibling);
                            }
                            // 高亮代码块
                            if(msgDiv&&window.hljs)decorateCodeBlocks(msgDiv);
                        }
                    }
                    // ★ 清除状态栏文本
                    var st = document.getElementById('status-text');
                    if (st) st.textContent = '';
                }else if(msg.type==='streamStatus'){
                    // 仅更新状态栏（避免额外通信）
                    var st=document.getElementById('status-text');
                    if(st&&msg.text)st.textContent=msg.text;
                }
            }catch(e) {
                console.error('[DeepSeek] Message handler error:', e);
            }
        });
    }

    function _flushStreamBuf(ts){
        var keys=Object.keys(_streamBuf);
        if(keys.length===0){_rafPending=false;return;}

        // 批量处理所有待更新的消息
        for(var i=0;i<keys.length;i++){
            var msg=_streamBuf[keys[i]];
            if(!msg)continue;

            var container=document.getElementById('msg-body-'+msg.i);
            var reasoningPanel=document.getElementById('reasoning-'+msg.i);
            var reasoningBody=document.getElementById('reasoning-body-'+msg.i);
            var cursor=document.getElementById('cursor-'+msg.i);

            // 更新正文内容
            if(container&&msg.c!==undefined){
                var textNode=container._textNode;
                if(!textNode){
                    textNode=document.createTextNode('');
                    container._textNode=textNode;
                    container.textContent='';
                    container.appendChild(textNode);
                    container.style.whiteSpace='pre-line';  // 流式内容保留换行
                }
                textNode.textContent=msg.c;
            }

            // 更新推理面板
            if(reasoningPanel&&reasoningBody&&msg.r!==undefined){
                if(msg.r.length>0){
                    reasoningPanel.style.display='block';
                    reasoningBody.textContent=msg.r;
                }else{
                    reasoningPanel.style.display='none';
                }
            }

            // 更新光标
            if(cursor&&msg.f!==undefined){
                cursor.style.display=msg.f?'none':'inline-block';
            }

            // 更新状态栏
            if(msg.s){
                var st=document.getElementById('status-text');
                if(st)st.textContent=msg.s;
            }
        }

        _streamBuf={};
        _rafPending=false;
        if(_rafTimer){clearTimeout(_rafTimer);_rafTimer=null;}
        if (typeof window.__scrollToBottom === 'function') {
            window.__scrollToBottom('smooth');
        }
    }
})();";
        }

        /// <summary>
        /// 转义字符串用于嵌入 JS 字符串字面量。
        /// </summary>
        private static string EscapeJsString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return System.Text.Json.JsonSerializer.Serialize(s);
        }

        #endregion
    }
}
