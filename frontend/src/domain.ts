import type {Selection,WorkTask,Validation,ArtifactPage,Provider} from './types';
export function selectableModels(provider?:Provider):string[]{
  if(!provider)return [];
  const configured=provider.configuredDefaultModel;
  const version=(id:string)=>{
    const match=/^(?:gpt|gemini)-(\d+(?:[.-]\d+)*)/i.exec(id)||/^(?:claude-)?(?:opus|sonnet|haiku|fable)-(\d+(?:[.-]\d+)*)/i.exec(id);
    return match?match[1].split(/[.-]/).map(Number):null;
  };
  return [...new Set([...provider.models,...(configured?[configured]:[])])].sort((a,b)=>{
    const left=version(a),right=version(b);
    if(!left||!right)return left?-1:right?1:0;
    for(let i=0;i<Math.max(left.length,right.length);i++){
      const difference=(right[i]||0)-(left[i]||0);
      if(difference)return difference;
    }
    return 0;
  });
}
export const providerNames:Record<string,string>={codex:'Codex CLI',antigravity:'Antigravity CLI',gemini:'Gemini CLI（舊任務）',claude:'Claude Code'};
export const statusNames:Record<string,string>={AwaitingStepApproval:'待確認步驟',AwaitingPlan:'待規劃',AwaitingApproval:'待核准計畫',Queued:'實作排隊',Running:'實作與驗收中',AwaitingReview:'待成果審核',Failed:'實作失敗',Cancelled:'已取消',Approved:'成果已核准',Rejected:'已退回',Merging:'合併核對中',Merged:'已合併',PlanningQueued:'規劃排隊',Planning:'規劃中',PlanningFailed:'規劃失敗',AiChangesRequested:'AI 要求修正'};
export const groups:Record<string,string[]>={active:['PlanningQueued','Planning','Queued','Running','Merging'],review:['AwaitingStepApproval','AwaitingApproval','AwaitingReview','Approved'],failed:['Failed','PlanningFailed','Rejected'],done:['Merged','Cancelled']};
export const editablePlan=['AwaitingPlan','AwaitingApproval','PlanningFailed'],cancellable=[...editablePlan,'AwaitingStepApproval','PlanningQueued','Planning','Queued','Running'],phases=['Baseline','Red','Green','Refactor'];
export function tone(status:string){return groups.failed.includes(status)?'danger':groups.review.includes(status)?'amber':status==='Merged'?'success':groups.active.includes(status)?'blue':'neutral';}
export const effortNames:Record<string,string>={none:'停用',minimal:'最少',low:'低',medium:'中',high:'高',xhigh:'極高',max:'最高',ultra:'Ultra'};
export function selectionText(s:Selection|null|undefined){return `${providerNames[s?.provider||'codex']} · ${s?.model||'CLI 預設'}${s?.reasoningEffort?' · 推理 '+(effortNames[s.reasoningEffort]||s.reasoningEffort):''}`;}
export function filterTasks(tasks:WorkTask[],{project='',search='',group='',status=''}={}){const q=search.trim().toLocaleLowerCase();return tasks.filter(t=>(!project||t.projectId===project)&&(!status||t.status===status)&&(!group||groups[group]?.includes(t.status))&&(!q||[t.title,t.description,t.id].some(v=>v.toLocaleLowerCase().includes(q)))).slice().sort((a,b)=>b.createdAt.localeCompare(a.createdAt)||b.id.localeCompare(a.id));}
export function validationState(e?:Validation){if(!e)return{label:'尚未驗證',tone:'neutral'};const valid=e.phase==='Red'?e.exitCode!==0&&e.total>0&&e.failed>0:e.exitCode===0&&e.failed===0&&(e.phase==='Baseline'||e.total>0);return{label:valid?e.phase==='Red'?'失敗證據成立':'驗證通過':'驗證未通過',tone:valid?'success':'danger'};}
export function artifactUrl(id:string,path:string,offset=0){return `/api/runs/${encodeURIComponent(id)}/artifacts/${path.split('/').map(encodeURIComponent).join('/')}?offset=${offset}&limit=32768`;}
export function appendPage(current:Omit<ArtifactPage,'offset'>,page:ArtifactPage){if(page.path!==current.path||page.offset!==current.nextOffset||page.nextOffset<page.offset||page.hasMore&&page.nextOffset<=page.offset)throw new Error('紀錄分頁已改變，請重新讀取檔案。');return{path:current.path,text:current.text+page.text,nextOffset:page.nextOffset,hasMore:page.hasMore};}
export function formatDate(v:string|null|undefined){return v?new Date(v).toLocaleString('zh-TW',{month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',hour12:false}):'—';}
export function formatBytes(n:number){return n<1024?`${n} B`:n<1048576?`${(n/1024).toFixed(1)} KB`:`${(n/1048576).toFixed(1)} MB`;}
export function shortCommit(v:string|null|undefined){return v?v.slice(0,10):'尚未產生';}

