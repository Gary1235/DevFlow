import {useCallback,useEffect,useRef,useState} from 'react';
import {api} from '../api';
import {useResource} from '../hooks';
import {Modal} from './ui';

interface FolderListing {path:string|null;parentPath:string|null;folders:{name:string;path:string}[];truncated:boolean}
export function FolderPicker({initialPath,onClose,onSelected}:{initialPath:string;onClose:()=>void;onSelected:(path:string)=>void}){
  const [inline,setInline]=useState(false),[attempt,setAttempt]=useState(0),[error,setError]=useState('');
  const callbacks=useRef({onClose,onSelected});callbacks.current={onClose,onSelected};
  useEffect(()=>{
    if(inline)return;
    const controller=new AbortController();
    // Defer until after React's development effect cleanup so one click opens one native window.
    const timer=setTimeout(()=>{setError('');void api<{path:string|null}>('/api/projects/pick-folder','POST',{path:initialPath.trim()||null},{signal:controller.signal,timeout:190000}).then(result=>{
      if(controller.signal.aborted)return;
      if(result.path)callbacks.current.onSelected(result.path);else callbacks.current.onClose();
    }).catch(reason=>{if(!controller.signal.aborted)setError(reason instanceof Error?reason.message:'無法開啟 Windows 選擇器。');});},0);
    return()=>{clearTimeout(timer);controller.abort();};
  },[initialPath,inline,attempt]);
  if(inline)return <InlineFolderPicker initialPath={initialPath} onClose={onClose} onSelected={onSelected}/>;
  if(!error)return null;
  return <Modal title="無法開啟資料夾選擇器" onClose={onClose}><div className="dialog-body">
    <div className="error-banner" role="alert">{error}</div>
    </div><div className="dialog-footer"><button type="button" className="secondary" onClick={onClose}>關閉</button><button type="button" className="secondary" onClick={()=>setInline(true)}>改用頁面瀏覽</button><button type="button" className="primary" onClick={()=>{setError('');setAttempt(v=>v+1);}}>重試</button></div></Modal>;
}
function InlineFolderPicker({initialPath,onClose,onSelected}:{initialPath:string;onClose:()=>void;onSelected:(path:string)=>void}){
  const [requested,setRequested]=useState<string|null>(initialPath.trim()||null),[address,setAddress]=useState(initialPath),[filter,setFilter]=useState('');
  const load=useCallback((signal:AbortSignal)=>api<FolderListing>('/api/projects/folders','POST',{path:requested},{signal,timeout:15000}),[requested]);
  const resource=useResource('folders:'+requested,load,0),listing=resource.data;
  useEffect(()=>{if(listing){setAddress(listing.path||'');setFilter('');}},[listing]);
  function navigate(path:string|null){if(path===requested)resource.refresh();else setRequested(path);}
  return <Modal title="選擇專案資料夾" eyebrow="LOCAL FOLDERS" onClose={onClose}><div className="dialog-body folder-picker">
    <label htmlFor="folder-address">資料夾路徑</label><div className="folder-address"><input id="folder-address" placeholder="輸入完整路徑，或從下方磁碟開始瀏覽" value={address} onChange={e=>setAddress(e.target.value)} onKeyDown={e=>{if(e.key==='Enter'){e.preventDefault();navigate(address.trim()||null);}}}/><button type="button" className="secondary" onClick={()=>navigate(address.trim()||null)}>前往</button></div>
    <div className="button-row folder-navigation"><button type="button" className="secondary" onClick={()=>navigate(null)}>磁碟列表</button><button type="button" className="secondary" disabled={!listing?.path} onClick={()=>navigate(listing?.parentPath||null)}>返回上層</button></div>
    {resource.error&&<div className="error-banner" role="alert">{resource.error}</div>}
    {!listing&&!resource.error&&<p role="status">正在讀取資料夾…</p>}
    {listing&&<><p className="folder-current">{listing.path||'選擇磁碟'}</p><label className="sr-only" htmlFor="folder-filter">篩選資料夾</label><input id="folder-filter" placeholder="篩選此層資料夾" value={filter} onChange={e=>setFilter(e.target.value)}/><div className="folder-list" aria-label="資料夾列表">{listing.folders.filter(f=>f.name.toLocaleLowerCase().includes(filter.toLocaleLowerCase())).map(f=><button type="button" className="folder-entry" key={f.path} onClick={()=>navigate(f.path)}><span>▸ {f.name}</span><span aria-hidden="true">→</span></button>)}{!listing.folders.length&&<p className="muted">此資料夾沒有子資料夾，可以直接選擇。</p>}{listing.folders.length>0&&!listing.folders.some(f=>f.name.toLocaleLowerCase().includes(filter.toLocaleLowerCase()))&&<p className="muted">沒有符合的資料夾。</p>}</div>{listing.truncated&&<p className="footnote">資料夾過多，目前顯示前 2000 個。也可輸入完整路徑後按「前往」。</p>}</>}
    </div><div className="dialog-footer"><button type="button" className="secondary" onClick={onClose}>取消選擇</button><button type="button" className="primary" disabled={!listing?.path} onClick={()=>listing?.path&&onSelected(listing.path)}>選擇此資料夾</button></div></Modal>;
}
