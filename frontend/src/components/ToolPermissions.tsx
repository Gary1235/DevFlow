import { useEffect, useRef, useState } from 'react';
import { api } from '../api';
import {providerNames} from '../domain';
import {permissionToolNames} from './PermissionSettings';
import {permissionArguments} from '../permissionPresentation';

export interface ToolPermission {
  id: string; operationId: string; tool: string; arguments: Record<string, unknown>;
  workspace: string; createdAt: string; expiresAt?: string|null; reminderAt?:string;
  endReason?:string|null;
  state: 'Pending' | 'Allowed' | 'AllowedByRule' | 'AllowedByPolicy' | 'Denied' | 'Expired';
  ruleOptions?: RuleOption[]; approvalScope?: string;provider?:string;rulesAvailable?:boolean;
  policyVersion?:number; risk?: {category:string;label:string;reason:string;reasonCode?:string;batchAllowed:boolean;autoAllowed?:boolean;fixedBoundary?:boolean};
}
interface RuleOption { kind: string; pattern: string; label: string }
const states: Record<string, string> = {
  Pending: '等待你的確認', Allowed: '已允許這一次', Denied: '已拒絕', Expired: '確認已失效',
  AllowedByRule: '依允許規則放行', AllowedTask: '本次任務已允許', AllowedProject: '此專案已永久允許',
  AllowedByPolicy:'依自動化政策放行',
  AllowedDiscussion:'本次對話已允許',
};
function PermissionCard({ request, state, busy, task, discussion, now, onReply }: {
  request: ToolPermission; state: string; busy: boolean; task: boolean; discussion:boolean; now:number; onReply: (allow: boolean, scope?: string, option?: RuleOption,provider?:string) => void;
}) {
  const [provider,setProvider]=useState('*');
  const risk=request.risk||{category:'Unknown',label:'需確認：未知操作',reason:'尚無分類資訊，僅能單次確認。',batchAllowed:false};
  const batch=risk.batchAllowed&&(task||discussion);
  const choice={kind:'GeneralRead',pattern:'.',label:'一般讀取'};
  const category=risk.autoAllowed||risk.category==='GeneralRead'?'general':risk.category==='Deletion'?'danger':'sensitive';
  const badge=<span className={'permission-risk-badge risk-'+category}>{category==='general'?'✓':category==='danger'?'⛔':'⚠'} {risk.label}</span>;
  const detail = <><dl><dt>操作</dt><dd>{permissionToolNames[request.tool]||request.tool}</dd><dt>AI CLI</dt><dd>{providerNames[request.provider||'antigravity']}</dd><dt>工作目錄</dt><dd>{request.workspace}</dd></dl>
    <pre aria-label="工具完整參數">{JSON.stringify(permissionArguments(request.arguments), null, 2)}</pre><p>已知帳密欄位會遮蔽；核准仍對應原始工具參數。</p></>;
  if (state !== 'Pending') return <section className={'tool-permission permission-record risk-'+category} aria-label="工具權限確認">
    {state==='Expired'&&request.endReason&&<p role="status" className="info-banner">{request.endReason}</p>}
    <details><summary><strong>{permissionToolNames[request.tool]||request.tool}</strong>{badge}<span role="status">{states[state]}</span><span>查看參數</span></summary><p>{risk.reason}</p>{request.policyVersion!=null&&<p>權限政策版本 v{request.policyVersion}</p>}{detail}</details>
  </section>;
  return <section className={'tool-permission risk-'+category} aria-label="工具權限確認">
    <div className="tool-permission-heading"><strong>需要你的確認</strong><span role="status">{states[state]}</span></div>
    <p>{badge}</p><p>{risk.reason}</p>{'reasonCode' in risk&&risk.reasonCode&&<p>原因代碼：{risk.reasonCode} · DevFlow 工具政策{request.policyVersion!=null?' v'+request.policyVersion:''}</p>}
    {!risk.batchAllowed&&<p><strong>敏感或未知操作只能允許這一次，不會被批次允許規則放行。</strong></p>}
    {now >= (request.reminderAt?Date.parse(request.reminderAt):Date.parse(request.createdAt)+120000)
      ? <p className="info-banner" role="status">提醒：已等待超過 2 分鐘，仍需你的確認。DevFlow 不會因等待時間自動拒絕或核准；若 CLI 中斷，會回報並讓此確認失效。</p>
      : <p>等待你回覆；2 分鐘後僅提醒，不設定確認期限。等待是否持續依 CLI 狀態而定。</p>}
    {detail}
    {batch && <div className="permission-match"><label>允許規則適用 CLI<select aria-label="允許規則適用 CLI" value={provider} onChange={e=>setProvider(e.target.value)}><option value="*">全部 CLI</option><option value={request.provider||'antigravity'}>僅 {providerNames[request.provider||'antigravity']}</option></select></label>
      <p>可批次允許本次{discussion?'對話':'任務'}讀取專案程式碼、一般文件與目錄；不含機敏資料、資料庫、寫入、刪除與未知指令。可到<a href="#permissions">工具權限設定</a>停用。</p>
    </div>}
    <div className="button-row"><button type="button" className="secondary" disabled={busy} onClick={() => onReply(false)}>拒絕</button>
      <button type="button" className="primary" disabled={busy} onClick={() => onReply(true)}>{busy ? '送出中…' : '允許這一次'}</button>
      {batch && <button type="button" className="secondary" disabled={busy} onClick={() => onReply(true, discussion?'Discussion':'Task', choice,provider)}>{discussion?'本次對話允許一般讀取':'本次任務允許一般讀取'}</button>}
    </div>
  </section>;
}
export function ToolPermissions({ discussionId, taskId, operationId, requests, onAnswered }: {
  discussionId?: string; taskId?: string; operationId: string; requests: ToolPermission[]; onAnswered: () => void;
}) {
  const [answering, setAnswering] = useState<string | null>(null);
  const [answered, setAnswered] = useState<Record<string, string>>({});
  const [error, setError] = useState('');
  const [now,setNow]=useState(()=>Date.now());
  const inFlight = useRef(false);
  const scroll = useRef<HTMLDivElement>(null);
  const stateOf = (r: ToolPermission) => r.state === 'Pending' ? answered[r.id] || r.state :
    r.state === 'Allowed' && r.approvalScope === 'Task' ? 'AllowedTask' :
    r.state === 'Allowed' && r.approvalScope === 'Discussion' ? 'AllowedDiscussion' :
    r.state === 'Allowed' && r.approvalScope === 'Project' ? 'AllowedProject' : r.state;
  const visible = requests.filter(r => r.operationId === operationId);
  const pending = visible.filter(r => stateOf(r) === 'Pending');
  const history = visible.filter(r => stateOf(r) !== 'Pending').slice().reverse();
  const pendingId = pending[0]?.id;
  const reminderKey=pending.map(r=>r.reminderAt||r.createdAt).join('|');
  useEffect(()=>{
    const upcoming=pending.map(r=>r.reminderAt?Date.parse(r.reminderAt):Date.parse(r.createdAt)+120000).filter(t=>t>now);
    if(!upcoming.length)return;
    const timer=setTimeout(()=>setNow(Date.now()),Math.min(2147483647,Math.max(0,Math.min(...upcoming)-Date.now())+10));
    return()=>clearTimeout(timer);
  },[reminderKey,now]);
  useEffect(() => { if (pendingId && scroll.current) scroll.current.scrollTop = 0; }, [pendingId]);
  async function reply(request: ToolPermission, allow: boolean, scope = 'Once', option?: RuleOption,provider='*') {
    if (inFlight.current) return;
    inFlight.current = true; setAnswering(request.id); setError('');
    try {
      await api((taskId ? '/api/tasks/' + taskId : '/api/discussions/' + discussionId) + '/permissions/' + request.id, 'POST', {
        operationId, allow, ...(scope === 'Once' ? {} : { rule: { scope,provider, kind: option?.kind || 'Exact', ...(option?.pattern ? { pattern: option.pattern } : {}) } }),
      });
      setAnswered(v => ({ ...v, [request.id]: !allow ? 'Denied' : scope === 'Task' ? 'AllowedTask' : scope === 'Discussion'?'AllowedDiscussion':scope === 'Project' ? 'AllowedProject' : 'Allowed' }));
    } catch (e) { setError(e instanceof Error ? e.message : '無法回覆確認，請重試。'); }
    finally { inFlight.current = false; setAnswering(null); onAnswered(); }
  }
  return <div className="tool-permissions" ref={scroll} role="region" aria-label="工具確認與紀錄" tabIndex={0}>
    <div className="permission-list-heading"><strong>工具確認 · 待確認 {pending.length} · 已處理 {history.length}</strong>{taskId && <a href="#permissions">管理允許規則</a>}</div>
    {error && <p className="error-banner" role="alert">{error}</p>}
    {[...pending, ...history].map(request => <PermissionCard key={request.id} request={request} state={stateOf(request)} busy={!!answering} now={now} task={!!taskId && !!request.ruleOptions?.length} discussion={!!discussionId&&!!request.rulesAvailable} onReply={(allow, scope, option,provider) => void reply(request, allow, scope, option,provider)} />)}
  </div>;
}
