import {useCallback} from 'react';
import {api} from '../api';
import {useResource} from '../hooks';

interface Presence {online:boolean;state:string;message:string;lastSeenAt:string|null;checkedAt:string}
function useWorkerStatus(){
  return useResource('worker-presence',useCallback((signal:AbortSignal)=>api<Presence>('/api/worker/status','GET',undefined,{signal}),[]),5000);
}
export function WorkerConnection(){
  const status=useWorkerStatus(),known=!!status.data&&!status.error,online=known&&status.data!.online;
  return <span className={'connection'+(known&&!online?' error':'')} title={status.error||status.data?.message||'正在檢查同一份資料庫的背景執行器'}><i/>{!known?'Worker 狀態未知':online?'Worker 已連線':'Worker 未連線'}</span>;
}
export function QueuedWorkerStatus(){
  const status=useWorkerStatus();
  const offline=!!status.data&&!status.data.online&&!status.error;
  return <section className="live-activity waiting" aria-label="執行動態"><strong>{offline?'Worker 未連線，任務尚未開始':'已排隊，等待執行'}</strong><p>{status.error?'暫時無法確認 Worker 狀態，請檢查 API 連線。':status.data?.message||'正在確認背景執行器是否已啟動…'}</p>{offline&&<p>在 DevFlow 專案根目錄執行 <code>npm run dev</code>，會一起啟動網頁、API 與 Worker；原有任務不需重新建立。</p>}</section>;
}
