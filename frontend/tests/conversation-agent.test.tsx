import {it,expect,vi} from 'vitest';
import {render,screen,fireEvent,waitFor} from '@testing-library/react';
import {Discussions} from '../src/components/Discussions';
import {NoticeProvider} from '../src/components/ui';
import {ConversationTask} from '../src/components/ConversationTask';
import type {BaseData,Discussion} from '../src/types';
const projectId='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',id='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',taskId='cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const data:BaseData={projects:[{id:projectId,name:'Demo',repositoryPath:'C:/demo',baseBranch:'main',validationArguments:['build'],version:0,aiDefaults:null}],tasks:[],providers:[{id:'codex',installed:true,modelsVerified:false,executable:'codex',models:['manager']},{id:'claude',installed:true,modelsVerified:false,executable:'claude',models:['strong']}]};
function setup(paused=false){
  let d:Discussion={id,projectId,title:'需求對話',agentMode:true,activeTaskId:paused?taskId:null,selection:{provider:'codex',model:'manager'},messages:[{id:'reply',role:'assistant',content:'需求已釐清，可以開始。',createdAt:'2026-10-27T01:00:00Z',...(!paused?{proposedPlan:'修改樣式並執行建置',executionPlan:{title:'改善畫面',description:'修改樣式',acceptanceCriteria:'建置通過',openQuestions:[]},agentProposal:{assignments:[{name:'改善畫面',instructions:'修改樣式',selection:{provider:'claude',model:'strong'},reason:'適合畫面調整'}]}}:{})}],drafts:[],rules:[],version:2,activeOperation:null,error:null,updatedAt:'2026-10-27T01:00:00Z'};
  const calls:{path:string;method:string;body:any}[]=[],created=vi.fn();
  vi.stubGlobal('fetch',async(input:string,options:RequestInit={})=>{
    const path=new URL(input,location.href).pathname,method=options.method||'GET',body=options.body?JSON.parse(String(options.body)):null;calls.push({path,method,body});let result:any=d;
    if(path==='/api/discussions')result=method==='POST'?{...d,id:'fresh-discussion',messages:[],version:0,activeTaskId:null}:[d];
    else if(path.endsWith('/agent')){d={...d,selection:body.selection,version:d.version+1};result=d;}
    else if(path.endsWith('/messages')){d={...d,version:d.version+2,messages:[...d.messages,{id:'user-'+d.version,role:'user',content:body.message,createdAt:d.updatedAt}]};result=d;}
    else if(path==='/api/tasks/'+taskId)result={id:taskId,title:'改善畫面',status:'AwaitingStepApproval',version:4,stepResults:[{summary:'是否保留舊 API？',requiresRework:true}],workflow:{steps:[{}]}};
    else if(path.endsWith('/runs')||path.endsWith('/permissions'))result=[];
    else if(path.endsWith('/preview'))result={state:'Stopped',services:[],log:''};
    return new Response(JSON.stringify(result),{headers:{'Content-Type':'application/json'}});
  });
  render(<NoticeProvider><Discussions data={data} projectId={projectId} discussionId={id} onTaskCreated={created}/></NoticeProvider>);return {calls,created};
}
it('approves a displayed plan beside chat without a start button in the header or model forms',async()=>{
  const {calls,created}=setup();fireEvent.click(await screen.findByRole('button',{name:'Proceed · 確認計畫'}));
  await waitFor(()=>expect(calls.some(c=>c.method==='POST')).toBe(true));
  expect(calls.find(c=>c.method==='POST')).toMatchObject({path:'/api/discussions/'+id+'/messages',body:{message:'開始實作',intent:'Start',selection:{provider:'codex',model:'manager'}}});
  expect(screen.queryByRole('dialog')).toBeNull();expect(screen.queryByRole('button',{name:'開始實作'})).toBeNull();expect(screen.queryByRole('button',{name:'整理實作計畫'})).toBeNull();expect(created).not.toHaveBeenCalled();
});
it('supports approval in chat and prevents approving an older plan after a followup',async()=>{
  const {calls}=setup();await screen.findByRole('list',{name:'預計模型流程'});expect(screen.getAllByText('Claude Code · strong').length).toBeGreaterThan(0);
  fireEvent.change(screen.getByLabelText('討論訊息'),{target:{value:'好，照這份計畫做'}});
  expect((screen.getByRole('button',{name:'Proceed · 確認計畫'}) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByRole('button',{name:'傳送訊息'}));
  await waitFor(()=>expect(calls.some(c=>c.method==='POST')).toBe(true));expect(calls.find(c=>c.method==='POST')?.body).toMatchObject({message:'好，照這份計畫做'});expect(calls.find(c=>c.method==='POST')?.body.intent).toBeUndefined();
  await waitFor(()=>expect((screen.getByRole('button',{name:'Proceed · 確認計畫'}) as HTMLButtonElement).disabled).toBe(true));
});
it('shows the current delegated model, active work and waiting work in the conversation',async()=>{
  vi.stubGlobal('fetch',async(input:string)=>{
    const path=new URL(input,location.href).pathname;
    const result=path.endsWith('/runs')?[{id:'run',finishedAt:null,selection:{provider:'codex',model:'manager'},activity:null}]:path.endsWith('/permissions')?[]:{id:taskId,title:'調整畫面',status:'Running',activeStepId:'front',ai:{planning:{provider:'codex',model:'manager'}},workflow:{steps:[{id:'front',name:'畫面實作',kind:'Implementation',selection:{provider:'claude',model:'strong'}},{id:'back',name:'API實作',kind:'Implementation',selection:{provider:'codex',model:'small'}}]},stepResults:[]};
    return new Response(JSON.stringify(result),{headers:{'Content-Type':'application/json'}});
  });
  render(<NoticeProvider><ConversationTask taskId={taskId} onResume={()=>{}}/></NoticeProvider>);
  await screen.findByRole('list',{name:'實作模型流程'});
  expect(screen.getByText('Claude Code · strong').closest('li')?.getAttribute('aria-current')).toBe('step');
  expect(screen.getByText('Codex CLI · small').closest('li')?.getAttribute('data-state')).toBe('pending');
  expect(screen.getByText('主 AI · 規劃與協調',{exact:false})).toBeTruthy();
});
it('allows asking about a paused question and requires a separate explicit continuation',async()=>{
  const {calls}=setup(true);await screen.findByText('是否保留舊 API？');
  fireEvent.change(screen.getByLabelText('討論訊息'),{target:{value:'什麼意思？會影響哪裡？'}});fireEvent.click(screen.getByRole('button',{name:'傳送訊息'}));
  await waitFor(()=>expect(calls.filter(c=>c.method==='POST').length).toBe(1));expect(calls.find(c=>c.method==='POST')?.body.intent).toBeUndefined();
  await waitFor(()=>expect((screen.getByLabelText('討論訊息') as HTMLTextAreaElement).value).toBe(''));
  fireEvent.click(screen.getByRole('button',{name:'依討論繼續'}));
  await waitFor(()=>expect(calls.filter(c=>c.method==='POST').length).toBe(2));expect(calls.filter(c=>c.method==='POST')[1].body.intent).toBe('Resume');
  expect(calls.some(c=>c.path.endsWith('/continue-workflow'))).toBe(false);
});
it('persists the chosen conversation model as primary after completing the model picker',async()=>{
  const {calls}=setup();fireEvent.click(await screen.findByRole('button',{name:/Codex CLI · manager/}));
  fireEvent.change(screen.getByLabelText('對話 AI'),{target:{value:'claude'}});fireEvent.change(screen.getByLabelText('對話模型'),{target:{value:'strong'}});fireEvent.click(screen.getByRole('button',{name:'完成'}));
  await waitFor(()=>expect(calls.some(c=>c.method==='PUT')).toBe(true));expect(calls.find(c=>c.method==='PUT')).toMatchObject({path:'/api/discussions/'+id+'/agent',body:{selection:{provider:'claude',model:'strong'}}});
});
it('an explicit new-task request sends the draft to a new conversation instead of the bound task',async()=>{
  const {calls}=setup(true);await screen.findByText('接續任務：改善畫面');
  fireEvent.change(screen.getByLabelText('討論訊息'),{target:{value:'另開任務：增加匯出功能'}});fireEvent.click(screen.getByRole('button',{name:'傳送訊息'}));
  await waitFor(()=>expect(location.hash).toBe('#discussion/fresh-discussion'));
  const posts=calls.filter(c=>c.method==='POST');expect(posts).toHaveLength(2);expect(posts[0]).toMatchObject({path:'/api/discussions',body:{projectId,agentMode:true}});
  expect(posts[1]).toMatchObject({path:'/api/discussions/fresh-discussion/messages',body:{message:'另開任務：增加匯出功能',expectedVersion:0}});
  expect(posts.some(c=>c.path==='/api/discussions/'+id+'/messages')).toBe(false);
});
