import { useCallback, useState } from 'react';
import { api } from '../api';
import { useResource } from '../hooks';
import { AiChoice, useNotice } from './ui';
import { selectionText } from '../domain';
import type { Provider, Selection, Workflow, WorkflowStep, WorkflowStepKind, ValidationProfile } from '../types';

export const stepKinds: Record<WorkflowStepKind, string> = {
  Implementation: '實作',
  Review: 'AI 審查',
  Validation: '整合測試',
  Approval: '人工確認',
};

export function makeStep(
  name: string,
  kind: WorkflowStepKind = 'Implementation',
  selection: Selection | null = { provider: 'codex', model: null }
): WorkflowStep {
  return {
    id: crypto.randomUUID(),
    name,
    kind,
    selection: kind === 'Implementation' || kind === 'Review' ? selection : null,
    instructions: '',
    allowedPaths: [],
    waitForApproval: false,
    acceptanceCriteria: '',
  };
}

export function workflowTemplate(name = 'backend', selection?: Selection | null): Workflow {
  let steps: WorkflowStep[];
  if (name === 'single') {
    steps = [makeStep('實作', 'Implementation', selection)];
  } else if (name === 'deep') {
    steps = [
      makeStep('實作', 'Implementation', selection),
      makeStep('驗收 1', 'Review'),
      makeStep('驗收 2', 'Review'),
    ];
  } else if (name === 'fullstack') {
    steps = [
      makeStep('後端 API', 'Implementation', selection),
      makeStep('前端實作', 'Implementation', selection),
      makeStep('整合測試', 'Validation'),
      makeStep('AI 審查', 'Review', selection),
    ];
  } else if (name === 'frontend') {
    steps = [
      makeStep('前端實作', 'Implementation', selection),
      makeStep('後端 API', 'Implementation', selection),
      makeStep('整合測試', 'Validation'),
      makeStep('AI 審查', 'Review', selection),
    ];
  } else if (name === 'api') {
    steps = [
      makeStep('後端 API', 'Implementation', selection),
      makeStep('整合測試', 'Validation'),
      makeStep('AI 審查', 'Review', selection),
    ];
  } else {
    steps = [
      makeStep('後端 API', 'Implementation', selection),
      makeStep('前端實作', 'Implementation', selection),
      makeStep('整合測試', 'Validation'),
    ];
  }
  return { planningEnabled: true, reviewers: [], maxReviewRetries: 0, steps };
}

interface SavedTemplate {
  name: string;
  workflow: Workflow;
}

