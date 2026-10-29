import { Fragment, useCallback, useEffect, useRef, useState } from 'react';
import { api } from './api';
import { useResource } from './hooks';
import { filterTasks, formatDate, groups, selectionText, statusNames } from './domain';
import { Badge, Empty, Heading, Meta, NoticeProvider, useNotice } from './components/ui';
import { ProjectDialog, ProjectSettings, TaskDialog } from './components/TaskDialogs';
import { AiConnections } from './components/AiConnections';
import { TaskDetail } from './components/TaskDetail';
import { RulesLibrary } from './components/RulesLibrary';
import { StoragePage } from './components/StoragePage';
import { WorkerConnection } from './components/WorkerStatus';
import { PermissionSettings } from './components/PermissionSettings';
import { PendingPermissions } from './components/PendingPermissions';
import { Discussions } from './components/Discussions';
import {ConversationTask} from './components/ConversationTask';
import {openTaskConversation} from './components/TaskDelivery';
import {useAction} from './hooks';
import { TitleActivity, taskTitleState } from './components/TitleActivity';
import type { BaseData, Project, WorkTask } from './types';

function useHash() {
  const [hash, setHash] = useState(location.hash.slice(1) || 'discussions');
  useEffect(() => {
    const handleHashChange = () => setHash(location.hash.slice(1) || 'discussions');
    window.addEventListener('hashchange', handleHashChange);
    return () => window.removeEventListener('hashchange', handleHashChange);
  }, []);
  return hash;
}

