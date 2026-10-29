import {afterEach,beforeEach,it,expect,vi} from 'vitest';
import {render,screen,fireEvent,waitFor,within} from '@testing-library/react';
import {Discussions} from '../src/components/Discussions';
import {NoticeProvider} from '../src/components/ui';
import type {BaseData,Discussion} from '../src/types';
const projectId='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',id='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',operationId='cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const data:BaseData={projects:[{id:projectId,name:'Demo',repositoryPath:'C:/demo',baseBranch:'main',validationArguments:['test'],version:0,aiDefaults:null}],tasks:[],providers:[{id:'codex',installed:true,executable:'codex',models:['first-model'],modelsVerified:false},{id:'antigravity',installed:true,executable:'agy',models:['agy-model-low','agy-model-high'],modelsVerified:false,modelDetails:['low','high'].map(effort=>({id:'agy-model-'+effort,displayName:'Agy Model ('+effort[0].toUpperCase()+effort.slice(1)+')',reasoningEfforts:[],defaultReasoningEffort:null,isDefault:false}))}]};
beforeEach(()=>localStorage.clear());afterEach(()=>localStorage.clear());
function fixture(active=false){
  let d:Discussion={id,projectId,title:'原來的對話',selection:{provider:'codex',model:'first-model'},messages:[{id:'old',role:'assistant',content:'之前已經確認的需求',selection:{provider:'codex',model:'first-model'},createdAt:'2026-10-19T01:00:00Z'}],drafts:[],rules:[],version:4,activeOperation:active?operationId:null,error:null,updatedAt:'2026-10-19T01:00:00Z'};
  const calls:{path:string;method:string;body:any}[]=[];
  vi.stubGlobal('fetch',async(input:string,options:RequestInit={})=>{
    const path=new URL(input,location.href).pathname,method=options.method||'GET',body=options.body?JSON.parse(String(options.body)):null;calls.push({path,method,body});
    let result:unknown=d;
    if(path==='/api/discussions'&&method==='GET')result=[d];
    else if(path==='/api/discussions'&&method==='POST'){d={...d,messages:[],version:0,title:'新對話'};result=d;}
    else if(path.endsWith('/messages')){d={...d,version:d.version+1,selection:body.selection,activeOperation:operationId,messages:[...d.messages,{id:'new',role:'user',content:body.message,createdAt:'2026-10-19T02:00:00Z'}]};result=d;}
    else if(path.endsWith('/progress'))result={operationId:d.activeOperation,content:'這是逐步顯示的回答',selection:d.selection};
    else if(path.endsWith('/stop')){d={...d,version:d.version+1,activeOperation:null,error:'已停止回覆'};result=null;}
    else if(path.endsWith('/title')){d={...d,title:body.title,version:d.version+1};result=d;}
    return new Response(JSON.stringify(result),{headers:{'Content-Type':'application/json'}});
  });
  return calls;
}
function view(discussionId?:string){render(<NoticeProvider><Discussions data={data} projectId={projectId} discussionId={discussionId} onTaskCreated={()=>{}}/></NoticeProvider>);}

it('starts from project and first question without a title or rule form',async()=>{
  const calls=fixture();view();
  expect(screen.getByLabelText('專案')).toBeTruthy();expect(screen.queryByLabelText('討論主題')).toBeNull();expect(screen.queryByText('任務草稿')).toBeNull();
  const message=screen.getByLabelText('討論訊息');fireEvent.change(message,{target:{value:'先討論訂單流程'}});
  fireEvent.keyDown(message,{key:'Enter',shiftKey:true});expect(calls.some(c=>c.method==='POST')).toBe(false);
  fireEvent.keyDown(message,{key:'Enter',isComposing:true});expect(calls.some(c=>c.method==='POST')).toBe(false);
  fireEvent.keyDown(message,{key:'Enter'});
  await waitFor(()=>expect(calls.some(c=>c.path.endsWith('/messages'))).toBe(true));
  expect(calls.find(c=>c.path==='/api/discussions'&&c.method==='POST')?.body).toEqual({projectId,selection:{provider:'codex',model:null},agentMode:true});
  expect(calls.find(c=>c.path.endsWith('/messages'))?.body.message).toBe('先討論訂單流程');
});

