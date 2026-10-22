import { useCallback, useState } from 'react';
import { api } from '../api';
import { useAction, useResource } from '../hooks';
import { Modal, ProseContent, useNotice } from './ui';
import { WorkflowEditor, stepKinds } from './WorkflowEditor';
import { selectionText, shortCommit } from '../domain';
import type { Provider, Project, WorkTask, Workflow, WorkflowStep, WorkflowStepResult } from '../types';

export function cleanWorkflow(flow: Workflow): Workflow {
  return {
    ...flow,
    steps: flow.steps?.map(stepItem => ({
      ...stepItem,
      allowedPaths: (stepItem.allowedPaths || []).map(pathItem => pathItem.trim()).filter(Boolean),
    })),
  };
}

export function WorkflowSettings({
  task,
  initialSelectedId,
  onClose,
  onChanged,
}: {
  task: WorkTask;
  initialSelectedId?: string;
  onClose: () => void;
  onChanged: () => void;
}) {
  const [flow, setFlow] = useState<Workflow>(() => structuredClone(task.workflow!));
  const [version] = useState(task.version);
  const notice = useNotice();
  const action = useAction(notice);
  const providersResource = useResource(
    'workflow-providers',
    useCallback(
      (signal: AbortSignal) => api<Provider[]>('/api/ai/providers', 'GET', undefined, { signal }),
      []
    ),
    0
  );
  const lockedCount = (task.stepResults?.length || 0) + (task.activeStepId ? 1 : 0);
  const projectResource = useResource('workflow-project:'+task.projectId,useCallback((signal:AbortSignal)=>api<Project>('/api/projects/'+task.projectId,'GET',undefined,{signal}),[task.projectId]),0);

  return (
    <Modal title="編輯尚未開始的步驟" eyebrow="TASK WORKFLOW" wide onClose={onClose}>
      <form
        onSubmit={event => {
          event.preventDefault();
          void action.run(async () => {
            await api('/api/tasks/' + task.id + '/workflow', 'PUT', {
              expectedVersion: version,
              workflow: cleanWorkflow(flow),
            });
            notice('任務流程已更新。');
            onChanged();
            onClose();
          });
        }}
      >
        <div className="dialog-body">
          {providersResource.error && <div className="error-banner">{providersResource.error}</div>}
          {projectResource.error && <div className="error-banner">無法讀取驗證設定：{projectResource.error}</div>}
          <WorkflowEditor
            value={flow}
            onChange={setFlow}
            providers={providersResource.data || []}
            lockedCount={lockedCount}
            initialSelectedId={initialSelectedId || task.workflow?.steps?.[lockedCount]?.id}
            planningLocked
            validationProfiles={projectResource.data?.validationProfiles||[]}
          />
        </div>
        <div className="dialog-footer">
          <button type="button" className="secondary" onClick={onClose}>
            取消
          </button>
          <button className="primary" disabled={action.busy}>
            儲存流程
          </button>
        </div>
      </form>
    </Modal>
  );
}

