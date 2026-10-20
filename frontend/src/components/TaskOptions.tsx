import { useCallback, useState } from 'react';
import { api } from '../api';
import { useResource } from '../hooks';
import { WorkflowEditor } from './WorkflowEditor';
import type { ExecutionPolicy, Provider, Rule, Workflow } from '../types';

export const stageLabels: Record<string, string> = {
  Discussion: '討論',
  Planning: '規劃',
  Implementation: '實作',
  Acceptance: 'AI 驗收',
};

export function useRules() {
  return useResource(
    'rules',
    useCallback((signal: AbortSignal) => api<Rule[]>('/api/rules', 'GET', undefined, { signal }), [])
  );
}

export function RulePicker({
  rules,
  selected,
  onChange,
}: {
  rules: Rule[];
  selected: string[];
  onChange: (selectedRuleIds: string[]) => void;
}) {
  const [searchQuery, setSearchQuery] = useState('');
  const visibleRules = rules.filter(
    ruleItem =>
      (!ruleItem.archived || selected.includes(ruleItem.id)) &&
      (!searchQuery.trim() ||
        (ruleItem.name + ' ' + ruleItem.content)
          .toLocaleLowerCase()
          .includes(searchQuery.trim().toLocaleLowerCase()))
  );

  return (
    <fieldset className="rule-picker">
      <legend>規則庫 · 已選 {selected.length} 條（規劃 AI 將依此判斷問題是否在規則內）</legend>
      <input
        aria-label="搜尋共用規則"
        placeholder="搜尋規則名稱或內容"
        value={searchQuery}
        onChange={event => setSearchQuery(event.target.value)}
      />
      {!visibleRules.length && (
        <p className="muted">沒有符合的可用規則，可至左側「規則庫」新增。</p>
      )}
      <div className="rule-grid">
        {visibleRules.map(ruleItem => (
          <label
            className={'rule-option' + (selected.includes(ruleItem.id) ? ' selected' : '')}
            key={ruleItem.id}
          >
            <input
              type="checkbox"
              checked={selected.includes(ruleItem.id)}
              onChange={event =>
                onChange(
                  event.target.checked
                    ? [...selected, ruleItem.id]
                    : selected.filter(ruleId => ruleId !== ruleItem.id)
                )
              }
            />
            <span>
              <strong>
                {ruleItem.name}{' '}
                <small>
                  v{ruleItem.version}
                  {ruleItem.archived ? ' · 已封存，請取消選取' : ''}
                </small>
              </strong>
              <span className="muted">
                {ruleItem.stages.map(stageName => stageLabels[stageName] || stageName).join(' · ')}
              </span>
              <span className="rule-description">{ruleItem.content}</span>
            </span>
          </label>
        ))}
      </div>
    </fieldset>
  );
}

export function TaskOptions({
  rules,
  ruleIds,
  onRules,
  taskRules,
  onTaskRules,
  policy,
  onPolicy,
  workflow,
  onWorkflow,
  providers,
}: {
  rules: Rule[];
  ruleIds: string[];
  onRules: (selectedRuleIds: string[]) => void;
  taskRules: string;
  onTaskRules: (rulesText: string) => void;
  policy: ExecutionPolicy;
  onPolicy: (nextPolicy: ExecutionPolicy) => void;
  workflow: Workflow;
  onWorkflow: (nextWorkflow: Workflow) => void;
  providers: Provider[];
}) {
  return (
    <>
      <section className="form-section">
        <div className="section-header">
          <span className="section-badge">2</span>
          <div>
            <h3>套用規則（AI 指揮與問題分流依據）</h3>
            <p>實作遇到問題時，規劃 AI 會先根據這些規則直接回覆；超出規則範圍才會暫停請你確認。</p>
          </div>
        </div>
        <RulePicker
          rules={rules}
          selected={ruleIds}
          onChange={selectedRuleIds => {
            onRules(selectedRuleIds);
            if (rules.some(ruleItem => selectedRuleIds.includes(ruleItem.id) && ruleItem.requiresTdd)) {
              onPolicy({ ...policy, tdd: true });
            }
          }}
        />
        <label>
          本次補充規則（選填）
          <textarea
            rows={2}
            maxLength={12000}
            placeholder="例如：不得新增第三方套件、API 錯誤格式統一回傳 ProblemDetails…"
            value={taskRules}
            onChange={event => onTaskRules(event.target.value)}
          />
        </label>
        <details className="advanced-details">
          <summary>進階限制（執行模式與允許修改路徑）</summary>
          <div className="two-col" style={{ marginTop: 10 }}>
            <label>
              執行模式
              <select
                value={policy.tdd ? 'tdd' : 'standard'}
                disabled={rules.some(ruleItem => ruleIds.includes(ruleItem.id) && ruleItem.requiresTdd)}
                onChange={event => onPolicy({ ...policy, tdd: event.target.value === 'tdd' })}
              >
                <option value="tdd">TDD：Baseline → Red → Green → Refactor</option>
                <option value="standard">一般實作：Baseline → 實作與測試</option>
              </select>
            </label>
            <label>
              允許編輯的路徑（每行一項）
              <textarea
                rows={2}
                placeholder={'frontend/src/\nbackend/'}
                value={policy.allowedPaths.join('\n')}
                onChange={event => onPolicy({ ...policy, allowedPaths: event.target.value.split('\n') })}
              />
            </label>
          </div>
        </details>
      </section>
      <WorkflowEditor value={workflow} onChange={onWorkflow} providers={providers} />
    </>
  );
}
