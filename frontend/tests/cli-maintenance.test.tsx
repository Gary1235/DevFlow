import {it,expect,vi} from 'vitest';
import {render,screen,fireEvent,waitFor} from '@testing-library/react';
import {AiConnections} from '../src/components/AiConnections';
import {NoticeProvider} from '../src/components/ui';
import type {Provider} from '../src/types';
const provider:Provider={id:'codex',installed:true,executable:'C:/npm/codex.exe',models:['gpt-6.1-sol'],modelsVerified:false};
const check={id:'check-id',provider:'codex',executable:'C:/npm/codex.exe',installedVersion:'0.155.1',latestVersion:'0.159.2',updateAvailable:true,canUpdate:true,message:'有更新',manualCommand:'npm install -g @openai/codex@latest'};
it('checks versions only on request, requires confirmation, then polls and refreshes models without a probe',async()=>{
  const requests:{path:string;method:string;body:any}[]=[];
  vi.stubGlobal('fetch',async(path:string,options:RequestInit={})=>{
    requests.push({path,method:options.method||'GET',body:options.body?JSON.parse(String(options.body)):null});
    const data=path.endsWith('/cli/check')?check:path.endsWith('/cli/update')?{id:'update-id',state:'Updating',provider:'codex',targetVersion:'0.159.2',message:'正在安裝'}:path.includes('/cli/updates/')?{id:'update-id',state:'Completed',provider:'codex',targetVersion:'0.159.2',installedVersion:'0.159.2',message:'更新完成'}:path.includes('/providers')?[provider]:{loginState:'LoggedIn',executionState:'Unchecked',message:'已登入'};
    return new Response(JSON.stringify(data));
  });
  render(<NoticeProvider><AiConnections providers={[provider]}/></NoticeProvider>);
  await screen.findByText('已登入',{selector:'.provider-status'});
  expect(requests.some(r=>r.path.includes('/cli/'))).toBe(false);
  fireEvent.click(screen.getByRole('button',{name:'檢查 CLI 更新'}));
  await screen.findByText('0.155.1');await screen.findByText('0.159.2');
  fireEvent.click(screen.getByRole('button',{name:'更新 CLI'}));
  expect(screen.getByRole('dialog')).toBeTruthy();
  expect(requests.some(r=>r.path.endsWith('/cli/update'))).toBe(false);
  fireEvent.click(screen.getByRole('button',{name:'確認更新'}));
  await screen.findByText('更新完成');
  await waitFor(()=>expect(requests.some(r=>r.path==='/api/ai/providers?refresh=true')).toBe(true));
  expect(requests.filter(r=>r.path.endsWith('/cli/update'))).toHaveLength(1);
  expect(requests.find(r=>r.path.endsWith('/cli/update'))?.body).toEqual({checkId:'check-id'});
  expect(requests.some(r=>r.path.endsWith('/probe'))).toBe(false);
});
it('keeps the version check and shows busy rejection without retrying installation',async()=>{
  const requests:string[]=[];
  vi.stubGlobal('fetch',async(path:string)=>{requests.push(path);return new Response(JSON.stringify(path.endsWith('/cli/check')?check:path.endsWith('/cli/update')?{title:'仍有任務執行中'}:{loginState:'LoggedIn',message:'已登入'}),{status:path.endsWith('/cli/update')?409:200});});
  render(<NoticeProvider><AiConnections providers={[provider]}/></NoticeProvider>);
  fireEvent.click(screen.getByRole('button',{name:'檢查 CLI 更新'}));await screen.findByText('0.159.2');
  fireEvent.click(screen.getByRole('button',{name:'更新 CLI'}));fireEvent.click(screen.getByRole('button',{name:'確認更新'}));
  await screen.findByText('仍有任務執行中');expect(requests.filter(p=>p.endsWith('/cli/update'))).toHaveLength(1);
});
