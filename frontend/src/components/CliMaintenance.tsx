import {useCallback,useEffect,useRef,useState} from 'react';
import {api} from '../api';
import {useAction,useResource} from '../hooks';
import {Modal,useNotice} from './ui';
import type {Provider} from '../types';

interface VersionCheck {id:string;provider:string;executable:string;entryPoint?:string|null;installedVersion:string|null;latestVersion:string|null;canUpdate:boolean;updateAvailable:boolean;message:string;manualCommand:string}
interface UpdateSession {id:string;provider:string;state:string;message:string;installedVersion:string|null;targetVersion:string;finishedAt?:string|null}
const terminal=['Completed','Failed','TimedOut'];
export function CliMaintenance({provider,loginActive,onUpdated}:{provider:Provider;loginActive:boolean;onUpdated:()=>Promise<void>}){
  const notice=useNotice(),action=useAction(notice),[check,setCheck]=useState<VersionCheck|null>(null),[confirm,setConfirm]=useState(false),[result,setResult]=useState<UpdateSession|null>(null),completed=useRef('');
  const key='devflow.cli-update.'+provider.id;
  const [id,setId]=useState<string|null>(()=>{try{const value=sessionStorage.getItem(key);return value&&/^[a-f0-9-]{36}$/i.test(value)?value:null;}catch{return null;}});
  useEffect(()=>{try{if(id)sessionStorage.setItem(key,id);else sessionStorage.removeItem(key);}catch{}},[id,key]);
  const load=useCallback((signal:AbortSignal)=>api<UpdateSession>('/api/ai/cli/updates/'+id,'GET',undefined,{signal}),[id]);
  const progress=useResource('update:'+id,load,1500,!!id);
  useEffect(()=>{
    const session=progress.data;if(!session)return;setResult(session);
    if(terminal.includes(session.state)){
      setId(null);setCheck(null);
      if(completed.current!==session.id){completed.current=session.id;if(session.state==='Completed')void onUpdated().catch(()=>notice('CLI 已更新，但模型清單刷新失敗；請按重新載入模型。',true));}
    }
  },[progress.data,onUpdated,notice]);
  function checkVersion(){return action.run(async()=>{setCheck(null);setResult(null);setCheck(await api<VersionCheck>('/api/ai/'+provider.id+'/cli/check','POST',{}, {timeout:35000}));});}
  const disabled=action.busy||!!id||loginActive;
  return <section className="cli-maintenance" aria-label="CLI 版本管理">
    <h3>CLI 版本與更新</h3><p className="footnote">版本查詢會連線至 npm，不呼叫模型。更新前請結束外部 CLI 終端；執行中的 DevFlow 作業會阻擋更新。</p>
    {check&&<><dl className="cli-versions"><div><dt>目前版本</dt><dd>{check.installedVersion||'無法確認'}</dd></div><div><dt>npm 最新版本</dt><dd>{check.latestVersion||'無法取得'}</dd></div></dl><p className="footnote cli-path">執行檔：{check.executable}{check.entryPoint&&<><br/>CLI 入口：{check.entryPoint}</>}</p><p className="footnote">{check.message}</p>{!check.canUpdate&&check.updateAvailable&&<p className="footnote">{check.manualCommand}</p>}</>}
    {result&&<div className="provider-status" role="status">{result.message}{result.state==='Completed'&&<p>已安裝版本：{result.installedVersion}</p>}</div>}
    {progress.error&&<div className="error-banner">{progress.error}<p>更新結果暫時無法確認；先檢查實際版本，不會自動重送安裝。</p><button className="secondary" onClick={()=>{setId(null);void checkVersion();}}>停止追蹤並檢查版本</button></div>}
    <div className="button-row"><button className="secondary" disabled={!provider.installed||disabled} onClick={()=>void checkVersion()}>{action.busy?'處理中…':'檢查 CLI 更新'}</button>{check?.canUpdate&&<button className="secondary" disabled={disabled} onClick={()=>setConfirm(true)}>更新 CLI</button>}{id&&<span role="status">{result?.state==='Verifying'?'驗證版本中…':'更新進行中…'}</span>}</div>
    {confirm&&check&&<Modal title="確認更新 CLI" onClose={()=>{if(!action.busy)setConfirm(false);}}><div className="dialog-body"><p>{check.installedVersion} → {check.latestVersion}</p><p className="cli-path">{check.entryPoint||check.executable}</p><p>將更新此使用者的 npm 全域 CLI，其他使用這套安裝的工具也會受到影響。更新期間暫停 DevFlow 的 CLI 調用，完成後重新載入模型；不會自動進行付費模型測試。</p></div><div className="dialog-footer"><button className="secondary" disabled={action.busy} onClick={()=>setConfirm(false)}>取消</button><button className="primary" disabled={action.busy} onClick={()=>void action.run(async()=>{const session=await api<UpdateSession>('/api/ai/'+provider.id+'/cli/update','POST',{checkId:check.id},{timeout:35000});setResult(session);setId(session.id);setCheck(null);setConfirm(false);})}>確認更新</button></div></Modal>}
  </section>;
}
