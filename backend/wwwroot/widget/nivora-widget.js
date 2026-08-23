"use strict";(()=>{var h=class extends Error{constructor(t,i){super(i);this.status=t;this.name="ApiError"}},w=class extends Error{constructor(e){super(e),this.name="NetworkError"}};async function R(n,e,t){let i;try{i=await fetch(n,{method:"POST",headers:{"Content-Type":"application/json",...t?{Authorization:`Bearer ${t}`}:{}},body:JSON.stringify(e)})}catch{throw new w("Could not reach the chat service.")}if(!i.ok)throw new h(i.status,`Request failed with status ${i.status}`);try{return await i.json()}catch{throw new h(i.status,"Received an unreadable response from the chat service.")}}async function C(n,e,t){let i=await R(`${n}/api/widget/init`,{publicKey:e,visitorSessionId:t??null});return{visitorSessionId:i.visitorSessionId,token:i.token}}async function M(n,e,t){let i=await R(`${n}/api/widget/messages`,{content:t},e);return{id:i.id,createdAt:i.createdAt,aiReply:i.aiReply}}var O="nivora_widget_";function L(n){return`${O}${n}`}function $(n){if(typeof n!="object"||n===null)return!1;let e=n,t=e.status===void 0||e.status==="pending"||e.status==="sent"||e.status==="failed";return typeof e.id=="string"&&(e.role==="customer"||e.role==="ai")&&typeof e.content=="string"&&typeof e.createdAt=="string"&&t}function A(n){let e;try{e=window.localStorage.getItem(L(n))}catch{return null}if(!e)return null;let t;try{t=JSON.parse(e)}catch{return null}if(typeof t!="object"||t===null)return null;let i=t;if(typeof i.visitorSessionId!="string"||typeof i.token!="string")return null;let p=Array.isArray(i.transcript)?i.transcript.filter($):[];return{visitorSessionId:i.visitorSessionId,token:i.token,transcript:p}}function W(n,e){try{window.localStorage.setItem(L(n),JSON.stringify(e))}catch{}}function B(n){return typeof n=="string"&&n.trim().length>0}function o(n,e,t){let i=document.createElement(n);return e&&(i.className=e),t!==void 0&&(i.textContent=t),i}var S=class{constructor(e,t){this.options=t;let i=o("div","root");this.launcher=o("button","launcher"),this.launcher.type="button",this.launcher.setAttribute("aria-label","Open chat"),this.launcher.textContent="\u{1F4AC}",this.launcher.addEventListener("click",()=>this.open()),this.panel=o("div","panel"),this.panel.hidden=!0;let p=o("div","header"),f=o("span","header-title",t.businessName??"Chat"),c=o("button","close");c.type="button",c.setAttribute("aria-label","Close chat"),c.textContent="\xD7",c.addEventListener("click",()=>this.close()),p.append(f,c),this.messagesEl=o("div","messages"),this.messagesEl.setAttribute("role","log"),this.typingEl=o("div","typing","\u2026"),this.typingEl.hidden=!0,this.errorTextEl=o("span"),this.retryButton=o("button","retry","Retry"),this.retryButton.type="button",this.retryButton.addEventListener("click",()=>this.options.onRetry()),this.errorEl=o("div","error"),this.errorEl.hidden=!0,this.errorEl.append(this.errorTextEl,this.retryButton);let x=o("div","composer");this.input=o("input","input"),this.input.type="text",this.input.placeholder="Type a message\u2026",this.input.addEventListener("keydown",y=>{y.key==="Enter"&&(y.preventDefault(),this.handleSend())}),this.sendButton=o("button","send","Send"),this.sendButton.type="button",this.sendButton.addEventListener("click",()=>this.handleSend()),x.append(this.input,this.sendButton),this.panel.append(p,this.messagesEl,this.typingEl,this.errorEl,x),i.append(this.launcher,this.panel),e.appendChild(i)}handleSend(){let e=this.input.value.trim();e.length!==0&&(this.input.value="",this.options.onSend(e))}open(){this.panel.hidden=!1}close(){this.panel.hidden=!0}get isOpen(){return!this.panel.hidden}renderMessages(e){this.messagesEl.replaceChildren();for(let t of e)this.messagesEl.appendChild(this.renderMessageNode(t));this.scrollToBottom()}appendMessage(e){this.messagesEl.appendChild(this.renderMessageNode(e)),this.scrollToBottom()}updateMessage(e){let t=Array.from(this.messagesEl.children).find(i=>i instanceof HTMLDivElement&&i.dataset.messageId===e.id);t?this.applyMessageState(t,e):this.appendMessage(e)}renderMessageNode(e){let t=o("div");return t.dataset.messageId=e.id,this.applyMessageState(t,e),t}applyMessageState(e,t){let i=t.status&&t.status!=="sent"?` message-${t.status}`:"";e.className=`message message-${t.role}${i}`,e.textContent=t.content}scrollToBottom(){this.messagesEl.scrollTop=this.messagesEl.scrollHeight}setSending(e){this.input.disabled=e,this.sendButton.disabled=e,this.typingEl.hidden=!e}showError(e,t={}){this.errorTextEl.textContent=e,this.retryButton.hidden=!t.retryable,this.errorEl.hidden=!1}clearError(){this.errorEl.hidden=!0}};var H=`/* Everything here lives inside the widget's own Shadow DOM \u2014 none of it
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
`;function K(){return document.currentScript instanceof HTMLScriptElement?document.currentScript:Array.from(document.querySelectorAll("script[src]")).find(e=>e.src.includes("nivora-widget.js"))??null}function j(n){let e=new URL(n,window.location.href);return`${e.protocol}//${e.host}`}function P(n){if(n instanceof w)return"Couldn't reach the chat service. Check your connection and try again.";if(n instanceof h)switch(n.status){case 400:return"That message couldn't be sent. Please try again.";case 401:return"Your session expired and couldn't be renewed. Tap Retry to send your message again.";case 403:return"This chat isn't available on this site.";case 429:return"You're sending messages too quickly. Please wait a moment.";default:return n.status>=500?"The chat service is temporarily unavailable. Please try again shortly.":"Something went wrong. Please try again."}return"Something went wrong. Please try again."}async function U(){let n=K();if(!n){console.error("[Nivora Widget] could not locate its own <script> element; aborting.");return}let e=n.getAttribute("data-public-key");if(!B(e)){console.error("[Nivora Widget] missing required data-public-key attribute; aborting.");return}let t=e,i=n.getAttribute("data-business-name")??void 0,p=j(n.src),f=document.createElement("div");f.id="nivora-widget-host",f.style.cssText="all: initial; position: fixed; inset: 0; pointer-events: none; z-index: 2147483647;",document.body.appendChild(f);let c=f.attachShadow({mode:"open"}),x=document.createElement("style");x.textContent=H,c.appendChild(x);let y=document.createElement("div");y.style.pointerEvents="auto",c.appendChild(y);let k=A(t),l=k?.transcript??[],v=k?.visitorSessionId,u=null,b=!1,g=null;function m(){W(t,{visitorSessionId:v??"",token:u??"",transcript:l})}let T=!1;for(let s of l)s.role==="customer"&&(s.status==="pending"?(s.status="failed",g=s.id,T=!0):s.status==="failed"&&(g=s.id));T&&m();let a=new S(y,{businessName:i,onSend:s=>void N(s),onRetry:()=>{b||(g?z(g):E())}});l.length>0&&a.renderMessages(l),g&&a.showError("Your previous message wasn't sent. Tap Retry to send it again.",{retryable:!0});async function E(){try{let s=await C(p,t,v);u=s.token,v&&s.visitorSessionId!==v&&(l.length=0,a.renderMessages(l)),v=s.visitorSessionId,m(),g||a.clearError()}catch(s){u=null,a.showError(P(s),{retryable:!0})}}async function I(s){b=!0,a.setSending(!0),a.clearError(),s.status="pending",a.updateMessage(s),m();try{if(u||await E(),!u)throw new Error("widget is not initialized");let r;try{r=await M(p,u,s.content)}catch(d){if(d instanceof h&&d.status===401){if(await E(),!u)throw d;r=await M(p,u,s.content)}else throw d}if(s.status="sent",a.updateMessage(s),g=null,r.aiReply){let d={id:r.id,role:"ai",content:r.aiReply,createdAt:r.createdAt};l.push(d),a.appendMessage(d)}m()}catch(r){s.status="failed",a.updateMessage(s),m(),g=s.id,a.showError(P(r),{retryable:!0})}finally{b=!1,a.setSending(!1)}}async function N(s){if(b)return;let r={id:`local-${Date.now()}-${Math.random().toString(36).slice(2)}`,role:"customer",content:s,createdAt:new Date().toISOString(),status:"pending"};l.push(r),a.appendMessage(r),m(),await I(r)}async function z(s){if(b)return;let r=l.find(d=>d.id===s&&d.role==="customer");r&&await I(r)}await E()}U().catch(n=>{console.error("[Nivora Widget] failed to initialize",n)});})();
