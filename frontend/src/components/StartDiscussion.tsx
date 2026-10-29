import {useState} from 'react';
import {api} from '../api';
import {useAction} from '../hooks';
import {AiChoice,Modal,ProseContent,useNotice} from './ui';
import type {BaseData,Discussion,DiscussionMessage,Selection,WorkTask} from '../types';

interface Choices {supervisor:Selection;implementation:Selection;reviewer:Selection|null}
function remembered(project:string):Partial<Choices>{try{return JSON.parse(localStorage.getItem('devflow.simple.'+project)||'{}');}catch{return {};}}
export function StartDiscussion({data,discussion,plan,selection,onClose,onStarted}:{data:BaseData;discussion:Discussion;plan:DiscussionMessage;selection:Selection;onClose:()=>void;onStarted:(task:WorkTask)=>void}){
  const project=data.projects.find(p=>p.id===discussion.projectId);
  const [saved]=useState(()=>remembered(discussion.projectId));
  const [supervisor,setSupervisor]=useState<Selection>(selection);
  const [implementation,setImplementation]=useState<Selection>(saved.implementation||project?.aiDefaults?.implementation||selection);
  const [reviewer,setReviewer]=useState<Selection|null>(saved.reviewer||null);
  const notice=useNotice(),action=useAction(notice);
  const profiles=project?.validationProfiles||[];
  return <Modal title="選擇執行模型" eyebrow="START IMPLEMENTATION" onClose={onClose} wide>
    <form onSubmit={e=>{e.preventDefault();void action.run(async()=>{
      const choices={supervisor,implementation,reviewer};
      const task=await api<WorkTask>('/api/discussions/'+discussion.id+'/start','POST',{expectedVersion:discussion.version,planMessageId:plan.id,...choices});
      try{localStorage.setItem('devflow.simple.'+discussion.projectId,JSON.stringify(choices));}catch{}
      notice('計畫已核准，任務已排隊。主管 AI 將協調實作與驗證。');onStarted(task);
    });}}>
      <div className="dialog-body">
        <p>確認計畫後，只需要選擇執行模型。規則內問題由主管處理，超出計畫才會詢問你。</p>
        <div className="two-col">
          <AiChoice label="主管" providers={data.providers} value={supervisor} onChange={s=>s&&setSupervisor(s)}/>
          <AiChoice label="實作" providers={data.providers} value={implementation} onChange={s=>s&&setImplementation(s)}/>
        </div>
        <label className="checkbox"><input type="checkbox" checked={!!reviewer} onChange={e=>setReviewer(e.target.checked?supervisor:null)}/>加上驗收 AI（選填）</label>
        {reviewer&&<AiChoice label="驗收" providers={data.providers} value={reviewer} onChange={setReviewer}/>}
        <div className="info-banner"><strong>自動建置與驗證</strong><p>{profiles.length?profiles.map(p=>p.name).join('、'):'沿用專案預設驗證設定'}</p><p>失敗會回報主管 AI，再指派實作修正；通過後通知你進行人工測試。連續無進展會暫停並說明原因。</p></div>
        <details><summary>查看本次核准計畫與驗收條件</summary><ProseContent content={plan.proposedPlan||''}/><h3>驗收條件</h3><ProseContent content={plan.executionPlan?.acceptanceCriteria||''}/></details>
      </div>
      <div className="dialog-footer"><button type="button" className="secondary" onClick={onClose}>繼續討論</button><button className="primary" disabled={action.busy||!!discussion.activeOperation||!!plan.executionPlan?.openQuestions.length}>核准計畫並開始</button></div>
    </form>
  </Modal>;
}
