import { useCallback } from 'react';
import { api } from '../api';
import { useResource } from '../hooks';
import { ToolPermissions } from './ToolPermissions';
import type { ToolPermission } from './ToolPermissions';

export function TaskPermissions({ taskId, runId, active }: { taskId: string; runId?: string; active: boolean }) {
  const load = useCallback((signal: AbortSignal) =>
    api<ToolPermission[]>('/api/tasks/' + taskId + '/permissions', 'GET', undefined, { signal }), [taskId]);
  const resource = useResource(taskId + ':' + runId, load, 1000, active && !!runId);
  const requests = (resource.data || []).filter(r => r.operationId === runId);
  return <>
    {resource.error && <div className="error-banner" role="alert">無法讀取工具確認：{resource.error}<button className="secondary" onClick={resource.refresh}>重試</button></div>}
    {requests.length > 0 && <ToolPermissions taskId={taskId} operationId={runId!} requests={requests} onAnswered={resource.refresh} />}
  </>;
}
