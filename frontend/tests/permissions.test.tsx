import {it,expect,vi} from 'vitest';
import {render,screen,fireEvent,waitFor} from '@testing-library/react';
import {ToolPermissions} from '../src/components/ToolPermissions';
import {TaskPermissions} from '../src/components/TaskPermissions';
import type {ToolPermission} from '../src/components/ToolPermissions';
import {permissionArguments} from '../src/permissionPresentation';
const generalRisk={category:'GeneralRead',label:'一般讀取：程式碼／文件',reason:'讀取專案程式碼。',batchAllowed:true};
it('redacts credential values without changing the request arguments',()=>{
  const args={password:'very-secret',nested:{apiKey:'another-secret'},CommandLine:'sqlcmd Password="hidden value"; --token abc123',Path:'D:/work/source.ts'};
  const display=JSON.stringify(permissionArguments(args));
  for(const value of ['very-secret','another-secret','hidden value','abc123'])expect(display).not.toContain(value);
  expect(display).toContain('D:/work/source.ts');expect(args.password).toBe('very-secret');
});
const request:ToolPermission={id:'request',operationId:'operation',tool:'run_command',arguments:{CommandLine:'Get-ChildItem -Directory',Cwd:'D:/worktree'},workspace:'D:/worktree',createdAt:'2026-10-19T10:00:00Z',state:'Pending'};
it.each(['codex','claude','gemini','antigravity'])('uses the same discussion rule controls for %s',async provider=>{
  const fetch=vi.fn(async(_input:RequestInfo|URL,_options?:RequestInit)=>new Response('{}'));vi.stubGlobal('fetch',fetch);
  render(<ToolPermissions discussionId="discussion" operationId="operation" requests={[{...request,provider,risk:generalRisk,rulesAvailable:true,ruleOptions:[{kind:'GeneralRead',pattern:'.',label:'一般讀取'}]}]} onAnswered={()=>{}}/>);
  fireEvent.click(screen.getByRole('button',{name:'本次對話允許一般讀取'}));
  await screen.findByText('本次對話已允許');
  expect(JSON.parse(String(fetch.mock.calls[0][1]?.body)).rule).toEqual({scope:'Discussion',provider:'*',kind:'GeneralRead',pattern:'.'});
});
it('shows the exact request and never replies until the user chooses',async()=>{
  const fetch=vi.fn(async(_input:RequestInfo|URL,_options?:RequestInit)=>new Response('{}'));vi.stubGlobal('fetch',fetch);const refresh=vi.fn();
  render(<ToolPermissions discussionId="discussion" operationId="operation" requests={[request]} onAnswered={refresh}/>);
  expect(screen.getByLabelText('工具完整參數').textContent).toContain('Get-ChildItem -Directory');
  expect(fetch).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button',{name:'允許這一次'}));
  await waitFor(()=>expect(refresh).toHaveBeenCalledOnce());
  expect(fetch.mock.calls[0][0]).toBe('/api/discussions/discussion/permissions/request');
  expect(JSON.parse(String((fetch.mock.calls[0][1] as RequestInit).body))).toEqual({operationId:'operation',allow:true});
  expect(screen.queryByRole('button',{name:'允許這一次'})).toBeNull();expect(screen.getByText('已允許這一次')).toBeTruthy();
});
it('can reject and does not expose expired or other-operation requests as actionable',async()=>{
  const fetch=vi.fn(async(_input:RequestInfo|URL,_options?:RequestInit)=>new Response('{}'));vi.stubGlobal('fetch',fetch);
  render(<ToolPermissions discussionId="d" operationId="operation" requests={[request,{...request,id:'old',state:'Expired'},{...request,id:'other',operationId:'different'}]} onAnswered={()=>{}}/>);
  expect(screen.getAllByLabelText('工具權限確認')).toHaveLength(2);
  fireEvent.click(screen.getByRole('button',{name:'拒絕'}));
  await screen.findByText('已拒絕');
  expect(JSON.parse(String((fetch.mock.calls[0][1] as RequestInit).body))).toEqual({operationId:'operation',allow:false});
});
it('keeps a failed reply observable without automatically retrying approval',async()=>{
  const fetch=vi.fn(async(_input:RequestInfo|URL,_options?:RequestInit)=>new Response('{"title":"確認已失效"}',{status:409}));vi.stubGlobal('fetch',fetch);
  render(<ToolPermissions discussionId="d" operationId="operation" requests={[request]} onAnswered={()=>{}}/>);
  fireEvent.click(screen.getByRole('button',{name:'允許這一次'}));
  expect(await screen.findByRole('alert')).toBeTruthy();expect(fetch).toHaveBeenCalledOnce();
});
it('shows a planning permission immediately above task content and posts to the task API',async()=>{
  const fetch=vi.fn(async(_input:RequestInfo|URL,options?:RequestInit)=>new Response(JSON.stringify(options?.method==='POST'?{}:[{...request,expiresAt:'2026-10-20T10:02:00Z'}])));
  vi.stubGlobal('fetch',fetch);
  render(<TaskPermissions taskId="task" runId="operation" active />);
  await screen.findByRole('button',{name:'允許這一次'});
  expect(screen.getByText(/最多等待 2 分鐘/)).toBeTruthy();
  expect(fetch.mock.calls.every(call=>call[1]?.method==='GET')).toBe(true);
  fireEvent.click(screen.getByRole('button',{name:'允許這一次'}));
  await screen.findByText('已允許這一次');
  const post=fetch.mock.calls.find(call=>call[1]?.method==='POST')!;
  expect(post[0]).toBe('/api/tasks/task/permissions/request');
  expect(JSON.parse(String(post[1]?.body))).toEqual({operationId:'operation',allow:true});
});
it('puts pending requests before collapsed history inside one scrollable region',()=>{
  const history=Array.from({length:50},(_,i)=>({...request,id:'history-'+i,state:'Allowed' as const}));
  render(<ToolPermissions taskId="task" operationId="operation" requests={[...history,request]} onAnswered={()=>{}}/>);
  const region=screen.getByRole('region',{name:'工具確認與紀錄'});
  expect(region.classList.contains('tool-permissions')).toBe(true);
  expect(region.tabIndex).toBe(0);
  expect(screen.getAllByLabelText('工具權限確認')[0].textContent).toContain('需要你的確認');
  const records=region.querySelectorAll('details');
  expect(records).toHaveLength(50);
  expect(Array.from(records).every(d=>!d.open)).toBe(true);
  expect(screen.getAllByRole('button',{name:'允許這一次'})).toHaveLength(1);
  fireEvent.click(records[0].querySelector('summary')!);
  expect(region.textContent).toContain('Get-ChildItem -Directory');
});
it('allows all general reads in the current task without offering permanent approval',async()=>{
  const fetch=vi.fn(async(_input:RequestInfo|URL,_options?:RequestInit)=>new Response('{}'));vi.stubGlobal('fetch',fetch);
  render(<ToolPermissions taskId="task" operationId="operation" requests={[{...request,tool:'read_file',arguments:{Path:'D:/worktree/source.ts'},risk:generalRisk,ruleOptions:[{kind:'GeneralRead',pattern:'.',label:'一般讀取'}]}]} onAnswered={()=>{}}/>);
  expect(screen.getByText('✓ 一般讀取：程式碼／文件')).toBeTruthy();
  expect(screen.queryByRole('button',{name:'永遠允許'})).toBeNull();
  fireEvent.click(screen.getByRole('button',{name:'本次任務允許一般讀取'}));
  await screen.findByText('本次任務已允許');
  expect(JSON.parse(String(fetch.mock.calls[0][1]?.body))).toEqual({operationId:'operation',allow:true,rule:{scope:'Task',provider:'*',kind:'GeneralRead',pattern:'.'}});
});
it.each(['SensitiveRead','Write','Deletion','Unknown'])('only offers a single decision for %s even when the payload contains legacy rule options',async category=>{
  const fetch=vi.fn(async(_input:RequestInfo|URL,_options?:RequestInit)=>new Response('{}'));vi.stubGlobal('fetch',fetch);
  render(<ToolPermissions taskId="task" operationId="operation" requests={[{...request,risk:{category,label:'敏感操作',reason:'需要單次確認',batchAllowed:false},ruleOptions:[{kind:'Exact',pattern:'{}',label:'舊選項'}]}]} onAnswered={()=>{}}/>);
  expect(screen.getByText('需要單次確認')).toBeTruthy();
  expect(screen.queryByRole('button',{name:/本次任務允許/})).toBeNull();
  expect(screen.queryByLabelText('允許規則適用 CLI')).toBeNull();
  expect(screen.queryByRole('button',{name:'永遠允許'})).toBeNull();
  const card=screen.getByLabelText('工具權限確認');
  expect(card.classList.contains(category==='Deletion'?'risk-danger':'risk-sensitive')).toBe(true);
  fireEvent.click(screen.getByRole('button',{name:'允許這一次'}));
  await screen.findByText('已允許這一次');
  expect(JSON.parse(String(fetch.mock.calls[0][1]?.body))).toEqual({operationId:'operation',allow:true});
});
