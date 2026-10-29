import {it,expect,vi} from 'vitest';
import {render,screen,fireEvent,waitFor} from '@testing-library/react';
import {TaskDelivery} from '../src/components/TaskDelivery';
import {NoticeProvider} from '../src/components/ui';
import type {Project,Run,WorkTask} from '../src/types';

function setup(oldPreview=false,mergeError=false){
  const calls:{path:string;method:string;body:any}[]=[],changed=vi.fn();
  const task={id:'task',projectId:'project',title:'功能',status:'AwaitingReview',version:7} as WorkTask;
  const run={id:'latest-run',resultCommit:'b'.repeat(40),finishedAt:'2026-10-29T00:00:00Z'} as Run;
  const project={id:'project',baseBranch:'develop',version:2,previewServices:[{id:'front',name:'前端'}]} as Project;
  vi.stubGlobal('fetch',async(input:string,options:RequestInit={})=>{
    const path=new URL(input,location.href).pathname,method=options.method||'GET',body=options.body?JSON.parse(String(options.body)):null;calls.push({path,method,body});
    if(path.endsWith('/accept-and-merge')&&mergeError){task.status='Approved';return new Response(JSON.stringify({title:'Target branch diverged'}),{status:409,headers:{'Content-Type':'application/json'}});}
    const result=path.endsWith('/preview')?{state:oldPreview?'Running':'Stopped',runId:oldPreview?'old-run':null,resultCommit:oldPreview?'c'.repeat(40):null,services:[],log:''}:path.endsWith('/git-state')?{branch:'develop',headCommit:'a'.repeat(40),clean:true}:path.endsWith('/feedback')||path.endsWith('/conversation')?{id:'conversation'}:path.endsWith('/integrate')?{id:'task'}:{state:'Succeeded'};
    return new Response(JSON.stringify(result),{headers:{'Content-Type':'application/json'}});
  });
  render(<NoticeProvider><TaskDelivery task={task} run={run} project={project} onChanged={changed}/></NoticeProvider>);return {calls,changed};
}
it('accepts and merges the displayed result in one action with an exact target snapshot',async()=>{
  const {calls,changed}=setup();fireEvent.click(screen.getByRole('button',{name:'驗收通過並合併至 develop'}));
  await waitFor(()=>expect(changed).toHaveBeenCalled());
  const writes=calls.filter(c=>c.method==='POST');expect(writes).toHaveLength(1);
  expect(writes[0]).toMatchObject({path:'/api/tasks/task/accept-and-merge',body:{expectedVersion:7,runId:'latest-run',resultCommit:'b'.repeat(40),expectedTargetCommit:'a'.repeat(40)}});
});
it('continues integration in the same task after a refused merge',async()=>{
  const {calls}=setup(false,true);fireEvent.click(screen.getByRole('button',{name:'驗收通過並合併至 develop'}));
  fireEvent.click(await screen.findByRole('button',{name:'整合最新分支並重新驗證'}));
  await waitFor(()=>expect(location.hash).toBe('#discussion/conversation'));
  expect(calls.find(c=>c.path.endsWith('/integrate'))).toMatchObject({path:'/api/tasks/task/integrate',body:{continueCurrent:true,runId:'latest-run',resultCommit:'b'.repeat(40)}});
  expect(calls.some(c=>c.path==='/api/tasks/task/conversation')).toBe(true);
});
it('feedback from an older preview preserves the version actually tested',async()=>{
  const {calls}=setup(true);fireEvent.click(await screen.findByRole('button',{name:'回報目前預覽問題'}));
  await waitFor(()=>expect(location.hash).toBe('#discussion/conversation'));
  expect(calls.find(c=>c.method==='POST')).toMatchObject({path:'/api/tasks/task/feedback',body:{expectedVersion:7,runId:'old-run',resultCommit:'c'.repeat(40)}});
});
