import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../api';
import { useAction, useResource } from '../hooks';
import { formatDate, providerNames, selectableModels, selectionText } from '../domain';
import { AiChoice, Empty, ProseContent, useNotice } from './ui';
import { TaskDialog } from './TaskDialogs';
import { ToolPermissions } from './ToolPermissions';
import { LiveActivity } from './LiveActivity';
import { TitleActivity, useActivityPageTitle } from './TitleActivity';
import { FailureRecovery } from './FailureRecovery';
import { DiscussionRecords } from './DiscussionRecords';
import { StartDiscussion } from './StartDiscussion';
import {ConversationTask} from './ConversationTask';
import { ImageThumbnailStrip, ImageUploadButton, MessageImages, useImageAttachments } from './ImageAttachments';
import type { ToolPermission } from './ToolPermissions';
import type { BaseData, Discussion, Provider, ProviderId, Selection, TaskSeed, WorkTask, RuntimeSnapshot } from '../types';

function remembered(key: string, fallback?: Selection | null): Selection {
  try {
    const raw = localStorage.getItem('devflow.chat.' + key);
    if (raw) {
      const s = JSON.parse(raw);
      if (['codex', 'antigravity', 'claude', 'gemini'].includes(s.provider)) return s;
    }
  } catch {}
  return fallback || { provider: 'codex', model: null };
}

function remember(key: string, s: Selection) {
  try {
    localStorage.setItem('devflow.chat.' + key, JSON.stringify(s));
  } catch {}
}

function ModelPicker({
  providers,
  selection,
  onSelect,
  onClose,
}: {
  providers: Provider[];
  selection: Selection;
  onSelect: (selectedOption: Selection) => void;
  onClose: () => void;
}) {
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    function handleClickOutside(event: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(event.target as Node)) {
        onClose();
      }
    }
    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [onClose]);

  return (
    <div
      ref={containerRef}
      className="chat-model-menu"
      role="dialog"
      aria-label="選擇對話模型"
      onKeyDown={event => {
        if (event.key === 'Escape') onClose();
      }}
    >
      <AiChoice
        label="對話"
        providers={providers}
        value={selection}
        onChange={selectedOption => {
          if (selectedOption) onSelect(selectedOption);
        }}
      />
      <button type="button" className="secondary" onClick={onClose}>
        完成
      </button>
    </div>
  );
}

function Composer({
  data,
  selection,
  onSelection,
  text,
  onText,
  onSend,
  busy,
  onStop,
  pickerRequest = 0,
  onCommitSelection,
  attachments,
  hasImageHistory = false,
}: {
  data: BaseData;
  selection: Selection;
  onSelection: (selectedOption: Selection) => void;
  text: string;
  onText: (value: string) => void;
  onSend: () => void;
  busy: boolean;
  onStop?: () => void;
  pickerRequest?: number;
  onCommitSelection?:()=>void;
  attachments: ReturnType<typeof useImageAttachments>;
  hasImageHistory?: boolean;
}) {
  const [choosing, setChoosing] = useState(false);
  const canSend =
    Boolean(text.trim() || attachments.images.length) &&
    !attachments.reading &&
    !(selection.provider === 'antigravity' && (attachments.images.length > 0 || hasImageHistory));

  useEffect(() => {
    if (pickerRequest) setChoosing(true);
  }, [pickerRequest]);

  return (
    <form
      className="chat-composer"
      onPaste={event => {
        if (!busy) attachments.paste(event);
      }}
      onDragOver={event => {
        if (event.dataTransfer.types.includes('Files')) event.preventDefault();
      }}
      onDrop={event => {
        event.preventDefault();
        if (!busy) attachments.drop(event);
      }}
      onSubmit={event => {
        event.preventDefault();
        if (!busy && canSend) onSend();
      }}
    >
      <ImageThumbnailStrip
        draft={attachments}
        busy={busy}
        provider={selection.provider}
        hasHistory={hasImageHistory}
      />
      <textarea
        aria-label="討論訊息"
        rows={3}
        maxLength={20000}
        value={text}
        onChange={event => onText(event.target.value)}
        placeholder="傳送訊息，聊聊這個專案…"
        onKeyDown={event => {
          if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
            event.preventDefault();
            if (!busy && canSend) onSend();
          }
        }}
      />
      <div className="chat-composer-toolbar">
        <div className="chat-composer-toolbar-left">
          <ImageUploadButton draft={attachments} busy={busy} />
          <div className="chat-model-control">
            <button
              type="button"
              className="chat-model-button"
              data-provider={selection.provider}
              aria-expanded={choosing}
              onClick={() => setChoosing(!choosing)}
            >
              <span className="model-dot" />
              <span className="model-label-text">{selectionText(selection)}</span>
              <span className="model-chevron">▾</span>
            </button>
            {choosing && (
              <ModelPicker
                providers={data.providers}
                selection={selection}
                onSelect={selectedOption => onSelection(selectedOption)}
                onClose={() => {setChoosing(false);onCommitSelection?.();}}
              />
            )}
          </div>
        </div>
        <div className="chat-composer-toolbar-right">
          {busy ? (
            <button
              type="button"
              className="danger-button chat-stop-button"
              disabled={!onStop}
              onClick={onStop}
            >
              ■ 停止回覆
            </button>
          ) : (
            <button
              className="primary chat-send-button"
              disabled={!canSend}
              aria-label="傳送訊息"
            >
              ↑ 傳送
            </button>
          )}
        </div>
      </div>
    </form>
  );
}

