import {useCallback,useEffect,useState} from 'react';
import {api} from '../api';
import {useAction,useResource} from '../hooks';
import {useNotice} from './ui';
import {shortCommit} from '../domain';
import type {Project,Run,WorkTask} from '../types';
import './execution-settings.css';

interface Preview {taskId:string;runId:string|null;resultCommit:string|null;state:string;services:{id:string;name:string;state:string;url:string;exitCode:number|null}[];log:string;error:string|null}
const states:Record<string,string>={Starting:'啟動中',Preparing:'準備依賴中',Running:'執行中',Stopping:'停止中',Stopped:'已停止',Failed:'失敗'};
export function TaskPreview({task,run,project,compact=false,onFeedback}:{task:WorkTask;run?:Run;project?:Project;compact?:boolean;onFeedback?:(runId:string,commit:string)=>void}){
  const notice=useNotice(),action=useAction(notice),[poll,setPoll]=useState(false);
  const resource=useResource('preview:'+task.id,useCallback((signal:AbortSignal)=>api<Preview>('/api/tasks/'+task.id+'/preview','GET',undefined,{signal}),[task.id]),poll?2000:0);
  const preview=resource.data,active=!!preview&&['Starting','Running','Stopping'].includes(preview.state);
  useEffect(()=>setPoll(active),[active]);
  const canStart=!!run?.resultCommit&&!!run.finishedAt&&!!project?.previewServices?.length&&['AwaitingReview','Approved','Rejected','Failed','AwaitingStepApproval'].includes(task.status);
  if(compact)return <section aria-label="本地驗收預覽">
    {preview?.runId&&<p className="footnote">預覽版本：{shortCommit(preview.resultCommit)}{preview.runId!==run?.id?' · 較舊版本，停止後重新啟動可查看最新成果':''}</p>}
    {!project?.previewServices?.length&&<p>此專案尚未設定預覽服務，可在專案設定指定一次啟動方式。</p>}
    {(resource.error||preview?.error)&&<p className="error-banner">{resource.error||preview?.error}</p>}
    <div className="button-row">
      <button className="primary" disabled={action.busy||active||!canStart} onClick={()=>void action.run(async()=>{await api('/api/tasks/'+task.id+'/preview/start','POST',{runId:run!.id,resultCommit:run!.resultCommit,expectedVersion:task.version,projectVersion:project!.version});setPoll(true);resource.refresh();})}>啟動測試預覽</button>
      {preview?.services.filter(s=>s.state==='Running'&&preview.state==='Running').map(service=><a key={service.id} className="primary" href={service.url} target="_blank" rel="noreferrer">開啟 {service.name} ↗</a>)}
      {active&&<button className="secondary" disabled={action.busy} onClick={()=>void action.run(async()=>{await api('/api/tasks/'+task.id+'/preview/stop','POST',undefined,{timeout:90000});setPoll(true);resource.refresh();})}>停止預覽</button>}
      {active&&preview?.runId&&preview.resultCommit&&onFeedback&&<button className="secondary" onClick={()=>onFeedback(preview.runId!,preview.resultCommit!)}>回報目前預覽問題</button>}
      {active&&<span role="status">{states[preview!.state]}</span>}
    </div><details><summary>預覽日誌</summary><pre className="preview-log">{preview?.log||'尚無預覽紀錄。'}</pre></details>
  </section>;
  return <section className="panel task-preview" aria-label="本地驗收預覽"><div className="panel-heading"><div><h2>本地驗收預覽</h2><p>從任務工作副本啟動，合併前實際操作功能。</p></div><span className="chip neutral">{states[preview?.state||'Stopped']||preview?.state}</span></div>
    <p className="preview-identity">預覽版本：{preview?.runId?`${preview.runId} · ${shortCommit(preview.resultCommit)}`:run?.resultCommit?shortCommit(run.resultCommit):'尚無可預覽成果'}</p>
    {preview?.runId&&preview.runId!==run?.id&&<p className="attention-banner">目前預覽的是先前執行版本；停止後重新啟動，才會使用上方所選成果。</p>}
    {!project?.previewServices?.length&&<p className="info-banner">請至 <a href="#projects">專案 → 專案設定</a> 設定前後端預覽指令與測試環境。</p>}
    <p className="footnote">只在此電腦啟動。資料庫不會自動隔離，請在專案設定指定測試連線。關閉分頁仍會繼續執行，請使用「停止預覽」。</p>
    {(resource.error||preview?.error)&&<p className="error-banner">{resource.error||preview?.error}</p>}
    <div className="button-row"><button className="primary" disabled={action.busy||active||!canStart} onClick={()=>void action.run(async()=>{await api('/api/tasks/'+task.id+'/preview/start','POST',{runId:run!.id,resultCommit:run!.resultCommit,expectedVersion:task.version,projectVersion:project!.version});setPoll(true);resource.refresh();notice('預覽啟動請求已送出，請查看服務狀態與日誌。');})}>啟動預覽</button><button className="secondary" disabled={action.busy||!preview?.runId} onClick={()=>void action.run(async()=>{await api('/api/tasks/'+task.id+'/preview/stop','POST',undefined,{timeout:90000});resource.refresh();notice('已處理停止預覽請求，請確認狀態。');})}>停止預覽</button><button className="secondary" onClick={resource.refresh}>重新讀取狀態</button></div>
    <div className="preview-services">{preview?.services.map(service=><div key={service.id}><strong>{service.name}</strong> · {states[service.state]||service.state}{service.exitCode!=null&&` · 退出碼 ${service.exitCode}`}{service.state==='Running'&&preview.state==='Running'&&<a className="action-pill" href={service.url} target="_blank" rel="noreferrer">開啟網站 ↗</a>}</div>)}</div>
    <details><summary>查看預覽日誌（保留最近 64 KiB）</summary><pre className="preview-log">{preview?.log||'尚無日誌。若缺少前端套件，可在專案設定勾選啟動前依賴準備，npm 使用 ci（需要 lockfile）。'}</pre></details>
  </section>;
}
