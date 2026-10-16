import {useState} from 'react';
import {api} from '../api';
import {useAction} from '../hooks';
import {AiChoice,Modal,useNotice} from './ui';
import {TaskOptions,useRules} from './TaskOptions';
import {FolderPicker} from './FolderPicker';
import type {BaseData,ExecutionPolicy,Project,Selection,TaskSeed,Workflow,WorkTask} from '../types';
export function TaskDialog({data,projectId,onClose,onCreated,seed}:{data:BaseData;projectId:string;onClose:()=>void;onCreated:(task:WorkTask)=>void;seed?:TaskSeed}){
  const initial=projectId||data.projects[0]?.id||'';
  const [project,setProject]=useState(initial),[title,setTitle]=useState(seed?.title||''),[description,setDescription]=useState(seed?.description||''),[criteria,setCriteria]=useState(seed?.acceptanceCriteria||''),[auto,setAuto]=useState(!seed),[planning,setPlanning]=useState<Selection|null>(null),[implementation,setImplementation]=useState<Selection|null>(null);
  const [ruleIds,setRuleIds]=useState<string[]>(seed?.ruleIds??data.projects.find(p=>p.id===initial)?.defaultRuleIds??[]),[taskRules,setTaskRules]=useState(seed?.taskRules||''),[policy,setPolicy]=useState<ExecutionPolicy>({tdd:true,allowedPaths:seed?.allowedPaths||[]}),[workflow,setWorkflow]=useState<Workflow>({planningEnabled:true,reviewers:[{provider:'codex',model:null}],maxReviewRetries:0}),[confirmed,setConfirmed]=useState(false);
  const notice=useNotice(),action=useAction(notice),rules=useRules();
  return <Modal title={seed?'確認討論產生的任務':'建立開發任務'} eyebrow="NEW TASK" onClose={onClose} wide><form onSubmit={e=>{e.preventDefault();void action.run(async()=>{
    const task=await api<WorkTask>('/api/tasks','POST',{projectId:project,title,description,acceptanceCriteria:criteria,autoPlan:auto,planning,implementation,ruleIds,taskRules,policy:{...policy,allowedPaths:policy.allowedPaths.map(p=>p.trim()).filter(Boolean)},workflow,sourceDiscussionId:seed?.discussionId,sourceDraftId:seed?.id,acknowledgeOpenQuestions:confirmed});
    notice(auto?'任務已提交，排隊作業需要 Worker 啟動。':'任務已建立，尚未開始執行。');onCreated(task);
  });}}><div className="dialog-body">
    {seed?.openQuestions?.length? <div className="info-banner"><strong>討論中尚待釐清的問題</strong><ul>{seed.openQuestions.map((q,i)=><li key={i}>{q}</li>)}</ul><label className="checkbox"><input type="checkbox" required checked={confirmed} onChange={e=>setConfirmed(e.target.checked)}/>我已檢視這些問題，並在任務內容中明確說明處理方式</label></div>:null}
    <label>專案<select required disabled={!!seed} value={project} onChange={e=>{setProject(e.target.value);setRuleIds(data.projects.find(p=>p.id===e.target.value)?.defaultRuleIds||[]);}}>{data.projects.map(p=><option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
    <label>任務名稱<input required maxLength={200} value={title} onChange={e=>setTitle(e.target.value)}/></label><div className="two-col"><label>需求<textarea required rows={5} maxLength={20000} value={description} onChange={e=>setDescription(e.target.value)}/></label><label>驗收條件<textarea required rows={5} maxLength={20000} value={criteria} onChange={e=>setCriteria(e.target.value)}/></label></div>
    <div className="two-col choice-grid">{workflow.planningEnabled&&<AiChoice label="規劃" providers={data.providers} value={planning} onChange={setPlanning} inherit/>}<AiChoice label="實作" providers={data.providers} value={implementation} onChange={setImplementation} inherit/></div>
    {rules.error&&<p className="error-banner">規則庫讀取失敗：{rules.error}</p>}<TaskOptions rules={rules.data||[]} ruleIds={ruleIds} onRules={setRuleIds} taskRules={taskRules} onTaskRules={setTaskRules} policy={policy} onPolicy={setPolicy} workflow={workflow} onWorkflow={setWorkflow} providers={data.providers}/>
    <label className="checkbox"><input type="checkbox" checked={auto} onChange={e=>setAuto(e.target.checked)}/>{workflow.planningEnabled?'提交後自動規劃（使用模型額度）':'提交後直接開始實作（使用模型額度）'}</label><p className="footnote">共用規則會保存版本快照。討論轉入的任務預設等待你手動啟動。</p>
    </div><div className="dialog-footer"><button type="button" className="secondary" onClick={onClose}>取消</button><button className="primary" disabled={action.busy}>提交任務 →</button></div></form></Modal>;
}
export function ProjectDialog({onClose,onCreated}:{onClose:()=>void;onCreated:(p:Project)=>void}){
  const [name,setName]=useState(''),[path,setPath]=useState(''),[branch,setBranch]=useState('main'),[validation,setValidation]=useState('["test","-m:1","/nr:false"]'),[picking,setPicking]=useState(false);
  const notice=useNotice(),action=useAction(notice);
  return <><Modal title="登錄本地專案" eyebrow="NEW PROJECT" onClose={onClose}><form onSubmit={e=>{e.preventDefault();void action.run(async()=>{
    let args:unknown;try{args=JSON.parse(validation);}catch{throw new Error('測試參數必須是 JSON 陣列。');}
    if(!Array.isArray(args)||args.some(v=>typeof v!=='string'))throw new Error('測試參數必須是字串陣列。');
    const p=await api<Project>('/api/projects','POST',{name,repositoryPath:path,baseBranch:branch,validationArguments:args});notice('專案已登錄。');onCreated(p);
  });}}><div className="dialog-body">
    <label>專案名稱<input required maxLength={200} value={name} onChange={e=>setName(e.target.value)}/></label>
    <label htmlFor="repository-path">Repository 絕對路徑</label><div className="folder-address"><input id="repository-path" required maxLength={4096} placeholder="C:\repos\MyProject" value={path} onChange={e=>setPath(e.target.value)}/><button type="button" className="secondary" disabled={action.busy||picking} onClick={()=>setPicking(true)}>選擇資料夾</button></div>
    <label>基底分支<input required value={branch} onChange={e=>setBranch(e.target.value)}/></label><label>dotnet test 參數（JSON 陣列）<textarea required rows={3} value={validation} onChange={e=>setValidation(e.target.value)}/></label><p className="footnote">測試 logger 與結果目錄由後端設定。</p>
    </div><div className="dialog-footer"><button type="button" className="secondary" onClick={onClose}>取消</button><button className="primary" disabled={action.busy}>登錄專案</button></div></form></Modal>
    {picking&&<FolderPicker initialPath={path} onClose={()=>setPicking(false)} onSelected={selected=>{setPath(selected);setPicking(false);}}/>}</>;
}
export function ProjectSettings({project,data,onClose,onSaved}:{project:Project;data:BaseData;onClose:()=>void;onSaved:()=>void}){
  const [planning,setPlanning]=useState<Selection|null>(project.aiDefaults?.planning||{provider:'codex',model:null}),[implementation,setImplementation]=useState<Selection|null>(project.aiDefaults?.implementation||{provider:'codex',model:null});const [version]=useState(project.version),notice=useNotice(),action=useAction(notice);
  return <Modal title={project.name+' · AI 預設'} eyebrow="PROJECT DEFAULTS" wide onClose={onClose}><form onSubmit={e=>{e.preventDefault();void action.run(async()=>{await api('/api/projects/'+project.id+'/ai-defaults','PUT',{expectedVersion:version,aiDefaults:{planning,implementation}});notice('AI 預設已保存，既有任務保留原設定。');onSaved();onClose();});}}><div className="dialog-body"><div className="two-col choice-grid"><AiChoice label="規劃預設" providers={data.providers} value={planning} onChange={setPlanning}/><AiChoice label="實作預設" providers={data.providers} value={implementation} onChange={setImplementation}/></div><p className="footnote">僅影響之後建立的任務。</p></div><div className="dialog-footer"><button type="button" className="secondary" onClick={onClose}>取消</button><button className="primary" disabled={action.busy}>保存預設</button></div></form></Modal>;
}
