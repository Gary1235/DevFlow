import type {Selection,Workflow} from './types';

interface Preference {workflow:Workflow;planning:Selection|null;implementation:Selection|null}
const key=(project:string)=>'devflow.workflow-preference.'+project;
export function readWorkflowPreference(project:string):Preference|null{
  try{
    const value=JSON.parse(localStorage.getItem(key(project))||'null');
    if(!value||!Array.isArray(value.workflow?.steps)||value.workflow.steps.length<1||value.workflow.steps.length>20)return null;
    if(value.workflow.steps.some((s:any)=>!['Implementation','Review','Validation','Approval'].includes(s.kind)||typeof s.name!=='string'||!s.name.trim()||s.name.length>100||s.selection&&!validSelection(s.selection)))return null;
    if(value.planning&&!validSelection(value.planning)||value.implementation&&!validSelection(value.implementation))return null;
    return {...value,workflow:{...value.workflow,steps:value.workflow.steps.map((s:any)=>({...s,id:crypto.randomUUID()}))}};
  }catch{return null;}
}
function validSelection(s:any){return ['codex','antigravity','gemini','claude'].includes(s.provider)&&(s.model==null||typeof s.model==='string')&&(s.reasoningEffort==null||typeof s.reasoningEffort==='string');}
export function saveWorkflowPreference(project:string,value:Preference):boolean{
  try{
    // Keep reusable settings only; task-specific instructions, criteria and edit scopes stay in the task.
    const workflow={...value.workflow,steps:value.workflow.steps?.map(s=>({...s,instructions:'',acceptanceCriteria:'',allowedPaths:[]}))};
    localStorage.setItem(key(project),JSON.stringify({...value,workflow}));return true;
  }catch{return false;}
}
