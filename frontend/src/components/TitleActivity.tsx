import {useEffect} from 'react';
import './title-activity.css';

export type TitleState={label:string;waiting?:boolean;problem?:boolean};
export function taskTitleState(status?:string):TitleState|null{
  if(status==='Planning')return {label:'規劃中'};
  if(status==='Running')return {label:'執行中'};
  if(status==='Merging')return {label:'合併中'};
  if(status==='Queued'||status==='PlanningQueued')return {label:'排隊中',waiting:true};
  if(status==='AwaitingStepApproval')return {label:'等待確認',waiting:true};
  return null;
}

export function TitleActivity({state,compact=false}:{state:TitleState|null;compact?:boolean}){
  if(!state)return null;
  const kind=state.problem?'problem':state.waiting?'waiting':'running';
  return <span className={'title-activity '+kind+(compact?' compact':'')} role="status" aria-label={state.label} title={state.label}>
    {kind==='running'?<span className="title-spinner" aria-hidden="true"/>:kind==='waiting'?<svg className="title-clock" viewBox="0 0 16 16" fill="none" aria-hidden="true"><circle cx="8" cy="8" r="6"/><path d="M8 4v4l2.5 1.5"/></svg>:<span aria-hidden="true">!</span>}
    {!compact&&<span>{state.label}</span>}
  </span>;
}

export function useActivityPageTitle(title:string|undefined,state:TitleState|null){
  const label=state?.label,waiting=state?.waiting,problem=state?.problem;
  useEffect(()=>{
    if(!title)return;
    const previous=document.title;
    document.title=(label?(problem?'⚠ ':waiting?'◷ ':'⟳ ')+label+' · ':'')+title+' · DevFlow';
    return()=>{document.title=previous;};
  },[title,label,waiting,problem]);
}
