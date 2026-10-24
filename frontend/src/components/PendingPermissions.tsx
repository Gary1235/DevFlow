import {useCallback,useEffect,useRef,useState} from 'react';
import {api} from '../api';
import {useResource} from '../hooks';

export interface PendingPermission {
  id:string;ownerId:string;kind:'task'|'discussion';title:string;tool:string;provider:string;
  createdAt:string;reminderAt:string;reasonCode:string;reason:string;
}
export function PendingPermissions(){
  const resource=useResource('pending-permissions',useCallback((signal:AbortSignal)=>
    api<PendingPermission[]>('/api/permissions/pending','GET',undefined,{signal}),[]),5000,true,true);
  const [notifications,setNotifications]=useState(()=>localStorage.getItem('devflow-permission-notifications')==='true');
  const [sound,setSound]=useState(()=>localStorage.getItem('devflow-permission-sound')==='true');
  const [message,setMessage]=useState('');
  const seen=useRef(new Set<string>());
  const pending=resource.data||[];
  const href=(p:PendingPermission)=>'#'+p.kind+'/'+p.ownerId;
  useEffect(()=>{
    for(const p of pending){
      if(seen.current.has(p.id))continue;
      seen.current.add(p.id);
      if(notifications&&typeof Notification!=='undefined'&&Notification.permission==='granted'){
        try{
        const n=new Notification('DevFlow 等待你的核准',{body:p.title+' · '+p.reason,tag:'devflow-'+p.id});
        n.onclick=()=>{window.focus();location.hash=href(p);n.close();};
        }catch{setMessage('系統無法顯示通知，請查看全域待確認清單。');}
      }
      if(sound){try{const c=new AudioContext();const o=c.createOscillator(),g=c.createGain();g.gain.value=.08;o.connect(g);g.connect(c.destination);o.start();o.stop(c.currentTime+.2);o.onended=()=>{void c.close();};}catch{/* Sound is optional and browsers may require a user gesture. */}}
    }
    const active=new Set(pending.map(p=>p.id));for(const id of seen.current)if(!active.has(id))seen.current.delete(id);
  },[resource.data,notifications,sound]);
  useEffect(()=>{
    if(!pending.length)return;
    const prefix=`[待核准 ${pending.length}] `;
    const update=()=>{if(!document.title.startsWith(prefix))document.title=prefix+document.title.replace(/^\[待核准 \d+\] /,'');};
    update();const observer=new MutationObserver(update);const title=document.querySelector('title');if(title)observer.observe(title,{childList:true,subtree:true,characterData:true});
    return()=>{observer.disconnect();document.title=document.title.replace(/^\[待核准 \d+\] /,'');};
  },[pending.length]);
  async function enable(){
    if(typeof Notification==='undefined'){setMessage('此瀏覽器不支援桌面通知，請查看待確認清單。');return;}
    const permission=await Notification.requestPermission();const enabled=permission==='granted';
    setNotifications(enabled);localStorage.setItem('devflow-permission-notifications',String(enabled));
    seen.current.clear();setMessage(enabled?'已啟用工具確認通知。':'瀏覽器未允許通知，請在網站設定開啟。');
  }
  return <section className="pending-permissions" aria-label="全域工具確認" style={{padding:'12px 0'}}>
    <div className="button-row"><strong>工具待確認 {pending.length}</strong>
      <button className="secondary" onClick={()=>{if(notifications){setNotifications(false);localStorage.setItem('devflow-permission-notifications','false');}else void enable();}}>{notifications?'關閉核准通知':'啟用核准通知'}</button>
      <label><input type="checkbox" checked={sound} onChange={e=>{setSound(e.target.checked);localStorage.setItem('devflow-permission-sound',String(e.target.checked));}}/>提示音</label>
    </div>
    {message&&<p role="status">{message}</p>}
    {resource.error&&<p role="alert">無法取得待確認清單：{resource.error}<button onClick={resource.refresh}>重試</button></p>}
    {pending.length>0&&<div className="info-banner" role="status">任務正在等待你的工具核准。
      <ul>{pending.map(p=><li key={p.id}><a href={href(p)}>{p.title}</a> · {p.provider}／{p.tool} · 已等待 {Math.max(0,Math.floor((Date.now()-Date.parse(p.createdAt))/60000))} 分鐘<p>{p.reason}（{p.reasonCode}）</p></li>)}</ul>
    </div>}
  </section>;
}
