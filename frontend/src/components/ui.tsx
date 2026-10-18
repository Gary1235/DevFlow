import {createContext,useContext,useEffect,useId,useRef,useState,type ReactNode} from 'react';
import {tone,statusNames,providerNames,effortNames,selectableModels} from '../domain';
import type {Provider,Selection,ProviderId} from '../types';
const NoticeContext=createContext<(message:string,error?:boolean)=>void>(()=>{});
export function NoticeProvider({children}:{children:ReactNode}){
  const [items,setItems]=useState<{id:number;message:string;error:boolean}[]>([]),next=useRef(0),timers=useRef<ReturnType<typeof setTimeout>[]>([]);
  useEffect(()=>()=>timers.current.forEach(clearTimeout),[]);
  function notice(message:string,error=false){const id=++next.current;setItems(v=>[...v,{id,message,error}]);if(!error)timers.current.push(setTimeout(()=>setItems(v=>v.filter(n=>n.id!==id)),6000));}
  return <NoticeContext.Provider value={notice}>{children}<div className="toast-stack" role="status" aria-live="polite">{items.map(n=><div key={n.id} className={'toast'+(n.error?' error':'')}><span>{n.message}</span><button aria-label="關閉通知" onClick={()=>setItems(v=>v.filter(i=>i.id!==n.id))}>✕</button></div>)}</div></NoticeContext.Provider>;
}
export const useNotice=()=>useContext(NoticeContext);
export function Badge({status,label}:{status:string;label?:string}){return <span className={'chip '+tone(status)}>{label||statusNames[status]||status}</span>;}
export function Meta({label,value}:{label:string;value?:string|null}){return <div className="meta-item"><span>{label}</span><strong>{value||'—'}</strong></div>;}
export function Empty({title,description,children}:{title:string;description?:string;children?:ReactNode}){return <div className="empty"><div className="empty-icon">◈</div><strong>{title}</strong><p>{description}</p>{children}</div>;}
export function Heading({eyebrow,title,description,children}:{eyebrow:string;title:string;description:string;children?:ReactNode}){return <div className="page-heading"><div><p className="eyebrow">{eyebrow}</p><h1>{title}</h1><p>{description}</p></div>{children}</div>;}
export function Modal({title,eyebrow,onClose,children,wide=false}:{title:string;eyebrow?:string;onClose:()=>void;children:ReactNode;wide?:boolean}){
  const ref=useRef<HTMLDialogElement>(null),id=useId();
  useEffect(()=>{const dialog=ref.current!;dialog.showModal();return()=>{if(dialog.open)dialog.close();};},[]);
  return <dialog ref={ref} className={wide?'wide-dialog':''} aria-labelledby={id} onCancel={e=>{e.preventDefault();onClose();}}><div className="dialog-heading"><div><p className="eyebrow">{eyebrow}</p><h2 id={id}>{title}</h2></div><button className="icon-button" aria-label="關閉視窗" onClick={onClose}>✕</button></div>{children}</dialog>;
}
export function AiChoice({providers,value,onChange,inherit=false,label}:{providers:Provider[];value:Selection|null;onChange:(s:Selection|null)=>void;inherit?:boolean;label:string}){
  const selectedProvider=providers.find(p=>p.id===value?.provider),models=selectableModels(selectedProvider),selectedModel=value?.model||'';
  const [customSelected,setCustomSelected]=useState(false),isCustom=customSelected||!!selectedModel&&!models.includes(selectedModel);
  return <fieldset><legend>{label}</legend><label>AI CLI<select aria-label={label+' AI'} value={value?.provider||''} onChange={e=>{setCustomSelected(false);onChange(e.target.value?{provider:e.target.value as ProviderId,model:null}:null);}}>{inherit&&<option value="">沿用專案預設</option>}{providers.map(p=><option key={p.id} value={p.id}>{providerNames[p.id]}{p.installed?'':'（未安裝）'}</option>)}</select></label><label>模型<select aria-label={label+'模型'} disabled={!value} value={isCustom?'__custom':selectedModel} onChange={e=>{if(!value)return;const custom=e.target.value==='__custom';setCustomSelected(custom);if(!custom)onChange({...value,model:e.target.value||null,reasoningEffort:null});}}><option value="">CLI 預設</option>{models.map(m=><option key={m} value={m}>{selectedProvider?.modelDetails?.find(d=>d.id===m)?.displayName||m}</option>)}<option value="__custom">自訂模型 ID…</option></select></label>{isCustom&&<label>自訂模型 ID<input aria-label={label+'自訂模型'} maxLength={128} value={selectedModel} onChange={e=>value&&onChange({...value,model:e.target.value||null,reasoningEffort:null})}/></label>}<ReasoningChoice label={label+'推理程度'} provider={selectedProvider} model={value?.model||null} value={value?.reasoningEffort||null} disabled={!value} onChange={effort=>value&&onChange({...value,reasoningEffort:effort})}/></fieldset>;
}
export function ReasoningChoice({provider,model,value,onChange,label,disabled=false}:{provider?:Provider;model:string|null;value:string|null;onChange:(effort:string|null)=>void;label:string;disabled?:boolean}){
  const detail=model?provider?.modelDetails?.find(m=>m.id===model):undefined;
  const manual=provider?.id==='codex'&&!!model&&model===provider.configuredDefaultModel&&!detail;
  const codexEfforts=['low','medium','high','xhigh','ultra'];
  const reported=detail?.reasoningEfforts||[];
  const efforts=manual?codexEfforts:provider?.id==='codex'?reported.filter(e=>codexEfforts.includes(e)):reported,unverified=!!value&&!efforts.includes(value);
  return <><label>推理程度<select aria-label={label} value={value||''} disabled={disabled||(!efforts.length&&!value)} onChange={e=>onChange(e.target.value||null)}><option value="">CLI 預設</option>{efforts.map(e=><option key={e} value={e}>{effortNames[e]||e}（{e}）</option>)}{unverified&&<option value={value!}>已保存：{effortNames[value!]||value}（支援狀態待確認）</option>}</select></label>{manual?<p className="footnote">手動指定，支援狀態待 CLI 驗證；目前模型目錄未提供此預設模型的推理選項。</p>:provider?.id==='gemini'?<p className="footnote">推理設定由 Gemini CLI 管理。</p>:provider&&detail&&efforts.length>0?<p className="footnote">較高程度通常需要較長時間與較多用量。{provider.id==='claude'&&'實際等級可能由 CLI 依模型或組織限制調整。'}</p>:provider&&<p className="footnote">{model?'尚未取得此模型的推理選項；可沿用 CLI 預設。':'先選擇模型即可設定推理程度；未指定時沿用 CLI 設定。'}</p>}</>;
}

