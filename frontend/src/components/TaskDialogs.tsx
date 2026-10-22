import { useState } from 'react';
import { api } from '../api';
import { useAction } from '../hooks';
import { AiChoice, Modal, useNotice } from './ui';
import { TaskOptions, useRules } from './TaskOptions';
import { FolderPicker } from './FolderPicker';
import { workflowTemplate } from './WorkflowEditor';
import { cleanWorkflow } from './WorkflowPanel';
import { ProjectExecutionFields } from './ProjectExecutionFields';
import { readWorkflowPreference, saveWorkflowPreference } from '../workflowPreferences';
import type {
  BaseData,
  ExecutionPolicy,
  Project,
  Selection,
  TaskSeed,
  Workflow,
  WorkTask,
} from '../types';

export function TaskDialog({
  data,
  projectId,
  onClose,
  onCreated,
  seed,
}: {
  data: BaseData;
  projectId: string;
  onClose: () => void;
  onCreated: (task: WorkTask) => void;
  seed?: TaskSeed;
}) {
  const initialProjectId = projectId || data.projects[0]?.id || '';
  const [preference] = useState(() => readWorkflowPreference(initialProjectId));
  const [selectedProjectId, setSelectedProjectId] = useState(initialProjectId);
  const [title, setTitle] = useState(seed?.title || '');
  const [description, setDescription] = useState(seed?.description || '');
  const [criteria, setCriteria] = useState(seed?.acceptanceCriteria || '');
  const [autoStart, setAutoStart] = useState(!seed);
  const [planningSelection, setPlanningSelection] = useState<Selection | null>(
    preference?.planning ?? null
  );
  const [implementationSelection, setImplementationSelection] = useState<Selection | null>(
    preference?.implementation ?? null
  );
  const [ruleIds, setRuleIds] = useState<string[]>(
    seed?.ruleIds ??
      data.projects.find(projectItem => projectItem.id === initialProjectId)?.defaultRuleIds ??
      []
  );
  const [taskRules, setTaskRules] = useState(seed?.taskRules || '');
  const [policy, setPolicy] = useState<ExecutionPolicy>({
    tdd: false,
    allowedPaths: seed?.allowedPaths || [],
  });
  const [workflow, setWorkflow] = useState<Workflow>(
    () =>
      preference?.workflow ??
      workflowTemplate(
        'backend',
        data.projects.find(projectItem => projectItem.id === initialProjectId)?.aiDefaults
          ?.implementation || undefined
      )
  );
  const [confirmed, setConfirmed] = useState(false);
  const [reviewing, setReviewing] = useState(!!seed);
  const [answers, setAnswers] = useState<string[]>(seed?.openQuestions?.map(() => '') || []);
  const [rememberFlow, setRememberFlow] = useState(true);
  const notice = useNotice();
  const action = useAction(notice);
  const rulesResource = useRules();

  return (
    <Modal
      title={seed ? (reviewing ? '先確認討論摘要' : '設定討論任務的執行流程') : '建立開發任務'}
      eyebrow="NEW TASK"
      onClose={onClose}
      wide
    >
      <form
        onSubmit={event => {
          event.preventDefault();
          if (reviewing) {
            if (answers.some(answerText => !answerText.trim())) {
              notice('請填入每個未決定事項的處理方式。', true);
              return;
            }
            setReviewing(false);
            return;
          }
          void action.run(async () => {
            if (
              seed?.openQuestions?.length &&
              (!confirmed || answers.some(answerText => !answerText.trim()))
            ) {
              throw new Error('請先完成未決定事項的處理方式與確認。');
            }
            const resolvedDescription =
              description +
              (seed?.openQuestions?.length
                ? '\n\n已確認的問題處理方式：\n' +
                  seed.openQuestions
                    .map(
                      (questionText, questionIndex) =>
                        questionText + '\n處理方式：' + answers[questionIndex].trim()
                    )
                    .join('\n\n')
                : '');
            if (resolvedDescription.length > 20000) {
              throw new Error('需求與問題處理方式合計不能超過 20000 字。');
            }
            const createdTask = await api<WorkTask>('/api/tasks', 'POST', {
              projectId: selectedProjectId,
              title,
              description: resolvedDescription,
              acceptanceCriteria: criteria,
              autoPlan: autoStart,
              planning: planningSelection,
              implementation: implementationSelection,
              ruleIds,
              taskRules,
              policy: {
                ...policy,
                allowedPaths: policy.allowedPaths.map(pathItem => pathItem.trim()).filter(Boolean),
              },
              workflow: cleanWorkflow(workflow),
              sourceDiscussionId: seed?.discussionId,
              sourceDraftId: seed?.id,
              acknowledgeOpenQuestions: confirmed,
            });
            if (
              rememberFlow &&
              !saveWorkflowPreference(selectedProjectId, {
                workflow: cleanWorkflow(workflow),
                planning: planningSelection,
                implementation: implementationSelection,
              })
            ) {
              notice('任務已建立，但瀏覽器無法保存流程偏好。', true);
            }
            notice(autoStart ? '任務已提交，排隊作業需要 Worker 啟動。' : '任務已建立，尚未開始執行。');
            onCreated(createdTask);
          });
        }}
      >
        <div className="dialog-body">
          {seed?.openQuestions?.length ? (
            <div className="info-banner">
              <strong>討論中尚待釐清的問題</strong>
              <div>
                {seed.openQuestions.map((questionText, questionIndex) => (
                  <label key={questionIndex}>
                    {questionText}
                    <textarea
                      aria-label={'問題處理方式 ' + (questionIndex + 1)}
                      required
                      rows={2}
                      maxLength={2000}
                      placeholder="請填入已決定的做法，或明確說明本次不處理的範圍。"
                      value={answers[questionIndex]}
                      onChange={event => {
                        setAnswers(previousAnswers =>
                          previousAnswers.map((answerItem, answerIndex) =>
                            answerIndex === questionIndex ? event.target.value : answerItem
                          )
                        );
                        setConfirmed(false);
                      }}
                    />
                  </label>
                ))}
              </div>
              <label className="checkbox">
                <input
                  type="checkbox"
                  required
                  checked={confirmed}
                  onChange={event => setConfirmed(event.target.checked)}
                />
                我已檢視這些問題，並在任務內容中明確說明處理方式
              </label>
            </div>
          ) : null}

          <section className="form-section">
            <div className="section-header">
              <span className="section-badge">1</span>
              <div>
                <h3>{reviewing ? '確認要做的事與完成標準' : '綁定專案與任務需求'}</h3>
                <p>選擇目標專案，定義需求與最終驗收標準。</p>
              </div>
            </div>
            <div className="two-col form-top-grid">
              <label>
                專案
                <select
                  required
                  disabled={!!seed}
                  value={selectedProjectId}
                  onChange={event => {
                    const nextProjectId = event.target.value;
                    setSelectedProjectId(nextProjectId);
                    const matchedProject = data.projects.find(
                      projectItem => projectItem.id === nextProjectId
                    );
                    setRuleIds(matchedProject?.defaultRuleIds || []);
                    const remembered = readWorkflowPreference(nextProjectId);
                    setPlanningSelection(remembered?.planning ?? null);
                    setImplementationSelection(remembered?.implementation ?? null);
                    setWorkflow(
                      remembered?.workflow ??
                        workflowTemplate('backend', matchedProject?.aiDefaults?.implementation)
                    );
                  }}
                >
                  {data.projects.map(projectItem => (
                    <option key={projectItem.id} value={projectItem.id}>
                      {projectItem.name}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                任務名稱
                <input
                  required
                  maxLength={200}
                  placeholder="例如：新增訂單查詢 API 與前端列表頁"
                  value={title}
                  onChange={event => setTitle(event.target.value)}
                />
              </label>
            </div>
            <div className="two-col">
              <label>
                需求
                <textarea
                  required
                  rows={4}
                  maxLength={20000}
                  placeholder="請描述功能背景、輸入輸出與預期行為…"
                  value={description}
                  onChange={event => setDescription(event.target.value)}
                />
              </label>
              <label>
                驗收條件
                <textarea
                  required
                  rows={4}
                  maxLength={20000}
                  placeholder="請列出明確可驗證的條件與邊界情況…"
                  value={criteria}
                  onChange={event => setCriteria(event.target.value)}
                />
              </label>
            </div>
          </section>

          {!reviewing && (
            <>
              {(workflow.planningEnabled || !workflow.steps) && (
                <div className="two-col choice-grid">
                  {workflow.planningEnabled && (
                    <AiChoice
                      label="規劃"
                      providers={data.providers}
                      value={planningSelection}
                      onChange={setPlanningSelection}
                      inherit
                    />
                  )}
                  {!workflow.steps && (
                    <AiChoice
                      label="實作"
                      providers={data.providers}
                      value={implementationSelection}
                      onChange={setImplementationSelection}
                      inherit
                    />
                  )}
                </div>
              )}
              {rulesResource.error && (
                <p className="error-banner">規則庫讀取失敗：{rulesResource.error}</p>
              )}
              <TaskOptions
                validationProfiles={data.projects.find(p=>p.id===selectedProjectId)?.validationProfiles||[]}
                rules={rulesResource.data || []}
                ruleIds={ruleIds}
                onRules={setRuleIds}
                taskRules={taskRules}
                onTaskRules={setTaskRules}
                policy={policy}
                onPolicy={setPolicy}
                workflow={workflow}
                onWorkflow={setWorkflow}
                providers={data.providers}
              />
              <label className="checkbox">
                <input
                  type="checkbox"
                  checked={rememberFlow}
                  onChange={event => setRememberFlow(event.target.checked)}
                />
                記住這個專案的模型選擇與實作順序
              </label>
              <div className="submit-bar">
                <label className="checkbox">
                  <input
                    type="checkbox"
                    checked={autoStart}
                    onChange={event => setAutoStart(event.target.checked)}
                  />
                  {workflow.planningEnabled
                    ? '提交後自動規劃（使用模型額度）'
                    : '提交後直接開始實作（使用模型額度）'}
                </label>
                <p className="footnote">
                  規劃 AI 產出計畫後會等待你確認；實作期間問題在規則內由規劃 AI 自動回覆，超出規則再由你確認。
                </p>
              </div>
            </>
          )}
        </div>
        <div className="dialog-footer">
          <button type="button" className="secondary" onClick={onClose}>
            取消
          </button>
          <div className="button-row">
            {seed && !reviewing && (
              <button type="button" className="secondary" onClick={() => setReviewing(true)}>
                返回確認摘要
              </button>
            )}
            <button className="primary" disabled={action.busy}>
              {reviewing ? '確認摘要，設定執行流程 →' : '提交任務 →'}
            </button>
          </div>
        </div>
      </form>
    </Modal>
  );
}

export function ProjectDialog({
  onClose,
  onCreated,
}: {
  onClose: () => void;
  onCreated: (createdProject: Project) => void;
}) {
  const [name, setName] = useState('');
  const [repositoryPath, setRepositoryPath] = useState('');
  const [baseBranch, setBaseBranch] = useState('main');
  const [validationJson, setValidationJson] = useState('["test","-m:1","/nr:false"]');
  const [isPickingFolder, setIsPickingFolder] = useState(false);
  const notice = useNotice();
  const action = useAction(notice);

  return (
    <>
      <Modal title="登錄本地專案" eyebrow="NEW PROJECT" onClose={onClose}>
        <form
          onSubmit={event => {
            event.preventDefault();
            void action.run(async () => {
              let parsedArgs: unknown;
              try {
                parsedArgs = JSON.parse(validationJson);
              } catch {
                throw new Error('測試參數必須是 JSON 陣列。');
              }
              if (
                !Array.isArray(parsedArgs) ||
                parsedArgs.some(argItem => typeof argItem !== 'string')
              ) {
                throw new Error('測試參數必須是字串陣列。');
              }
              const createdProject = await api<Project>('/api/projects', 'POST', {
                name,
                repositoryPath,
                baseBranch,
                validationArguments: parsedArgs,
              });
              notice('專案已登錄。');
              onCreated(createdProject);
            });
          }}
        >
          <div className="dialog-body">
            <label>
              專案名稱
              <input
                required
                maxLength={200}
                value={name}
                onChange={event => setName(event.target.value)}
              />
            </label>
            <label htmlFor="repository-path">Repository 絕對路徑</label>
            <div className="folder-address">
              <input
                id="repository-path"
                required
                maxLength={4096}
                placeholder="C:\repos\MyProject"
                value={repositoryPath}
                onChange={event => setRepositoryPath(event.target.value)}
              />
              <button
                type="button"
                className="secondary"
                disabled={action.busy || isPickingFolder}
                onClick={() => setIsPickingFolder(true)}
              >
                選擇資料夾
              </button>
            </div>
            <label>
              基底分支
              <input
                required
                value={baseBranch}
                onChange={event => setBaseBranch(event.target.value)}
              />
            </label>
            <label>
              dotnet test 參數（JSON 陣列）
              <textarea
                required
                rows={3}
                value={validationJson}
                onChange={event => setValidationJson(event.target.value)}
              />
            </label>
            <p className="footnote">測試 logger 與結果目錄由後端設定。</p>
          </div>
          <div className="dialog-footer">
            <button type="button" className="secondary" onClick={onClose}>
              取消
            </button>
            <button className="primary" disabled={action.busy}>
              登錄專案
            </button>
          </div>
        </form>
      </Modal>
      {isPickingFolder && (
        <FolderPicker
          initialPath={repositoryPath}
          onClose={() => setIsPickingFolder(false)}
          onSelected={selectedPath => {
            setRepositoryPath(selectedPath);
            setIsPickingFolder(false);
          }}
        />
      )}
    </>
  );
}

export function ProjectSettings({
  project,
  data,
  onClose,
  onSaved,
}: {
  project: Project;
  data: BaseData;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [planningSelection, setPlanningSelection] = useState<Selection | null>(
    project.aiDefaults?.planning || { provider: 'codex', model: null }
  );
  const [implementationSelection, setImplementationSelection] = useState<Selection | null>(
    project.aiDefaults?.implementation || { provider: 'codex', model: null }
  );
  const [version] = useState(project.version);
  const [validationProfiles,setValidationProfiles]=useState(project.validationProfiles||[]);
  const [defaultValidationProfileId,setDefaultValidationProfileId]=useState(project.defaultValidationProfileId||null);
  const [previewServices,setPreviewServices]=useState(project.previewServices||[]);
  const notice = useNotice();
  const action = useAction(notice);

  return (
    <Modal title={project.name + ' · 專案設定'} eyebrow="PROJECT SETTINGS" wide onClose={onClose}>
      <form
        className="project-settings-form"
        onSubmit={event => {
          event.preventDefault();
          void action.run(async () => {
            await api('/api/projects/' + project.id + '/execution-settings', 'PUT', {
              expectedVersion: version,
              validationProfiles,defaultValidationProfileId,previewServices,
              aiDefaults: {
                planning: planningSelection,
                implementation: implementationSelection,
              },
            });
            notice('專案設定已保存。驗證於下次執行時套用，AI 預設僅影響新任務。');
            onSaved();
            onClose();
          });
        }}
      >
        <div className="dialog-body">
          <div className="two-col choice-grid">
            <AiChoice
              label="規劃預設"
              providers={data.providers}
              value={planningSelection}
              onChange={setPlanningSelection}
            />
            <AiChoice
              label="實作預設"
              providers={data.providers}
              value={implementationSelection}
              onChange={setImplementationSelection}
            />
          </div>
          <p className="footnote">僅影響之後建立的任務。</p>
          <ProjectExecutionFields profiles={validationProfiles} onProfiles={setValidationProfiles} defaultId={defaultValidationProfileId} onDefault={setDefaultValidationProfileId} services={previewServices} onServices={setPreviewServices}/>
        </div>
        <div className="dialog-footer">
          <button type="button" className="secondary" onClick={onClose}>
            取消
          </button>
          <button className="primary" disabled={action.busy}>
            保存專案設定
          </button>
        </div>
      </form>
    </Modal>
  );
}