it('changes provider, model and effort for the next turn while retaining history',async()=>{
  const calls=fixture();view(id);await screen.findByText('之前已經確認的需求');
  fireEvent.click(screen.getByRole('button',{name:/Codex CLI · first-model/}));
  fireEvent.change(screen.getByLabelText('對話 AI'),{target:{value:'antigravity'}});
  fireEvent.change(screen.getByLabelText('對話模型'),{target:{value:'__agy_family:agy-model'}});
  fireEvent.change(screen.getByLabelText('對話推理程度'),{target:{value:'high'}});
  fireEvent.click(screen.getByRole('button',{name:'完成'}));
  fireEvent.change(screen.getByLabelText('討論訊息'),{target:{value:'再用另一個模型分析'}});
  fireEvent.click(screen.getByRole('button',{name:'傳送訊息'}));
  await waitFor(()=>expect(calls.find(c=>c.path.endsWith('/messages'))?.body).toMatchObject({expectedVersion:4,message:'再用另一個模型分析',selection:{provider:'antigravity',model:'agy-model-high',reasoningEffort:null}}));
  expect(screen.getByText('之前已經確認的需求')).toBeTruthy();
  expect(await screen.findByText('這是逐步顯示的回答')).toBeTruthy();
  expect(screen.getByText('Codex CLI · first-model',{selector:'strong'})).toBeTruthy();
});

it('allows choosing the next model during a reply and stops only the active operation',async()=>{
  const calls=fixture(true);view(id);await screen.findByText('之前已經確認的需求');
  fireEvent.click(screen.getByRole('button',{name:/Codex CLI · first-model/}));
  expect((screen.getByLabelText('對話 AI') as HTMLSelectElement).disabled).toBe(false);
  fireEvent.click(screen.getByRole('button',{name:'完成'}));
  fireEvent.click(screen.getByRole('button',{name:/停止回覆/}));
  await waitFor(()=>expect(calls.find(c=>c.path.endsWith('/stop'))?.body).toEqual({operationId}));
  expect(await screen.findByText('已停止回覆')).toBeTruthy();
});

it('renames a conversation inline without opening model or task forms',async()=>{
  const calls=fixture();view(id);fireEvent.click(await screen.findByTitle('重新命名對話'));
  fireEvent.change(screen.getByLabelText('對話名稱'),{target:{value:'訂單架構'}});fireEvent.click(screen.getByRole('button',{name:'儲存'}));
  await waitFor(()=>expect(calls.find(c=>c.path.endsWith('/title'))?.body).toEqual({title:'訂單架構',expectedVersion:4}));
  expect(await screen.findByRole('heading',{name:'訂單架構'})).toBeTruthy();
});

it('filters the conversation list by name',async()=>{
  fixture();view();expect(await screen.findByRole('link',{name:/原來的對話/})).toBeTruthy();
  fireEvent.change(screen.getByRole('searchbox',{name:'搜尋對話'}),{target:{value:'不存在'}});
  expect(screen.queryByRole('link',{name:/原來的對話/})).toBeNull();expect(screen.getByText('找不到符合的對話')).toBeTruthy();
});

it('selects the actual project when base data arrives after the page first renders',async()=>{
  const calls=fixture();
  const page=render(<NoticeProvider><Discussions data={{...data,projects:[]}} projectId="" onTaskCreated={()=>{}}/></NoticeProvider>);
  page.rerender(<NoticeProvider><Discussions data={data} projectId="" onTaskCreated={()=>{}}/></NoticeProvider>);
  fireEvent.change(screen.getByLabelText('討論訊息'),{target:{value:'專案載入後的第一題'}});
  fireEvent.click(screen.getByRole('button',{name:'傳送訊息'}));
  await waitFor(()=>expect(calls.find(c=>c.path==='/api/discussions'&&c.method==='POST')?.body.projectId).toBe(projectId));
});
