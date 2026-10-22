import {useCallback,useEffect,useState} from 'react';
import {api} from '../api';
import {useResource} from '../hooks';
import {useNotice} from './ui';
import './automation-policy.css';

interface Policy {version:number;automationEnabled:boolean;sensitivePaths:string[];sensitiveCommands:string[];sensitiveDestinations:string[];approvedDownloadHosts:string[]}
const fields:{key:'sensitivePaths'|'sensitiveCommands'|'sensitiveDestinations'|'approvedDownloadHosts';label:string;hint:string}[]=[
  {key:'sensitivePaths',label:'連線資源敏感路徑',hint:'每行一項，例如 data/**、**/*.secret；比對網址資源的相對路徑，可用 * 與 **。舊的敏感路徑名單僅用於連線資源，不再攔截工作副本內檔案。'},
  {key:'sensitiveCommands',label:'自訂敏感指令',hint:'每行一項，例如 git log、npm run deploy；比對指令前綴或 glob，不能用這裡放行未知腳本。'},
  {key:'sensitiveDestinations',label:'自訂敏感連線目的地',hint:'每行一項，例如 production.example.com 或 https://internal.example.com/*；敏感名單優先於下載來源。'},
  {key:'approvedDownloadHosts',label:'核准的下載網域',hint:'每行完整網域，不含路徑。僅核准可完整解析、無憑證／查詢參數／重新導向的 HTTPS GET；不會授權上傳、任意腳本或套件安裝。'},
];
export function AutomationPolicy(){
  const resource=useResource('automation-policy',useCallback((signal:AbortSignal)=>api<Policy>('/api/tool-permission-policy','GET',undefined,{signal}),[]),0);
  const [draft,setDraft]=useState<Policy|null>(null),[busy,setBusy]=useState(false),[error,setError]=useState('');
  const notice=useNotice();
  useEffect(()=>{if(resource.data)setDraft({...resource.data,sensitivePaths:resource.data.sensitivePaths||[],sensitiveCommands:resource.data.sensitiveCommands||[],sensitiveDestinations:resource.data.sensitiveDestinations||[],approvedDownloadHosts:resource.data.approvedDownloadHosts||[]});},[resource.data]);
  return <section className="panel automation-policy" aria-label="全域自動化政策">
    <h2>全域自動化政策</h2><p>適用所有專案與 CLI，授權範圍限目前任務指定的工作副本；副本內檔案視為允許 AI 存取。</p>
    {(resource.error||error)&&<p className="error-banner" role="alert">{error||resource.error}</p>}
    {!draft?<button className="secondary" onClick={resource.refresh}>重新讀取政策</button>:<form onSubmit={event=>{event.preventDefault();if(busy)return;setBusy(true);setError('');void api<Policy>('/api/tool-permission-policy','PUT',{
      ...draft,expectedVersion:draft.version,...Object.fromEntries(fields.map(f=>[f.key,draft[f.key].map(v=>v.trim()).filter(Boolean)])),
    }).then(saved=>{setDraft(saved);notice('自動化政策已保存，套用於後續工具請求。');}).catch(e=>setError(e instanceof Error?e.message:'保存失敗')).finally(()=>setBusy(false));}}>
      <label className="checkbox"><input type="checkbox" checked={draft.automationEnabled} onChange={e=>setDraft({...draft,automationEnabled:e.target.checked})}/>啟用指定工作副本內預設允許（保留固定邊界）</label>
      <p className="permission-policy general-policy">自動執行：工作副本內檔案的讀取、搜尋、新增、修改及單檔刪除，不依檔名或副檔名攔截。討論、規劃與 AI 審查仍維持唯讀；任務與步驟的修改路徑限制仍有效。</p>
      <p className="footnote">依操作與全部目標判斷，支援多路徑、引號及萬用字元展開；任何一個來源或目的地越界，整個操作仍需確認。管線中的搜尋、輸出與修改會逐段檢查，不會沿用前段授權執行腳本。</p>
      <p className="footnote">包含 .env、憑證、資料庫檔及密碼元件名稱的副本內檔案也視為已授權；開發者應事先隔離機密，使用測試設定與資料。搜尋仍檢查符號連結及越界，不會因只顯示前幾筆而略過檢查。</p>
      <details><summary>指令、連線與固定邊界</summary>
        <p>Git 推送、發布、部署、資料庫指令、目錄／遞迴刪除與 Git／代理權限設定修改不自動放行；直接操作副本內資料庫檔案則視為檔案操作。</p>
        <p>專案外存取、符號連結、額外原生權限及無法完整解析的指令／腳本保留確認。建置、測試與依賴準備可由你設定的 Worker 驗證流程執行；AI 直接提出的未知腳本不會因此取得權限。</p>
        <p>網路上傳、帶憑證／參數的請求及未核准外連需確認。本機 GET 與已核准來源的簡單 HTTPS GET 可自動判斷；資料庫操作仍屬敏感。</p>
      </details>
      <div className="automation-policy-fields">{fields.map(field=><label key={field.key}>{field.label}<textarea rows={3} value={draft[field.key].join('\n')} onChange={e=>setDraft({...draft,[field.key]:e.target.value.split('\n')})}/><span className="footnote">{field.hint}</span></label>)}</div>
      <p className="footnote">這是 DevFlow 收到工具請求時的政策，不是作業系統沙箱。CLI 內部直接執行的操作、程式碼內隱藏的秘密及腳本的間接存取，無法保證由這裡攔截。保留 CLI 原生限制，不使用全域跳過確認。AI 修改政策需人工核准。</p>
      <p className="footnote">保存只影響之後的請求；已開始的操作不會撤回，現有待確認項目仍需回覆。政策版本 v{draft.version}。</p>
      <div className="button-row"><button className="primary" disabled={busy}>保存自動化政策</button><button type="button" className="secondary" disabled={busy} onClick={()=>{setError('');resource.refresh();}}>重新讀取政策（捨棄編輯）</button></div>
    </form>}
  </section>;
}
