import {useCallback, useEffect, useRef, useState} from 'react';
import {api} from '../api';
import {useAction, useResource} from '../hooks';
import {editablePlan, selectionText} from '../domain';
import {AiChoice, ProseContent, useNotice} from './ui';
import {LiveActivity} from './LiveActivity';
import {ToolPermissions, type ToolPermission} from './ToolPermissions';
import type {Discussion, DiscussionMessage, Plan, Provider, RuntimeSnapshot, Selection, WorkTask} from '../types';
import './plan-discussion.css';

interface Progress {operationId:string|null;content:string;activity:RuntimeSnapshot|null;permissions:ToolPermission[]}

export function PlanDiscussion({task, plan, dirty, onApplied, onClose}: {
  task:WorkTask;plan?:Plan|null;dirty:boolean;onApplied:()=>void;onClose:()=>void;
}) {
  const [accepted, setAccepted] = useState<Discussion|null>(null);
  const [error, setError] = useState('');
  const [text, setText] = useState('');
  const [selection, setSelection] = useState<Selection|null>(null);
  const [choosing, setChoosing] = useState(false);
  const tail = useRef<HTMLDivElement>(null);
  const notice = useNotice();
  const action = useAction(notice);
  const base = '/api/discussions/' + task.id;
  const initialize = useCallback(async () => {
    try {
      setError('');
      setAccepted(await api<Discussion>('/api/tasks/'+task.id+'/plan-discussion', 'POST'));
    } catch (e) {setError(e instanceof Error ? e.message : '無法載入計畫討論');}
  }, [task.id]);
  useEffect(()=>{void initialize();}, [initialize]);
  const resource = useResource('plan-discussion.'+task.id,
    useCallback((signal:AbortSignal)=>api<Discussion>(base, 'GET', undefined, {signal}), [base]),
    accepted?.activeOperation ? 1500 : 5000, !!accepted);
  const d = resource.data && resource.data.version >= (accepted?.version ?? -1) ? resource.data : accepted;
  useEffect(()=>{if(resource.data && resource.data.version > (accepted?.version ?? -1))setAccepted(resource.data);}, [resource.data, accepted?.version]);
  const running = !!d?.activeOperation;
  const progress = useResource('plan-discussion-progress.'+task.id,
    useCallback((signal:AbortSignal)=>api<Progress>(base+'/progress', 'GET', undefined, {signal}), [base]), 1000, running);
  const live = progress.data?.operationId === d?.activeOperation ? progress.data : undefined;
  const providers = useResource('plan-discussion-models',
    useCallback((signal:AbortSignal)=>api<Provider[]>('/api/ai/providers', 'GET', undefined, {signal}), []), 0, choosing);
  const busy = action.busy || running;
  const revision = plan?.revision ?? 0;

  useEffect(()=>{tail.current?.scrollIntoView?.({block:'nearest'});}, [d?.messages.length, live?.content]);

  async function send(proposal:boolean) {
    if (!d) return;
    await action.run(async()=>{
      const updated = await api<Discussion>(base+(proposal?'/plan-proposal':'/messages')+'?background=1', 'POST', {
        expectedVersion:d.version, message:text.trim() || undefined,
        selection:selection ?? d.selection, planRevision:revision,
      });
      setAccepted(updated); setText(''); resource.refresh();
    });
  }

  async function apply(message:DiscussionMessage) {
    if (dirty || !editablePlan.includes(task.status)) return;
    await action.run(async()=>{
      await api('/api/tasks/'+task.id+'/plan-discussion/apply', 'POST', {messageId:message.id, expectedVersion:task.version});
      notice('已建立新版計畫，請檢視後再核准執行。'); onApplied();
    });
  }

  return <aside className="panel plan-discussion" aria-label="與規劃 AI 討論">
    <div className="panel-heading"><div><h2>與規劃 AI 討論</h2><p>依據{revision ? `版本 ${revision}` : '任務需求'} · 基底 {plan?.baseCommit?.slice(0,10) || '專案目前基底'}</p></div>
      <button className="icon-button" onClick={onClose} aria-label="關閉計畫討論">✕</button></div>
    <p className="footnote">討論不會核准或啟動實作。產生草案後，可預覽並套用為新版。</p>
    {dirty && <div className="info-banner">目前有未儲存的編輯。討論以已儲存版本為準，請先儲存或重新讀取，再套用草案。</div>}
    {(error || resource.error) && <div role="alert" className="error-banner">{error || resource.error}<button onClick={()=>void initialize()}>重新載入</button></div>}
    {!d && !error && <p role="status">載入計畫討論…</p>}
    <div className="plan-discussion-messages" aria-live="polite" aria-relevant="additions text">
      {d?.messages.length===0 && <p className="muted">可詢問修改範圍、比較方案，或補充需求。例如：「這個步驟會影響哪些頁面？」</p>}
      {d?.messages.map(message=>{
        const stale = message.planContext?.taskVersion !== task.version || message.planContext?.revision !== (task.plan?.revision ?? 0);
        return <article key={message.id} className={'plan-discussion-message '+message.role}>
          <div className="chat-message-meta"><strong>{message.role==='user'?'你':selectionText(message.selection || d.selection)}</strong>
            {message.planContext && <span>{message.planContext.revision ? `依據版本 ${message.planContext.revision}` : '依據任務需求'}</span>}</div>
          <ProseContent content={message.content}/>
          {message.proposedPlan && <div className="plan-proposal">
            <details><summary>查看修訂前版本 {message.planContext?.revision}</summary><ProseContent content={message.planContext?.text || '尚無原始計畫'}/></details>
            <details><summary>預覽完整修訂計畫</summary><ProseContent content={message.proposedPlan}/></details>
            <button className="primary" disabled={busy || dirty || stale || !editablePlan.includes(task.status)} onClick={()=>void apply(message)}>
              套用為版本 {(task.plan?.revision ?? 0)+1}
            </button>
            <p className="footnote">{stale?'來源任務或計畫已變更，請依最新版本重新產生草案。':!editablePlan.includes(task.status)?'目前任務狀態不能修改計畫；仍可繼續討論。':'套用會保留舊版本；核准執行仍需另外操作。'}</p>
          </div>}
        </article>;
      })}
      {running && <article className="plan-discussion-message assistant"><strong role="status">AI 處理中…</strong>
        {live?.content && <ProseContent content={live.content}/>}
        <LiveActivity activity={live?.activity} waiting={live?.permissions.some(p=>p.state==='Pending')} error={progress.error}/>
        <ToolPermissions discussionId={task.id} operationId={d!.activeOperation!} requests={live?.permissions || []} onAnswered={progress.refresh}/>
      </article>}
      {d?.error && <div role="alert" className="error-banner">{d.error} 訊息已保留，可重新提問或產生草案。</div>}
      <div ref={tail}/>
    </div>
    {d && <form className="plan-discussion-composer" onSubmit={e=>{e.preventDefault();if (!busy && text.trim()) void send(false);}}>
      <button type="button" className="secondary" aria-expanded={choosing} onClick={()=>setChoosing(!choosing)}>{selectionText(selection ?? d.selection)} ▾</button>
      {choosing && <div className="plan-discussion-models">
        {providers.error && <p role="alert">{providers.error}</p>}
        <AiChoice label="計畫討論" providers={providers.data || []} value={selection ?? d.selection} onChange={s=>setSelection(s)}/>
        <button type="button" className="secondary" onClick={()=>setChoosing(false)}>完成</button>
      </div>}
      <textarea aria-label="計畫討論訊息" rows={3} maxLength={20000} value={text} onChange={e=>setText(e.target.value)} placeholder="針對這份計畫提問或補充修改需求…"/>
      <div className="button-row">
        {running?<button type="button" className="danger-button" disabled={action.busy} onClick={()=>void action.run(async()=>{await api(base+'/stop','POST',{operationId:d.activeOperation});resource.refresh();})}>停止回覆</button>
          :<><button className="primary" disabled={busy || !text.trim()}>傳送</button>
            <button type="button" className="secondary" disabled={busy || dirty} onClick={()=>void send(true)}>產生修訂草案</button></>}
      </div>
      <p className="footnote">可隨時切換模型；每次回覆會使用模型額度。附上所選版本、需求、驗收條件與對話紀錄。</p>
    </form>}
  </aside>;
}
