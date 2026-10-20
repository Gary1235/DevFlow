import {useEffect,useState} from 'react';
import type {DetailData,RuntimeSnapshot} from '../types';
import './live-activity.css';
import {QueuedWorkerStatus} from './WorkerStatus';

function duration(seconds:number){
  const value=Math.max(0,Math.floor(seconds));
  return value<60?value+' 秒':Math.floor(value/60)+' 分 '+value%60+' 秒';
}

export function LiveActivity({activity,startedAt,waiting,error}:{activity?:RuntimeSnapshot|null;startedAt?:string;waiting?:boolean;error?:string}){
  const [now,setNow]=useState(Date.now());
  const [openedAt]=useState(Date.now());
  useEffect(()=>{const timer=setInterval(()=>setNow(Date.now()),1000);return()=>clearInterval(timer);},[]);
  const age=(value:string)=>Math.max(0,(now-Date.parse(value))/1000);
  const stale=!!error||(activity?age(activity.updatedAt)>20:now-openedAt>20000);
  const quiet=activity?.lastOutputAt?age(activity.lastOutputAt):undefined;
  const label=stale?'執行回報中斷':waiting?'等待你的工具確認':activity?.currentWork||activity?.stage||'等待第一筆執行回報';
  return <section className={'live-activity'+(stale?' stale':'')+(waiting?' waiting':'')} aria-label="執行動態">
    <div className="live-activity-heading"><strong><span className="live-indicator" aria-hidden="true"/>{label}</strong>{(startedAt||activity?.startedAt)&&<span>已執行 {duration(age(startedAt||activity!.startedAt))}</span>}</div>
    {stale?<p role="status">暫時無法確認是否仍在執行，請查看執行器與 API 連線。{error&&<span> {error}</span>}</p>:waiting?<p>正在等待你選擇允許或拒絕；期間不會執行這個工具。</p>:activity?<>
      {activity.currentWork&&<p>目前階段：{activity.stage}</p>}<p>{activity.processRunning?`程序執行中：${activity.process}`:'執行器處理中'} · 最近回報 {duration(age(activity.updatedAt))}前</p>
      <p className="live-output">{quiet===undefined?'尚未收到程序輸出':`最近輸出 ${duration(quiet)}前 · 已收到 ${activity.outputCharacters.toLocaleString()} 字元`}{(quiet??age(activity.startedAt))>=30&&'。目前沒有新輸出，仍在等待結果。'}</p>
    </>:<p>已送出執行請求，等待執行器回報。若持續沒有更新，請確認 API 與 Worker 已重新啟動。</p>}
    {!!activity?.recent.length&&<details><summary>最近活動 · {activity.recent.length} 筆</summary><ol>{activity.recent.map((item,index)=><li key={index}><time>{new Date(item.at).toLocaleTimeString()}</time><span>{item.text}</span></li>)}</ol></details>}
    <span className="sr-only" role="status">{label}</span>
  </section>;
}

export function TaskActivity({data,error}:{data:DetailData;error?:string}){
  const status=data.task.status;
  if(['Queued','PlanningQueued'].includes(status))return <QueuedWorkerStatus/>;
  if(status==='AwaitingStepApproval')return <section className="live-activity waiting" aria-label="執行動態"><strong>等待你的步驟確認</strong><p>此步驟已完成，核准後才會繼續下一步。</p></section>;
  if(!['Running','Planning'].includes(status))return null;
  const run=data.runs.filter(r=>r.kind===(status==='Planning'?'Planning':'Implementation')&&!r.finishedAt).at(-1);
  return <LiveActivity key={run?.id||status} activity={run?.activity} startedAt={run?.startedAt} error={error}/>;
}
