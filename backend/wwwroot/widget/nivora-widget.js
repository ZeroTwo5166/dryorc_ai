"use strict";(()=>{var p=class extends Error{constructor(t,s){super(s);this.status=t;this.name="ApiError"}},M=class extends Error{constructor(e){super(e),this.name="NetworkError"}};async function N(n,e,t){let s;try{s=await fetch(n,{method:"POST",headers:{"Content-Type":"application/json",...t?{Authorization:`Bearer ${t}`}:{}},body:JSON.stringify(e)})}catch{throw new M("Could not reach the chat service.")}if(!s.ok)throw new p(s.status,`Request failed with status ${s.status}`);try{return await s.json()}catch{throw new p(s.status,"Received an unreadable response from the chat service.")}}async function X(n,e){let t;try{t=await fetch(n,{method:"GET",headers:{Authorization:`Bearer ${e}`}})}catch{throw new M("Could not reach the chat service.")}if(!t.ok)throw new p(t.status,`Request failed with status ${t.status}`);try{return await t.json()}catch{throw new p(t.status,"Received an unreadable response from the chat service.")}}async function O(n,e,t){let s=await N(`${n}/api/widget/init`,{publicKey:e,visitorSessionId:t??null});return{visitorSessionId:s.visitorSessionId,token:s.token}}async function L(n,e,t){let s=await N(`${n}/api/widget/messages`,{content:t},e);return{id:s.id,createdAt:s.createdAt,aiReply:s.aiReply,aiReplyId:s.aiReplyId,aiReplyCreatedAt:s.aiReplyCreatedAt}}async function $(n,e,t){let s=t?`?afterCreatedAt=${encodeURIComponent(t.createdAt)}&afterId=${encodeURIComponent(t.id)}`:"",c=await X(`${n}/api/widget/messages${s}`,e);return{messages:c.messages,nextCursor:c.nextCursor}}var Q="nivora_widget_";function z(n){return`${Q}${n}`}function Z(n){if(typeof n!="object"||n===null)return!1;let e=n,t=e.status===void 0||e.status==="pending"||e.status==="sent"||e.status==="failed";return typeof e.id=="string"&&(e.role==="customer"||e.role==="ai"||e.role==="agent")&&typeof e.content=="string"&&typeof e.createdAt=="string"&&t}function ee(n){if(typeof n!="object"||n===null)return!1;let e=n;return typeof e.createdAt=="string"&&typeof e.id=="string"}function D(n){let e;try{e=window.localStorage.getItem(z(n))}catch{return null}if(!e)return null;let t;try{t=JSON.parse(e)}catch{return null}if(typeof t!="object"||t===null)return null;let s=t;if(typeof s.visitorSessionId!="string"||typeof s.token!="string")return null;let c=Array.isArray(s.transcript)?s.transcript.filter(Z):[],g=ee(s.lastPolledCursor)?s.lastPolledCursor:void 0;return{visitorSessionId:s.visitorSessionId,token:s.token,transcript:c,lastPolledCursor:g}}function j(n,e){try{window.localStorage.setItem(z(n),JSON.stringify(e))}catch{}}function U(n){return typeof n=="string"&&n.trim().length>0}function l(n,e,t){let s=document.createElement(n);return e&&(s.className=e),t!==void 0&&(s.textContent=t),s}var k=class{constructor(e,t){this.options=t;let s=l("div","root");this.launcher=l("button","launcher"),this.launcher.type="button",this.launcher.setAttribute("aria-label","Open chat"),this.launcher.textContent="\u{1F4AC}",this.launcher.addEventListener("click",()=>this.open()),this.panel=l("div","panel"),this.panel.hidden=!0;let c=l("div","header"),g=l("span","header-title",t.businessName??"Chat"),f=l("button","close");f.type="button",f.setAttribute("aria-label","Close chat"),f.textContent="\xD7",f.addEventListener("click",()=>this.close()),c.append(g,f),this.messagesEl=l("div","messages"),this.messagesEl.setAttribute("role","log"),this.typingEl=l("div","typing","\u2026"),this.typingEl.hidden=!0,this.errorTextEl=l("span"),this.retryButton=l("button","retry","Retry"),this.retryButton.type="button",this.retryButton.addEventListener("click",()=>this.options.onRetry()),this.errorEl=l("div","error"),this.errorEl.hidden=!0,this.errorEl.append(this.errorTextEl,this.retryButton);let E=l("div","composer");this.input=l("input","input"),this.input.type="text",this.input.placeholder="Type a message\u2026",this.input.addEventListener("keydown",m=>{m.key==="Enter"&&(m.preventDefault(),this.handleSend())}),this.sendButton=l("button","send","Send"),this.sendButton.type="button",this.sendButton.addEventListener("click",()=>this.handleSend()),E.append(this.input,this.sendButton),this.panel.append(c,this.messagesEl,this.typingEl,this.errorEl,E),s.append(this.launcher,this.panel),e.appendChild(s)}handleSend(){let e=this.input.value.trim();e.length!==0&&(this.input.value="",this.options.onSend(e))}open(){this.panel.hidden=!1,this.options.onOpen?.()}close(){this.panel.hidden=!0,this.options.onClose?.()}get isOpen(){return!this.panel.hidden}renderMessages(e){this.messagesEl.replaceChildren();for(let t of e)this.messagesEl.appendChild(this.renderMessageNode(t));this.scrollToBottom()}appendMessage(e){this.messagesEl.appendChild(this.renderMessageNode(e)),this.scrollToBottom()}updateMessage(e){let t=Array.from(this.messagesEl.children).find(s=>s instanceof HTMLDivElement&&s.dataset.messageId===e.id);t?this.applyMessageState(t,e):this.appendMessage(e)}renderMessageNode(e){let t=l("div");return t.dataset.messageId=e.id,this.applyMessageState(t,e),t}applyMessageState(e,t){let s=t.status&&t.status!=="sent"?` message-${t.status}`:"";e.className=`message message-${t.role}${s}`,e.textContent=t.content}scrollToBottom(){this.messagesEl.scrollTop=this.messagesEl.scrollHeight}setSending(e){this.input.disabled=e,this.sendButton.disabled=e,this.typingEl.hidden=!e}showError(e,t={}){this.errorTextEl.textContent=e,this.retryButton.hidden=!t.retryable,this.errorEl.hidden=!1}clearError(){this.errorEl.hidden=!0}};var K=`/* Everything here lives inside the widget's own Shadow DOM \u2014 none of it
   can leak onto the host page, and none of the host page's CSS (resets,
   frameworks, whatever) can leak in. No assumptions about the host page's
   own styling are made or needed. */

:host,
* {
  box-sizing: border-box;
}

.root {
  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
  font-size: 14px;
  line-height: 1.4;
  color: #1a1a1a;
}

.launcher {
  position: fixed;
  right: 20px;
  bottom: 20px;
  width: 56px;
  height: 56px;
  border-radius: 50%;
  border: none;
  background: #1a1a2e;
  color: #fff;
  font-size: 24px;
  cursor: pointer;
  box-shadow: 0 4px 16px rgba(0, 0, 0, 0.25);
}

.launcher:hover {
  filter: brightness(1.1);
}

.panel {
  position: fixed;
  right: 20px;
  bottom: 88px;
  width: min(360px, calc(100vw - 32px));
  height: min(520px, calc(100vh - 140px));
  background: #fff;
  border-radius: 12px;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.25);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.panel[hidden],
.error[hidden],
.typing[hidden] {
  display: none;
}

.header {
  flex: 0 0 auto;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px;
  background: #1a1a2e;
  color: #fff;
}

.header-title {
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.close {
  border: none;
  background: transparent;
  color: #fff;
  font-size: 20px;
  line-height: 1;
  cursor: pointer;
  padding: 4px 6px;
}

.messages {
  flex: 1 1 auto;
  overflow-y: auto;
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.message {
  max-width: 80%;
  padding: 8px 12px;
  border-radius: 12px;
  white-space: pre-wrap;
  word-break: break-word;
}

.message-customer {
  align-self: flex-end;
  background: #1a1a2e;
  color: #fff;
  border-bottom-right-radius: 2px;
}

.message-ai {
  align-self: flex-start;
  background: #f0f0f3;
  color: #1a1a1a;
  border-bottom-left-radius: 2px;
}

/* Same side as AI (business's side of the conversation) but visually
   distinct \u2014 signals "a person replied" without naming who. No identity
   is ever rendered here, only the role. */
.message-agent {
  align-self: flex-start;
  background: #eaf2ff;
  color: #1a1a1a;
  border-bottom-left-radius: 2px;
  border-left: 3px solid #2f6fed;
}

.message-pending {
  opacity: 0.6;
}

.message-failed {
  background: #7a1f1f;
}

.typing {
  align-self: flex-start;
  font-size: 12px;
  color: #666;
  padding: 0 4px;
}

.error {
  flex: 0 0 auto;
  margin: 0 12px;
  padding: 8px 10px;
  background: #fdecea;
  color: #7a1f1f;
  border-radius: 8px;
  font-size: 13px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.retry {
  border: none;
  background: #7a1f1f;
  color: #fff;
  border-radius: 6px;
  padding: 4px 8px;
  cursor: pointer;
  font-size: 12px;
  flex: 0 0 auto;
}

.composer {
  flex: 0 0 auto;
  display: flex;
  gap: 8px;
  padding: 12px;
  border-top: 1px solid #e5e5e5;
}

.input {
  flex: 1 1 auto;
  min-width: 0;
  padding: 8px 10px;
  border: 1px solid #d0d0d5;
  border-radius: 8px;
  font-size: 14px;
  font-family: inherit;
}

.input:disabled {
  background: #f5f5f5;
}

.send {
  flex: 0 0 auto;
  border: none;
  background: #1a1a2e;
  color: #fff;
  border-radius: 8px;
  padding: 8px 14px;
  cursor: pointer;
  font-size: 14px;
}

.send:disabled,
.input:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

@media (max-width: 420px) {
  .panel {
    right: 16px;
    left: 16px;
    width: auto;
    bottom: 84px;
  }

  .launcher {
    right: 16px;
    bottom: 16px;
  }
}
`;var ne=5e3;function se(){return document.currentScript instanceof HTMLScriptElement?document.currentScript:Array.from(document.querySelectorAll("script[src]")).find(e=>e.src.includes("nivora-widget.js"))??null}function ie(n){let e=new URL(n,window.location.href);return`${e.protocol}//${e.host}`}function q(n){if(n instanceof M)return"Couldn't reach the chat service. Check your connection and try again.";if(n instanceof p)switch(n.status){case 400:return"That message couldn't be sent. Please try again.";case 401:return"Your session expired and couldn't be renewed. Tap Retry to send your message again.";case 403:return"This chat isn't available on this site.";case 429:return"You're sending messages too quickly. Please wait a moment.";default:return n.status>=500?"The chat service is temporarily unavailable. Please try again shortly.":"Something went wrong. Please try again."}return"Something went wrong. Please try again."}async function re(){let n=se();if(!n){console.error("[Nivora Widget] could not locate its own <script> element; aborting.");return}let e=n.getAttribute("data-public-key");if(!U(e)){console.error("[Nivora Widget] missing required data-public-key attribute; aborting.");return}let t=e,s=n.getAttribute("data-business-name")??void 0,c=ie(n.src),g=document.createElement("div");g.id="nivora-widget-host",g.style.cssText="all: initial; position: fixed; inset: 0; pointer-events: none; z-index: 2147483647;",document.body.appendChild(g);let f=g.attachShadow({mode:"open"}),E=document.createElement("style");E.textContent=K,f.appendChild(E);let m=document.createElement("div");m.style.pointerEvents="auto",f.appendChild(m);let A=D(t),d=A?.transcript??[],S=A?.visitorSessionId,u=null,C=!1,h=null;function J(){let i;for(let r of d)r.role!=="ai"&&r.role!=="agent"||(!i||r.createdAt>i.createdAt)&&(i=r);return i?{createdAt:i.createdAt,id:i.id}:void 0}let x=A?.lastPolledCursor??J();function y(){j(t,{visitorSessionId:S??"",token:u??"",transcript:d,lastPolledCursor:x})}let W=!1;for(let i of d)i.role==="customer"&&(i.status==="pending"?(i.status="failed",h=i.id,W=!0):i.status==="failed"&&(h=i.id));W&&y();let a=new k(m,{businessName:s,onSend:i=>void _(i),onRetry:()=>{C||(h?F(h):R())},onOpen:()=>Y(),onClose:()=>G()});d.length>0&&a.renderMessages(d),h&&a.showError("Your previous message wasn't sent. Tap Retry to send it again.",{retryable:!0});async function R(){try{let i=await O(c,t,S);u=i.token,S&&i.visitorSessionId!==S&&(d.length=0,a.renderMessages(d)),S=i.visitorSessionId,y(),h||a.clearError()}catch(i){u=null,a.showError(q(i),{retryable:!0})}}async function B(i){C=!0,a.setSending(!0),a.clearError(),i.status="pending",a.updateMessage(i),y();try{if(u||await R(),!u)throw new Error("widget is not initialized");let r;try{r=await L(c,u,i.content)}catch(o){if(o instanceof p&&o.status===401){if(await R(),!u)throw o;r=await L(c,u,i.content)}else throw o}if(i.status="sent",a.updateMessage(i),h=null,r.aiReply&&r.aiReplyId&&r.aiReplyCreatedAt){let o=r.aiReplyId,w=r.aiReplyCreatedAt;if(!d.some(b=>b.id===o)){let b={id:o,role:"ai",content:r.aiReply,createdAt:w};d.push(b),a.appendMessage(b)}(!x||w>x.createdAt)&&(x={createdAt:w,id:o})}y()}catch(r){i.status="failed",a.updateMessage(i),y(),h=i.id,a.showError(q(r),{retryable:!0})}finally{C=!1,a.setSending(!1)}}async function _(i){if(C)return;let r={id:`local-${Date.now()}-${Math.random().toString(36).slice(2)}`,role:"customer",content:i,createdAt:new Date().toISOString(),status:"pending"};d.push(r),a.appendMessage(r),y(),await B(r)}async function F(i){if(C)return;let r=d.find(o=>o.id===i&&o.role==="customer");r&&await B(r)}let P=!1,v=!1,T=null;function H(){T!==null&&(clearTimeout(T),T=null)}async function V(){if(!(P||!u)){P=!0;try{let i=await $(c,u,x),r=!1;for(let o of i.messages){if(d.some(b=>b.id===o.id))continue;let w={id:o.id,role:o.role==="Agent"?"agent":"ai",content:o.content,createdAt:o.createdAt};d.push(w),a.appendMessage(w),r=!0}i.nextCursor&&(x=i.nextCursor),(r||i.nextCursor)&&y()}catch{}finally{P=!1}}}async function I(){T=null,v&&document.visibilityState==="visible"&&(await V(),v&&(T=setTimeout(()=>void I(),ne)))}function Y(){v||(v=!0,I())}function G(){v=!1,H()}document.addEventListener("visibilitychange",()=>{v&&document.visibilityState==="visible"&&(H(),I())}),await R()}re().catch(n=>{console.error("[Nivora Widget] failed to initialize",n)});})();
