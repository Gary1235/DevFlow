import {recoveryAdvice} from '../recovery';
import {useCallback,useState} from 'react';
import {api} from '../api';
import {useAction,useResource} from '../hooks';
import {AiChoice,Modal,useNotice} from './ui';
import type {Provider,Selection,WorkTask} from '../types';
import './recovery.css';

export function FailureRecovery({error,onRecords,onModel,onRetry,onReply}:{error:string;onRecords?:()=>void;onModel?:()=>void;onRetry?:()=>void;onReply?:()=>void}){
  const advice=recoveryAdvice(error);
  return <section className="failure-recovery" aria-label="失敗處理建議"><strong>{advice.title}</strong><p>{advice.next}</p><div className="button-row">
    {onRecords&&<button type="button" className="secondary" onClick={onRecords}>查看執行紀錄</button>}
    {advice.connection&&<a className="text-link" href="#ai">前往 AI 連線 →</a>}
    {advice.model&&onModel&&<button type="button" className="secondary" onClick={onModel}>更換模型</button>}
    {onRetry&&<button type="button" className="secondary" onClick={onRetry}>查看重試範圍</button>}
    {onReply&&<button type="button" className="secondary" onClick={onReply}>重試上一則訊息</button>}
  </div><details><summary>原始錯誤訊息</summary><p className="prose">{error}</p></details></section>;
}

export function TaskModelSettings({task,onClose,onChanged}:{task:WorkTask;onClose:()=>void;onChanged:()=>void}){
  const stage=task.status==='PlanningFailed'?'Planning':'Implementation';
  const [selection,setSelection]=useState<Selection>(task.ai?.[stage==='Planning'?'planning':'implementation']||{provider:'codex',model:null});
  const [version]=useState(task.version),notice=useNotice(),action=useAction(notice);
  const providers=useResource('recovery-models',useCallback((signal:AbortSignal)=>api<Provider[]>('/api/ai/providers','GET',undefined,{signal}),[]),0);
  return <Modal title="更換重試使用的模型" eyebrow="MODEL SETTINGS" onClose={onClose}><form onSubmit={e=>{e.preventDefault();void action.run(async()=>{await api('/api/tasks/'+task.id+'/model','PUT',{expectedVersion:version,stage,selection});notice('模型已更新，尚未開始重試。');onChanged();onClose();});}}><div className="dialog-body">{providers.error&&<p className="error-banner">{providers.error}</p>}<AiChoice label={stage==='Planning'?'規劃':'實作'} value={selection} providers={providers.data||[]} onChange={s=>{if(s)setSelection(s);}}/><p>儲存後，再由你決定何時重新規劃或重試，不會自動呼叫模型。</p></div><div className="dialog-footer"><button type="button" className="secondary" onClick={onClose}>取消</button><button className="primary" disabled={action.busy||!providers.data}>儲存模型</button></div></form></Modal>;
}
