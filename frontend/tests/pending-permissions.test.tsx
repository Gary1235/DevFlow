// @vitest-environment jsdom
import {render,screen,waitFor,fireEvent} from '@testing-library/react';
import {describe,it,expect,vi} from 'vitest';
import {PendingPermissions} from '../src/components/PendingPermissions';

describe('global pending permissions',()=>{
  const request={id:'r1',ownerId:'t1',kind:'task',title:'後端任務',provider:'codex',tool:'run_command',createdAt:new Date(Date.now()-180000).toISOString(),reminderAt:new Date().toISOString(),reasonCode:'UnapprovedExecution',reason:'此指令尚未核准'};
  it('shows pending work without opening its task and refreshes on returning to the page',async()=>{
    const fetch=vi.fn().mockResolvedValue(new Response(JSON.stringify([request])));
    vi.stubGlobal('fetch',fetch);
    document.title='DevFlow';
    render(<PendingPermissions/>);
    expect((await screen.findByText('後端任務')).getAttribute('href')).toBe('#task/t1');
    expect(screen.getByText(/此指令尚未核准/)).toBeTruthy();
    await waitFor(()=>expect(document.title).toContain('待核准 1'));
    fetch.mockResolvedValue(new Response('[]'));
    fireEvent(document,new Event('visibilitychange'));
    await waitFor(()=>expect(screen.getByText('工具待確認 0')).toBeTruthy());
    expect(document.title).not.toContain('待核准');
  });
  it('notifies existing pending requests after explicit opt-in and deduplicates refreshes',async()=>{
    const notifications=vi.fn();
    Object.assign(notifications,{permission:'granted',requestPermission:vi.fn().mockResolvedValue('granted')});
    vi.stubGlobal('Notification',notifications);
    vi.stubGlobal('fetch',vi.fn().mockImplementation(()=>Promise.resolve(new Response(JSON.stringify([request])))));
    render(<PendingPermissions/>);
    await screen.findByText('後端任務');
    expect(notifications).not.toHaveBeenCalled();
    fireEvent.click(screen.getByText('啟用核准通知'));
    await waitFor(()=>expect(notifications).toHaveBeenCalledTimes(1));
    fireEvent(document,new Event('visibilitychange'));
    await waitFor(()=>expect(screen.getByText('工具待確認 1')).toBeTruthy());
    expect(notifications).toHaveBeenCalledTimes(1);
  });
});
