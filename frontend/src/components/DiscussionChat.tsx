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
  pickerRequest=0,
}: {
  data: BaseData;
  selection: Selection;
  onSelection: (s: Selection) => void;
  text: string;
  onText: (s: string) => void;
  onSend: () => void;
  busy: boolean;
  onStop?: () => void;
  pickerRequest?: number;
}) {
  const [choosing, setChoosing] = useState(false);
  useEffect(()=>{if(pickerRequest)setChoosing(true);},[pickerRequest]);

  return (
    <form
      className="chat-composer"
      onSubmit={e => {
        e.preventDefault();
        if (!busy && text.trim()) onSend();
      }}
    >
      <textarea
        aria-label="討論訊息"
        rows={3}
        maxLength={20000}
        value={text}
        onChange={e => onText(e.target.value)}
        placeholder="傳送訊息，聊聊這個專案…"
        onKeyDown={e => {
          if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
            e.preventDefault();
            if (!busy && text.trim()) onSend();
          }
        }}
      />
      <div className="chat-composer-toolbar">
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
              onClose={() => setChoosing(false)}
            />
          )}
        </div>
        {busy ? (
          <button type="button" className="danger-button chat-stop-button" disabled={!onStop} onClick={onStop}>
            ■ 停止回覆
          </button>
        ) : (
          <button className="primary chat-send-button" disabled={!text.trim()} aria-label="傳送訊息">
            ↑ 傳送
          </button>
        )}
      </div>
      <p className="chat-composer-hint">Enter 傳送 · Shift＋Enter 換行 · 回覆會使用模型額度</p>
    </form>
  );
}

function NewConversation({ data, projectId }: { data: BaseData; projectId: string }) {
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
        createdConversation.current || (await api<Discussion>('/api/discussions', 'POST', { projectId: project, selection }));
      createdConversation.current = created;
      await api('/api/discussions/' + created.id + '/messages?background=1', 'POST', {
        expectedVersion: created.version,
        message: text,
        selection,
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
        <p>選擇專案，直接開始。對話依第一個問題自動命名。</p>
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
  const [selection, setSelection] = useState<Selection>({ provider: 'codex', model: null });
  const [seed, setSeed] = useState<TaskSeed>();
  const [extras, setExtras] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [pickerRequest,setPickerRequest]=useState(0),[recordsOpen,setRecordsOpen]=useState(false);
  const [title, setTitle] = useState('');
  const initialized = useRef(false);
  const tail = useRef<HTMLDivElement>(null);
  const notice = useNotice();
  const action = useAction(notice);

  useEffect(() => {
    if (d && !initialized.current) {
      initialized.current = true;
      setSelection(remembered('discussion.' + id, d.selection));
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

  async function perform(kind: 'messages' | 'reply' | 'drafts') {
    if (!d) return;
    await action.run(async () => {
      try {
        setAccepted(
          await api<Discussion>('/api/discussions/' + id + '/' + kind + '?background=1', 'POST', {
            expectedVersion: d.version,
            selection: chosen,
            ...(kind === 'messages' ? { message: text } : {}),
          })
        );
        if (kind === 'messages') setText('');
      } finally {
        resource.refresh();
      }
    });
  }

  if (!d) return <Empty title={resource.error || '讀取對話…'} />;

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
          <button
            className={'secondary chat-extras-toggle' + (extras ? ' active' : '')}
            aria-expanded={extras}
            onClick={() => setExtras(!extras)}
          >
            📋 任務草稿與規則
            {d.drafts.length > 0 && <span className="draft-pill">{d.drafts.length}</span>}
          </button>
        </div>
      </header>

      {resource.error && (
        <div className="error-banner" role="status">
          <span>{resource.error}</span>
        </div>
      )}
      {d.error&&<FailureRecovery error={d.error} onRecords={()=>setRecordsOpen(true)} onModel={()=>setPickerRequest(v=>v+1)} onReply={!busy&&d.messages.at(-1)?.role==='user'?()=>void perform('reply'):undefined}/>}
      {recordsOpen&&<DiscussionRecords id={id} onClose={()=>setRecordsOpen(false)}/>}

      <div className={'chat-body' + (extras ? ' with-extras' : '')}>
        <div className="chat-conversation">
          <div className="chat-messages" aria-live="polite" aria-relevant="additions text">
            {!d.messages.length && (
              <Empty
                title="直接開始提問"
                description="可以聊需求、比較做法，或請 AI 幫你讀專案。有共識後可隨時整理成任務草稿。"
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
                    {progress.data?.permissions?.some(p=>p.state==='Pending')?'等待你的工具確認':'● 思考並回覆中…'}
                  </span>
                </div>
                <ProseContent content={preview || '正在讀取專案並思考…'} />
                <LiveActivity key={d.activeOperation} activity={progress.data?.operationId===d.activeOperation?progress.data.activity:null} waiting={progress.data?.operationId===d.activeOperation&&progress.data.permissions?.some(p=>p.state==='Pending')} error={progress.error||resource.error}/>
                <ToolPermissions discussionId={id} operationId={d.activeOperation} requests={progress.data?.permissions||[]} onAnswered={progress.refresh}/>
              </article>
            )}
            <div ref={tail} />
          </div>

          <Composer
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
