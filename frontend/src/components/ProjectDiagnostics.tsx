import {useCallback,useEffect,useState} from 'react';
import {api} from '../api';
import {useResource} from '../hooks';
import type {ValidationProfile,PreviewService} from '../types';
interface Check {name:string;success:boolean;detail:string}
interface Preflight {ready:boolean;checks:Check[]}
interface Probe {id:string;state:string;profileName?:string;projectVersion?:number;createdAt?:string;preflight?:Preflight;evidence?:{exitCode:number;total:number;failed:number;kind:string;error?:string};error?:string;retainedWorkspace?:string}
const probeActive=(p:Probe)=>p.state==='Running'||p.state==='Stopping';
const probeLabel=(p:Probe)=>({Running:'執行中',Stopping:'正在停止並清理',Completed:'通過',Failed:'未通過',Cancelled:'已取消',Interrupted:'異常中斷'}[p.state]||p.state);
export function ProjectDiagnostics({projectId,version,profiles,onValidation,onPreview}:{projectId:string;version:number;profiles:ValidationProfile[];onValidation:(p:ValidationProfile)=>void;onPreview:(p:PreviewService)=>void}){
  const [discovery,setDiscovery]=useState<{validations:ValidationProfile[];previews:PreviewService[];warnings:string[]}|null>(null);
  const [selected,setSelected]=useState(''),[busy,setBusy]=useState(false),[error,setError]=useState(''),[preflight,setPreflight]=useState<Preflight|null>(null),[probeId,setProbeId]=useState('');
  const progress=useResource('probe:'+probeId,useCallback((signal:AbortSignal)=>api<Probe>('/api/execution-probes/'+probeId,'GET',undefined,{signal}),[probeId]),1000,!!probeId&&busy);
  const [result,setResult]=useState<Probe|null>(null);
  const [history,setHistory]=useState<Probe[]|null>(null),[historyBusy,setHistoryBusy]=useState(false),[cancelling,setCancelling]=useState(false);
  useEffect(()=>{if(progress.data){setResult(progress.data);if(!probeActive(progress.data)){setBusy(false);setCancelling(false);}}},[progress.data]);
  async function loadHistory(){
    setHistoryBusy(true);setError('');
    try{setHistory(await api<Probe[]>('/api/projects/'+projectId+'/execution-probes'));}
    catch(e){setError(e instanceof Error?e.message:'無法載入紀錄');}finally{setHistoryBusy(false);}
  }
  function inspect(p:Probe){setResult(p);setPreflight(null);setError('');setCancelling(p.state==='Stopping');setProbeId(p.id);setBusy(probeActive(p));}
  async function cancel(){
    setCancelling(true);setError('');
    try{setResult(await api<Probe>('/api/execution-probes/'+probeId+'/cancel','POST',{}));progress.refresh();}
    catch(e){setError(e instanceof Error?e.message:'無法取消測試');setCancelling(false);}
  }
  async function run(mode:'discover'|'preflight'|'test'){
    setBusy(true);setError('');setResult(null);setPreflight(null);setProbeId('');setCancelling(false);
    try{
      if(mode==='discover'){setDiscovery(await api('/api/projects/'+projectId+'/execution-discovery'));setBusy(false);return;}
      const profile=profiles.find(p=>p.id===selected);if(!profile)throw new Error('請先選擇要檢查的驗證設定。');
      const body={expectedVersion:version,profile};
      if(mode==='preflight'){setPreflight(await api('/api/projects/'+projectId+'/execution-preflight','POST',body));setBusy(false);}
      else{const probe=await api<Probe>('/api/projects/'+projectId+'/execution-probes','POST',body);setProbeId(probe.id);setResult(probe);}
    }catch(e){setError(e instanceof Error?e.message:'檢查失敗');setBusy(false);}
  }
  const checks=result?.preflight||preflight;
  return <section className="execution-settings" aria-label="專案環境檢查"><h3>偵測與測試設定</h3>
    <button className="secondary" type="button" disabled={busy} onClick={()=>void run('discover')}>偵測專案建置與預覽目標</button>
    {discovery&&<div>{discovery.warnings.map((w,i)=><p key={i}>{w}</p>)}{!discovery.validations.length&&!discovery.previews.length&&<p>未找到可辨識的目標，請手動填寫設定。</p>}
      {discovery.validations.map(p=><p key={p.id}>{p.name}<button type="button" disabled={busy} onClick={()=>{onValidation({...p,id:crypto.randomUUID()});}}>加入驗證設定</button></p>)}
      {discovery.previews.map(p=><p key={p.id}>{p.name}<button type="button" disabled={busy} onClick={()=>onPreview({...p,id:crypto.randomUUID()})}>加入預覽服務</button></p>)}
    </div>}
    <label>要檢查的驗證設定<select aria-label="要檢查的驗證設定" value={selected} disabled={busy} onChange={e=>setSelected(e.target.value)}><option value="">請選擇</option>{profiles.map(p=><option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
    <div className="button-row"><button type="button" disabled={busy||!selected} onClick={()=>void run('preflight')}>檢查路徑與工具版本</button><button type="button" disabled={busy||!selected} onClick={()=>void run('test')}>在臨時工作副本測試此設定</button></div>
    <p className="footnote">測試會執行目前表單中的驗證與依賴準備指令，使用基底分支的已提交內容；結束後清除臨時副本，不會呼叫 AI。關閉設定視窗後測試仍會完成。</p>
    <button className="secondary" type="button" disabled={historyBusy} onClick={()=>void loadHistory()}>{historyBusy?'載入中…':'載入最近設定測試'}</button>
    {history&&<div aria-label="設定測試紀錄">{!history.length&&<p>尚無設定測試紀錄。</p>}{history.map(p=><p key={p.id}>{p.profileName||'設定測試'} · {probeLabel(p)} · 專案版本 {p.projectVersion} · {p.createdAt?new Date(p.createdAt).toLocaleString():''} <button type="button" disabled={busy} onClick={()=>inspect(p)}>{probeActive(p)?'追蹤進度':'檢視結果'}</button></p>)}</div>}
    {busy&&<p role="status">正在檢查或執行設定測試…{probeId&&<button type="button" disabled={cancelling} onClick={()=>void cancel()}>{cancelling?'正在停止並清理…':'取消設定測試'}</button>}</p>}{(error||progress.error)&&<p role="alert">{error||progress.error}</p>}
    {checks&&<ul>{checks.checks.map((c,i)=><li key={i}>{c.success?'✓':'✕'} {c.name}：{c.detail}</li>)}</ul>}
    {result&&<div role="status">設定測試{probeLabel(result)}{result.evidence&&<p>exit {result.evidence.exitCode} · {result.evidence.kind==='DotnetTest'?`測試 ${result.evidence.total}，失敗 ${result.evidence.failed}`:'依指令退出碼判斷'}</p>}<p>{result.error||result.evidence?.error}</p>{result.retainedWorkspace&&<p>臨時副本已保留，請先確認舊程序停止再檢查：{result.retainedWorkspace}</p>}</div>}
  </section>;
}
