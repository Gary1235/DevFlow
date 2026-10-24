import {render,screen,fireEvent,waitFor} from '@testing-library/react';
import {it,expect,vi} from 'vitest';
import {ProjectDiagnostics} from '../src/components/ProjectDiagnostics';
import {WorkspaceRecovery} from '../src/components/WorkspaceRecovery';
import {TaskPermissions} from '../src/components/TaskPermissions';
import {NoticeProvider} from '../src/components/ui';
import type {WorkTask,ValidationProfile} from '../src/types';
const profile:ValidationProfile={id:'build',name:'前端建置',workingDirectory:'web',executable:'npm',arguments:['run','build'],kind:'Command',timeoutSeconds:600};

it('project discovery only adds targets when explicitly selected',async()=>{
  const add=vi.fn();
  vi.stubGlobal('fetch',vi.fn().mockResolvedValue(new Response(JSON.stringify({validations:[profile],previews:[],warnings:[]}))));
  render(<ProjectDiagnostics projectId="project" version={2} profiles={[]} onValidation={add} onPreview={vi.fn()}/>);
  expect(add).not.toHaveBeenCalled();
  fireEvent.click(screen.getByText('偵測專案建置與預覽目標'));
  fireEvent.click(await screen.findByText('加入驗證設定'));
  expect(add).toHaveBeenCalledWith(expect.objectContaining({executable:'npm',arguments:['run','build'],workingDirectory:'web'}));
});
it('preflight checks the current form settings and displays failures without starting a test',async()=>{
  const fetch=vi.fn().mockResolvedValue(new Response(JSON.stringify({ready:false,checks:[{name:'lockfile',success:false,detail:'npm ci 需要 lockfile'}]})));
  vi.stubGlobal('fetch',fetch);
  render(<ProjectDiagnostics projectId="project" version={2} profiles={[profile]} onValidation={vi.fn()} onPreview={vi.fn()}/>);
  fireEvent.change(screen.getByLabelText('要檢查的驗證設定'),{target:{value:'build'}});
  fireEvent.click(screen.getByText('檢查路徑與工具版本'));
  expect(await screen.findByText(/npm ci 需要 lockfile/)).toBeTruthy();
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(fetch.mock.calls[0][0]).toBe('/api/projects/project/execution-preflight');
  expect(JSON.parse(String((fetch.mock.calls[0][1] as RequestInit).body))).toMatchObject({expectedVersion:2,profile});
});
it('recovering a worktree requires an explicit preview and confirmation that the old process stopped',async()=>{
  const requests:any[]=[];
  vi.stubGlobal('fetch',async(path:string,options:RequestInit={})=>{
    requests.push({path,body:JSON.parse(String(options.body))});
    return new Response(JSON.stringify(path.endsWith('/preview')?{id:'preview',files:['feature.cs'],bytes:12,headCommit:'head'}:{id:'recovered'}));
  });
  render(<NoticeProvider><WorkspaceRecovery task={{id:'task'} as WorkTask} onChanged={vi.fn()}/></NoticeProvider>);
  fireEvent.click(screen.getByText('預覽可恢復檔案'));
  const recover=await screen.findByText('複製副本並建立修復任務');
  expect((recover as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByRole('checkbox'));
  fireEvent.click(recover);
  await waitFor(()=>expect(location.hash).toBe('#task/recovered'));
  expect(requests[1].body).toEqual({previewId:'preview',oldProcessStopped:true});
});
it('loads older permission records separately from the active polling response',async()=>{
  vi.stubGlobal('fetch',async(path:string)=>new Response(JSON.stringify(path.includes('/history')?{items:[{id:'old',operationId:'run',tool:'read_file',provider:'codex',workspace:'/work',arguments:{Path:'source.cs'},createdAt:new Date().toISOString(),state:'AllowedByPolicy'}],nextBefore:null}:[])));
  render(<TaskPermissions taskId="task" runId="run" active={false}/>);
  fireEvent.click(screen.getByText('載入較舊的工具權限紀錄'));
  expect(await screen.findByText('工具確認 · 待確認 0 · 已處理 1')).toBeTruthy();
  expect(screen.queryByText('載入較舊的工具權限紀錄')).toBeNull();
});