function NewConversation({ data, projectId }: { data: BaseData; projectId: string }) {
  const attachments=useImageAttachments();
  const initial = projectId || data.projects[0]?.id || '';
  const [project, setProject] = useState(initial);
  const [text, setText] = useState('');
  const [selection, setSelection] = useState(() =>
    remembered('project.' + initial, data.projects.find(p => p.id === initial)?.aiDefaults?.planning)
  );
  const notice = useNotice();
  const action = useAction(notice);
  const createdConversation = useRef<Discussion | null>(null);

  async function send() {
    await action.run(async () => {
      const created =
        createdConversation.current || (await api<Discussion>('/api/discussions', 'POST', { projectId: project, selection,agentMode:true }));
      createdConversation.current = created;
      await api('/api/discussions/' + created.id + '/messages?background=1', 'POST', {
        expectedVersion: created.version,
        message: text,
        selection,
        ...(attachments.images.length?{images:attachments.images}:{}),
      });
      location.hash = '#discussion/' + created.id;
    });
  }

  if (!data.projects.length) return <Empty title="先新增一個專案" description="指定專案後，就可以開始對話。" />;

  return (
    <section className="chat-room chat-room-new">
      <div className="chat-welcome">
        <div className="chat-welcome-icon">✦</div>
        <h1>想聊聊什麼？</h1>
        <p>選專案、選主 AI，直接說需求。討論、實作、修正都在這裡完成。</p>
        <div className="chat-project-selector">
          <label>
            專案
            <select
              value={project}
              disabled={action.busy}
              onChange={e => {
                createdConversation.current = null;
                setProject(e.target.value);
                setSelection(
                  remembered(
                    'project.' + e.target.value,
                    data.projects.find(p => p.id === e.target.value)?.aiDefaults?.planning
                  )
                );
              }}
            >
              {data.projects.map(p => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </label>
        </div>
      </div>
      <Composer
        attachments={attachments}
        data={data}
        selection={selection}
        onSelection={s => {
          setSelection(s);
          remember('project.' + project, s);
        }}
        text={text}
        onText={setText}
        onSend={() => void send()}
        busy={action.busy}
      />
    </section>
  );
}

function Conversation({
  id,
  data,
  onTaskCreated,
  onUpdated,
}: {
  id: string;
  data: BaseData;
  onTaskCreated: (t: WorkTask) => void;
  onUpdated:()=>void;
}) {
  const resource = useResource(
    'discussion:' + id,
    useCallback((signal: AbortSignal) => api<Discussion>('/api/discussions/' + id, 'GET', undefined, { signal }), [id]),
    1000
  );
  const [accepted, setAccepted] = useState<Discussion | null>(null);
  const d = accepted && accepted.version > (resource.data?.version ?? -1) ? accepted : resource.data;
  useEffect(()=>{if(d)onUpdated();},[d?.version,onUpdated]);
  const [text, setText] = useState('');
  const attachments=useImageAttachments();
  const [selection, setSelection] = useState<Selection>({ provider: 'codex', model: null });
  const [seed, setSeed] = useState<TaskSeed>();
  const [extras, setExtras] = useState(false);
  const [planVisible,setPlanVisible]=useState(true),[starting,setStarting]=useState(false);
  const [renaming, setRenaming] = useState(false);
  const [pickerRequest,setPickerRequest]=useState(0),[recordsOpen,setRecordsOpen]=useState(false);
  const [title, setTitle] = useState('');
  const initialized = useRef(false);
  const newConversationRef=useRef<Discussion|null>(null);
  const proposalId=d?.messages.filter(m=>m.agentProposal).at(-1)?.id;
  useEffect(()=>{if(proposalId){setPlanVisible(true);setExtras(false);}},[proposalId]);
  const tail = useRef<HTMLDivElement>(null);
  const notice = useNotice();
  const action = useAction(notice);

  useEffect(() => {
    if (d && !initialized.current) {
      initialized.current = true;
      setSelection(d.agentMode?d.selection:remembered('discussion.' + id, d.selection));
    }
  }, [d, id]);

  const progress = useResource(
    'discussion-progress:' + id,
    useCallback(
      (signal: AbortSignal) =>
        api<{ operationId: string | null; content: string; selection: Selection | null; permissions?: ToolPermission[]; activity?: RuntimeSnapshot | null }>(
          '/api/discussions/' + id + '/progress',
          'GET',
          undefined,
          { signal }
        ),
      [id]
    ),
    500,
    !!d?.activeOperation
  );
  const boundTask=useResource('chat-bound-task:'+d?.activeTaskId,useCallback((signal:AbortSignal)=>api<WorkTask>('/api/tasks/'+d?.activeTaskId,'GET',undefined,{signal}),[d?.activeTaskId]),1500,!!d?.activeTaskId);

  const preview = progress.data?.operationId === d?.activeOperation ? progress.data?.content || '' : '';
  const busy = action.busy || !!d?.activeOperation;
  const titleState = !d?.activeOperation ? null : progress.error || resource.error ? {label:'連線中斷',problem:true} : progress.data?.operationId===d.activeOperation&&progress.data.permissions?.some(p=>p.state==='Pending') ? {label:'等待確認',waiting:true} : {label:'回覆中'};
  useActivityPageTitle(d?.title,titleState);
  const chosen = initialized.current ? selection : d?.selection || selection;

  useEffect(() => {
    tail.current?.scrollIntoView?.({ behavior: 'smooth', block: 'end' });
  }, [d?.messages.length, preview, progress.data?.permissions?.filter(p => p.state === 'Pending').map(p => p.id).join(',')]);

  function choose(s: Selection) {
    initialized.current = true;
    setSelection(s);
    remember('discussion.' + id, s);
    if (d) remember('project.' + d.projectId, s);
  }
  async function newTask(){
    if(!d)return;
    await action.run(async()=>{
      const fresh=newConversationRef.current||await api<Discussion>('/api/discussions','POST',{projectId:d.projectId,selection:chosen,agentMode:true});
      newConversationRef.current=fresh;
      if(text.trim()||attachments.images.length)await api('/api/discussions/'+fresh.id+'/messages?background=1','POST',{expectedVersion:fresh.version,message:text||'請查看附圖，協助規劃新任務。',selection:chosen,...(attachments.images.length?{images:attachments.images}:{})});
      attachments.clear();setText('');location.hash='#discussion/'+fresh.id;
    });
  }

  async function perform(kind: 'messages' | 'reply' | 'drafts' | 'execution-plan',intent?:'Start'|'Resume') {
    if (!d) return;
    if(d.agentMode&&kind==='messages'&&!intent&&/^(?:請)?另開(?:一個)?(?:獨立)?任務(?:[：:，,。\r\n]|$)/.test(text.trim())){await newTask();return;}
    await action.run(async () => {
      try {
        setAccepted(
          await api<Discussion>('/api/discussions/' + id + '/' + kind + '?background=1', 'POST', {
            expectedVersion: d.version,
            selection: chosen,
            ...(kind === 'messages' ? { message: text||(intent==='Start'?'開始實作':intent==='Resume'?'依討論繼續':''),...(intent?{intent}:{}) } : {}),
            ...(kind==='messages'&&attachments.images.length?{images:attachments.images}:{}),
          })
        );
        if (kind === 'messages'){setText('');attachments.clear();}
        if (kind === 'execution-plan'){setPlanVisible(true);setExtras(false);}
      } finally {
        resource.refresh();
      }
    });
  }

  if (!d) return <Empty title={resource.error || '讀取對話…'} />;
  const plan=d.messages.slice().reverse().find(m=>m.executionPlan&&m.proposedPlan);
  const planCurrent=!!plan&&d.messages.at(-1)?.id===plan.id;
  const showPlan=planVisible&&!!plan&&!extras&&(!d.agentMode||!!plan.agentProposal&&!plan.createdTaskId);
  const boundPlanChanged=!!plan?.agentProposal?.targetTaskId&&boundTask.data?.version!==plan.agentProposal.targetTaskVersion;
  const canProceed=!!plan?.agentProposal&&planCurrent&&!plan.createdTaskId&&!busy&&!text.trim()&&!attachments.images.length&&!attachments.reading&&!boundPlanChanged;
  const hasImageHistory=d.messages.some(m=>!!m.images?.length)||!!boundTask.data?.imageReferences?.length;

  return (
    <section className="chat-room">
      <header className="chat-room-header">
        <div className="chat-header-main">
          <div className="chat-header-project">
            <span className="project-badge">{data.projects.find(p => p.id === d.projectId)?.name}</span>
          </div>
          {renaming ? (
            <form
              className="chat-title-form"
              onSubmit={e => {
                e.preventDefault();
                void action.run(async () => {
                  setAccepted(
                    await api<Discussion>('/api/discussions/' + id + '/title', 'PUT', {
                      title,
                      expectedVersion: d.version,
                    })
                  );
                  setRenaming(false);
                  resource.refresh();
                });
              }}
            >
              <input
                aria-label="對話名稱"
                autoFocus
                required
                maxLength={200}
                value={title}
                onChange={e => setTitle(e.target.value)}
              />
              <button className="primary">儲存</button>
              <button type="button" className="secondary" onClick={() => setRenaming(false)}>
                取消
              </button>
            </form>
          ) : (
            <div className="chat-title-row"><button
              className="chat-title-button"
              title="重新命名對話"
              onClick={() => {
                setTitle(d.title);
                setRenaming(true);
              }}
            >
              <h1>{d.title}</h1>
              <span className="edit-icon" aria-hidden="true">✎</span>
            </button><TitleActivity state={titleState}/></div>
          )}
        </div>
        <div className="chat-header-actions">
          {d.agentMode?<>
            {plan?.agentProposal&&!plan.createdTaskId&&<button className="secondary" aria-expanded={showPlan} onClick={()=>{setPlanVisible(!showPlan);setExtras(false);}}>計畫表</button>}
          </>:<><button className="primary" disabled={busy||attachments.reading||attachments.images.length>0||!d.messages.some(m=>m.role==='assistant')||hasImageHistory&&chosen.provider==='antigravity'} onClick={()=>void perform('execution-plan')}>{plan?'更新實作計畫':'整理實作計畫'}</button>
            <button className="secondary" disabled={busy} onClick={()=>void action.run(async()=>{setAccepted(await api<Discussion>('/api/discussions/'+id+'/agent','PUT',{expectedVersion:d.version,selection:chosen}));resource.refresh();})}>改用單一對話</button></>}
          {!d.agentMode&&plan&&<button className="secondary" aria-expanded={showPlan} onClick={()=>{setPlanVisible(!showPlan);setExtras(false);}}>實作計畫</button>}
          <details><summary>進階</summary><button
            className={'secondary chat-extras-toggle' + (extras ? ' active' : '')}
            aria-expanded={extras}
            onClick={() => setExtras(!extras)}
          >
            進階：任務草稿與規則
            {d.drafts.length > 0 && <span className="draft-pill">{d.drafts.length}</span>}
          </button></details>
        </div>
      </header>
      {d.agentMode&&<div className="chat-task-binding">
        <span>{boundTask.data&&!['Merged','Cancelled'].includes(boundTask.data.status)?'接續任務：'+boundTask.data.title:boundTask.data?'上一任務已結束；確認下一份計畫會建立新任務。':'目前討論：確認計畫後建立任務。'}</span>
        <button className="secondary" disabled={busy||attachments.reading} title="另開獨立對話，從專案最新本地基底開始；已輸入的需求與圖片會送到新對話。" onClick={()=>void newTask()}>另開任務</button>
        {d.feedbackContext&&<small>驗收回報版本：{d.feedbackContext.resultCommit.slice(0,10)} · 可直接描述問題或貼圖</small>}
      </div>}

      {resource.error && (
        <div className="error-banner" role="status">
          <span>{resource.error}</span>
        </div>
      )}
      {d.error&&<FailureRecovery error={d.error} onRecords={()=>setRecordsOpen(true)} onModel={()=>setPickerRequest(v=>v+1)} onReply={!busy&&d.messages.at(-1)?.role==='user'?()=>void perform('reply'):undefined}/>}
      {recordsOpen&&<DiscussionRecords id={id} onClose={()=>setRecordsOpen(false)}/>}

      <div className={'chat-body' + (extras||showPlan ? ' with-extras' : '')}>
        <div className="chat-conversation">
          <div className="chat-messages" aria-live="polite" aria-relevant="additions text">
            {!d.messages.length && (
              <Empty
                title="直接開始提問"
                description="直接說需求，主 AI 會提出建議與計畫；確認後就在這裡實作。"
              />
            )}
            {d.messages.map(m => (
              <article key={m.id} className={'chat-message ' + m.role}>
                <div className="chat-message-meta">
                  <div className="sender-indicator" data-role={m.role}>
                    {m.role === 'user' ? (
                      <strong>👤 你</strong>
                    ) : (
                      <>
                        <span aria-hidden="true">🤖 </span>
                        <strong>{selectionText(m.selection || d.selection)}</strong>
                      </>
                    )}
                  </div>
                  <time>{formatDate(m.createdAt)}</time>
                </div>
                <ProseContent content={m.content} />
                <MessageImages discussionId={id} images={m.images}/>
                {m.feedbackContext&&<p className="footnote">此回報對應驗收版本 {m.feedbackContext.resultCommit.slice(0,10)}</p>}
                {d.agentMode&&m.agentProposal&&<button className="secondary" onClick={()=>{setPlanVisible(true);setExtras(false);}} disabled={m.id!==plan?.id||!!m.createdTaskId}>{m.createdTaskId?'計畫已執行':'查看計畫與模型分工 →'}</button>}
              </article>
            ))}
            {d.activeOperation && (
              <article className="chat-message assistant chat-pending">
                <div className="chat-message-meta">
                  <div className="sender-indicator" data-role="assistant">
                    <span aria-hidden="true">🤖 </span>
                    <strong>{selectionText(progress.data?.selection || d.selection)}</strong>
                  </div>
                  <span className="pulse-tag" role="status">
                    {progress.data?.permissions?.some(p=>p.state==='Pending')?'等待你的工具確認':d.agentMode&&!d.activeTaskId?'● 主 AI 分析與規劃中…':'● 思考並回覆中…'}
                  </span>
                </div>
                <ProseContent content={preview || '正在讀取專案並思考…'} />
                <LiveActivity key={d.activeOperation} activity={progress.data?.operationId===d.activeOperation?progress.data.activity:null} waiting={progress.data?.operationId===d.activeOperation&&progress.data.permissions?.some(p=>p.state==='Pending')} error={progress.error||resource.error}/>
                <ToolPermissions discussionId={id} operationId={d.activeOperation} requests={progress.data?.permissions||[]} onAnswered={progress.refresh}/>
              </article>
            )}
            {d.agentMode&&d.activeTaskId&&<ConversationTask taskId={d.activeTaskId} busy={busy} project={data.projects.find(p=>p.id===d.projectId)} onResume={()=>void perform('messages','Resume')}/>}
            <div ref={tail} />
          </div>

          <Composer
            attachments={attachments} hasImageHistory={hasImageHistory}
            onCommitSelection={()=>{if(d.agentMode&&JSON.stringify(chosen)!==JSON.stringify(d.selection))void action.run(async()=>{setAccepted(await api<Discussion>('/api/discussions/'+id+'/agent','PUT',{expectedVersion:d.version,selection:chosen}));resource.refresh();});}}
            data={data}
            pickerRequest={pickerRequest}
            selection={chosen}
            onSelection={choose}
            text={text}
            onText={setText}
            onSend={() => void perform('messages')}
            busy={busy}
            onStop={
              d.activeOperation
                ? () =>
                    void action.run(async () => {
                      try {
                        await api('/api/discussions/' + id + '/stop', 'POST', { operationId: d.activeOperation });
                      } finally {
                        resource.refresh();
                      }
                    })
                : undefined
            }
          />
        </div>

        {showPlan&&plan&&<aside className="chat-extras discussion-plan">
          <div className="extras-header"><h2>預計實作計畫</h2><p className="footnote">{d.agentMode?'確認後按 Proceed，或直接回覆「好，照這份計畫做」。也可以繼續提出調整。':'可繼續聊天調整，再按「更新實作計畫」。'}</p>
            {plan.agentProposal&&<p className="info-banner">{plan.agentProposal.targetTaskId?'接續目前任務，保留既有成果':'建立新任務'}{plan.agentProposal.taskReason&&'：'+plan.agentProposal.taskReason}</p>}
            {d.agentMode?<button className="primary" disabled={!canProceed} onClick={()=>void perform('messages','Start')}>Proceed · 確認計畫</button>:plan.createdTaskId?<a className="primary" href={'#task/'+plan.createdTaskId}>查看任務進度 →</a>:<button className="primary" disabled={busy||!planCurrent||!!plan.executionPlan?.openQuestions.length} onClick={()=>setStarting(true)}>選模型並開始</button>}
            {!planCurrent&&!plan.createdTaskId&&<p className="footnote">討論已有新內容，請主 AI 更新計畫後再確認。</p>}
            {boundPlanChanged&&!plan.createdTaskId&&<p className="footnote">任務版本已更新，請主 AI 重新確認延續計畫。</p>}
          </div>
          {plan.agentProposal&&<ol className="agent-model-flow" aria-label="預計模型流程"><li><strong>主 AI · 規劃與協調</strong><span>{selectionText(chosen)}</span></li>{plan.agentProposal.assignments.map((a,i)=><li key={i}><strong>{i+1}. {a.name}</strong><span>{selectionText(a.selection)}</span><small>{a.reason}</small></li>)}<li><strong>建置驗證 → 人工測試</strong><span>依專案驗證設定執行</span></li></ol>}
          {!!plan.executionPlan?.openQuestions.length&&<div className="info-banner"><strong>還需要釐清</strong><ul>{plan.executionPlan.openQuestions.map((q,i)=><li key={i}>{q}</li>)}</ul></div>}
          <div className="discussion-plan-document"><ProseContent content={plan.proposedPlan||''}/><h3>驗收條件</h3><ProseContent content={plan.executionPlan?.acceptanceCriteria||''}/></div>
        </aside>}
        {extras && (
          <aside className="chat-extras">
            <div className="extras-header">
              <h2>任務草稿</h2>
              <p className="footnote">聊出具體想法後，由 AI 自動整理成可追蹤的任務草稿。</p>
              <button
                className="warning-button full-width"
                disabled={busy || !d.messages.some(m => m.role === 'assistant')}
                onClick={() => void perform('drafts')}
              >
                ✨ 整理成任務 · 使用額度
              </button>
            </div>

            <div className="extras-draft-list">
              {!d.drafts.length && (
                <div className="draft-empty-state">
                  <p className="muted">尚無任務草稿。討論有結論後按上方按鈕產生。</p>
                </div>
              )}
              {d.drafts
                .slice()
                .reverse()
                .map(draft => (
                  <div className="draft-card" key={draft.id}>
                    <strong>{draft.title}</strong>
                    <p className="draft-desc">{draft.description}</p>
                    {!!draft.openQuestions?.length && (
                      <div className="draft-questions-box">
                        <span className="draft-warning">⚠ 有 {draft.openQuestions.length} 個待釐清問題</span>
                        <ul>
                          {draft.openQuestions.map((q, i) => (
                            <li key={i}>{q}</li>
                          ))}
                        </ul>
                      </div>
                    )}
                    <div className="draft-card-footer">
                      {draft.createdTaskId ? (
                        <a className="action-pill" href={'#task/' + draft.createdTaskId}>
                          查看已建立任務 →
                        </a>
                      ) : (
                        <button
                          className="primary"
                          disabled={busy}
                          onClick={() => setSeed({ ...draft, discussionId: id })}
                        >
                          預覽並建立任務
                        </button>
                      )}
                    </div>
                  </div>
                ))}
            </div>

            <details className="extras-rules-section">
              <summary>已綁定規則快照 · {d.rules.length}</summary>
              {!d.rules.length && <p className="muted">本次未選取共用規則。</p>}
              {d.rules.map(r => (
                <details key={r.id} className="history-item">
                  <summary>
                    {r.name} · v{r.version}
                  </summary>
                  <p>{r.content}</p>
                </details>
              ))}
            </details>
          </aside>
        )}
      </div>

      {seed && (
        <TaskDialog
          data={data}
          projectId={d.projectId}
          seed={seed}
          onClose={() => setSeed(undefined)}
          onCreated={t => {
            setSeed(undefined);
            resource.refresh();
            onTaskCreated(t);
            notice('任務已連結到此討論。');
          }}
        />
      )}
      {starting&&plan&&<StartDiscussion data={data} discussion={d} plan={plan} selection={chosen} onClose={()=>setStarting(false)} onStarted={t=>{setStarting(false);onTaskCreated(t);resource.refresh();}}/>}
    </section>
  );
}

export function Discussions({
  data,
  projectId,
  discussionId,
  onTaskCreated,
}: {
  data: BaseData;
  projectId: string;
  discussionId?: string;
  onTaskCreated: (t: WorkTask) => void;
}) {
  const list = useResource(
    'discussions',
    useCallback((signal: AbortSignal) => api<Discussion[]>('/api/discussions', 'GET', undefined, { signal }), [])
  );
  const [search, setSearch] = useState('');

  useEffect(() => {
    list.refresh();
  }, [discussionId, list.refresh]);

  const discussions = (list.data || [])
    .filter(d => (!projectId || d.projectId === projectId) && d.title.toLowerCase().includes(search.toLowerCase()))
    .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));

  return (
    <div className="chat-workspace">
      <aside className="chat-sidebar">
        <div className="chat-sidebar-heading">
          <h2>對話</h2>
          <a href="#discussions" className="primary" aria-label="新增對話">
            ＋ 新對話
          </a>
        </div>
        <input
          type="search"
          aria-label="搜尋對話"
          placeholder="搜尋對話主題…"
          value={search}
          onChange={e => setSearch(e.target.value)}
        />
        {list.error && <p className="error-banner">{list.error}</p>}
        <nav aria-label="對話列表">
          {discussions.map(d => {
            const project = data.projects.find(p => p.id === d.projectId);
            const isSelected = d.id === discussionId;
            return (
              <a
                className={'chat-thread' + (isSelected ? ' selected' : '')}
                aria-current={isSelected ? 'page' : undefined}
                key={d.id}
                href={'#discussion/' + d.id}
              >
                <div className="chat-thread-top">
                  <strong>{d.title}</strong>
                  <TitleActivity state={d.activeOperation?{label:'回覆中'}:null} compact/>
                </div>
                <div className="chat-thread-meta">
                  <span className="thread-project">{project?.name || '未指定專案'}</span>
                  {d.drafts?.length > 0 && <span className="thread-drafts">{d.drafts.length} 草稿</span>}
                </div>
              </a>
            );
          })}
          {list.data && !discussions.length && (
            <p className="muted">{search ? '找不到符合的對話' : '從第一個問題開始吧。'}</p>
          )}
        </nav>
      </aside>
      {discussionId ? (
        <Conversation key={discussionId} id={discussionId} data={data} onTaskCreated={onTaskCreated} onUpdated={list.refresh}/>
      ) : (
        <NewConversation key={projectId || data.projects[0]?.id || 'empty'} data={data} projectId={projectId} />
      )}
    </div>
  );
}
