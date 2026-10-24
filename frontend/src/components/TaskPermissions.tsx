import { useCallback, useState } from 'react';
import { api } from '../api';
import { useResource } from '../hooks';
import { ToolPermissions } from './ToolPermissions';
import type { ToolPermission } from './ToolPermissions';

export function TaskPermissions({ taskId, runId, active }: { taskId: string; runId?: string; active: boolean }) {
  const load = useCallback((signal: AbortSignal) =>
    api<ToolPermission[]>('/api/tasks/' + taskId + '/permissions', 'GET', undefined, { signal }), [taskId]);
  const key=taskId+':'+runId;
  const resource = useResource(key, load, active?1000:0, !!runId);
  const [history,setHistory]=useState<{key:string;items:ToolPermission[];before:number|null}>({key:'',items:[],before:null});
  const [loading,setLoading]=useState(false),[error,setError]=useState('');
  const older=history.key===key?history.items:[];
  const requests = [...new Map([...older,...(resource.data||[])].map(r=>[r.id,r])).values()].filter(r => r.operationId === runId);
  async function more(){
    setLoading(true);setError('');
    try{
      const before=history.key===key?history.before:null;
      const page=await api<{items:ToolPermission[];nextBefore:number|null}>('/api/tasks/'+taskId+'/permissions/history?runId='+runId+'&limit=200'+(before?'&before='+before:''));
      setHistory({key,items:[...older,...page.items],before:page.nextBefore});
    }catch(e){setError(e instanceof Error?e.message:'無法載入歷史');}finally{setLoading(false);}
  }
  return <>
    {resource.error && <div className="error-banner" role="alert">無法讀取工具確認：{resource.error}<button className="secondary" onClick={resource.refresh}>重試</button></div>}
    {requests.length > 0 && <ToolPermissions taskId={taskId} operationId={runId!} requests={requests} onAnswered={resource.refresh} />}
    {runId&&(history.key!==key||history.before!==null)&&<button className="secondary" type="button" disabled={loading} onClick={()=>void more()}>載入較舊的工具權限紀錄</button>}
    {error&&<p role="alert">{error}</p>}
  </>;
}