function TaskTable({
  tasks,
  projects,
  onCreate,
}: {
  tasks: WorkTask[];
  projects: Project[];
  onCreate: () => void;
}) {
  const [expanded,setExpanded]=useState<string|null>(null);
  const notice=useNotice(),action=useAction(notice);
  if (!tasks.length) {
    return (
      <Empty title="這裡還沒有任務" description="先開新對話討論需求，確認計畫後再開始實作。">
        <button className="primary" onClick={onCreate}>
          開始第一個對話
        </button>
      </Empty>
    );
  }

  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            {['任務 / 專案', '目前狀態', '規劃 → 實作流程', '建立時間', '操作'].map((headerLabel, columnIndex) => (
              <th key={columnIndex}>{headerLabel}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {tasks.map(taskItem => {
            const needsAction =
              groups.review.includes(taskItem.status) || groups.failed.includes(taskItem.status);
            const matchedProject = projects.find(projectItem => projectItem.id === taskItem.projectId);
            return (
              <Fragment key={taskItem.id}><tr className={needsAction ? 'row-highlight' : ''}>
                <td>
                  <a className="task-title" href={'#task/' + taskItem.id} onClick={event=>{event.preventDefault();setExpanded(expanded===taskItem.id?null:taskItem.id);}}>
                    {taskItem.title}
                    <TitleActivity state={taskTitleState(taskItem.status)} compact />
                  </a>
                  <div className="subline">{matchedProject?.name || '未指定專案'}</div>
                </td>
                <td>
                  <Badge status={taskItem.status} />
                </td>
                <td>
                  <div className="flow-summary">{selectionText(taskItem.ai?.planning)}</div>
                  <div className="subline">
                    →{' '}
                    {taskItem.workflow?.steps
                      ? taskItem.workflow.steps.map(stepItem => stepItem.name).join(' → ')
                      : selectionText(taskItem.ai?.implementation)}
                  </div>
                </td>
                <td>{formatDate(taskItem.createdAt)}</td>
                <td>
                  <div className="button-row"><button className="secondary" disabled={action.busy} onClick={()=>void action.run(()=>openTaskConversation(taskItem.id))}>回到對話</button>
                  <button className={needsAction?'primary':'secondary'} aria-expanded={expanded===taskItem.id} onClick={()=>setExpanded(expanded===taskItem.id?null:taskItem.id)}>{['AwaitingReview','Approved'].includes(taskItem.status)?'驗收與預覽':'查看進度'}</button></div>
                </td>
              </tr>{expanded===taskItem.id&&<tr><td colSpan={5}><ConversationTask taskId={taskItem.id} project={matchedProject} busy={action.busy} onResume={()=>void action.run(()=>openTaskConversation(taskItem.id))}/></td></tr>}</Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function Workbench() {
  const cachedProvidersRef = useRef<BaseData['providers'] | null>(null);
  const loadBase = useCallback(async (signal: AbortSignal): Promise<BaseData> => {
    const [projects, tasks, providers] = await Promise.all([
      api<BaseData['projects']>('/api/projects', 'GET', undefined, { signal }),
      api<BaseData['tasks']>('/api/tasks', 'GET', undefined, { signal }),
      cachedProvidersRef.current
        ? Promise.resolve(cachedProvidersRef.current)
        : api<BaseData['providers']>('/api/ai/providers', 'GET', undefined, { signal }),
    ]);
    cachedProvidersRef.current = providers;
    return { projects, tasks, providers };
  }, []);

  const base = useResource('base', loadBase);
  const hash = useHash();
  const notice = useNotice();
  const previousTaskStates=useRef(new Map<string,string>());
  useEffect(()=>{
    if(!base.data)return;
    for(const task of base.data.tasks){
      const previous=previousTaskStates.current.get(task.id);
      if(task.workflow?.simpleMode&&previous&&previous!==task.status){
        if(task.status==='AwaitingReview')notice(task.title+'：自動驗證已通過，請進行人工測試與成果審核。');
        else if(task.status==='AwaitingStepApproval')notice(task.title+'：主管需要你確認，請查看任務問題。');
      }
      previousTaskStates.current.set(task.id,task.status);
    }
  },[base.data,notice]);
  const [project, setProject] = useState('');
  const [search, setSearch] = useState('');
  const [group, setGroup] = useState('');
  const [status, setStatus] = useState('');
  const [dialog, setDialog] = useState<'task' | 'project' | null>(null);
  const [settings, setSettings] = useState<Project | null>(null);

  const data = base.data || { projects: [], tasks: [], providers: [] };
  const match = /^task\/([a-f0-9-]{36})$/i.exec(hash);
  const taskId = match?.[1].toLowerCase();
  const discussionId=/^discussion\/([a-f0-9-]{36})$/i.exec(hash)?.[1].toLowerCase();
  const view = taskId
    ? 'detail'
    : discussionId ? 'discussions' : ['discussions','overview', 'tasks', 'projects', 'ai', 'rules', 'storage', 'permissions'].includes(hash)
      ? hash
      : 'overview';

  const scoped = filterTasks(data.tasks, { project });
  const visible = filterTasks(data.tasks, { project, search, group, status });
  const reviewCount = scoped.filter(taskItem => groups.review.includes(taskItem.status)).length;

  function newTask() {
    if (!data.projects.length) {
      notice('請先登錄本地專案。');
      location.hash = '#projects';
      return;
    }
    location.hash = '#discussions';
  }

  const heading = {
    discussions: '對話與計畫',
    overview: '總覽',
    tasks: '任務工作台',
    projects: '專案',
    ai: 'AI 連線',
    detail: '任務詳情',
    rules: '規則庫',
    storage: '儲存空間',
    permissions: '工具權限設定',
  }[view];

  const metrics = [
    ['任務總數', scoped.length, '目前專案範圍', '◈', ''],
    [
      '執行與排隊',
      scoped.filter(taskItem => groups.active.includes(taskItem.status)).length,
      '規劃與 AI 指揮實作',
      '↗',
      'blue',
    ],
    ['待人工確認', reviewCount, '計畫確認、超規問題與最終驗收', '◷', 'amber'],
    [
      '已完成合併',
      scoped.filter(taskItem => taskItem.status === 'Merged').length,
      '已合併回本地分支',
      '✓',
      '',
    ],
  ];

  const attentionTasks = scoped.filter(
    taskItem => groups.review.includes(taskItem.status) || groups.failed.includes(taskItem.status)
  );
  const activeTasks = scoped.filter(taskItem => groups.active.includes(taskItem.status));

  return (
    <>
      <aside className="sidebar">
        <a className="brand" href="#overview">
          <span className="brand-mark">D</span>DevFlow<span className="version">0.7</span>
        </a>
        <div className="workspace-label">本地開發空間</div>
        <nav aria-label="主要導覽">
          {[
            ['discussions', '✦', '對話與計畫'],
            ['overview', '◈', '總覽'],
            ['tasks', '▤', '任務工作台'],
            ['rules', '≡', '規則庫'],
            ['projects', '⌘', '專案'],
            ['ai', '◎', 'AI 連線'],
            ['storage', '▣', '儲存空間'],
            ['permissions', '⚿', '工具權限'],
          ].map(([navigationId, iconSymbol, labelText]) => (
            <a
              key={navigationId}
              href={'#' + navigationId}
              className={(view === 'detail' ? 'tasks' : view) === navigationId ? 'active' : ''}
            >
              <span>{iconSymbol}</span>
              {labelText}
              {navigationId === 'tasks' && reviewCount > 0 && (
                <span className="nav-count">{reviewCount}</span>
              )}
            </a>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <span className="local-dot" /> CLI 驅動 · 人工審核
          <p>每個成果，都有測試證據。</p>
        </div>
      </aside>

      <div className="app-shell">
        <header className="topbar">
          <div className="breadcrumb">
            工作空間 <span>/</span>
            <strong>{heading}</strong>
          </div>
          <div className="topbar-actions">
            <select
              aria-label="篩選專案"
              value={project}
              onChange={event => setProject(event.target.value)}
            >
              <option value="">所有專案</option>
              {data.projects.map(projectItem => (
                <option key={projectItem.id} value={projectItem.id}>
                  {projectItem.name}
                </option>
              ))}
            </select>
            <div className="topbar-status-group">
              <span className={'connection' + (base.error ? ' error' : '')}>
              <i />
              {base.error ? 'API 未連線' : base.data ? 'API 已連線' : '連線中'}
            </span>
            <WorkerConnection/>
            <PendingPermissions/>
            </div>
  
            <button className="primary topbar-new-chat-btn" disabled={!data.projects.length} onClick={newTask}>
              ＋ 新對話
            </button>
          </div>
        </header>

        <main>

          {view==='discussions'&&base.data&&<Discussions data={data} projectId={project} discussionId={discussionId} onTaskCreated={task=>{base.refresh();location.hash='#task/'+task.id;}}/>}
          {base.error && (
            <div className="offline">
              <strong>{base.error}</strong>
              <span>正在編輯的內容會保留。</span>
              <button className="secondary" onClick={base.refresh}>
                重新連線
              </button>
            </div>
          )}
          {!base.data && !base.error && <Empty title="讀取工作空間…" />}

          {view === 'overview' && (
            <>
              <Heading
                eyebrow="WORKSPACE OVERVIEW"
                title="開發總覽"
                description="把規劃、實作與審核放在同一條流程上。"
              >
                <button className="secondary" onClick={base.refresh}>
                  ↻ 更新狀態
                </button>
              </Heading>
              <div className="metric-grid">
                {metrics.map(([labelText, metricValue, noteText, iconSymbol, toneClass]) => (
                  <div className={'metric ' + toneClass} key={String(labelText)}>
                    <div className="metric-label">{labelText}</div>
                    <strong>{metricValue}</strong>
                    <div className="metric-note">{noteText}</div>
                    <span className="metric-icon">{iconSymbol}</span>
                  </div>
                ))}
              </div>
              <div className="overview-grid">
                <article className="panel">
                  <div className="panel-heading">
                    <div>
                      <h2>待你處理 ({attentionTasks.length})</h2>
                      <p>人工核准或處理異常後，流程才會繼續推進。</p>
                    </div>
                    <a className="text-link" href="#tasks">
                      查看全部任務 →
                    </a>
                  </div>
                  {attentionTasks.slice(0, 6).map(taskItem => (
                    <div className="attention-item" key={taskItem.id}>
                      <div className="attention-main">
                        <div className="attention-top">
                          <Badge status={taskItem.status} />
                          <span className="subline">
                            {data.projects.find(projectItem => projectItem.id === taskItem.projectId)?.name}
                          </span>
                        </div>
                        <a href={'#task/' + taskItem.id}>
                          <strong>{taskItem.title}</strong>
                          <p>
                            {taskItem.status === 'AwaitingApproval'
                              ? 'AI 規劃已完成，等待你確認計畫'
                              : taskItem.status === 'AwaitingStepApproval'
                                ? '超出規則問題或階段確認點，等待你回覆確認'
                                : taskItem.status === 'AwaitingReview'
                                  ? '全部實作完成，請檢查差異並驗收'
                                  : taskItem.status === 'Approved'
                                    ? '驗收已核准，可執行本地分支合併'
                                    : '執行或規劃受阻，請檢查紀錄並重試'}
                          </p>
                        </a>
                      </div>
                      <a className="action-pill" href={'#task/' + taskItem.id}>
                        處理 →
                      </a>
                    </div>
                  ))}
                  {!attentionTasks.length && (
                    <Empty
                      title="目前沒有待處理事項"
                      description="新的計畫確認、超規提問或待驗收成果會顯示在這裡。"
                    />
                  )}
                </article>

                <article className="panel flow-panel">
                  <div className="panel-heading">
                    <div>
                      <h2>執行中與佇列 ({activeTasks.length})</h2>
                      <p>規劃 AI 總指揮與分階段實作進度。</p>
                    </div>
                    <span className="chip blue">AI 指揮實作</span>
                  </div>
                  {activeTasks.length > 0 ? (
                    <div className="active-task-list">
                      {activeTasks.slice(0, 4).map(activeTask => (
                        <div className="attention-item" key={activeTask.id}>
                          <div className="attention-main">
                            <div className="attention-top">
                              <Badge status={activeTask.status} />
                              <span className="subline">
                                {data.projects.find(projectItem => projectItem.id === activeTask.projectId)?.name}
                              </span>
                            </div>
                            <a href={'#task/' + activeTask.id}>
                              <strong>{activeTask.title}</strong>
                              <TitleActivity state={taskTitleState(activeTask.status)} compact />
                            </a>
                          </div>
                          <a className="text-link" href={'#task/' + activeTask.id}>
                            監看 →
                          </a>
                        </div>
                      ))}
                    </div>
                  ) : (
                    <ol className="workflow">
                      {[
                        ['01', 'AI 規劃 → 你確認', '綁定專案與規則，由規劃 AI 擬定計畫交由你確認。'],
                        ['02', 'AI 指揮 AI 實作', '依序執行前後端模型；規則內問題由規劃 AI 直接回覆，超出規則才請你確認。'],
                        ['03', '最終驗收與本地合併', '檢視完整程式碼差異，驗收確認後合併回本地端分支。'],
                      ].map(([stepNumber, titleText, descriptionText]) => (
                        <li key={stepNumber}>
                          <span>{stepNumber}</span>
                          <div>
                            <strong>{titleText}</strong>
                            <p>{descriptionText}</p>
                          </div>
                        </li>
                      ))}
                    </ol>
                  )}
                  <p className="footnote">排隊中的任務需要 Worker 啟動後才會執行。</p>
                </article>
              </div>

              <article className="panel table-panel">
                <div className="panel-heading table-panel-header">
                  <div>
                    <h2>最近任務</h2>
                    <p>點擊任務進入計畫、執行紀錄與差異審核。</p>
                  </div>
                  <a className="text-link" href="#tasks">
                    前往任務工作台 →
                  </a>
                </div>
                <TaskTable tasks={scoped.slice(0, 8)} projects={data.projects} onCreate={newTask} />
              </article>
            </>
          )}

          {view === 'tasks' && (
            <>
              <Heading
                eyebrow="TASK WORKSPACE"
                title="任務工作台"
                description="從需求到成果，每個階段都可追溯。"
              ><details><summary>進階建立方式</summary><button className="secondary" disabled={!data.projects.length} onClick={()=>setDialog('task')}>使用完整任務設定</button></details></Heading>
              <div className="filter-bar">
                <div className="tabs">
                  {[
                    ['', '全部 (' + scoped.length + ')'],
                    [
                      'active',
                      '執行中 (' +
                        scoped.filter(taskItem => groups.active.includes(taskItem.status)).length +
                        ')',
                    ],
                    ['review', '待審核 (' + reviewCount + ')'],
                    [
                      'failed',
                      '需處理 (' +
                        scoped.filter(taskItem => groups.failed.includes(taskItem.status)).length +
                        ')',
                    ],
                    [
                      'done',
                      '已結束 (' +
                        scoped.filter(taskItem => groups.done.includes(taskItem.status)).length +
                        ')',
                    ],
                  ].map(([groupKey, labelText]) => (
                    <button
                      key={groupKey}
                      className={group === groupKey ? 'active' : ''}
                      aria-pressed={group === groupKey}
                      onClick={() => setGroup(groupKey)}
                    >
                      {labelText}
                    </button>
                  ))}
                </div>
                <div className="filters">
                  <input
                    aria-label="搜尋任務"
                    placeholder="搜尋任務名稱、需求或 ID"
                    value={search}
                    onChange={event => setSearch(event.target.value)}
                  />
                  <select
                    aria-label="任務狀態"
                    value={status}
                    onChange={event => setStatus(event.target.value)}
                  >
                    <option value="">所有狀態</option>
                    {Object.entries(statusNames).map(([statusKey, labelText]) => (
                      <option key={statusKey} value={statusKey}>
                        {labelText}
                      </option>
                    ))}
                  </select>
                </div>
              </div>
              <article className="panel table-panel">
                {!visible.length && data.tasks.length ? (
                  <Empty title="沒有符合條件的任務" description="試著調整專案、搜尋或狀態條件。" />
                ) : (
                  <TaskTable tasks={visible} projects={data.projects} onCreate={newTask} />
                )}
                <div className="table-footer">共 {visible.length} 個任務 · 每五秒更新</div>
              </article>
            </>
          )}

          {view === 'projects' && (
            <>
              <Heading
                eyebrow="PROJECTS"
                title="專案"
                description="連結本地 Repository，設定測試與 AI 預設。"
              >
                <button className="primary" onClick={() => setDialog('project')}>
                  ＋ 登錄專案
                </button>
              </Heading>
              <div className="project-grid">
                {!data.projects.length && <Empty title="登錄你的第一個專案" />}
                {data.projects.map(projectItem => (
                  <article className="panel project-card" key={projectItem.id}>
                    <div className="project-heading">
                      <div className="project-icon">⌘</div>
                      <div>
                        <h2>{projectItem.name}</h2>
                        <p>
                          {projectItem.baseBranch} ·{' '}
                          {data.tasks.filter(taskItem => taskItem.projectId === projectItem.id).length} 個任務
                        </p>
                      </div>
                    </div>
                    <div className="project-path">{projectItem.repositoryPath}</div>
                    <div className="project-defaults">
                      <Meta label="規劃預設" value={selectionText(projectItem.aiDefaults?.planning)} />
                      <Meta
                        label="實作預設"
                        value={selectionText(projectItem.aiDefaults?.implementation)}
                      />
                    </div>
                    <div className="button-row">
                      <button
                        className="secondary"
                        onClick={() => {
                          setProject(projectItem.id);
                          location.hash = '#tasks';
                        }}
                      >
                        查看任務
                      </button>
                      <button className="secondary" onClick={() => setSettings(projectItem)}>
                        專案設定
                      </button>
                    </div>
                  </article>
                ))}
              </div>
            </>
          )}

          {view === 'storage' && <StoragePage />}
          {view === 'rules' && <RulesLibrary projects={data.projects} onChanged={base.refresh} />}
          {view === 'ai' && <AiConnections providers={data.providers} />}
          {view === 'permissions' && <PermissionSettings projects={data.projects} tasks={data.tasks} projectId={project} />}
          {taskId && (
            <TaskDetail key={taskId} taskId={taskId} projects={data.projects} onChanged={base.refresh} />
          )}
        </main>
        <footer>DevFlow v0.7 · React 工作台 · 成果由你核准</footer>
      </div>

      {dialog === 'task' && (
        <TaskDialog
          data={data}
          projectId={project}
          onClose={() => setDialog(null)}
          onCreated={createdTask => {
            setDialog(null);
            base.refresh();
            location.hash = '#task/' + createdTask.id;
          }}
        />
      )}
      {dialog === 'project' && (
        <ProjectDialog
          onClose={() => setDialog(null)}
          onCreated={createdProject => {
            setProject(createdProject.id);
            setDialog(null);
            base.refresh();
          }}
        />
      )}
      {settings && (
        <ProjectSettings
          project={settings}
          data={data}
          onClose={() => setSettings(null)}
          onSaved={base.refresh}
        />
      )}
    </>
  );
}

export default function App() {
  return (
    <NoticeProvider>
      <Workbench />
    </NoticeProvider>
  );
}
