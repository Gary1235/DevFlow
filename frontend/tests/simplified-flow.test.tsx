import {beforeEach,it,expect,vi} from 'vitest';
import {render,screen,fireEvent,waitFor,within} from '@testing-library/react';
import {Discussions} from '../src/components/Discussions';
import {NoticeProvider} from '../src/components/ui';
import type {BaseData,Discussion} from '../src/types';
const projectId='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',id='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const data:BaseData={projects:[{id:projectId,name:'Demo',repositoryPath:'C:/demo',baseBranch:'main',validationArguments:['build'],version:0,aiDefaults:null,validationProfiles:[{id:'front',name:'前端建置',workingDirectory:'frontend',executable:'npm',arguments:['run','build'],kind:'Command',timeoutSeconds:600}]}],tasks:[],providers:[{id:'codex',installed:true,modelsVerified:false,executable:'codex',models:['manager'],modelDetails:[{id:'manager',displayName:'Manager',reasoningEfforts:['low','high'],defaultReasoningEffort:null,isDefault:false}]},{id:'antigravity',installed:true,modelsVerified:false,executable:'agy',models:['implementer']},{id:'claude',installed:true,modelsVerified:false,executable:'claude',models:['reviewer']}]};
beforeEach(()=>localStorage.clear());
function setup(mode:'ready'|'stale'|'questions'|'chat'='ready'){
  const plan={id:'plan',role:'assistant' as const,content:'已整理計畫',selection:{provider:'codex' as const,model:'manager'},createdAt:'2026-10-27T01:00:00Z',proposedPlan:'1. 修改列表樣式。\n2. 執行前後端建置。',executionPlan:{title:'改善列表',description:'改善列表樣式',acceptanceCriteria:'建置通過並人工測試',openQuestions:mode==='questions'?['需要哪個版面？']:[]}};
  const d:Discussion={id,projectId,title:'改善列表',selection:{provider:'codex',model:'manager'},messages:mode==='chat'?[{...plan,id:'chat',executionPlan:undefined,proposedPlan:undefined,content:'我們先討論版面'}]:[plan],drafts:[],rules:[],version:4,activeOperation:null,error:null,updatedAt:'2026-10-27T01:00:00Z'};
  if(mode==='stale')d.messages.push({id:'new',role:'user',content:'再修改需求',createdAt:d.updatedAt});
  const calls:{path:string;method:string;body:any}[]=[],started=vi.fn();
  vi.stubGlobal('fetch',async(input:string,options:RequestInit={})=>{
    const path=new URL(input,location.href).pathname,method=options.method||'GET',body=options.body?JSON.parse(String(options.body)):null;calls.push({path,method,body});
    const result=path.endsWith('/start')?{id:'task',title:'改善列表'}:path==='/api/discussions'?[d]:d;
    return new Response(JSON.stringify(result),{headers:{'Content-Type':'application/json'}});
  });
  render(<NoticeProvider><Discussions data={data} projectId={projectId} discussionId={id} onTaskCreated={started}/></NoticeProvider>);
  return {calls,started};
}
it('reviews the plan then explicitly approves the chosen supervisor, implementer and optional reviewer',async()=>{
  const {calls,started}=setup();
  fireEvent.click(await screen.findByRole('button',{name:'選模型並開始'}));
  const dialog=screen.getByRole('dialog'),q=within(dialog);
  expect(q.getByText('前端建置')).toBeTruthy();
  expect(screen.queryByLabelText('任務名稱')).toBeNull();
  expect(calls.every(c=>c.method==='GET')).toBe(true);
  fireEvent.change(q.getByLabelText('主管模型'),{target:{value:'manager'}});
  fireEvent.change(q.getByLabelText('主管推理程度'),{target:{value:'high'}});
  fireEvent.change(q.getByLabelText('實作 AI'),{target:{value:'antigravity'}});
  fireEvent.change(q.getByLabelText('實作模型'),{target:{value:'implementer'}});
  fireEvent.click(q.getByLabelText('加上驗收 AI（選填）'));
  fireEvent.change(q.getByLabelText('驗收 AI'),{target:{value:'claude'}});
  fireEvent.change(q.getByLabelText('驗收模型'),{target:{value:'reviewer'}});
  fireEvent.click(q.getByRole('button',{name:'核准計畫並開始'}));
  await waitFor(()=>expect(started).toHaveBeenCalledOnce());
  expect(calls.filter(c=>c.method==='POST')).toEqual([{path:'/api/discussions/'+id+'/start',method:'POST',body:{expectedVersion:4,planMessageId:'plan',supervisor:{provider:'codex',model:'manager',reasoningEffort:'high'},implementation:{provider:'antigravity',model:'implementer',reasoningEffort:null},reviewer:{provider:'claude',model:'reviewer',reasoningEffort:null}}}]);
});
it.each(['stale','questions'] as const)('does not start a %s plan',async mode=>{
  const {calls}=setup(mode);expect((await screen.findByRole('button',{name:'選模型並開始'})).hasAttribute('disabled')).toBe(true);
  expect(screen.queryByRole('dialog')).toBeNull();expect(calls.every(c=>c.method==='GET')).toBe(true);
});
it('keeps legacy task forms hidden and uses the selected chat model when collecting the plan',async()=>{
  const {calls}=setup('chat');
  fireEvent.click(await screen.findByRole('button',{name:'整理實作計畫'}));
  await waitFor(()=>expect(calls.some(c=>c.method==='POST')).toBe(true));
  expect(calls.find(c=>c.method==='POST')).toMatchObject({path:'/api/discussions/'+id+'/execution-plan',body:{expectedVersion:4,selection:{provider:'codex',model:'manager'}}});
  expect(screen.queryByText('任務草稿',{selector:'h2'})).toBeNull();expect(screen.queryByLabelText('驗收 AI')).toBeNull();
});


