import {it,expect,vi} from 'vitest';
import {render,screen,act} from '@testing-library/react';
import {LiveActivity,TaskActivity} from '../src/components/LiveActivity';
import type {DetailData,RuntimeSnapshot} from '../src/types';

function snapshot():RuntimeSnapshot{
  const now=new Date().toISOString();
  return {startedAt:now,updatedAt:now,stage:'AI 正在規劃',lastActivityAt:now,lastOutputAt:now,process:'agy.exe',processRunning:true,outputCharacters:420,recent:[{at:now,text:'啟動程序：agy.exe'}]};
}

it('shows real activity and elapsed time, then reports a stale heartbeat without pretending it finished',()=>{
  vi.useFakeTimers();vi.setSystemTime(new Date('2026-10-19T01:00:00Z'));
  const state=snapshot();const view=render(<LiveActivity activity={state}/>);
  expect(screen.getByText(/程序執行中：agy.exe/)).toBeTruthy();
  expect(screen.getByText(/已收到 420 字元/)).toBeTruthy();
  expect(screen.getByText('已執行 0 秒')).toBeTruthy();
  act(()=>vi.advanceTimersByTime(21000));
  expect(screen.getByText('已執行 21 秒')).toBeTruthy();
  expect(screen.getByText(/暫時無法確認是否仍在執行/)).toBeTruthy();
  view.rerender(<LiveActivity activity={{...state,updatedAt:new Date().toISOString()}}/>);
  expect(screen.queryByText(/暫時無法確認/)).toBeNull();
  expect(screen.getByText(/最近輸出 21 秒前/)).toBeTruthy();
});

it('distinguishes quiet processes, permission waiting and an API connection failure',()=>{
  vi.useFakeTimers();vi.setSystemTime(new Date('2026-10-19T01:00:00Z'));
  const state=snapshot();
  state.startedAt='2026-10-19T00:59:00Z';state.lastOutputAt=null;
  const view=render(<LiveActivity activity={state}/>);
  expect(screen.getByText(/目前沒有新輸出，仍在等待結果/)).toBeTruthy();
  view.rerender(<LiveActivity activity={state} waiting/>);
  expect(screen.getByText(/期間不會執行這個工具/)).toBeTruthy();
  view.rerender(<LiveActivity activity={state} error="API 無法連線"/>);
  expect(screen.getByText(/暫時無法確認是否仍在執行/)).toBeTruthy();
});

it('does not display an old run as the current run and explains queued and paused tasks',()=>{
  const data={task:{status:'Running'},runs:[{kind:'Implementation',finishedAt:'2026-10-19T00:00:00Z',activity:snapshot()}]} as DetailData;
  const view=render(<TaskActivity data={data}/>);
  expect(screen.getByText(/等待第一筆執行回報/,{selector:'strong'})).toBeTruthy();
  expect(screen.queryByText(/程序執行中/)).toBeNull();
  view.rerender(<TaskActivity data={{...data,task:{...data.task,status:'Queued'}}}/>);
  expect(screen.getByText('已排隊，等待執行')).toBeTruthy();
  view.rerender(<TaskActivity data={{...data,task:{...data.task,status:'AwaitingStepApproval'}}}/>);
  expect(screen.getByText('等待你的步驟確認')).toBeTruthy();
  view.rerender(<TaskActivity data={{...data,task:{...data.task,status:'Failed'}}}/>);
  expect(screen.queryByLabelText('執行動態')).toBeNull();
});
