import {useState} from 'react';
import {api} from '../api';
import {useAction} from '../hooks';
import {useNotice} from './ui';
import type {WorkTask} from '../types';
interface Preview {id:string;files:string[];bytes:number;headCommit:string;expiresAt:string}
export function WorkspaceRecovery({task,onChanged}:{task:WorkTask;onChanged:()=>void}){
  const [preview,setPreview]=useState<Preview|null>(null),[stopped,setStopped]=useState(false);
  const notice=useNotice(),action=useAction(notice);
  return <section className="panel" aria-label="恢復失敗工作副本"><h3>恢復失敗工作副本</h3>
    <p>將舊副本中尚未提交的修改複製成獨立修復任務，原副本保留。已完成步驟沿用；恢復步驟會重新修正、驗證與審查。</p>
    <button type="button" disabled={action.busy} onClick={()=>void action.run(async()=>{setPreview(await api('/api/tasks/'+task.id+'/recovery/preview','POST',{}));setStopped(false);})}>預覽可恢復檔案</button>
    {preview&&<div><p>未提交修改：{preview.files.length} 個檔案，{preview.bytes} bytes。忽略檔案不會複製。</p><ul>{preview.files.map(f=><li key={f}>{f}</li>)}</ul>
      <label><input type="checkbox" checked={stopped} onChange={e=>setStopped(e.target.checked)}/>我已確認舊 CLI 與驗證程序停止，且不再修改此副本</label>
      <button type="button" disabled={action.busy||!stopped} onClick={()=>void action.run(async()=>{
        const created=await api<WorkTask>('/api/tasks/'+task.id+'/recovery','POST',{previewId:preview.id,oldProcessStopped:stopped});
        notice('已建立保留舊修改的修復任務。');onChanged();location.hash='#task/'+created.id;
      })}>複製副本並建立修復任務</button>
    </div>}
  </section>;
}
