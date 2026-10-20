import {vi,it,expect} from 'vitest';
import {render,screen,fireEvent,waitFor,within} from '@testing-library/react';
import {Discussions} from '../src/components/Discussions';
import {NoticeProvider} from '../src/components/ui';
import {TaskDialog} from '../src/components/TaskDialogs';
import type {BaseData,Discussion} from '../src/types';
const projectId='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',discussionId='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',draftId='cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const base:BaseData={projects:[{id:projectId,name:'Demo',repositoryPath:'C:\\demo',baseBranch:'main',validationArguments:['test'],version:0,aiDefaults:null}],tasks:[],providers:[{id:'codex',installed:true,executable:'codex',models:[],modelsVerified:false}]};
const discussion:Discussion={id:discussionId,projectId,title:'Audit log',selection:{provider:'codex',model:'chosen'},rules:[],messages:[{id:'message',role:'assistant',content:'Confirmed requirement',createdAt:'2026-10-16T10:00:00Z'}],drafts:[{id:draftId,title:'Draft title',description:'Draft description',acceptanceCriteria:'Draft criteria',openQuestions:['Retention?'],sourceVersion:2}],version:4,updatedAt:'2026-10-16T10:00:00Z',activeOperation:null,error:null};
function fixture(){const requests:{path:string;body:any}[]=[];vi.stubGlobal('fetch',async(input:string,options:RequestInit={})=>{const path=new URL(input,location.href).pathname,body=options.body?JSON.parse(String(options.body)):null;requests.push({path,body});let value:any;if(path==='/api/rules')value=[{id:'tdd',name:'TDD',content:'Require TDD',stages:['Implementation'],version:1,requiresTdd:true}];else if(path==='/api/discussions')value=[discussion];else if(path==='/api/discussions/'+discussionId)value=discussion;else if(path==='/api/tasks')value={...body,id:'new-task',status:'AwaitingPlan'};else throw new Error('Unexpected request: '+path);return new Response(JSON.stringify(value),{headers:{'Content-Type':'application/json'}});});return requests;}
it('lets users edit discussion drafts and creates a linked task without automatic execution',async()=>{
  const requests=fixture(),created=vi.fn();render(<NoticeProvider><Discussions data={base} projectId="" discussionId={discussionId} onTaskCreated={created}/></NoticeProvider>);
  fireEvent.click(await screen.findByRole('button',{name:/任務草稿與規則/}));
  fireEvent.click(await screen.findByRole('button',{name:'預覽並建立任務'}));const modal=screen.getByRole('dialog');
  expect(within(modal).queryByLabelText('提交後自動規劃（使用模型額度）')).toBeNull();
  expect(within(modal).getByText('Retention?')).toBeTruthy();
  fireEvent.change(within(modal).getByLabelText('任務名稱'),{target:{value:'Confirmed audit task'}});
  fireEvent.change(within(modal).getByLabelText('問題處理方式 1'),{target:{value:'保留 30 天'}});
  fireEvent.click(within(modal).getByLabelText('我已檢視這些問題，並在任務內容中明確說明處理方式'));
  fireEvent.click(within(modal).getByRole('button',{name:'確認摘要，設定執行流程 →'}));
  expect(requests.some(r=>r.path==='/api/tasks')).toBe(false);
  expect((within(modal).getByLabelText('提交後自動規劃（使用模型額度）') as HTMLInputElement).checked).toBe(false);
  fireEvent.click(within(modal).getByRole('button',{name:'提交任務 →'}));await waitFor(()=>expect(created).toHaveBeenCalledOnce());
  expect(requests.find(r=>r.path==='/api/tasks')?.body).toMatchObject({title:'Confirmed audit task',autoPlan:false,sourceDiscussionId:discussionId,sourceDraftId:draftId,acknowledgeOpenQuestions:true});
  expect(requests.find(r=>r.path==='/api/tasks')?.body.description).toContain('保留 30 天');
  expect(requests.filter(r=>r.path.includes('/messages')||r.path.includes('/drafts'))).toHaveLength(0);
});
it('makes selected TDD rules enforce the execution mode and exposes stage model choices',async()=>{
  fixture();render(<NoticeProvider><TaskDialog data={base} projectId={projectId} onClose={()=>{}} onCreated={()=>{}}/></NoticeProvider>);
  fireEvent.click(await screen.findByLabelText(/TDD v1/));expect((screen.getByLabelText('執行模式') as HTMLSelectElement).disabled).toBe(true);
  fireEvent.change(screen.getByLabelText('流程範本'),{target:{value:'deep'}});
  fireEvent.click(screen.getByRole('button',{name:/02 · 驗收 1/}));expect(screen.getByLabelText('驗收 1模型')).toBeTruthy();
  fireEvent.click(screen.getByRole('button',{name:/03 · 驗收 2/}));expect(screen.getByLabelText('驗收 2模型')).toBeTruthy();
  expect(screen.getByText('人工驗收成果 → 確認後合併')).toBeTruthy();
});
