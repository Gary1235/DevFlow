import {it,expect,vi} from 'vitest';
import {fireEvent,render,screen,waitFor} from '@testing-library/react';
import {AiConnections} from '../src/components/AiConnections';
import {NoticeProvider} from '../src/components/ui';
import type {Diagnostic,Provider} from '../src/types';
const provider:Provider={id:'antigravity',installed:true,executable:'agy.exe',models:[],modelsVerified:false};
const verified:Diagnostic={version:'1.0',loginState:'VerifiedByExecution',executionState:'Ready',message:'最近一次呼叫成功',lastVerifiedAt:'2026-10-21T03:00:00Z',verifiedModel:'tested-model',verificationSource:'Task'};
function view(){return render(<NoticeProvider><AiConnections providers={[provider]}/></NoticeProvider>);}
it('shows shared execution evidence, time and model without automatically spending quota',async()=>{
  const fetch=vi.fn(async(_path:RequestInfo|URL)=>new Response(JSON.stringify(verified)));vi.stubGlobal('fetch',fetch);view();
  const status=await screen.findByLabelText('Antigravity CLI登入狀態');
  await waitFor(()=>expect(status.textContent).toBe('最近驗證可用'));
  expect(status.classList.contains('success')).toBe(true);
  expect(screen.getByText(/最近成功驗證/).textContent).toContain('tested-model');
  expect(document.querySelector('time')?.dateTime).toBe(verified.lastVerifiedAt);
  expect(screen.queryByRole('button',{name:'開啟官方登入'})).toBeNull();
  expect(fetch.mock.calls.every(call=>String(call[0]).endsWith('/diagnostics'))).toBe(true);
});
it('allows explicit verification after an earlier auth failure and updates the badge after a successful probe',async()=>{
  let saved:Diagnostic={version:'1.0',loginState:'LoggedOut',executionState:'Unchecked',message:'先前驗證失敗'};
  const fetch=vi.fn(async(path:RequestInfo|URL)=>{
    if(String(path).endsWith('/probe'))saved={...verified,verificationSource:'Probe'};
    return new Response(JSON.stringify(saved));
  });vi.stubGlobal('fetch',fetch);view();
  await screen.findByText('先前驗證失敗');
  const probe=screen.getByRole('button',{name:'連線測試 · 使用額度'});
  expect((probe as HTMLButtonElement).disabled).toBe(false);fireEvent.click(probe);
  await waitFor(()=>expect(screen.getByLabelText('Antigravity CLI登入狀態').textContent).toBe('最近驗證可用'));
  expect(fetch.mock.calls.filter(call=>String(call[0]).endsWith('/probe'))).toHaveLength(1);
});
it('does not let an old failed login terminal hide a later successful model call',async()=>{
  const id='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';sessionStorage.setItem('devflow.login.antigravity',id);
  vi.stubGlobal('fetch',vi.fn(async(path:RequestInfo|URL)=>new Response(JSON.stringify(String(path).includes('/logins/')?{id,provider:'antigravity',state:'Failed',startedAt:'2026-10-21T02:00:00Z',message:'old login terminal failure'}:verified))));
  view();await waitFor(()=>expect(screen.getByLabelText('Antigravity CLI登入狀態').textContent).toBe('最近驗證可用'));
  expect(screen.queryByText(/old login terminal failure/)).toBeNull();
  expect(screen.getByText('最近一次呼叫成功')).toBeTruthy();
});
it('clears the original login instructions as well as its ended session when later execution succeeds',async()=>{
  const id='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';let started=false;
  vi.stubGlobal('fetch',vi.fn(async(path:RequestInfo|URL)=>{
    let data:unknown=started?verified:{loginState:'Unknown',message:'尚未驗證'};
    if(String(path).endsWith('/login')){started=true;data={id,state:'WaitingForUser',message:'old login instructions'};}
    else if(String(path).includes('/logins/'))data={id,state:'Failed',startedAt:'2026-10-21T02:00:00Z',message:'old terminal failure'};
    return new Response(JSON.stringify(data));
  }));view();fireEvent.click(await screen.findByRole('button',{name:'開啟官方登入'}));
  await waitFor(()=>expect(screen.getByLabelText('Antigravity CLI登入狀態').textContent).toBe('最近驗證可用'));
  expect(screen.queryByText(/old login instructions|old terminal failure/)).toBeNull();
});
