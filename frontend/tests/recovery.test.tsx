import {it,expect,vi} from 'vitest';
import {render,screen,fireEvent,waitFor} from '@testing-library/react';
import {FailureRecovery} from '../src/components/FailureRecovery';
import {RetryPanel} from '../src/components/RetryPanel';
import {NoticeProvider} from '../src/components/ui';
import {saveWorkflowPreference,readWorkflowPreference} from '../src/workflowPreferences';
import {workflowTemplate} from '../src/components/WorkflowEditor';
import type {WorkTask} from '../src/types';

it('offers appropriate recovery actions without performing them automatically',()=>{
  const change=vi.fn(),records=vi.fn();
  const view=render(<FailureRecovery error="[ModelUnavailable] 模型不可用" onModel={change} onRecords={records}/>);
  expect(screen.getByRole('link',{name:'前往 AI 連線 →'}).getAttribute('href')).toBe('#ai');
  expect(change).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button',{name:'更換模型'}));expect(change).toHaveBeenCalledOnce();
  view.rerender(<FailureRecovery error="Baseline validation failed" onRecords={records}/>);
  expect(screen.getByText('測試或驗證未通過')).toBeTruthy();
  expect(screen.queryByRole('button',{name:'更換模型'})).toBeNull();
  fireEvent.click(screen.getByRole('button',{name:'查看執行紀錄'}));expect(records).toHaveBeenCalledOnce();
});

it('previews the selected retry scope and submits only the exact reviewed task version',async()=>{
  const requests:{method:string;path:string;body:any}[]=[],changed=vi.fn();
  vi.stubGlobal('fetch',async(input:string,options:RequestInit={})=>{
    const method=options.method||'GET',body=options.body?JSON.parse(String(options.body)):null;
    requests.push({method,path:input,body});
    const restart=input.includes('Restart');
    return new Response(JSON.stringify(method==='GET'?{scope:restart?'Restart':'Resume',expectedVersion:7,summary:restart?'全部重做':'接續前端',retainedSteps:restart?[]:['後端 API'],repeatedSteps:restart?['後端 API','前端實作']:['前端實作'],baseCommit:'a'.repeat(40)}:{}));
  });
  const task={id:'task',status:'Failed',version:7} as WorkTask;
  render(<NoticeProvider><RetryPanel task={task} onChanged={changed}/></NoticeProvider>);
  await screen.findByText('接續前端');
  expect(requests.every(r=>r.method==='GET')).toBe(true);
  fireEvent.change(screen.getByLabelText('重試方式'),{target:{value:'Restart'}});
  await screen.findByText('全部重做');
  fireEvent.change(screen.getByLabelText('修正指示'),{target:{value:'從頭修正'}});
  fireEvent.click(screen.getByRole('button',{name:'確認從基底全部重做'}));
  await waitFor(()=>expect(changed).toHaveBeenCalledOnce());
  expect(requests.filter(r=>r.method==='POST')).toEqual([{path:'/api/tasks/task/retry',method:'POST',body:{expectedVersion:7,scope:'Restart',instructions:'從頭修正'}}]);
});

it('remembers per-project workflow models without reusing identities or task-specific instructions',()=>{
  const flow=workflowTemplate('fullstack');
  flow.steps![1].selection={provider:'antigravity',model:'agy-model',reasoningEffort:'high'};
  flow.steps![0].instructions='Do this one task only';flow.steps![0].allowedPaths=['private/'];
  expect(saveWorkflowPreference('project-a',{workflow:flow,planning:{provider:'codex',model:'planner'},implementation:null})).toBe(true);
  const restored=readWorkflowPreference('project-a')!;
  expect(restored.workflow.steps!.map(s=>s.name)).toEqual(flow.steps!.map(s=>s.name));
  expect(restored.workflow.steps![1].selection).toEqual(flow.steps![1].selection);
  expect(restored.workflow.steps![0].id).not.toBe(flow.steps![0].id);
  expect(restored.workflow.steps![0].instructions).toBe('');
  expect(restored.workflow.steps![0].allowedPaths).toEqual([]);
  expect(readWorkflowPreference('project-b')).toBeNull();
  localStorage.setItem('devflow.workflow-preference.project-a','broken json');
  expect(readWorkflowPreference('project-a')).toBeNull();
});
