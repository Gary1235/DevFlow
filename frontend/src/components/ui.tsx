import {createContext,useContext,useEffect,useId,useRef,useState,type ReactNode} from 'react';
import {tone,statusNames,providerNames} from '../domain';
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
  const id=useId();return <fieldset><legend>{label}</legend><label>AI CLI<select aria-label={label+' AI'} value={value?.provider||''} onChange={e=>onChange(e.target.value?{provider:e.target.value as ProviderId,model:null}:null)}>{inherit&&<option value="">沿用專案預設</option>}{providers.map(p=><option key={p.id} value={p.id}>{providerNames[p.id]}{p.installed?'':'（未安裝）'}</option>)}</select></label><label>模型<input aria-label={label+'模型'} maxLength={128} list={id} disabled={!value} value={value?.model||''} placeholder="留空沿用 CLI 預設" onChange={e=>value&&onChange({...value,model:e.target.value||null})}/><datalist id={id}>{providers.find(p=>p.id===value?.provider)?.models.map(m=><option key={m} value={m}/>)}</datalist></label></fieldset>;
}
