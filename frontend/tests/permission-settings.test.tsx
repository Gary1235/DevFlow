import { expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { PermissionSettings } from '../src/components/PermissionSettings';
import type { Project,WorkTask } from '../src/types';
const projects=[{id:'project',name:'示範專案'}] as Project[];
const tasks=[{id:'task',projectId:'project',title:'測試任務'}] as WorkTask[];
const rule={id:'rule',projectId:'project',taskId:'task',provider:'*',tool:'general_read',kind:'GeneralRead',pattern:'.',enabled:true,version:0};
it('offers task-scoped general reads across all CLI providers in the padded modal',async()=>{
  const fetch=vi.fn(async(_input:RequestInfo|URL,options?:RequestInit)=>new Response(JSON.stringify(options?.method==='POST'?rule:[])));vi.stubGlobal('fetch',fetch);
  render(<PermissionSettings projects={projects} tasks={tasks}/>);
  await screen.findByText('尚無允許規則');fireEvent.click(screen.getByRole('button',{name:'新增允許規則'}));
  const provider=screen.getByLabelText('規則 CLI') as HTMLSelectElement;
  expect(provider.value).toBe('*');expect(provider.closest('.dialog-body')).not.toBeNull();
  expect(Array.from(provider.options).map(o=>o.value)).toEqual(expect.arrayContaining(['*','codex','claude','gemini','antigravity']));
  expect(screen.getByRole('checkbox',{name:'啟用規則'}).closest('label')?.className).toBe('checkbox');
  expect(screen.queryByRole('option',{name:'此專案永久允許'})).toBeNull();
  fireEvent.click(screen.getByRole('button',{name:'儲存規則'}));await screen.findByText('尚無允許規則');
  expect(JSON.parse(String(fetch.mock.calls.find(c=>c[1]?.method==='POST')?.[1]?.body))).toMatchObject({taskId:'task',tool:'general_read',kind:'GeneralRead',pattern:'.',provider:'*'});
});
it('lets the user edit, disable and delete general read rules with their current version',async()=>{
  let saved:typeof rule|null={...rule};
  const fetch=vi.fn(async(_input:RequestInfo|URL,options?:RequestInit)=>{
    if(options?.method==='PUT'){const body=JSON.parse(String(options.body));saved={...saved!,...body,version:saved!.version+1};return new Response(JSON.stringify(saved));}
    if(options?.method==='DELETE'){saved=null;return new Response('{}');}
    return new Response(JSON.stringify(saved?[saved]:[]));
  });vi.stubGlobal('fetch',fetch);
  render(<PermissionSettings projects={projects} tasks={tasks}/>);
  await screen.findByText('一般讀取 · 已啟用');fireEvent.click(screen.getByRole('button',{name:'編輯'}));
  fireEvent.change(screen.getByLabelText('規則 CLI'),{target:{value:'codex'}});fireEvent.click(screen.getByRole('button',{name:'儲存規則'}));
  await screen.findByText(/示範專案 · Codex CLI/);
  expect(JSON.parse(String(fetch.mock.calls.find(c=>c[1]?.method==='PUT')?.[1]?.body))).toMatchObject({provider:'codex',expectedVersion:0});
  fireEvent.click(screen.getByRole('button',{name:'停用'}));await screen.findByText('一般讀取 · 已停用');
  fireEvent.click(screen.getByRole('button',{name:'刪除'}));await screen.findByText('尚無允許規則');
  expect(String(fetch.mock.calls.find(c=>c[1]?.method==='DELETE')?.[0])).toContain('expectedVersion=2');
});
it('marks legacy permanent rules as ineffective and does not offer broad new grants',async()=>{
  vi.stubGlobal('fetch',vi.fn(async()=>new Response(JSON.stringify([{...rule,taskId:null,tool:'run_command',kind:'CommandPrefix',pattern:'git diff'}]))));
  render(<PermissionSettings projects={projects} tasks={tasks}/>);
  await screen.findByText(/舊專案永久規則（已失效）/);
  expect(screen.queryByRole('button',{name:'編輯'})).toBeNull();
  expect(screen.getByText(/舊版規則不再提供批次放行/)).toBeTruthy();
});
it('keeps the modal open on a stale rule edit',async()=>{
  vi.stubGlobal('fetch',vi.fn(async(_input:RequestInfo|URL,options?:RequestInit)=>new Response(JSON.stringify(options?.method==='PUT'?{title:'工具規則已改變'}:[rule]),{status:options?.method==='PUT'?409:200})));
  render(<PermissionSettings projects={projects} tasks={tasks}/>);
  await screen.findByText('一般讀取 · 已啟用');fireEvent.click(screen.getByRole('button',{name:'編輯'}));
  fireEvent.click(screen.getByRole('button',{name:'儲存規則'}));await screen.findByRole('alert');expect(screen.getByLabelText('規則 CLI')).toBeTruthy();
});
