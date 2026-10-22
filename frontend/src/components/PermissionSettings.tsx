import { useCallback, useState } from 'react';
import { api } from '../api';
import { useAction, useResource } from '../hooks';
import { Empty, Heading, Modal, useNotice } from './ui';
import { providerNames } from '../domain';
import { AutomationPolicy } from './AutomationPolicy';
import type { Project, WorkTask } from '../types';

interface PermissionRule { id:string;projectId:string;taskId:string|null;discussionId?:string|null;provider?:string;tool:string;kind:string;pattern:string;enabled:boolean;version:number }
export const permissionToolNames:Record<string,string>={general_read:'一般讀取',run_command:'執行指令',read_file:'讀取檔案',write_file:'修改檔案',edit_file:'修改檔案',list_dir:'列出目錄',grep_search:'搜尋內容',find_by_name:'搜尋檔名',view_file:'讀取檔案'};
function RuleEditor({rule,projects,tasks,onClose,onSaved}:{rule:Partial<PermissionRule>;projects:Project[];tasks:WorkTask[];onClose:()=>void;onSaved:()=>void}) {
  const initialProject=rule.projectId||projects[0]?.id||'';
  const [draft,setDraft]=useState({projectId:initialProject,taskId:rule.taskId||tasks.find(t=>t.projectId===initialProject)?.id||'',discussionId:rule.discussionId||null,provider:rule.provider||'*',enabled:rule.enabled??true});
  const [busy,setBusy]=useState(false);const [error,setError]=useState('');
  async function save(){
    if(busy)return;setBusy(true);setError('');
    try{await api('/api/tool-permission-rules'+(rule.id?'/'+rule.id:''),rule.id?'PUT':'POST',{
      ...draft,taskId:draft.discussionId?null:draft.taskId||null,tool:'general_read',kind:'GeneralRead',pattern:'.',...(rule.id?{expectedVersion:rule.version}:{})
    });onSaved();}catch(e){setError(e instanceof Error?e.message:'無法保存規則。');}finally{setBusy(false);}
  }
  return <Modal title={rule.id?'編輯允許規則':'新增允許規則'} onClose={onClose}><form className="permission-rule-editor" onSubmit={e=>{e.preventDefault();void save();}}>
    <div className="dialog-body permission-rule-fields">
      <p className="permission-policy general-policy">✓ 僅可批次允許一般讀取：專案程式碼、一般開發文件與目錄。敏感資料、資料庫、寫入、刪除與未知指令每次確認。</p>
      <label>適用 CLI<select aria-label="規則 CLI" value={draft.provider} onChange={e=>setDraft({...draft,provider:e.target.value})}><option value="*">全部 CLI</option>{Object.entries(providerNames).map(([id,name])=><option key={id} value={id}>{name}</option>)}</select></label>
      <label>專案<select aria-label="規則專案" value={draft.projectId} onChange={e=>setDraft({...draft,projectId:e.target.value,taskId:tasks.find(t=>t.projectId===e.target.value)?.id||'',discussionId:null})}>{projects.map(p=><option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
      <label>有效範圍<select required aria-label="規則範圍" value={draft.discussionId?'discussion:'+draft.discussionId:draft.taskId} onChange={e=>setDraft({...draft,taskId:e.target.value.startsWith('discussion:')?'':e.target.value,discussionId:e.target.value.startsWith('discussion:')?e.target.value.slice(11):null})}>
        <option value="" disabled>選擇本次任務</option>{rule.discussionId&&<option value={'discussion:'+rule.discussionId}>僅此對話</option>}{tasks.filter(t=>t.projectId===draft.projectId).map(t=><option key={t.id} value={t.id}>僅任務：{t.title}</option>)}
      </select></label>
      <p>跨任務永久允許已停用。對話規則可在對話中的一般讀取確認卡建立。</p>
      <label className="checkbox"><input type="checkbox" checked={draft.enabled} onChange={e=>setDraft({...draft,enabled:e.target.checked})}/>啟用規則</label>
      {error&&<p className="error-banner" role="alert">{error}</p>}
    </div><div className="dialog-footer"><button type="button" className="secondary" onClick={onClose} disabled={busy}>取消</button><button className="primary" disabled={busy||!draft.projectId||(!draft.taskId&&!draft.discussionId)}>儲存規則</button></div>
  </form></Modal>;
}
export function PermissionSettings({projects,tasks,projectId=''}:{projects:Project[];tasks:WorkTask[];projectId?:string}){
  const load=useCallback((signal:AbortSignal)=>api<PermissionRule[]>('/api/tool-permission-rules','GET',undefined,{signal}),[]);
  const resource=useResource('permission-rules',load);const [editing,setEditing]=useState<Partial<PermissionRule>|null>(null);
  const notice=useNotice();const action=useAction(notice);
  const rules=(resource.data||[]).filter(r=>!projectId||r.projectId===projectId);
  return <><Heading eyebrow="TOOL PERMISSIONS" title="工具權限設定" description="指定工作副本內檔案預設允許；指令、連線與固定邊界保留確認，各 CLI 共用 DevFlow 政策。">
    <button className="primary" disabled={!projects.length} onClick={()=>setEditing({projectId:projectId||projects[0]?.id})}>新增允許規則</button>
  </Heading>
    <AutomationPolicy/>
    <h2>手動確認模式的一般讀取規則</h2>
    <div className="permission-policy-legend"><p className="permission-policy general-policy">✓ 綠色：依自動化政策或手動核准放行的一般操作。</p><p className="permission-policy sensitive-policy">⚠ 橘色：命中指令、連線限制或固定邊界，需單次確認。</p><p className="permission-policy danger-policy">⛔ 紅色：需確認的刪除／高影響操作。</p></div>
    <p className="info-banner">下列舊一般讀取規則只在關閉自動化政策時使用，不會覆蓋自動化模式的指令、連線限制與固定邊界。</p>
    <p className="footnote">此分類適用 CLI 提交給 DevFlow 的工具確認；CLI 原生政策直接執行的操作不會由此頁攔截。機敏檔名與檔案類型採保守判斷，無法自動識別程式碼內硬編碼的秘密。</p>
    {resource.error&&<p role="alert" className="error-banner">{resource.error}<button onClick={resource.refresh}>重新讀取</button></p>}
    {resource.data&&!rules.length&&<Empty title="尚無允許規則" description="可在一般讀取確認卡允許本次任務／對話的一般讀取，也能在這裡新增任務規則。"/>}
    <div className="permission-rule-list">{rules.map(rule=>{
      const current=rule.kind==='GeneralRead'&&!!(rule.taskId||rule.discussionId);
      return <article className="panel" key={rule.id}><div className="panel-heading"><div><h2>{permissionToolNames[rule.tool]||rule.tool} · {rule.enabled?'已啟用':'已停用'}</h2><p>{projects.find(p=>p.id===rule.projectId)?.name||rule.projectId} · {rule.provider==='*'?'全部 CLI':providerNames[rule.provider||'antigravity']} · {rule.taskId?'僅任務：'+(tasks.find(t=>t.id===rule.taskId)?.title||rule.taskId):rule.discussionId?'僅此對話':'舊專案永久規則（已失效）'}</p></div><div className="button-row">
        {current&&<button className="secondary" onClick={()=>setEditing(rule)}>編輯</button>}
        {(current||rule.enabled)&&<button className="secondary" disabled={action.busy} onClick={()=>void action.run(async()=>{await api('/api/tool-permission-rules/'+rule.id,'PUT',{...rule,enabled:!rule.enabled,expectedVersion:rule.version});resource.refresh();})}>{rule.enabled?'停用':'啟用'}</button>}
        <button className="danger-button" disabled={action.busy} onClick={()=>void action.run(async()=>{await api('/api/tool-permission-rules/'+rule.id+'?expectedVersion='+rule.version,'DELETE');resource.refresh();})}>刪除</button>
      </div></div>{current?<p>一般讀取；敏感或未知操作不在授權範圍內。</p>:<><p className="sensitive-policy permission-policy">舊版規則不再提供批次放行，請改建本次任務／對話的一般讀取規則。</p><pre>{rule.pattern}</pre></>}</article>;
    })}</div>
    {editing&&<RuleEditor rule={editing} projects={projects} tasks={tasks} onClose={()=>setEditing(null)} onSaved={()=>{setEditing(null);resource.refresh();notice('工具規則已保存。');}}/>}
  </>;
}
