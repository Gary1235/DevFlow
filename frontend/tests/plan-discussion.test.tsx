import {it, expect, vi} from 'vitest';
import {render, screen, fireEvent, waitFor} from '@testing-library/react';
import {PlanDiscussion} from '../src/components/PlanDiscussion';
import {NoticeProvider} from '../src/components/ui';
import type {Discussion, WorkTask} from '../src/types';

const task:WorkTask = {id:'task',projectId:'project',title:'訂單列表',description:'修改樣式',acceptanceCriteria:'建置成功',
  status:'AwaitingApproval',plan:{text:'原始計畫',revision:2,baseCommit:'123456789abcdef'},version:7,ai:{planning:{provider:'antigravity',model:'planner'},implementation:null},createdAt:'2026-10-22'};
const proposal = {id:'proposal',role:'assistant',content:'加入響應式驗證',proposedPlan:'新版完整計畫',createdAt:'2026-10-22',
  planContext:{taskVersion:7,revision:2,text:'原始計畫',baseCommit:'123456789abcdef',requirements:'修改樣式',acceptanceCriteria:'建置成功'}};

function fixture(messages:Discussion['messages'] = [], active=false) {
  let d:Discussion={id:'task',projectId:'project',title:'計畫討論',selection:task.ai!.planning!,rules:[],messages,drafts:[],version:4,
    updatedAt:'2026-10-22',activeOperation:active?'operation':null,error:null};
  const calls:{path:string;body:any;method:string}[]=[];
  vi.stubGlobal('fetch', async(input:string, options:RequestInit={})=>{
    const path=new URL(input,location.href).pathname, body=options.body?JSON.parse(String(options.body)):null,method=options.method || 'GET';
    calls.push({path,body,method});
    let result:unknown=d;
    if (path.endsWith('/messages') || path.endsWith('/plan-proposal')) {d={...d,version:5,activeOperation:'operation'};result=d;}
    if (path.endsWith('/progress')) result={operationId:'operation',content:'搜尋中的內容',permissions:[],activity:null};
    if (path.endsWith('/stop')) {d={...d,activeOperation:null,version:6};result=null;}
    if (path==='/api/ai/providers') result=[{id:'codex',installed:true,models:['new-model'],modelsVerified:true}, {id:'antigravity',installed:true,models:['planner'],modelsVerified:true}];
    return new Response(JSON.stringify(result), {headers:{'Content-Type':'application/json'}});
  });
  return calls;
}
function view(options:{dirty?:boolean;task?:WorkTask;onApplied?:()=>void}={}) {
  return render(<NoticeProvider><PlanDiscussion task={options.task || task} plan={task.plan} dirty={options.dirty || false} onApplied={options.onApplied || vi.fn()} onClose={vi.fn()}/></NoticeProvider>);
}

it('opens existing task discussion without running AI and sends the selected saved revision',async()=>{
  const calls=fixture();view();
  await screen.findByLabelText('計畫討論訊息');
  expect(calls.some(c=>c.path.endsWith('/messages'))).toBe(false);
  fireEvent.change(screen.getByLabelText('計畫討論訊息'), {target:{value:'好，解釋一下風險'}});
  fireEvent.click(screen.getByRole('button',{name:'傳送'}));
  await waitFor(()=>expect(calls.find(c=>c.path.endsWith('/messages'))?.body).toEqual({expectedVersion:4,message:'好，解釋一下風險',selection:task.ai!.planning,planRevision:2}));
  expect(calls.some(c=>c.path.includes('approve') || c.path.endsWith('/apply'))).toBe(false);
  expect(await screen.findByText('搜尋中的內容')).toBeTruthy();
});

it('allows switching the planning discussion model without changing task execution settings',async()=>{
  const calls=fixture();view();
  fireEvent.click(await screen.findByRole('button',{name:/Antigravity CLI · planner/}));
  await screen.findByRole('option',{name:'Codex CLI'});
  fireEvent.change(screen.getByLabelText('計畫討論 AI'),{target:{value:'codex'}});
  fireEvent.change(screen.getByLabelText('計畫討論模型'),{target:{value:'new-model'}});
  fireEvent.click(screen.getByRole('button',{name:'完成'}));
  fireEvent.click(screen.getByRole('button',{name:'產生修訂草案'}));
  await waitFor(()=>expect(calls.find(c=>c.path.endsWith('/plan-proposal'))?.body.selection).toMatchObject({provider:'codex',model:'new-model'}));
  expect(calls.some(c=>c.method==='PUT' || c.path.endsWith('/apply'))).toBe(false);
});

it('previews both versions and only applies a proposal after the explicit action',async()=>{
  const calls=fixture([proposal]);const applied=vi.fn();view({onApplied:applied});
  await screen.findByText('加入響應式驗證');
  expect(calls.some(c=>c.path.endsWith('/apply'))).toBe(false);
  fireEvent.click(screen.getByText('預覽完整修訂計畫'));
  expect(screen.getByText('新版完整計畫')).toBeTruthy();
  expect(screen.getByText('原始計畫')).toBeTruthy();
  fireEvent.click(screen.getByRole('button',{name:'套用為版本 3'}));
  await waitFor(()=>expect(applied).toHaveBeenCalledOnce());
  expect(calls.find(c=>c.path.endsWith('/apply'))?.body).toEqual({messageId:'proposal',expectedVersion:7});
  expect(calls.some(c=>c.path.includes('approve'))).toBe(false);
});

it.each([
  {dirty:true,task},
  {dirty:false,task:{...task,version:8}},
  {dirty:false,task:{...task,status:'Running'}},
])('prevents applying while dirty, stale or running: %j',async options=>{
  fixture([proposal]);view(options);
  const button=await screen.findByRole('button',{name:'套用為版本 3'});
  expect((button as HTMLButtonElement).disabled).toBe(true);
});

it('lets users stop the same CLI operation from the sidebar',async()=>{
  const calls=fixture([],true);view();
  fireEvent.click(await screen.findByRole('button',{name:'停止回覆'}));
  await waitFor(()=>expect(calls.find(c=>c.path.endsWith('/stop'))?.body).toEqual({operationId:'operation'}));
});
