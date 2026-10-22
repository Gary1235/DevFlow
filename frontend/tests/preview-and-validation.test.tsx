import {it,expect,vi} from 'vitest';
import {render,screen,fireEvent,waitFor} from '@testing-library/react';
import {NoticeProvider} from '../src/components/ui';
import {TaskPreview} from '../src/components/TaskPreview';
import {ProjectSettings} from '../src/components/TaskDialogs';
import {WorkflowEditor,workflowTemplate} from '../src/components/WorkflowEditor';
import {validationState} from '../src/domain';
import {recoveryAdvice} from '../src/recovery';
import type {Project,Run,WorkTask,ValidationProfile} from '../src/types';

const profiles:ValidationProfile[]=[{id:'web',name:'前端建置',workingDirectory:'frontend',executable:'npm',arguments:['run','build'],kind:'Command',timeoutSeconds:600}];
const project:Project={id:'project',name:'Demo',repositoryPath:'D:/repo',baseBranch:'main',validationArguments:['test'],aiDefaults:null,version:2,validationProfiles:profiles,defaultValidationProfileId:'web',previewServices:[{id:'web',name:'網站',workingDirectory:'frontend',executable:'npm',arguments:['run','dev','--','--port','{port}'],port:0}]};
const task:WorkTask={id:'task',projectId:project.id,title:'Test',description:'Build',acceptanceCriteria:'Pass',status:'AwaitingReview',plan:null,ai:null,version:8,createdAt:'2026-10-21'};
const run:Run={id:'run',taskId:task.id,kind:'Implementation',status:'AwaitingReview',worktreePath:'D:/worktree',baseCommit:'base',resultCommit:'result',error:null,startedAt:'2026-10-21',finishedAt:'2026-10-21',selection:null};

it('selects step validation independently of its model',()=>{
  vi.stubGlobal('fetch',async()=>new Response('[]'));
  const onChange=vi.fn();
  render(<NoticeProvider><WorkflowEditor value={workflowTemplate()} onChange={onChange} providers={[]} validationProfiles={profiles}/></NoticeProvider>);
  fireEvent.change(screen.getByLabelText('此步驟的驗證設定'),{target:{value:'web'}});
  expect(onChange.mock.lastCall![0].steps[0]).toMatchObject({validationProfileId:'web',selection:{provider:'codex'}});
});
it('saves project validation and preview configuration atomically without launching anything',async()=>{
  const requests:any[]=[],onSaved=vi.fn();
  vi.stubGlobal('fetch',async(path:string,options:RequestInit={})=>{requests.push({path,method:options.method,body:options.body?JSON.parse(String(options.body)):null});return new Response('{}');});
  render(<NoticeProvider><ProjectSettings project={project} data={{projects:[project],tasks:[],providers:[]}} onClose={vi.fn()} onSaved={onSaved}/></NoticeProvider>);
  fireEvent.click(screen.getByRole('button',{name:'保存專案設定'}));
  await waitFor(()=>expect(onSaved).toHaveBeenCalledOnce());
  expect(requests).toHaveLength(1);
  expect(requests[0]).toMatchObject({path:'/api/projects/project/execution-settings',method:'PUT',body:{expectedVersion:2,defaultValidationProfileId:'web',validationProfiles:profiles,previewServices:project.previewServices}});
});
it('starts the selected exact result, opens only ready services, and stops via explicit actions',async()=>{
  let current:any={taskId:'task',runId:null,resultCommit:null,state:'Stopped',services:[],log:'',error:null};const requests:any[]=[];
  vi.stubGlobal('fetch',async(path:string,options:RequestInit={})=>{
    requests.push({path,method:options.method||'GET',body:options.body?JSON.parse(String(options.body)):null});
    if(path.endsWith('/start'))current={...current,runId:'run',resultCommit:'result',state:'Running',services:[{id:'web',name:'網站',state:'Running',url:'http://127.0.0.1:53124',exitCode:null}],log:'ready'};
    if(path.endsWith('/stop'))current={...current,state:'Stopped',services:[]};
    return new Response(JSON.stringify(current));
  });
  render(<NoticeProvider><TaskPreview task={task} run={run} project={project}/></NoticeProvider>);
  expect(screen.queryByRole('link',{name:/開啟網站/})).toBeNull();
  fireEvent.click(screen.getByRole('button',{name:'啟動預覽'}));
  expect((await screen.findByRole('link',{name:/開啟網站/})).getAttribute('href')).toBe('http://127.0.0.1:53124');
  expect(requests.find(r=>r.path.endsWith('/start')).body).toEqual({runId:'run',resultCommit:'result',expectedVersion:8,projectVersion:2});
  fireEvent.click(screen.getByRole('button',{name:'停止預覽'}));
  await waitFor(()=>expect(screen.queryByRole('link',{name:/開啟網站/})).toBeNull());
  expect(requests.some(r=>r.method==='POST'&&r.path.endsWith('/stop'))).toBe(true);
});
it('explains missing configuration and does not allow previewing an active run',()=>{
  vi.stubGlobal('fetch',async()=>new Response(JSON.stringify({state:'Stopped',services:[]})));
  render(<NoticeProvider><TaskPreview task={{...task,status:'Running'}} run={{...run,finishedAt:null}} project={{...project,previewServices:[]}}/></NoticeProvider>);
  expect((screen.getByRole('button',{name:'啟動預覽'}) as HTMLButtonElement).disabled).toBe(true);
  expect(screen.getByRole('link',{name:/專案 → 專案設定/})).toBeTruthy();
});
it('does not present a successful build as passing function tests',()=>{
  expect(validationState({phase:'Implementation',exitCode:0,total:0,failed:0,evidencePath:'x',kind:'Command'}).label).toContain('非逐項測試');
});
it('points validation setup and baseline errors to the correct configuration',()=>{
  expect(recoveryAdvice('基準驗證未通過：MSBUILD MSB1003').kind).toBe('validation');
  expect(recoveryAdvice('找不到驗證設定「web」').kind).toBe('validation-config');
  expect(recoveryAdvice('TDD 需要 DotnetTest').next).toContain('專案設定');
});
