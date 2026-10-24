import {render,screen,fireEvent,waitFor} from '@testing-library/react';
import {it,expect,vi} from 'vitest';
import {ProjectDiagnostics} from '../src/components/ProjectDiagnostics';

const props={projectId:'project',version:2,profiles:[],onValidation:vi.fn(),onPreview:vi.fn()};
it('reopens an interrupted diagnostic from durable history and shows the retained workspace',async()=>{
  const probe={id:'old',state:'Interrupted',profileName:'Build',projectVersion:1,createdAt:'2026-10-24T01:00:00Z',retainedWorkspace:'C:/worktrees/old',error:'API interrupted; inspect the old process.'};
  const fetch=vi.fn().mockResolvedValue(new Response(JSON.stringify([probe])));vi.stubGlobal('fetch',fetch);
  render(<ProjectDiagnostics {...props}/>);
  fireEvent.click(screen.getByText('載入最近設定測試'));
  fireEvent.click(await screen.findByText('檢視結果'));
  expect(screen.getByText('設定測試異常中斷')).toBeTruthy();
  expect(screen.getByText(/C:\/worktrees\/old/)).toBeTruthy();
  expect(fetch).toHaveBeenCalledTimes(1); // Viewing old evidence never restarts validation.
});
it('reattaches to an active test and explicitly cancels it while waiting for final cleanup',async()=>{
  const running={id:'active',state:'Running',profileName:'Build',projectVersion:2};let stopped=false;
  const fetch=vi.fn(async(path:string)=>new Response(JSON.stringify(path.endsWith('/execution-probes')?[running]:path.endsWith('/cancel')?(stopped=true,{...running,state:'Stopping'}):{...running,state:stopped?'Cancelled':'Running'})));
  vi.stubGlobal('fetch',fetch);
  render(<ProjectDiagnostics {...props}/>);
  fireEvent.click(screen.getByText('載入最近設定測試'));
  fireEvent.click(await screen.findByText('追蹤進度'));
  fireEvent.click(await screen.findByText('取消設定測試'));
  await screen.findByText('設定測試已取消');
  await waitFor(()=>expect(fetch).toHaveBeenCalledWith('/api/execution-probes/active/cancel',expect.objectContaining({method:'POST',headers:expect.objectContaining({'X-DevFlow-Action':'1'})})));
  expect(screen.queryByText('取消設定測試')).toBeNull();
});