export function WorkflowEditor({
  value,
  onChange,
  providers,
  lockedCount = 0,
  planningLocked = false,
  initialSelectedId,
  validationProfiles = [],
}: {
  value: Workflow;
  onChange: (flow: Workflow) => void;
  providers: Provider[];
  lockedCount?: number;
  planningLocked?: boolean;
  initialSelectedId?: string;
  validationProfiles?: ValidationProfile[];
}) {
  const steps = value.steps || [];
  const [selectedStepId, setSelectedStepId] = useState(initialSelectedId || steps[0]?.id || '');
  const [newStepKind, setNewStepKind] = useState<WorkflowStepKind>('Implementation');
  const [templateName, setTemplateName] = useState('');
  const [isSavingTemplate, setIsSavingTemplate] = useState(false);
  const notice = useNotice();

  const savedTemplatesResource = useResource(
    'workflow-templates',
    useCallback(
      (signal: AbortSignal) => api<SavedTemplate[]>('/api/workflow-templates', 'GET', undefined, { signal }),
      []
    ),
    0
  );
  const templates = savedTemplatesResource.data || [];
  const activeStep = steps.find(stepItem => stepItem.id === selectedStepId) || steps[0];
  const activeStepIndex = activeStep ? steps.indexOf(activeStep) : -1;
  const isStepLocked = activeStepIndex < lockedCount;

  function updateSteps(nextSteps: WorkflowStep[]) {
    onChange({ ...value, steps: nextSteps, reviewers: [], maxReviewRetries: 0 });
  }

  function patchActiveStep(stepUpdate: Partial<WorkflowStep>) {
    if (!activeStep || isStepLocked) return;
    updateSteps(
      steps.map(stepItem => (stepItem.id === activeStep.id ? { ...stepItem, ...stepUpdate } : stepItem))
    );
  }

  function moveStep(fromIndex: number, toIndex: number) {
    if (fromIndex < lockedCount || toIndex < lockedCount || toIndex < 0 || toIndex >= steps.length) {
      return;
    }
    const nextSteps = [...steps];
    nextSteps.splice(toIndex, 0, nextSteps.splice(fromIndex, 1)[0]);
    updateSteps(nextSteps);
  }

  function addStep() {
    if (steps.length >= 20) return;
    const addedStep = makeStep(stepKinds[newStepKind], newStepKind);
    const nextSteps = [...steps];
    nextSteps.splice(Math.max(lockedCount, activeStepIndex + 1), 0, addedStep);
    updateSteps(nextSteps);
    setSelectedStepId(addedStep.id);
  }

  function applyTemplate(templateWorkflow: Workflow, preserveModels = false) {
    const clonedWorkflow = structuredClone(templateWorkflow);
    clonedWorkflow.steps = clonedWorkflow.steps?.map(stepItem => ({
      ...stepItem,
      id: crypto.randomUUID(),
      selection: preserveModels
        ? (steps.find(oldStep => oldStep.name === stepItem.name && oldStep.kind === stepItem.kind)
            ?.selection ?? stepItem.selection)
        : stepItem.selection,
    }));
    onChange({ ...clonedWorkflow, reviewers: [], maxReviewRetries: 0 });
    setSelectedStepId(clonedWorkflow.steps?.[0]?.id || '');
  }

  return (
    <section className="workflow-editor form-section" aria-label="任務流程編輯器">
      <div className="panel-heading">
        <div className="section-header">
          <span className="section-badge">3</span>
          <div>
            <h3>實作步驟與模型順序（AI 指揮 AI 實作）</h3>
            <p>可將前後端分開指定不同模型與順序；規劃 AI 會在規則內直接回覆問題，超出規則才請你確認。</p>
          </div>
        </div>
        <span className="chip blue">{steps.length} 個步驟</span>
      </div>

      {!planningLocked && (
        <div className="two-col workflow-top-controls">
          <label>
            流程範本
            <select
              aria-label="流程範本"
              defaultValue="custom"
              onChange={event => {
                if (event.target.value.startsWith('saved:')) {
                  applyTemplate(templates[Number(event.target.value.slice(6))].workflow);
                } else if (event.target.value !== 'custom') {
                  applyTemplate(
                    workflowTemplate(
                      event.target.value,
                      steps.find(stepItem => stepItem.kind === 'Implementation')?.selection
                    ),
                    true
                  );
                }
              }}
            >
              <option value="custom">目前自訂順序</option>
              <option value="backend">後端 API → 前端實作 → 整合測試（推薦）</option>
              <option value="fullstack">後端 API → 前端實作 → 整合測試 → AI 審查</option>
              <option value="frontend">前端實作 → 後端 API → 整合測試 → AI 審查</option>
              <option value="api">後端 API → 整合測試 → AI 審查</option>
              <option value="single">單一步驟實作</option>
              <option value="deep">實作 → 兩次 AI 審查</option>
              {templates.map((templateItem, templateIndex) => (
                <option key={templateIndex} value={'saved:' + templateIndex}>
                  {templateItem.name}
                </option>
              ))}
            </select>
          </label>
          <label className="checkbox workflow-plan-toggle">
            <input
              type="checkbox"
              checked={value.planningEnabled}
              onChange={event => onChange({ ...value, planningEnabled: event.target.checked })}
            />
            先由規劃 AI 產生計畫，經我確認後再開始實作
          </label>
        </div>
      )}

      <div className="workflow-layout">
        <div className="workflow-lane" role="list" aria-label="依序執行的步驟">
          {value.planningEnabled && (
            <div className="workflow-anchor start-gate">
              ① AI 規劃 → 你確認計畫 → 規劃 AI 依序指揮實作
            </div>
          )}
          {steps.map((stepItem, stepIndex) => (
            <div
              key={stepItem.id}
              role="listitem"
              data-kind={stepItem.kind}
              className={
                'workflow-node' +
                (activeStep?.id === stepItem.id ? ' selected' : '') +
                (stepIndex < lockedCount ? ' locked' : '')
              }
              onDragOver={event => {
                if (stepIndex >= lockedCount) event.preventDefault();
              }}
              onDrop={event => {
                event.preventDefault();
                moveStep(
                  steps.findIndex(candidate => candidate.id === event.dataTransfer.getData('text/plain')),
                  stepIndex
                );
              }}
            >
              <div className="workflow-node-heading">
                <button
                  type="button"
                  className="workflow-drag"
                  title="拖曳排序"
                  draggable={stepIndex >= lockedCount}
                  disabled={stepIndex < lockedCount}
                  aria-label={'拖曳 ' + stepItem.name}
                  onDragStart={event => event.dataTransfer.setData('text/plain', stepItem.id)}
                >
                  ⠿
                </button>
                <button
                  type="button"
                  className="workflow-select"
                  aria-pressed={activeStep?.id === stepItem.id}
                  onClick={() => setSelectedStepId(stepItem.id)}
                >
                  <div className="workflow-title-row">
                    <span className="step-title-text">
                      {String(stepIndex + 1).padStart(2, '0')} · {stepItem.name}
                    </span>
                    <small className={'kind-tag kind-' + stepItem.kind.toLowerCase()}>
                      {stepKinds[stepItem.kind]}
                      {stepIndex < lockedCount ? ' · 已鎖定' : ''}
                    </small>
                  </div>
                </button>
              </div>
              <div className="workflow-node-body">
                <p className="step-provider-line" data-provider={stepItem.selection?.provider || ''}>
                  {stepItem.selection
                    ? selectionText(stepItem.selection)
                    : stepItem.kind === 'Validation'
                      ? '執行專案設定的 dotnet test'
                      : '等待你確認後繼續'}
                </p>
                {(stepItem.waitForApproval || stepItem.kind === 'Approval') && (
                  <span className="chip amber">完成後等我確認</span>
                )}
              </div>
              <div className="workflow-node-footer">
                <span className="step-edit-hint">
                  {activeStep?.id === stepItem.id ? '● 右側設定模型與指示' : '點擊切換模型'}
                </span>
                <div className="step-action-group">
                  <button
                    type="button"
                    className="step-move-btn"
                    title="上移步驟"
                    aria-label={'上移 ' + stepItem.name}
                    disabled={stepIndex <= lockedCount}
                    onClick={() => moveStep(stepIndex, stepIndex - 1)}
                  >
                    ▲ 上移
                  </button>
                  <button
                    type="button"
                    className="step-move-btn"
                    title="下移步驟"
                    aria-label={'下移 ' + stepItem.name}
                    disabled={stepIndex < lockedCount || stepIndex === steps.length - 1}
                    onClick={() => moveStep(stepIndex, stepIndex + 1)}
                  >
                    ▼ 下移
                  </button>
                  <button
                    type="button"
                    className="step-delete-btn"
                    title="刪除此步驟"
                    aria-label={'刪除 ' + stepItem.name}
                    disabled={stepIndex < lockedCount || steps.length === 1}
                    onClick={() => {
                      updateSteps(steps.filter(candidate => candidate.id !== stepItem.id));
                      if (selectedStepId === stepItem.id) setSelectedStepId('');
                    }}
                  >
                    刪除
                  </button>
                </div>
              </div>
            </div>
          ))}
          <div className="workflow-add">
            <label>
              新增步驟類型
              <select
                value={newStepKind}
                onChange={event => setNewStepKind(event.target.value as WorkflowStepKind)}
              >
                {Object.entries(stepKinds).map(([kindKey, labelText]) => (
                  <option key={kindKey} value={kindKey}>
                    {labelText}
                  </option>
                ))}
              </select>
            </label>
            <button type="button" className="secondary" disabled={steps.length >= 20} onClick={addStep}>
              ＋ 插入步驟
            </button>
          </div>
          <div className="workflow-anchor end-gate">人工驗收成果 → 確認後合併</div>
        </div>

        <div className="workflow-settings" data-kind={activeStep?.kind || ''} aria-label="步驟設定">
          {activeStep && (
            <>
              <div className="settings-header">
                <span className={'kind-tag kind-' + activeStep.kind.toLowerCase()}>
                  {stepKinds[activeStep.kind]}
                </span>
                <h3>步驟 {activeStepIndex + 1} · 模型與指示</h3>
              </div>
              {isStepLocked && (
                <p className="info-banner">已完成或執行中的步驟已鎖定。可選擇後面的步驟繼續編排。</p>
              )}
              <fieldset disabled={isStepLocked}>
                <label>
                  步驟名稱
                  <input
                    maxLength={100}
                    required
                    placeholder="例如：後端 API、前端畫面"
                    value={activeStep.name}
                    onChange={event => patchActiveStep({ name: event.target.value })}
                  />
                </label>

                {(activeStep.kind === 'Implementation' || activeStep.kind === 'Review') && (
                  <AiChoice
                    key={activeStep.id}
                    label={activeStep.name || '目前步驟'}
                    providers={providers}
                    value={activeStep.selection || { provider: 'codex', model: null }}
                    onChange={selection => patchActiveStep({ selection })}
                  />
                )}

                {activeStep.kind === 'Validation' && (
                  <p className="info-banner">
                    由系統執行下方選定的驗證設定，不呼叫模型。建置通過與測試通過會分別標示。
                  </p>
                )}
                {(activeStep.kind === 'Implementation' || activeStep.kind === 'Validation') && <label>此步驟的驗證設定<select aria-label="此步驟的驗證設定" value={activeStep.validationProfileId||''} onChange={e=>patchActiveStep({validationProfileId:e.target.value||null})}><option value="">沿用專案預設驗證</option>{activeStep.validationProfileId&&!validationProfiles.some(p=>p.id===activeStep.validationProfileId)&&<option value={activeStep.validationProfileId}>已保存的設定（請確認專案設定仍存在）</option>}{validationProfiles.map(p=><option value={p.id} key={p.id}>{p.name} · {p.workingDirectory} · {p.executable} {p.arguments.join(' ')}</option>)}</select><span className="footnote">實作步驟會在修改前後使用此設定。請先至「專案設定」建立前端／後端驗證。</span></label>}

                <label>
                  這一步要做什麼（選填，留空則由規劃 AI 依整體計畫指揮）
                  <textarea
                    rows={3}
                    maxLength={12000}
                    placeholder="可輸入此步驟專屬指示；留空則依核准計畫執行…"
                    value={activeStep.instructions || ''}
                    onChange={event => patchActiveStep({ instructions: event.target.value })}
                  />
                </label>

                <details className="advanced-details">
                  <summary>進階步驟設定（路徑限制、步驟類型、完成後暫停）</summary>
                  <div style={{ display: 'grid', gap: 10, marginTop: 10 }}>
                    <label>
                      步驟類型
                      <select
                        value={activeStep.kind}
                        onChange={event => {
                          const nextKind = event.target.value as WorkflowStepKind;
                          patchActiveStep({
                            kind: nextKind,
                            selection:
                              nextKind === 'Implementation' || nextKind === 'Review'
                                ? activeStep.selection || { provider: 'codex', model: null }
                                : null,
                          });
                        }}
                      >
                        {Object.entries(stepKinds).map(([kindKey, labelText]) => (
                          <option key={kindKey} value={kindKey}>
                            {labelText}
                          </option>
                        ))}
                      </select>
                    </label>
                    {activeStep.kind === 'Implementation' && (
                      <label>
                        允許修改的路徑（每行一項）
                        <textarea
                          rows={2}
                          placeholder={'backend/\nfrontend/'}
                          value={(activeStep.allowedPaths || []).join('\n')}
                          onChange={event =>
                            patchActiveStep({ allowedPaths: event.target.value.split('\n') })
                          }
                        />
                      </label>
                    )}
                    {activeStep.kind !== 'Approval' && (
                      <label>
                        完成條件
                        <textarea
                          rows={2}
                          maxLength={12000}
                          placeholder="留空沿用任務的驗收條件"
                          value={activeStep.acceptanceCriteria || ''}
                          onChange={event =>
                            patchActiveStep({ acceptanceCriteria: event.target.value })
                          }
                        />
                      </label>
                    )}
                    {activeStep.kind !== 'Approval' && (
                      <label className="checkbox pause-gate-box">
                        <input
                          type="checkbox"
                          checked={activeStep.waitForApproval}
                          onChange={event =>
                            patchActiveStep({ waitForApproval: event.target.checked })
                          }
                        />
                        完成這一步後，強制等我確認再繼續下一步
                      </label>
                    )}
                  </div>
                </details>
              </fieldset>
            </>
          )}
        </div>
      </div>

      <details className="advanced-details" style={{ marginTop: 10 }}>
        <summary>儲存目前流程為範本</summary>
        <div className="workflow-template-save" style={{ marginTop: 8 }}>
          <label>
            儲存為我的範本
            <input
              maxLength={60}
              value={templateName}
              placeholder="例如：前後端分離開發"
              onChange={event => setTemplateName(event.target.value)}
            />
          </label>
          <button
            type="button"
            className="secondary"
            disabled={!templateName.trim() || isSavingTemplate}
            onClick={() => {
              setIsSavingTemplate(true);
              void api('/api/workflow-templates', 'POST', {
                name: templateName.trim(),
                workflow: {
                  ...value,
                  steps: value.steps?.map(stepItem => ({
                    ...stepItem,
                    allowedPaths: (stepItem.allowedPaths || [])
                      .map(pathItem => pathItem.trim())
                      .filter(Boolean),
                  })),
                },
              })
                .then(() => {
                  savedTemplatesResource.refresh();
                  notice('流程範本已保存；同名範本會更新。');
                })
                .catch(error => notice(error.message, true))
                .finally(() => setIsSavingTemplate(false));
            }}
          >
            儲存範本
          </button>
        </div>
      </details>
      {savedTemplatesResource.error && (
        <p className="error-banner">流程範本讀取失敗：{savedTemplatesResource.error}</p>
      )}
    </section>
  );
}
