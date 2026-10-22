import {expect,it,vi} from 'vitest';
import {fireEvent,render,screen,waitFor} from '@testing-library/react';
import {AutomationPolicy} from '../src/components/AutomationPolicy';
import {ToolPermissions} from '../src/components/ToolPermissions';

const policy={version:3,automationEnabled:true,sensitivePaths:['private/**'],sensitiveCommands:[],sensitiveDestinations:[],approvedDownloadHosts:['registry.npmjs.org']};
it('saves sensitive lists with the current version and retains drafts on conflict',async()=>{
  const fetch=vi.fn(async(_url:RequestInfo|URL,options?:RequestInit)=>new Response(JSON.stringify(options?.method==='PUT'?{title:'權限政策已改變'}:policy),{status:options?.method==='PUT'?409:200}));
  vi.stubGlobal('fetch',fetch);render(<AutomationPolicy/>);
  const input=await screen.findByLabelText(/連線資源敏感路徑/);
  fireEvent.change(input,{target:{value:'private/**\n data/** '}});
  fireEvent.click(screen.getByRole('checkbox'));
  fireEvent.click(screen.getByRole('button',{name:'保存自動化政策'}));
  await screen.findByRole('alert');
  const put=fetch.mock.calls.find(call=>call[1]?.method==='PUT')!;
  expect(JSON.parse(String(put[1]?.body))).toMatchObject({expectedVersion:3,automationEnabled:false,sensitivePaths:['private/**','data/**']});
  expect(put[1]?.headers).toMatchObject({'X-DevFlow-Action':'1'});
  expect((input as HTMLTextAreaElement).value).toBe('private/**\n data/** ');
});
it('displays the saved policy version without silently reverting edits or refetching',async()=>{
  const fetch=vi.fn(async(_url:RequestInfo|URL,options?:RequestInit)=>new Response(JSON.stringify(options?.method==='PUT'?{...policy,version:4,sensitivePaths:['data/**']}:policy)));
  vi.stubGlobal('fetch',fetch);render(<AutomationPolicy/>);
  fireEvent.change(await screen.findByLabelText(/連線資源敏感路徑/),{target:{value:'data/**'}});
  fireEvent.click(screen.getByRole('button',{name:'保存自動化政策'}));
  await waitFor(()=>expect(screen.getByText(/政策版本 v4/)).toBeTruthy());
  expect(fetch).toHaveBeenCalledTimes(2);
});
it('shows automatic writes as policy decisions with no manual approval buttons',()=>{
  render(<ToolPermissions taskId="task" operationId="run" onAnswered={()=>{}} requests={[{id:'auto',operationId:'run',tool:'write_file',arguments:{Path:'src/app.ts'},workspace:'D:/task',createdAt:'2026-10-21T00:00:00Z',state:'AllowedByPolicy',policyVersion:3,risk:{category:'Write',label:'自動允許：一般修改',reason:'未命中敏感名單',batchAllowed:false,autoAllowed:true}}]}/>);
  expect(screen.getByText('依自動化政策放行')).toBeTruthy();
  expect(screen.getByLabelText('工具權限確認').classList.contains('risk-general')).toBe(true);
  expect(screen.queryByRole('button',{name:'允許這一次'})).toBeNull();
  expect(screen.getByText('權限政策版本 v3')).toBeTruthy();
});
