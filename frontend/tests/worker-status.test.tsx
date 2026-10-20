import {it,expect,vi} from 'vitest';
import {render,screen} from '@testing-library/react';
import {QueuedWorkerStatus,WorkerConnection} from '../src/components/WorkerStatus';

it('explains that offline workers do not consume queued tasks and gives the single startup command',async()=>{
  vi.stubGlobal('fetch',async()=>new Response(JSON.stringify({online:false,state:'Offline',message:'這份資料庫的 Worker 未連線。'})));
  render(<QueuedWorkerStatus/>);
  expect(await screen.findByText('Worker 未連線，任務尚未開始')).toBeTruthy();
  expect(screen.getByText('npm run dev')).toBeTruthy();
});
it('shows an API error as unknown rather than claiming the worker is offline',async()=>{
  vi.stubGlobal('fetch',async()=>new Response(JSON.stringify({title:'連線失敗'}),{status:503}));
  render(<WorkerConnection/>);
  expect(await screen.findByText('Worker 狀態未知')).toBeTruthy();
  expect(screen.queryByText('Worker 未連線')).toBeNull();
});
