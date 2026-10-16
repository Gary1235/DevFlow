import {useCallback,useEffect,useState} from 'react';
import {api,ApiError} from '../api';
import {useAction,useResource} from '../hooks';
import {providerNames} from '../domain';
import {Heading,ReasoningChoice,useNotice} from './ui';
import type {Diagnostic,LoginSession,Provider} from '../types';
const labels:Record<string,string>={LoggedIn:'已登入',LoggedOut:'未登入',Unknown:'無法確認登入狀態',Unchecked:'尚未測試',Ready:'可執行',Error:'執行失敗',TimedOut:'逾時',WaitingForUser:'等待完成登入',AwaitingVerification:'正在確認登入狀態',Failed:'失敗',Cancelled:'已取消',CleanupFailed:'終端清理失敗'};
function Card({provider,onReloadModels}:{provider:Provider;onReloadModels:()=>Promise<void>}){
  const notice=useNotice(),action=useAction(notice),[model,setModel]=useState(''),[reasoningEffort,setReasoningEffort]=useState<string|null>(null),[customSelected,setCustomSelected]=useState(false),[mode,setMode]=useState('browser'),[message,setMessage]=useState(''),[operationMessage,setOperationMessage]=useState('');
  const storageKey='devflow.login.'+provider.id;
  const [session,setSession]=useState<string|null>(()=>{try{const id=sessionStorage.getItem(storageKey);return id&&/^[a-f0-9-]{36}$/i.test(id)?id:null;}catch{return null;}});
  useEffect(()=>{try{if(session)sessionStorage.setItem(storageKey,session);else sessionStorage.removeItem(storageKey);}catch{}},[session,storageKey]);
  const loadDiagnostic=useCallback((signal:AbortSignal)=>api<Diagnostic>('/api/ai/'+provider.id+'/diagnostics','GET',undefined,{signal}),[provider.id]);
  const diagnostic=useResource('diagnostic:'+provider.id,loadDiagnostic,0,provider.installed&&!session);
  const loadSession=useCallback((signal:AbortSignal)=>api<LoginSession>('/api/ai/logins/'+session,'GET',undefined,{signal}),[session]);
  const progress=useResource('login:'+session,loadSession,2000,!!session);
  useEffect(()=>{if(!progress.data)return;const state=progress.data.state;setOperationMessage((labels[state]||state)+'\n'+progress.data.message);if(!['WaitingForUser','CleanupFailed'].includes(state)){setSession(null);diagnostic.refresh();}},[progress.data]);
  const loginState=provider.installed?(diagnostic.data?.loginState||'Checking'):'NotInstalled';
  const isLoggedIn=loginState==='LoggedIn';
  const isLoggedOut=loginState==='LoggedOut';
  const statusLabel=session?'登入作業進行中':loginState==='Checking'?'正在檢查登入狀態…':loginState==='NotInstalled'?'未安裝':labels[loginState]||loginState;
  const statusClass=isLoggedIn?'success':isLoggedOut?'amber':'neutral';
  const models=provider.models;
  const customModel=customSelected||!!model&&!models.includes(model);
  const diagnosticMessage=diagnostic.error||diagnostic.data?.message||(!provider.installed?'找不到 CLI，安裝或設定路徑後請重啟 API 與 Worker。':'');
  return <article className="panel provider-card" data-provider={provider.id}>
    <div className="provider-logo">{provider.id==='codex'?'◈':provider.id==='gemini'?'✧':'✳'}</div>
    <div className="provider-top"><h2>{providerNames[provider.id]}</h2><span className={'chip '+(provider.installed?'success':'neutral')}>{provider.installed?'已安裝':'未安裝'}</span><span className={'chip '+statusClass} aria-label={providerNames[provider.id]+'登入狀態'}>{statusLabel}</span></div>
    <p className="muted">{provider.id==='claude'?'登入狀態與模型使用資格分開檢查。':'使用官方 CLI 登入，依任務選擇模型。'}</p>
    <div className="provider-status" role="status">{operationMessage||message||diagnosticMessage||'登入狀態會自動檢查。'}</div>
    {provider.configuredDefaultModel&&<p className="footnote">目前 CLI 預設模型：<strong>{provider.configuredDefaultModel}</strong>。選擇其他模型只套用本次測試。</p>}
    {provider.modelCatalogMessage&&<p className="footnote" role="status">{provider.modelCatalogMessage}</p>}
    <label>測試模型<select aria-label={providerNames[provider.id]+'測試模型'} value={customModel?'__custom':model} onChange={e=>{if(e.target.value==='__custom')setCustomSelected(true);else{setCustomSelected(false);setModel(e.target.value);setReasoningEffort(null);}}} disabled={!provider.installed}><option value="">CLI 預設</option>{models.map(m=><option value={m} key={m}>{provider.modelDetails?.find(d=>d.id===m)?.displayName||m}</option>)}<option value="__custom">自訂模型 ID…</option></select></label>
    {customModel&&<label>自訂模型 ID<input maxLength={128} value={model} onChange={e=>{setModel(e.target.value);setReasoningEffort(null);}} disabled={!provider.installed} placeholder="輸入 CLI 支援的模型 ID"/></label>}
    <ReasoningChoice label={providerNames[provider.id]+'測試推理程度'} provider={provider} model={model||null} value={reasoningEffort} onChange={setReasoningEffort} disabled={!provider.installed}/>
    {!isLoggedIn&&<label>登入方式<select value={mode} onChange={e=>setMode(e.target.value)} disabled={!provider.installed}><option value="browser">{provider.id==='gemini'?'Google 登入':'瀏覽器登入'}</option>{provider.id==='codex'&&<option value="device">裝置碼登入</option>}</select></label>}
    <div className="button-row">
      {provider.id==='codex'&&<button className="secondary" disabled={!provider.installed||action.busy||!!session} onClick={()=>void action.run(onReloadModels)}>重新載入模型</button>}
      {!isLoggedIn&&!session&&loginState!=='Checking'&&<button className="primary" disabled={!provider.installed||action.busy} onClick={()=>void action.run(async()=>{const s=await api<LoginSession>('/api/ai/'+provider.id+'/login','POST',{mode});setSession(s.id);setMessage(s.message);setOperationMessage(s.message);})}>{isLoggedOut?'登入 AI':'開啟官方登入'}</button>}
      <button className="secondary" disabled={!provider.installed||action.busy||!!session} onClick={()=>{setMessage('');setOperationMessage('');diagnostic.refresh();}}>重新檢查</button>
      <button className="secondary" disabled={!provider.installed||action.busy||!!session||isLoggedOut} onClick={()=>void action.run(async()=>{setMessage('正在測試所選模型…');setOperationMessage('');const d=await api<Diagnostic>('/api/ai/'+provider.id+'/probe','POST',{provider:provider.id,model:model.trim()||null,reasoningEffort},{timeout:125000});setMessage((labels[d.executionState]||d.executionState)+'\n'+d.message+'\n'+providerNames[provider.id]+' · '+(model.trim()||'CLI 預設模型')+' · 推理 '+(reasoningEffort||'CLI 預設')+(d.errorCode?'\n錯誤分類：'+d.errorCode:'')+(d.exitCode!=null?' · 結束代碼：'+d.exitCode:''));})}>連線測試 · 使用額度</button>
      {session&&<button className="danger-button" disabled={action.busy} onClick={()=>void action.run(async()=>{try{const s=await api<LoginSession>('/api/ai/logins/'+session+'/cancel','POST');setMessage(s.message);if(s.state!=='CleanupFailed')setSession(null);}catch(error){if(error instanceof ApiError&&error.status===404){setSession(null);setMessage('登入作業已不可查詢，可重新啟動登入。');}else throw error;}})}>取消登入</button>}
    </div>
    <p className="footnote">模型清單來自 CLI 目錄或已設定別名，不代表帳號權限已驗證；連線測試會使用額度。未知登入狀態仍可手動確認。</p>
  </article>;
}
export function AiConnections({providers}:{providers:Provider[]}){
  const [reloaded,setReloaded]=useState<Provider[]|null>(null);
  const reload=async()=>setReloaded(await api<Provider[]>('/api/ai/providers?refresh=true','GET',undefined,{timeout:25000}));
  return <><Heading eyebrow="AI CONNECTIONS" title="AI 連線" description="登入狀態自動確認，依任務選擇合適的 CLI 模型。"/><div className="info-banner">狀態檢查不呼叫模型；連線測試會使用額度。憑證由官方 CLI 管理。</div><div className="provider-grid">{(reloaded||providers).map(p=><Card key={p.id} provider={p} onReloadModels={reload}/>)}</div></>;
}
