import {useState} from 'react';
import {api} from '../api';
import {useAction} from '../hooks';
import {useNotice} from './ui';
import {TaskPreview} from './TaskPreview';
import {shortCommit} from '../domain';
import type {Discussion,GitState,Project,Run,WorkTask} from '../types';

export async function openTaskConversation(taskId:string){
  const d=await api<Discussion>('/api/tasks/'+taskId+'/conversation','POST');
  location.hash='#discussion/'+d.id;
}

export function TaskDelivery({task,run,project,onChanged}:{task:WorkTask;run?:Run;project?:Project;onChanged:()=>void}){
  const notice=useNotice(),action=useAction(notice),[mergeFailed,setMergeFailed]=useState(false);
  const ready=['AwaitingReview','Approved'].includes(task.status)&&!!run?.resultCommit&&!!run.finishedAt;
  async function feedback(runId:string,commit:string){
    await action.run(async()=>{
      const d=await api<Discussion>('/api/tasks/'+task.id+'/feedback','POST',{expectedVersion:task.version,runId,resultCommit:commit});
      location.hash='#discussion/'+d.id;onChanged();notice('已綁定驗收版本，直接在對話描述問題或貼圖。');
    });
  }
  return <section className="task-delivery" aria-label="成果驗收">
    <TaskPreview task={task} run={run} project={project} compact onFeedback={(id,commit)=>void feedback(id,commit)}/>
    {run?.resultCommit&&<p className="footnote">最新成果 {shortCommit(run.resultCommit)} · 合併至本地 {project?.baseBranch||'專案目標分支'}</p>}
    <div className="button-row">
      {run?.resultCommit&&run.finishedAt&&<button className="secondary" disabled={action.busy} onClick={()=>void feedback(run.id,run.resultCommit!)}>回報此成果問題</button>}
      {ready&&<button className="primary" disabled={action.busy||!project} onClick={()=>void action.run(async()=>{
        try{
          const target=await api<GitState>('/api/projects/'+task.projectId+'/git-state');
          await api('/api/tasks/'+task.id+'/accept-and-merge','POST',{expectedVersion:task.version,runId:run!.id,resultCommit:run!.resultCommit,expectedTargetCommit:target.headCommit});
          setMergeFailed(false);notice('人工驗收已確認，成果已合併至本地 '+project!.baseBranch+'。');
        }catch(error){setMergeFailed(true);throw error;}finally{onChanged();}
      })}>驗收通過並合併至 {project?.baseBranch}</button>}
      {mergeFailed&&task.status==='Approved'&&<button className="secondary" disabled={action.busy} onClick={()=>void action.run(async()=>{
        const target=await api<GitState>('/api/projects/'+task.projectId+'/git-state');
        const integrated=await api<WorkTask>('/api/tasks/'+task.id+'/integrate','POST',{expectedVersion:task.version,runId:run!.id,resultCommit:run!.resultCommit,expectedTargetCommit:target.headCommit,continueCurrent:true});
        await openTaskConversation(integrated.id);notice('已接續目前任務整合最新分支；重新驗證後再人工驗收。');onChanged();
      })}>整合最新分支並重新驗證</button>}
    </div>
  </section>;
}
