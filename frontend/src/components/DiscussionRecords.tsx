import {useCallback,useState} from 'react';
import {api} from '../api';
import {useResource} from '../hooks';
import {Modal} from './ui';
import type {Artifact,ArtifactPage} from '../types';

export function DiscussionRecords({id,onClose}:{id:string;onClose:()=>void}){
  const root='/api/discussions/'+id+'/artifacts';
  const files=useResource(root,useCallback((signal:AbortSignal)=>api<Artifact[]>(root,'GET',undefined,{signal}),[root]),0);
  const [path,setPath]=useState(''),[offset,setOffset]=useState(0);
  const page=useResource(root+path+offset,useCallback((signal:AbortSignal)=>api<ArtifactPage>(root+'/'+path.split('/').map(encodeURIComponent).join('/')+'?offset='+offset,'GET',undefined,{signal}),[root,path,offset]),0,!!path);
  return <Modal title="最近一次討論的執行紀錄" eyebrow="DISCUSSION RECORDS" wide onClose={onClose}><div className="dialog-body"><p>這裡包含 CLI 診斷與工具確認紀錄。切換檔案不會重送訊息或呼叫模型。</p>{files.error&&<p className="error-banner">{files.error}</p>}<label>紀錄檔案<select value={path} onChange={e=>{setPath(e.target.value);setOffset(0);}}><option value="">選擇檔案</option>{files.data?.map(file=><option key={file.path} value={file.path}>{file.path}</option>)}</select></label>{files.data&&!files.data.length&&<p>尚無紀錄檔案；執行中或中斷前尚未保存的輸出可能不在此處。</p>}{page.error&&<p className="error-banner">{page.error}</p>}<pre className="code-view">{page.data?.text||(!path?'選擇檔案以查看內容。':'讀取中…')}</pre><div className="button-row">{offset>0&&<button type="button" className="secondary" onClick={()=>setOffset(0)}>回到第一頁</button>}{page.data?.hasMore&&<button type="button" className="secondary" onClick={()=>setOffset(page.data!.nextOffset)}>讀取下一頁</button>}</div></div><div className="dialog-footer"><button type="button" className="secondary" onClick={onClose}>關閉</button></div></Modal>;
}
