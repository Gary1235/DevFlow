import {useCallback} from 'react';
import {api} from '../api';
import {useAction,useResource} from '../hooks';
import {Badge,ProseContent,useNotice} from './ui';
import {ToolPermissions,type ToolPermission} from './ToolPermissions';
import {LiveActivity} from './LiveActivity';
import {TaskDelivery} from './TaskDelivery';
import {selectionText} from '../domain';
import type {Project,Run,WorkTask} from '../types';
export function ConversationTask({taskId,onResume,project,busy=false}:{taskId:string;onResume:()=>void;project?:Project;busy?:boolean}){
  const resource=useResource('conversation-task:'+taskId,useCallback((signal:AbortSignal)=>api<WorkTask>('/api/tasks/'+taskId,'GET',undefined,{signal}),[taskId]),1500);
  const runs=useResource('conversation-runs:'+taskId,useCallback((signal:AbortSignal)=>api<Run[]>('/api/tasks/'+taskId+'/runs','GET',undefined,{signal}),[taskId]),1500);
  const permissions=useResource('conversation-permissions:'+taskId,useCallback((signal:AbortSignal)=>api<ToolPermission[]>('/api/tasks/'+taskId+'/permissions','GET',undefined,{signal}),[taskId]),1500);
  const notice=useNotice(),action=useAction(notice),task=resource.data,run=runs.data?.at(-1);
  if(!task)return <p role="status">{resource.error||'讀取任務進度…'}</p>;
  const paused=task.status==='AwaitingStepApproval',question=task.stepResults?.filter(s=>s.requiresRework).at(-1)?.summary;
  return <section className="conversation-task" aria-label="目前任務">
    <div className="button-row"><strong>{task.title}</strong><Badge status={task.status}/></div>
    <ol className="agent-model-flow execution-flow" aria-label="實作模型流程">
      <li data-state="completed"><strong>✓ 主 AI · 規劃與協調</strong><span>{selectionText(task.ai?.planning)}</span></li>
      {!task.workflow?.steps?.length&&<li data-state={task.status==='Running'?'running':['AwaitingReview','Approved','Merged'].includes(task.status)?'completed':'pending'}><strong>實作與建置驗證</strong><span>{selectionText(task.ai?.implementation)}</span><small>{task.status==='Running'?run?.activity?.stage||'執行中':'依目前成果與專案驗證設定執行'}</small></li>}
      {(task.workflow?.steps||[]).map((step,i)=>{
        const result=task.stepResults?.find(s=>s.stepId===step.id)||run?.steps?.find(s=>s.stepId===step.id);
        const active=task.activeStepId===step.id&&task.status==='Running';
        const state=active?'running':result?.state==='Completed'&&!result.requiresRework?'completed':result?.state==='Failed'||result?.requiresRework?'attention':'pending';
        return <li key={step.id||i} data-state={state} aria-current={active?'step':undefined}><strong>{state==='running'?'◌ ':state==='completed'?'✓ ':''}{i+1}. {step.name||'實作'}</strong><span>{step.kind==='Validation'?'系統驗證':step.kind==='Approval'?'人工確認':selectionText(step.selection||result?.selection)}</span><small>{state==='running'?run?.activity?.stage||'實作中':state==='completed'?'已完成':state==='attention'?'等待處理':'等待執行'}</small></li>;
      })}
      <li data-state={['AwaitingReview','Merged'].includes(task.status)?'completed':'pending'}><strong>建置驗證 → 人工測試</strong><span>{['AwaitingReview','Merged'].includes(task.status)?'驗證已通過':'依專案驗證設定執行'}</span></li>
    </ol>
    {paused&&<div className="info-banner"><strong>先聊清楚，再繼續</strong><ProseContent content={question||'主 AI 需要你的決定。'}/><p>直接在下方提問，例如「這是什麼意思？會影響哪裡？」。提問不會核准或重新啟動任務。</p><button className="primary" disabled={action.busy||busy} onClick={onResume}>依討論繼續</button></div>}
    {['AwaitingReview','Approved','Rejected','Failed','AwaitingStepApproval','Merged'].includes(task.status)&&<>{task.status==='AwaitingReview'&&<p className="info-banner">自動驗證已通過，等待人工驗收。測試後可直接回報問題或合併。</p>}<TaskDelivery task={task} run={run} project={project} onChanged={resource.refresh}/></>}
    {['Failed','Rejected'].includes(task.status)&&<div className="error-banner"><p>實作未完成。請直接詢問主 AI 原因與處理建議，討論後可從既有起點重試。</p><button className="primary" disabled={action.busy||busy} onClick={onResume}>依討論重試</button><a href={'#task/'+taskId}>查看完整紀錄 →</a></div>}
    {run&&!run.finishedAt&&<><LiveActivity activity={run.activity} waiting={permissions.data?.some(p=>p.state==='Pending')} error={runs.error||permissions.error}/><ToolPermissions taskId={taskId} operationId={run.id} requests={permissions.data||[]} onAnswered={permissions.refresh}/></>}
    <div className="button-row"><details><summary>進階執行紀錄</summary><p>{task.stepResults?.filter(s=>s.state==='Completed'&&!s.requiresRework).length||0} / {task.workflow?.steps?.length||0} 項工作完成</p><a href={'#task/'+taskId}>開啟完整任務紀錄 →</a></details>
      {['Queued','Running','PlanningQueued','Planning','AwaitingStepApproval'].includes(task.status)&&<button className="secondary" disabled={action.busy} onClick={()=>void action.run(async()=>{await api('/api/tasks/'+taskId+'/cancel','POST');resource.refresh();})}>停止實作</button>}
    </div>
  </section>;
}
