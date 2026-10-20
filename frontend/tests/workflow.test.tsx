import {useState} from 'react';
import {it,expect,vi} from 'vitest';
import {render,screen,fireEvent,waitFor,within} from '@testing-library/react';
import {WorkflowEditor,workflowTemplate,makeStep} from '../src/components/WorkflowEditor';
import {WorkflowPanel} from '../src/components/WorkflowPanel';
import {NoticeProvider} from '../src/components/ui';
import type {Provider,Workflow,WorkTask} from '../src/types';

const providers:Provider[]=['codex','claude','antigravity'].map(id=>({id:id as Provider['id'],installed:true,executable:id,models:['model-one','model-two'],modelsVerified:true}));
function fixture(initial=workflowTemplate(),lockedCount=0){
  const changes=vi.fn(),requests:{path:string;method:string;body:any}[]=[];let templates:any[]=[];
  vi.stubGlobal('fetch',async(path:string,options:RequestInit={})=>{const method=options.method||'GET',body=options.body?JSON.parse(String(options.body)):null;requests.push({path,method,body});
    if(method==='POST'&&path==='/api/workflow-templates'){templates=[body];return new Response(JSON.stringify(body));}
    return new Response(JSON.stringify(path==='/api/workflow-templates'?templates:providers));
  });
  function Wrapper(){const [flow,setFlow]=useState(initial);return <WorkflowEditor value={flow} providers={providers} onChange={next=>{changes(next);setFlow(next);}} lockedCount={lockedCount} planningLocked={lockedCount>0}/>;}
  render(<NoticeProvider><Wrapper/></NoticeProvider>);return {changes,requests};
}
it('reorders steps while keeping their selected model and edit scope attached',()=>{
  const {changes}=fixture();
  fireEvent.change(screen.getByLabelText('後端 API AI'),{target:{value:'claude'}});
  fireEvent.change(screen.getByLabelText('後端 API模型'),{target:{value:'model-two'}});
  fireEvent.change(screen.getByLabelText('允許修改的路徑（每行一項）'),{target:{value:'backend/'}});
  fireEvent.click(screen.getByRole('button',{name:'下移 後端 API'}));
  const steps:Workflow['steps']=changes.mock.lastCall![0].steps;
  expect(steps!.map(s=>s.name)).toEqual(['前端實作','後端 API','整合測試']);
  expect(steps![1]).toMatchObject({selection:{provider:'claude',model:'model-two'},allowedPaths:['backend/']});
  expect((screen.getByLabelText('後端 API模型') as HTMLSelectElement).value).toBe('model-two');
});
it('offers API-first and frontend-first templates while preserving models for matching roles',()=>{
  const {changes}=fixture();
  fireEvent.change(screen.getByLabelText('後端 API AI'),{target:{value:'claude'}});
  fireEvent.change(screen.getByLabelText('後端 API模型'),{target:{value:'model-two'}});
  fireEvent.change(screen.getByLabelText('流程範本'),{target:{value:'frontend'}});
  expect(changes.mock.lastCall![0].steps.map((s:any)=>s.name)).toEqual(['前端實作','後端 API','整合測試','AI 審查']);
  expect(changes.mock.lastCall![0].steps[1].selection).toMatchObject({provider:'claude',model:'model-two'});
});
it('inserts approval after selected step and permits repeated implementation steps',()=>{
  const {changes}=fixture();
  fireEvent.change(screen.getByLabelText('新增步驟類型'),{target:{value:'Approval'}});
  fireEvent.click(screen.getByRole('button',{name:'＋ 插入步驟'}));
  expect(changes.mock.lastCall![0].steps.map((s:any)=>s.kind)).toEqual(['Implementation','Approval','Implementation','Validation']);
  fireEvent.change(screen.getByLabelText('新增步驟類型'),{target:{value:'Implementation'}});
  fireEvent.click(screen.getByRole('button',{name:'＋ 插入步驟'}));
  expect(changes.mock.lastCall![0].steps.filter((s:any)=>s.kind==='Implementation')).toHaveLength(3);
});
it('locks completed and active steps and prevents pending steps from moving before them',()=>{
  const {changes}=fixture(workflowTemplate(),1);
  expect((screen.getByLabelText('步驟名稱') as HTMLInputElement).closest('fieldset')!.disabled).toBe(true);
  expect((screen.getByRole('button',{name:'刪除 後端 API'}) as HTMLButtonElement).disabled).toBe(true);
  expect((screen.getByRole('button',{name:'上移 前端實作'}) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByRole('button',{name:/02 · 前端實作/}));
  fireEvent.change(screen.getByLabelText('步驟名稱'),{target:{value:'新前端'}});
  expect(changes.mock.lastCall![0].steps[0].name).toBe('後端 API');
  expect(changes.mock.lastCall![0].steps[1].name).toBe('新前端');
});
it('saves templates through the API and gives applied steps new identities',async()=>{
  const initial=workflowTemplate(),{changes,requests}=fixture(initial);
  fireEvent.change(screen.getByLabelText('儲存為我的範本'),{target:{value:'我的 API 流程'}});
  fireEvent.click(screen.getByRole('button',{name:'儲存範本'}));
  await screen.findByText('流程範本已保存；同名範本會更新。');
  await screen.findByRole('option',{name:'我的 API 流程'});
  fireEvent.change(screen.getByLabelText('流程範本'),{target:{value:'saved:0'}});
  expect(requests.find(r=>r.method==='POST')?.body.workflow.steps).toHaveLength(3);
  expect(changes.mock.lastCall![0].steps[0].id).not.toBe(initial.steps![0].id);
  expect(changes.mock.lastCall![0].steps.map((s:any)=>s.name)).toEqual(initial.steps!.map(s=>s.name));
});
it('does not call a model for validation or human approval steps',()=>{
  fixture();fireEvent.click(screen.getByRole('button',{name:/03 · 整合測試/}));
  const settings=screen.getByLabelText('步驟設定');
  expect(within(settings).queryByLabelText(/模型/)).toBeNull();
  expect(within(settings).getByText(/不呼叫模型/)).toBeTruthy();
});
it('requires explicit confirmation before continuing a paused workflow',async()=>{
  const step=makeStep('檢查 API','Approval'),changed=vi.fn(),requests:any[]=[];
  vi.stubGlobal('fetch',async(path:string,options:RequestInit={})=>{requests.push({path,options});return new Response('{}');});
  const task:WorkTask={id:'task',projectId:'project',title:'API',description:'Build',acceptanceCriteria:'Tests',plan:null,ai:null,createdAt:'2026-10-19T00:00:00Z',status:'AwaitingStepApproval',version:8,workflow:{planningEnabled:false,reviewers:[],maxReviewRetries:0,steps:[step]},stepResults:[{stepId:step.id,name:step.name,kind:step.kind,state:'Completed',runId:'run',summary:null,selection:null,startedAt:'2026-10-19T00:00:00Z',finishedAt:null,resultCommit:'a'.repeat(40)}]};
  render(<NoticeProvider><WorkflowPanel task={task} onChanged={changed}/></NoticeProvider>);
  expect(requests).toHaveLength(0);
  fireEvent.click(screen.getByRole('button',{name:'確認並繼續流程'}));
  await waitFor(()=>expect(changed).toHaveBeenCalledOnce());
  expect(requests[0].path).toBe('/api/tasks/task/workflow/continue');
  expect(JSON.parse(requests[0].options.body)).toEqual({expectedVersion:8});
});