export function WorkflowPanel({ task, onChanged }: { task: WorkTask; onChanged: () => void }) {
  const [isEditingWorkflow, setIsEditingWorkflow] = useState(false);
  const [editingStepId, setEditingStepId] = useState<string | undefined>(undefined);
  const [confirmationReply, setConfirmationReply] = useState('');
  const [inspectedStepModal, setInspectedStepModal] = useState<{
    step: WorkflowStep;
    result?: WorkflowStepResult;
    stepNumber: number;
  } | null>(null);
  const notice = useNotice();
  const action = useAction(notice);
  const steps = task.workflow?.steps || [];
  const completedSteps = task.stepResults || [];
  const latestCompletedStep = completedSteps.at(-1);
  const isOutOfRulesQuestion = Boolean(latestCompletedStep?.summary?.startsWith('⚠️'));
  const isEditable = [
    'AwaitingPlan',
    'AwaitingApproval',
    'PlanningFailed',
    'AwaitingStepApproval',
    'Running',
    'Failed',
    'Cancelled',
    'Queued',
    'PlanningQueued',
    'Planning',
  ].includes(task.status);

  return (
    <>
      <div className="panel-heading">
        <p>
          規劃 AI 依序指揮各步驟；規則內問題自動回覆，超出規則由你確認（目前完成{' '}
          <strong>
            {completedSteps.length} / {steps.length}
          </strong>
          ）
        </p>
        {isEditable && (
          <button
            className="secondary"
            onClick={() => {
              setEditingStepId(undefined);
              setIsEditingWorkflow(true);
            }}
          >
            編輯流程
          </button>
        )}
      </div>
      <ol className="workflow-progress">
        {task.workflow?.planningEnabled && (
          <li className="workflow-anchor start-gate">
            整體規劃 · {selectionText(task.ai?.planning)} → 人工核准計畫
          </li>
        )}
        {steps.map((stepItem, stepIndex) => {
          const stepResult = completedSteps.find(resultItem => resultItem.stepId === stepItem.id);
          const isStepActive = task.activeStepId === stepItem.id;
          const isWaitingApproval =
            task.status === 'AwaitingStepApproval' && latestCompletedStep?.stepId === stepItem.id;
          const isStepFailed = task.status === 'Failed' && stepIndex === completedSteps.length;
          const stepStateText = isWaitingApproval
            ? '等待你確認'
            : stepResult
              ? '已完成'
              : isStepActive
                ? '執行中'
                : isStepFailed
                  ? '失敗，等待處理'
                  : '尚未開始';
          const toneClass = isWaitingApproval
            ? 'amber'
            : stepResult
              ? 'success'
              : isStepActive
                ? 'blue'
                : isStepFailed
                  ? 'danger'
                  : 'neutral';
          const hasAllowedPaths = Boolean(stepItem.allowedPaths && stepItem.allowedPaths.length);
          const canChangeStepModel =
            isEditable &&
            !stepResult &&
            !isStepActive &&
            (stepItem.kind === 'Implementation' || stepItem.kind === 'Review');

          return (
            <li
              key={stepItem.id}
              data-kind={stepItem.kind}
              className={
                'workflow-progress-step state-' + toneClass + (isStepActive ? ' active' : '')
              }
            >
              <div className="workflow-progress-heading">
                <strong>
                  {String(stepIndex + 1).padStart(2, '0')}. {stepItem.name}
                </strong>
                <div className="button-row">
                  {canChangeStepModel && (
                    <button
                      type="button"
                      className="secondary"
                      aria-label={'更換模型：' + stepItem.name}
                      onClick={() => {
                        setEditingStepId(stepItem.id);
                        setIsEditingWorkflow(true);
                      }}
                    >
                      更換模型
                    </button>
                  )}
                  {(stepResult?.summary || stepItem.instructions) && (
                    <button
                      type="button"
                      className="text-link step-inspect-btn"
                      onClick={() =>
                        setInspectedStepModal({
                          step: stepItem,
                          result: stepResult,
                          stepNumber: stepIndex + 1,
                        })
                      }
                    >
                      ⤢ 視窗檢視
                    </button>
                  )}
                  <span className={'chip ' + toneClass}>{stepStateText}</span>
                </div>
              </div>
              <p>
                <span className={'kind-tag kind-' + stepItem.kind.toLowerCase()}>
                  {stepKinds[stepItem.kind]}
                </span>
                {stepItem.selection ? ' · ' + selectionText(stepItem.selection) : ''}
              </p>
              {hasAllowedPaths && (
                <p className="muted">範圍：{stepItem.allowedPaths.join('、')}</p>
              )}
              {stepResult?.resultCommit && (
                <p className="muted">成果：{shortCommit(stepResult.resultCommit)}</p>
              )}
              <details open={isWaitingApproval}>
                <summary>步驟內容與結果</summary>
                <div className="step-structured-preview">
                  <ProseContent
                    content={stepItem.instructions || '依任務需求與核准計畫執行。'}
                  />
                  {stepItem.acceptanceCriteria && (
                    <ProseContent content={'**完成條件**：' + stepItem.acceptanceCriteria} />
                  )}
                  {stepResult?.summary && <ProseContent content={stepResult.summary} />}
                </div>
              </details>
            </li>
          );
        })}
        <li className="workflow-anchor end-gate">人工驗收成果 → 確認後合併</li>
      </ol>
      {task.status === 'AwaitingStepApproval' && (
        <div className="attention-banner step-confirmation-banner">
          <div style={{ width: '100%' }}>
            <p>
              <strong>
                {isOutOfRulesQuestion ? '⚠️ 超出規則問題，等待你確認：' : '⏸ 步驟確認點：'}
              </strong>
              「{latestCompletedStep?.name}」
              {isOutOfRulesQuestion
                ? '遇到超出規則庫或規格未定義的問題，請確認處理方式後繼續。'
                : '已完成。請查看成果與紀錄，確認後才會接續下一步。'}
            </p>
            {latestCompletedStep?.summary && (
              <div className="step-question-card">
                <ProseContent content={latestCompletedStep.summary} />
              </div>
            )}
          </div>
          <label style={{ width: '100%', marginBottom: 4 }}>
            給 AI 的確認回覆／補充指示（選填）
            <textarea
              rows={2}
              maxLength={12000}
              placeholder="若有超出規則的問題或補充決定，請在此輸入回覆；直接確認可留空…"
              value={confirmationReply}
              onChange={event => setConfirmationReply(event.target.value)}
            />
          </label>
          <div className="button-row">
            <button
              className="warning-button"
              disabled={action.busy}
              onClick={() =>
                void action.run(async () => {
                  const requestPayload = confirmationReply.trim()
                    ? {
                        expectedVersion: task.version,
                        instructions: confirmationReply.trim(),
                      }
                    : { expectedVersion: task.version };
                  await api('/api/tasks/' + task.id + '/workflow/continue', 'POST', requestPayload);
                  setConfirmationReply('');
                  notice('已確認，接續流程已排隊。');
                  onChanged();
                })
              }
            >
              確認並繼續流程
            </button>
          </div>
        </div>
      )}
      {inspectedStepModal && (
        <Modal
          title={`步驟 ${String(inspectedStepModal.stepNumber).padStart(2, '0')} · ${inspectedStepModal.step.name}`}
          eyebrow={'WORKFLOW STEP · ' + stepKinds[inspectedStepModal.step.kind]}
          wide
          onClose={() => setInspectedStepModal(null)}
        >
          <div className="dialog-body plan-modal-body">
            <div className="plan-modal-toolbar">
              <div className="plan-modal-meta">
                <span className={'kind-tag kind-' + inspectedStepModal.step.kind.toLowerCase()}>
                  {stepKinds[inspectedStepModal.step.kind]}
                </span>
                {inspectedStepModal.step.selection && (
                  <span className="muted">
                    模型：{selectionText(inspectedStepModal.step.selection)}
                  </span>
                )}
                {inspectedStepModal.result?.resultCommit && (
                  <span className="muted">
                    成果 Commit：{shortCommit(inspectedStepModal.result.resultCommit)}
                  </span>
                )}
              </div>
            </div>
            <div className="plan-modal-document">
              <h4 className="md-heading md-h2">步驟指示</h4>
              <ProseContent
                content={inspectedStepModal.step.instructions || '依任務需求與核准計畫執行。'}
              />
              {inspectedStepModal.step.acceptanceCriteria && (
                <>
                  <h4 className="md-heading md-h2">完成條件</h4>
                  <ProseContent content={inspectedStepModal.step.acceptanceCriteria} />
                </>
              )}
              {inspectedStepModal.result?.summary && (
                <>
                  <h4 className="md-heading md-h2">執行結果與規劃 AI 紀錄</h4>
                  <ProseContent content={inspectedStepModal.result.summary} />
                </>
              )}
            </div>
          </div>
          <div className="dialog-footer">
            <button
              type="button"
              className="secondary"
              onClick={() => setInspectedStepModal(null)}
            >
              關閉
            </button>
          </div>
        </Modal>
      )}
      {isEditingWorkflow && (
        <WorkflowSettings
          task={task}
          initialSelectedId={editingStepId}
          onClose={() => setIsEditingWorkflow(false)}
          onChanged={onChanged}
        />
      )}
    </>
  );
}
