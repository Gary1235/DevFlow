import {useCallback,useState} from 'react';
import {api} from '../api';
import {useAction,useResource} from '../hooks';
import {formatDate} from '../domain';
import {Empty,Heading,Modal,useNotice} from './ui';

interface Category {id:string;label:string;path:string;bytes:number;files:number;complete:boolean;warning:string|null}
interface Overview {categories:Category[];scannedAt:string}
interface Item {key:string;taskId:string;runId:string;title:string;projectName:string;kind:string;path:string;bytes:number;files:number;lastActivity:string;canClean:boolean;reason:string|null}
interface Preview {id:string;expiresAt:string;olderThanDays:number;items:Item[];truncated:boolean}
interface CleanupReport {completedAt:string;items:{key:string;title:string;kind:string;bytes:number;success:boolean;message:string}[]}
const kinds:Record<string,string>={artifacts:'日誌與原始測試報告',worktree:'工作副本與編譯檔案'};
function size(bytes:number){const units=['B','KB','MB','GB','TB'];let n=bytes,i=0;while(n>=1024&&i<units.length-1){n/=1024;i++;}return `${i?n.toFixed(1):n} ${units[i]}`;}

export function StoragePage(){
  const notice=useNotice(),action=useAction(notice);
  const load=useCallback((signal:AbortSignal)=>api<Overview>('/api/storage','GET',undefined,{signal,timeout:45000}),[]);
  const overview=useResource('storage',load,0);
  const [days,setDays]=useState(30),[artifacts,setArtifacts]=useState(true),[worktrees,setWorktrees]=useState(true);
  const [preview,setPreview]=useState<Preview|null>(null),[selected,setSelected]=useState<string[]>([]),[confirm,setConfirm]=useState(false),[report,setReport]=useState<CleanupReport|null>(null);
  const chosen=preview?.items.filter(i=>selected.includes(i.key))||[],total=chosen.reduce((s,i)=>s+i.bytes,0);
  function reset(){setPreview(null);setSelected([]);setReport(null);}
  return <>
    <Heading eyebrow="STORAGE" title="儲存空間管理" description="查看所有專案的資料占用，預覽並清理已結束任務的執行資料。"><button className="secondary" disabled={action.busy} onClick={overview.refresh}>重新掃描容量</button></Heading>
    {overview.error&&<div className="error-banner" role="alert">{overview.error}</div>}
    {!overview.data&&!overview.error&&<Empty title="正在計算儲存空間…"/>}
    <div className="storage-categories">{overview.data?.categories.map(c=><article className="panel" key={c.id}><h2>{c.label}</h2><strong className="storage-size">{size(c.bytes)}{!c.complete&&'（部分）'}</strong><p className="muted">{c.files.toLocaleString()} 個檔案</p><p className="storage-path">{c.path}</p>{c.warning&&<p className="draft-warning">{c.warning}</p>}{c.id==='database'&&<span className="chip neutral">保留紀錄</span>}</article>)}</div>
    <article className="panel">
      <h2>清理已結束任務</h2><p className="muted">僅處理已合併或已取消、且所有執行都已結束的任務。先產生預覽，再由你勾選確認。</p>
      <div className="storage-options"><label>保留天數<select aria-label="保留天數" value={days} disabled={action.busy} onChange={e=>{reset();setDays(Number(e.target.value));}}>{[[7,'保留最近 7 天'],[30,'保留最近 30 天'],[90,'保留最近 90 天'],[180,'保留最近 180 天'],[0,'所有已結束任務']].map(([value,label])=><option key={value} value={value}>{label}</option>)}</select></label><label className="checkbox"><input type="checkbox" checked={artifacts} disabled={action.busy} onChange={e=>{reset();setArtifacts(e.target.checked);}}/>日誌與原始測試報告</label><label className="checkbox"><input type="checkbox" checked={worktrees} disabled={action.busy} onChange={e=>{reset();setWorktrees(e.target.checked);}}/>工作副本與編譯檔案</label><button className="secondary" disabled={action.busy||(!artifacts&&!worktrees)} onClick={()=>void action.run(async()=>{reset();const result=await api<Preview>('/api/storage/preview','POST',{olderThanDays:days,artifacts,worktrees},{timeout:120000});setPreview(result);})}>{action.busy?'處理中…':'預覽可清理項目'}</button></div>
      <p className="footnote">任務內容、規則、討論、AI／人工驗收紀錄與測試摘要會保留。Git 分支與成果 commit 會保留。工作副本有未提交變更、未追蹤檔案或連結時會跳過。</p>
    </article>
    {preview&&<article className="panel table-panel"><div className="storage-preview-heading"><div><h2>清理預覽</h2><p className="muted">預覽至 {formatDate(preview.expiresAt)} 有效 · 已選 {chosen.length} 項 · 預估釋放 {size(total)}</p></div><button className="danger-button" disabled={action.busy||!chosen.length} onClick={()=>setConfirm(true)}>清理所選項目</button></div>
      {preview.truncated&&<div className="info-banner">本次最多列出 500 項；清理後可再產生預覽。</div>}
      {!preview.items.length?<Empty title="目前沒有符合條件的資料" description="可調整保留天數，或等待任務合併／取消並結束執行。"/>:<><label className="checkbox storage-select-all"><input type="checkbox" disabled={action.busy||!preview.items.some(i=>i.canClean)} checked={selected.length>0&&selected.length===Math.min(100,preview.items.filter(i=>i.canClean).length)} onChange={e=>setSelected(e.target.checked?preview.items.filter(i=>i.canClean).slice(0,100).map(i=>i.key):[])}/>選取可清理項目（每次最多 100 項）</label><div className="table-wrap"><table><thead><tr><th>選擇</th><th>任務／資料</th><th>容量</th><th>最後活動</th><th>檢查結果</th></tr></thead><tbody>{preview.items.map(item=><tr key={item.key}><td><input type="checkbox" aria-label={'選取 '+item.title+' '+kinds[item.kind]} disabled={action.busy||!item.canClean||(!selected.includes(item.key)&&selected.length>=100)} checked={selected.includes(item.key)} onChange={e=>setSelected(v=>e.target.checked?[...v,item.key]:v.filter(k=>k!==item.key))}/></td><td><a className="task-title" href={'#task/'+item.taskId}>{item.title}</a><div className="subline">{item.projectName} · {kinds[item.kind]}</div><div className="storage-path">{item.path}</div></td><td>{size(item.bytes)}</td><td>{formatDate(item.lastActivity)}</td><td>{item.canClean?<span className="chip success">可清理</span>:<span className="draft-warning">{item.reason}</span>}</td></tr>)}</tbody></table></div></>}
      <p className="footnote storage-select-all">容量總覽也包含尚未符合條件或無法歸屬到任務的資料，因此可能大於預覽可釋放容量。</p>
    </article>}
    {report&&<article className="panel"><h2>清理結果</h2><p>已釋放 {size(report.items.reduce((s,i)=>s+i.bytes,0))} · {formatDate(report.completedAt)}</p>{report.items.map(item=><div className="history-item" key={item.key}><span className={'chip '+(item.success?'success':'amber')}>{item.success?'已清理':'未完成／已跳過'}</span><strong> {item.title} · {kinds[item.kind]}</strong><p>{size(item.bytes)} · {item.message}</p></div>)}</article>}
    {confirm&&preview&&<Modal title="確認清理所選資料" onClose={()=>{if(!action.busy)setConfirm(false);}}><div className="dialog-body"><p>將清理 {chosen.length} 項，預估釋放 {size(total)}。</p><p>所選執行日誌、原始測試報告與工作副本刪除後無法由 DevFlow 還原。任務文字、驗收紀錄、測試摘要及 Git 成果會保留。</p><p className="footnote">執行前會再次檢查狀態與路徑；有變動的項目會跳過。</p></div><div className="dialog-footer"><button className="secondary" disabled={action.busy} onClick={()=>setConfirm(false)}>取消</button><button className="danger-button" disabled={action.busy} onClick={()=>void action.run(async()=>{try{const result=await api<CleanupReport>('/api/storage/cleanup','POST',{previewId:preview.id,keys:selected},{timeout:130000});setReport(result);setPreview(null);setSelected([]);overview.refresh();notice('清理已結束，請查看各項結果。');}finally{setConfirm(false);}})}>{action.busy?'清理中…':'確認清理'}</button></div></Modal>}
  </>;
}
