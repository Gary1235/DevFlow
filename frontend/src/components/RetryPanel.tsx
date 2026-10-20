import {useCallback,useState} from 'react';
import {api} from '../api';
import {useAction,useResource} from '../hooks';
import {shortCommit} from '../domain';
import {useNotice} from './ui';
import type {WorkTask} from '../types';
import './recovery.css';

interface Preview {scope:string;expectedVersion:number;summary:string;retainedSteps:string[];repeatedSteps:string[];baseCommit:string|null}
export function RetryPanel({task,onChanged}:{task:WorkTask;onChanged:()=>void}){
  const [scope,setScope]=useState('Resume'),[instructions,setInstructions]=useState('');
  const notice=useNotice(),action=useAction(notice);
  const preview=useResource('retry:'+task.id+':'+task.version+':'+scope,useCallback((signal:AbortSignal)=>api<Preview>('/api/tasks/'+task.id+'/retry-preview?scope='+scope,'GET',undefined,{signal}),[task.id,task.version,scope]),0);
  const p=preview.data;
  return <article className="panel retry-panel" aria-label="重試範圍"><h2>修正與重試</h2><label>重試方式<select value={scope} disabled={action.busy} onChange={e=>setScope(e.target.value)}><option value="Resume">接續既有成果</option><option value="Restart">從原始基底全部重做</option></select></label>
    {preview.error&&<div className="error-banner">{preview.error}<button type="button" className="secondary" onClick={preview.refresh}>重新讀取範圍</button></div>}
    {p?<div className="retry-preview"><strong>{p.summary}</strong><dl><dt>保留的步驟</dt><dd>{p.retainedSteps.join(' → ')||'無'}</dd><dt>會重新執行</dt><dd>{p.repeatedSteps.join(' → ')}</dd><dt>起始成果</dt><dd>{p.baseCommit?shortCommit(p.baseCommit):'專案設定的基底分支'}</dd></dl><p>會建立新的執行紀錄；舊紀錄與成果仍保留。這不是從步驟內的中斷位置繼續。</p></div>:!preview.error&&<p>正在核對重試範圍…</p>}
    <label>修正指示<textarea rows={3} maxLength={20000} placeholder="例如：修正執行紀錄中的測試失敗，保留已通過的 API 行為。" value={instructions} onChange={e=>setInstructions(e.target.value)}/></label>
    <button type="button" className="warning-button" disabled={action.busy||!p||p.expectedVersion!==task.version} onClick={()=>void action.run(async()=>{
      await api('/api/tasks/'+task.id+'/retry','POST',{expectedVersion:p!.expectedVersion,scope,instructions:instructions.trim()||'請根據這次執行紀錄與審核意見修正問題，遵守既有計畫與驗收條件。'});
      notice('已依顯示範圍排隊重試。');onChanged();
    })}>{scope==='Restart'?'確認從基底全部重做':'確認依上述範圍重試'}</button>
  </article>;
}
