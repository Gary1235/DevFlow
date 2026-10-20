import { selectionText, shortCommit, formatDate } from '../domain';
import type { Run, WorkTask } from '../types';
import { stageLabels } from './TaskOptions';
import { WorkflowPanel } from './WorkflowPanel';

export function TaskContext({
  task,
  onChanged = () => {},
}: {
  task: WorkTask;
  onChanged?: () => void;
}) {
  return (
    <article className="panel task-context">
      <h2>AI 指揮與實作流程</h2>
      {task.workflow?.steps ? (
        <WorkflowPanel task={task} onChanged={onChanged} />
      ) : (
        <div className="flow-tags">
          {task.workflow?.planningEnabled !== false && (
            <>
              <span className="chip neutral">
                規劃與總指揮 · {selectionText(task.ai?.planning)}
              </span>
              <span>→</span>
            </>
          )}
          <span className="chip blue">實作 · {selectionText(task.ai?.implementation)}</span>
          {task.workflow?.reviewers?.map((reviewerSelection, reviewerIndex) => (
            <span key={reviewerIndex} className="flow-stage">
              <span>→</span>
              <span className="chip neutral">
                驗收 {reviewerIndex + 1} · {selectionText(reviewerSelection)}
              </span>
            </span>
          ))}
          <span>→</span>
          <span className="chip amber">人工驗收合併</span>
        </div>
      )}
      <details>
        <summary>查看本次套用規則與範圍</summary>
        {!task.rules?.length && !task.taskRules && (
          <p className="muted">未指定額外文字規則。</p>
        )}
        {task.rules?.map(ruleItem => (
          <div className="history-item" key={ruleItem.id}>
            <strong>
              {ruleItem.name} · v{ruleItem.version}
            </strong>
            <p>{ruleItem.stages.map(stageName => stageLabels[stageName]).join(' · ')}</p>
            <p className="prose">{ruleItem.content}</p>
          </div>
        ))}
        {task.taskRules && (
          <>
            <h3>本次補充規則與確認紀錄</h3>
            <p className="prose">{task.taskRules}</p>
          </>
        )}
        {Boolean(task.policy?.allowedPaths?.length) && (
          <>
            <h3>允許編輯路徑</h3>
            <p className="prose">{task.policy!.allowedPaths.join('\n')}</p>
          </>
        )}
      </details>
    </article>
  );
}

export function AiReviewReports({ run }: { run?: Run }) {
  if (!run?.aiReviews?.length) return null;
  const verdictNames: Record<string, string> = {
    Pass: '通過',
    NeedsChanges: '需要修正',
    Uncertain: '無法判定',
  };
  return (
    <article className="panel">
      <h2>AI 驗收報告</h2>
      <p className="muted">這些報告提供審核依據；最終仍需由你核准成果。</p>
      {run.aiReviews.map(reviewReport => (
        <div className="history-item" key={reviewReport.id}>
          <div className="flow-tags">
            <span className={'chip ' + (reviewReport.verdict === 'Pass' ? 'success' : 'amber')}>
              {verdictNames[reviewReport.verdict] || reviewReport.verdict}
            </span>
            <span className="chip neutral">
              驗收 {reviewReport.stage} · {selectionText(reviewReport.selection)}
            </span>
          </div>
          <p>
            第 {reviewReport.attempt} 輪 · 成果 {shortCommit(reviewReport.resultCommit)} ·{' '}
            {formatDate(reviewReport.createdAt)}
          </p>
          <p className="prose">{reviewReport.summary}</p>
        </div>
      ))}
    </article>
  );
}
