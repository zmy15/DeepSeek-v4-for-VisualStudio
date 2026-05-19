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
            string copyLabel = EscapeJsString(L["chat.html.codeCopyButton"]);
            string copyTitle = EscapeJsString(L["chat.html.codeCopyButton"]);
            string copyDone = EscapeJsString(L["chat.html.codeCopyDone"]);
            string writeLabel = EscapeJsString(L["chat.html.codeInsertButton"]);
            string writeTitle = EscapeJsString(L["chat.html.codeInsertButton"]);
            string writeDone = EscapeJsString(L["chat.html.codeCopyDone"]);

            return $@"
window.decorateCodeBlocks=function(container){{
    if(!container)return;
    var pres=container.querySelectorAll('pre:not(.mermaid-block)');
    pres.forEach(function(pre){{
        if(pre.querySelector('.copy-btn'))return;
        var code=pre.querySelector('code');
        if(!code)return;
        var lang='';
        if(code.className){{
            var m=code.className.match(/language-(\w+)/);
            if(m)lang=m[1];
        }}
        if(lang){{
            var label=document.createElement('span');
            label.className='code-lang';
            label.textContent=lang;
            pre.insertBefore(label,pre.firstChild);
        }}
        // highlight.js syntax highlighting
        if(window.hljs){{
            try{{window.hljs.highlightElement(code);}}catch(e){{}}
        }}
        // Copy button
        var copyBtn=document.createElement('button');
        copyBtn.className='copy-btn';
        copyBtn.textContent='{copyLabel}';
        copyBtn.title='{copyTitle}';
        copyBtn.onclick=function(){{
            var target=pre.querySelector('code')||pre;
            var text=target.innerText,ok=false;
            if(navigator.clipboard&&navigator.clipboard.writeText){{
                navigator.clipboard.writeText(text);ok=true;
            }}else{{
                var ta=document.createElement('textarea');
                ta.value=text;ta.style.cssText='position:fixed;opacity:0';
                document.body.appendChild(ta);ta.select();
                try{{document.execCommand('copy');ok=true;}}catch(e){{}}
                document.body.removeChild(ta);
            }}
            if(ok){{copyBtn.textContent='{copyDone}';copyBtn.classList.add('copied');}}
            setTimeout(function(){{copyBtn.textContent='{copyLabel}';copyBtn.classList.remove('copied');}},1500);
        }};
        pre.appendChild(copyBtn);
        // Insert/Write button
        var applyBtn=document.createElement('button');
        applyBtn.className='copy-btn';
        applyBtn.textContent='{writeLabel}';
        applyBtn.title='{writeTitle}';
        applyBtn.style.right='60px';
        applyBtn.onclick=function(){{
            var target=pre.querySelector('code')||pre;
            var codeText=target.innerText;
            try{{
                window.chrome.webview.postMessage(JSON.stringify({{type:'applyCode',code:codeText}}));
            }}catch(e1){{
                try{{
                    window.external.notify(JSON.stringify({{type:'applyCode',code:codeText}}));
                }}catch(e2){{}}
            }}
            applyBtn.textContent='{writeDone}';
            applyBtn.classList.add('copied');
            setTimeout(function(){{applyBtn.textContent='{writeLabel}';applyBtn.classList.remove('copied');}},1500);
        }};
        pre.appendChild(applyBtn);
    }});
}};";
";
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
window.__executeHandoff=function(targetAgent,label){
    window.__sendToHost({type:'executeHandoff',targetAgent:targetAgent,label:label});
};";
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
