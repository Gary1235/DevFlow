import { useCallback, useState } from 'react';
import { api } from '../api';
import { useAction, useResource } from '../hooks';
import { Modal, useNotice } from './ui';
import { WorkflowEditor, stepKinds } from './WorkflowEditor';
import { selectionText, shortCommit } from '../domain';
import type { Provider, WorkTask, Workflow } from '../types';

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
  onClose,
  onChanged,
}: {
  task: WorkTask;
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
          <WorkflowEditor
            value={flow}
            onChange={setFlow}
            providers={providersResource.data || []}
            lockedCount={lockedCount}
            initialSelectedId={task.workflow?.steps?.[lockedCount]?.id}
            planningLocked
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
  const [confirmationReply, setConfirmationReply] = useState('');
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
          <button className="secondary" onClick={() => setIsEditingWorkflow(true)}>
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
                <span className={'chip ' + toneClass}>{stepStateText}</span>
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
                <p className="prose">{stepItem.instructions || '依任務需求與核准計畫執行。'}</p>
                {stepItem.acceptanceCriteria && (
                  <p className="prose">完成條件：{stepItem.acceptanceCriteria}</p>
                )}
                {stepResult?.summary && <p className="prose">{stepResult.summary}</p>}
              </details>
            </li>
          );
        })}
        <li className="workflow-anchor end-gate">人工驗收成果 → 確認後合併</li>
      </ol>
      {task.status === 'AwaitingStepApproval' && (
        <div className="attention-banner" style={{ display: 'grid', gap: 10 }}>
          <div>
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
              <p className="prose" style={{ marginTop: 6 }}>
                {latestCompletedStep.summary}
              </p>
            )}
          </div>
          <label>
            給 AI 的確認回覆／補充指示（選填）
            <textarea
              rows={2}
              maxLength={12000}
              placeholder="若有超出規則的問題或補充決定，請在此輸入回覆；直接確認可留空…"
              value={confirmationReply}
              onChange={event => setConfirmationReply(event.target.value)}
            />
          </label>
          <div>
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
      {isEditingWorkflow && (
        <WorkflowSettings
          task={task}
          onClose={() => setIsEditingWorkflow(false)}
          onChanged={onChanged}
        />
      )}
    </>
  );
}
