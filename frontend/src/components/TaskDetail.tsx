import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { api, LatestRequest } from '../api';
import { useAction, useResource } from '../hooks';
import {
  appendPage,
  artifactUrl,
  cancellable,
  editablePlan,
  formatBytes,
  formatDate,
  phases,
  selectionText,
  shortCommit,
  statusNames,
  validationState,
} from '../domain';
import { Badge, Empty, Meta, Modal, ProseContent, useNotice } from './ui';
import { TaskContext, AiReviewReports } from './TaskContext';
import { PlanDiscussion } from './PlanDiscussion';
import { TaskActivity } from './LiveActivity';
import { TitleActivity, taskTitleState, useActivityPageTitle } from './TitleActivity';
import { FailureRecovery, TaskModelSettings } from './FailureRecovery';
import { RetryPanel } from './RetryPanel';
import { WorkspaceRecovery } from './WorkspaceRecovery';
import { TaskPreview } from './TaskPreview';
import { WorkflowSettings } from './WorkflowPanel';
import { TaskPermissions } from './TaskPermissions';
import type {
  Artifact,
  ArtifactPage,
  DetailData,
  Diff,
  GitState,
  Project,
  Run,
  TaskEvent,
  Validation,
  WorkTask,
} from '../types';

interface PlanScrollAnchor {
  lineNumber: number;
  charOffset: number;
  scrollRatio: number;
  windowScrollY: number;
}

function createTextareaMirror(textareaElement: HTMLTextAreaElement): HTMLDivElement {
  const computedStyle = window.getComputedStyle(textareaElement);
  const mirrorDiv = document.createElement('div');
  mirrorDiv.style.position = 'fixed';
  mirrorDiv.style.top = '-9999px';
  mirrorDiv.style.left = '-9999px';
  mirrorDiv.style.visibility = 'hidden';
  mirrorDiv.style.pointerEvents = 'none';
  mirrorDiv.style.whiteSpace = 'pre-wrap';
  mirrorDiv.style.overflowWrap = 'break-word';
  mirrorDiv.style.wordBreak = 'break-word';
  mirrorDiv.style.boxSizing = computedStyle.boxSizing || 'border-box';
  mirrorDiv.style.width = `${textareaElement.clientWidth || 680}px`;
  mirrorDiv.style.fontFamily = computedStyle.fontFamily;
  mirrorDiv.style.fontSize = computedStyle.fontSize;
  mirrorDiv.style.fontWeight = computedStyle.fontWeight;
  mirrorDiv.style.lineHeight = computedStyle.lineHeight;
  mirrorDiv.style.letterSpacing = computedStyle.letterSpacing;
  mirrorDiv.style.paddingTop = computedStyle.paddingTop;
  mirrorDiv.style.paddingRight = computedStyle.paddingRight;
  mirrorDiv.style.paddingBottom = computedStyle.paddingBottom;
  mirrorDiv.style.paddingLeft = computedStyle.paddingLeft;
  document.body.appendChild(mirrorDiv);
  return mirrorDiv;
}

function measureTextareaCharOffsetTop(
  textareaElement: HTMLTextAreaElement,
  fullText: string,
  targetCharOffset: number
): number {
  const mirrorDiv = createTextareaMirror(textareaElement);
  try {
    const normalizedText = fullText.replace(/\r\n/g, '\n');
    const safeOffset = Math.min(normalizedText.length, Math.max(0, targetCharOffset));
    mirrorDiv.textContent = normalizedText.slice(0, safeOffset);
    const markerSpan = document.createElement('span');
    markerSpan.textContent = '\u200b';
    mirrorDiv.appendChild(markerSpan);
    return markerSpan.offsetTop;
  } finally {
    mirrorDiv.remove();
  }
}

function capturePreviewScrollAnchor(previewContainer: HTMLElement | null): PlanScrollAnchor {
  const windowScrollY = typeof window !== 'undefined' ? window.scrollY : 0;
  if (!previewContainer) {
    return { lineNumber: 0, charOffset: 0, scrollRatio: 0, windowScrollY };
  }
  const maxScroll = Math.max(0, previewContainer.scrollHeight - previewContainer.clientHeight);
  const scrollRatio = maxScroll > 0 ? previewContainer.scrollTop / maxScroll : 0;
  if (previewContainer.scrollTop <= 2) {
    return { lineNumber: 0, charOffset: 0, scrollRatio: 0, windowScrollY };
  }

  const allSourceBlocks = Array.from(
    previewContainer.querySelectorAll<HTMLElement>('[data-source-line]')
  );
  const leafBlocks = allSourceBlocks.filter(
    blockElement => !blockElement.querySelector('[data-source-line]')
  );
  const candidateBlocks = leafBlocks.length > 0 ? leafBlocks : allSourceBlocks;
  const containerRect = previewContainer.getBoundingClientRect();
  let matchedBlock = candidateBlocks[0];

  for (const blockElement of candidateBlocks) {
    const blockRect = blockElement.getBoundingClientRect();
    if (blockRect.bottom > containerRect.top + 10) {
      matchedBlock = blockElement;
      break;
    }
  }

  return {
    lineNumber: Number(matchedBlock?.dataset.sourceLine || 0),
    charOffset: Number(matchedBlock?.dataset.charOffset || 0),
    scrollRatio,
    windowScrollY,
  };
}

function captureEditorScrollAnchor(
  textareaElement: HTMLTextAreaElement | null,
  fullText: string
): PlanScrollAnchor {
  const windowScrollY = typeof window !== 'undefined' ? window.scrollY : 0;
  if (!textareaElement) {
    return { lineNumber: 0, charOffset: 0, scrollRatio: 0, windowScrollY };
  }
  const maxScroll = Math.max(0, textareaElement.scrollHeight - textareaElement.clientHeight);
  const scrollRatio = maxScroll > 0 ? textareaElement.scrollTop / maxScroll : 0;
  const normalizedText = fullText.replace(/\r\n/g, '\n');
  const textLines = normalizedText.split('\n');
  const lineOffsets: number[] = [];
  let runningOffset = 0;
  for (const lineText of textLines) {
    lineOffsets.push(runningOffset);
    runningOffset += lineText.length + 1;
  }

  if (textareaElement.scrollTop <= 2) {
    return { lineNumber: 0, charOffset: 0, scrollRatio: 0, windowScrollY };
  }

  const targetScrollY = textareaElement.scrollTop + 14;
  const mirrorDiv = createTextareaMirror(textareaElement);
  try {
    let lowIndex = 0;
    let highIndex = textLines.length - 1;
    let bestLineIndex = 0;

    while (lowIndex <= highIndex) {
      const midIndex = Math.floor((lowIndex + highIndex) / 2);
      mirrorDiv.textContent = normalizedText.slice(0, lineOffsets[midIndex]);
      const markerSpan = document.createElement('span');
      markerSpan.textContent = '\u200b';
      mirrorDiv.appendChild(markerSpan);
      const measuredTop = markerSpan.offsetTop;

      if (measuredTop <= targetScrollY) {
        bestLineIndex = midIndex;
        lowIndex = midIndex + 1;
      } else {
        highIndex = midIndex - 1;
      }
    }

    if (bestLineIndex === 0 && scrollRatio > 0.05 && mirrorDiv.scrollHeight === 0) {
      bestLineIndex = Math.min(
        textLines.length - 1,
        Math.max(0, Math.round(scrollRatio * (textLines.length - 1)))
      );
    }

    return {
      lineNumber: bestLineIndex,
      charOffset: lineOffsets[bestLineIndex] || 0,
      scrollRatio,
      windowScrollY,
    };
  } finally {
    mirrorDiv.remove();
  }
}

function applyScrollAnchorToEditor(
  textareaElement: HTMLTextAreaElement | null,
  anchor: PlanScrollAnchor,
  fullText: string
) {
  if (!textareaElement) return;
  const normalizedText = fullText.replace(/\r\n/g, '\n');
  const safeCharOffset = Math.min(normalizedText.length, Math.max(0, anchor.charOffset));
  const maxScroll = Math.max(0, textareaElement.scrollHeight - textareaElement.clientHeight);

  let targetScrollTop = 0;
  if (anchor.scrollRatio >= 0.99 && maxScroll > 0) {
    targetScrollTop = maxScroll;
  } else if (anchor.lineNumber > 0 || anchor.scrollRatio > 0.01) {
    const measuredPixelTop = measureTextareaCharOffsetTop(
      textareaElement,
      normalizedText,
      safeCharOffset
    );
    if (measuredPixelTop > 0) {
      targetScrollTop = Math.min(maxScroll, Math.max(0, measuredPixelTop - 14));
    } else if (maxScroll > 0) {
      targetScrollTop = Math.round(anchor.scrollRatio * maxScroll);
    }
  }

  textareaElement.focus({ preventScroll: true });
  textareaElement.setSelectionRange(safeCharOffset, safeCharOffset);
  textareaElement.scrollTop = targetScrollTop;
  if (typeof window !== 'undefined' && Math.abs(window.scrollY - anchor.windowScrollY) > 2) {
    window.scrollTo({ top: anchor.windowScrollY });
  }
}

function applyScrollAnchorToPreview(
  previewContainer: HTMLElement | null,
  anchor: PlanScrollAnchor
) {
  if (!previewContainer) return;
  const maxScroll = Math.max(0, previewContainer.scrollHeight - previewContainer.clientHeight);
  if (anchor.scrollRatio >= 0.99 && maxScroll > 0) {
    previewContainer.scrollTop = maxScroll;
  } else if (anchor.lineNumber <= 0 && anchor.scrollRatio <= 0.01) {
    previewContainer.scrollTop = 0;
  } else {
    const allSourceBlocks = Array.from(
      previewContainer.querySelectorAll<HTMLElement>('[data-source-line]')
    );
    const leafBlocks = allSourceBlocks.filter(
      blockElement => !blockElement.querySelector('[data-source-line]')
    );
    const candidateBlocks = leafBlocks.length > 0 ? leafBlocks : allSourceBlocks;
    let targetBlock = candidateBlocks[0];

    for (const blockElement of candidateBlocks) {
      const blockLine = Number(blockElement.dataset.sourceLine || 0);
      if (blockLine <= anchor.lineNumber) {
        targetBlock = blockElement;
      } else {
        break;
      }
    }

    if (targetBlock && maxScroll > 0) {
      const containerRect = previewContainer.getBoundingClientRect();
      const blockRect = targetBlock.getBoundingClientRect();
      const targetScrollTop = previewContainer.scrollTop + (blockRect.top - containerRect.top) - 12;
      previewContainer.scrollTop = Math.min(maxScroll, Math.max(0, targetScrollTop));
    } else if (maxScroll > 0) {
      previewContainer.scrollTop = Math.round(anchor.scrollRatio * maxScroll);
    }
  }

  if (typeof window !== 'undefined' && Math.abs(window.scrollY - anchor.windowScrollY) > 2) {
    window.scrollTo({ top: anchor.windowScrollY });
  }
}

function PlanPanel({
  data,
  onChanged,
  onDirty,
  modalOpenExternal = false,
  onCloseExternalModal,
}: {
  data: DetailData;
  onChanged: () => void;
  onDirty: (dirty: boolean) => void;
  modalOpenExternal?: boolean;
  onCloseExternalModal?: () => void;
}) {
  const currentTask = data.task;
  const [selectedRevision, setSelectedRevision] = useState<number | null>(null);
  const [draftText, setDraftText] = useState<string | null>(null);
  const [versionSnapshot, setVersionSnapshot] = useState<number | null>(null);
  const [revisionSnapshot, setRevisionSnapshot] = useState<number | null>(null);
  const [isInlineEditing, setIsInlineEditing] = useState(false);
  const [discussionOpen, setDiscussionOpen] = useState(false);
  const [isPlanModalOpen, setIsPlanModalOpen] = useState(false);
  const [modalViewMode, setModalViewMode] = useState<'preview' | 'edit'>('preview');
  const inlinePreviewRef = useRef<HTMLDivElement | null>(null);
  const inlineEditorRef = useRef<HTMLTextAreaElement | null>(null);
  const modalPreviewRef = useRef<HTMLDivElement | null>(null);
  const modalEditorRef = useRef<HTMLTextAreaElement | null>(null);
  const pendingInlineAnchorRef = useRef<PlanScrollAnchor | null>(null);
  const pendingModalAnchorRef = useRef<PlanScrollAnchor | null>(null);
  const notice = useNotice();
  const action = useAction(notice);

  const isCurrentRevision =
    selectedRevision === null || selectedRevision === currentTask.plan?.revision;
  const canEdit = editablePlan.includes(currentTask.status) && isCurrentRevision;
  const chosenPlan =
    selectedRevision === null
      ? currentTask.plan
      : data.plans.find(planItem => planItem.revision === selectedRevision);
  const isDirty = draftText !== null;
  const activePlanText = draftText ?? chosenPlan?.text ?? '';
  const showModal = isPlanModalOpen || modalOpenExternal;

  useEffect(() => {
    onDirty(isDirty || !isCurrentRevision);
  }, [isDirty, isCurrentRevision, onDirty]);

  useLayoutEffect(() => {
    const anchor = pendingInlineAnchorRef.current;
    if (!anchor) return;
    pendingInlineAnchorRef.current = null;
    if (isInlineEditing) {
      applyScrollAnchorToEditor(inlineEditorRef.current, anchor, activePlanText);
    } else {
      applyScrollAnchorAnchorWithFrame(inlinePreviewRef.current, anchor);
    }
  }, [isInlineEditing, activePlanText]);

  useLayoutEffect(() => {
    if (!showModal) return;
    const anchor = pendingModalAnchorRef.current;
    if (!anchor) return;
    pendingModalAnchorRef.current = null;
    if (modalViewMode === 'edit') {
      applyScrollAnchorToEditor(modalEditorRef.current, anchor, activePlanText);
    } else {
      applyScrollAnchorAnchorWithFrame(modalPreviewRef.current, anchor);
    }
  }, [showModal, modalViewMode, activePlanText]);

  function applyScrollAnchorAnchorWithFrame(
    previewContainer: HTMLElement | null,
    anchor: PlanScrollAnchor
  ) {
    applyScrollAnchorToPreview(previewContainer, anchor);
    requestAnimationFrame(() => {
      applyScrollAnchorToPreview(previewContainer, anchor);
    });
  }

  function switchInlineMode(targetEditMode: boolean) {
    if (targetEditMode === isInlineEditing) return;
    pendingInlineAnchorRef.current = isInlineEditing
      ? captureEditorScrollAnchor(inlineEditorRef.current, activePlanText)
      : capturePreviewScrollAnchor(inlinePreviewRef.current);
    setIsInlineEditing(targetEditMode);
  }

  function switchModalMode(targetMode: 'preview' | 'edit') {
    if (targetMode === modalViewMode) return;
    pendingModalAnchorRef.current =
      modalViewMode === 'edit'
        ? captureEditorScrollAnchor(modalEditorRef.current, activePlanText)
        : capturePreviewScrollAnchor(modalPreviewRef.current);
    setModalViewMode(targetMode);
  }

  function openPlanModalWithSync(initialMode: 'preview' | 'edit' = 'preview') {
    pendingModalAnchorRef.current = isInlineEditing
      ? captureEditorScrollAnchor(inlineEditorRef.current, activePlanText)
      : capturePreviewScrollAnchor(inlinePreviewRef.current);
    setModalViewMode(initialMode);
    setIsPlanModalOpen(true);
  }

  function handleDraftChange(nextValue: string) {
    if (!isDirty) {
      setVersionSnapshot(currentTask.version);
      setRevisionSnapshot(currentTask.plan?.revision ?? null);
    }
    setDraftText(nextValue);
  }

  function handleResetPlan() {
    setDraftText(null);
    setSelectedRevision(null);
    setVersionSnapshot(null);
  }

  async function handleSavePlan() {
    if (isInlineEditing) {
      pendingInlineAnchorRef.current = captureEditorScrollAnchor(
        inlineEditorRef.current,
        activePlanText
      );
    }
    if (showModal && modalViewMode === 'edit') {
      pendingModalAnchorRef.current = captureEditorScrollAnchor(
        modalEditorRef.current,
        activePlanText
      );
    }
    await api('/api/tasks/' + currentTask.id + '/plan', 'POST', {
      text: activePlanText,
      expectedVersion: versionSnapshot ?? currentTask.version,
    });
    setDraftText(null);
    setSelectedRevision(null);
    setIsInlineEditing(false);
    setModalViewMode('preview');
    notice('計畫已保存，請核准新版本。');
    onChanged();
  }

  function closePlanModal() {
    pendingInlineAnchorRef.current =
      modalViewMode === 'edit'
        ? captureEditorScrollAnchor(modalEditorRef.current, activePlanText)
        : capturePreviewScrollAnchor(modalPreviewRef.current);
    setIsPlanModalOpen(false);
    onCloseExternalModal?.();
    requestAnimationFrame(() => {
      const anchor = pendingInlineAnchorRef.current;
      if (!anchor) return;
      pendingInlineAnchorRef.current = null;
      if (isInlineEditing) {
        applyScrollAnchorToEditor(inlineEditorRef.current, anchor, activePlanText);
      } else {
        applyScrollAnchorToPreview(inlinePreviewRef.current, anchor);
      }
    });
  }

  return (
    <>
      <div className="detail-grid">
        <article className="panel plan-panel-card">
          <div className="panel-heading plan-toolbar-heading">
            <div>
              <h2>實作計畫（由規劃 AI 擬定，經你確認後執行）</h2>
              <p>
                目前版本 {currentTask.plan?.revision || '尚未建立'} · 基底{' '}
                {shortCommit(currentTask.plan?.baseCommit)}
              </p>
            </div>
            <div className="plan-toolbar-actions">
              <button type="button" className="secondary" aria-expanded={discussionOpen} onClick={()=>setDiscussionOpen(!discussionOpen)}>
                與規劃 AI 討論
              </button>
              <select
                aria-label="計畫版本"
                value={selectedRevision ?? currentTask.plan?.revision ?? ''}
                onChange={event => {
                  if (isDirty) {
                    notice('請先保存編輯或重新讀取當前版本。', true);
                    return;
                  }
                  setSelectedRevision(Number(event.target.value));
                }}
              >
                {!currentTask.plan && <option value="">尚無計畫</option>}
                {data.plans
                  .slice()
                  .reverse()
                  .map(planItem => (
                    <option key={planItem.id || planItem.revision} value={planItem.revision}>
                      版本 {planItem.revision}
                    </option>
                  ))}
              </select>
              <div className="plan-mode-switch" role="group" aria-label="檢視模式">
                <button
                  type="button"
                  className={!isInlineEditing ? 'active' : ''}
                  onClick={() => switchInlineMode(false)}
                >
                  📄 格式預覽
                </button>
                <button
                  type="button"
                  className={isInlineEditing ? 'active' : ''}
                  onClick={() => switchInlineMode(true)}
                >
                  ✏️ 編輯文字
                </button>
              </div>
              <button
                type="button"
                className="secondary plan-expand-btn"
                onClick={() => openPlanModalWithSync(isInlineEditing ? 'edit' : 'preview')}
              >
                ⤢ 視窗檢視
              </button>
            </div>
          </div>

          {isDirty && revisionSnapshot !== currentTask.plan?.revision && (
            <div className="info-banner">計畫已更新；未保存的編輯仍保留，請先比對新版。</div>
          )}

          {!isInlineEditing && (
            <div
              ref={inlinePreviewRef}
              className="plan-document-surface"
              onDoubleClick={() => openPlanModalWithSync('preview')}
            >
              {activePlanText.trim() ? (
                <ProseContent content={activePlanText} />
              ) : (
                <Empty
                  title="尚無實作計畫內容"
                  description="點擊上方「AI 產生計畫」交由規劃 AI 擬定，或切換至「✏️ 編輯文字」手動撰寫。"
                />
              )}
            </div>
          )}

          <label className="sr-only" htmlFor="plan-editor">
            計畫內容
          </label>
          <textarea
            ref={inlineEditorRef}
            id="plan-editor"
            className={isInlineEditing ? 'plan-editor' : 'sr-only'}
            rows={15}
            readOnly={!canEdit}
            value={activePlanText}
            placeholder="手動撰寫計畫，或交由規劃 AI 產生。"
            onChange={event => handleDraftChange(event.target.value)}
          />

          <div className="panel-footer">
            <span className="muted">
              {isDirty ? '● 有未保存的編輯' : '✓ 核准的是當前已顯示版本（雙擊計畫可放大檢視）'}
            </span>
            <div className="button-row">
              <button className="secondary" onClick={handleResetPlan}>
                重新讀取當前版本
              </button>
              {editablePlan.includes(currentTask.status) && (
                <button
                  className="secondary"
                  disabled={!canEdit || action.busy}
                  onClick={() => void action.run(handleSavePlan)}
                >
                  保存計畫
                </button>
              )}
            </div>
          </div>
        </article>

        <div className="plan-side-column">
          {discussionOpen && <PlanDiscussion key={currentTask.id} task={currentTask} plan={chosenPlan} dirty={isDirty}
            onClose={()=>setDiscussionOpen(false)} onApplied={()=>{handleResetPlan();onChanged();}}/>}
          <article className="panel">
            <h2>需求</h2>
            <ProseContent content={currentTask.description} />
            <h3>驗收條件</h3>
            <ProseContent content={currentTask.acceptanceCriteria} />
          </article>
          <article className="panel">
            <h2>計畫版本歷史</h2>
            {!data.plans.length && <Empty title="尚無計畫版本" />}
            {data.plans
              .slice()
              .reverse()
              .map(planItem => (
                <details className="history-item" key={planItem.id || planItem.revision}>
                  <summary>
                    版本 {planItem.revision} · {formatDate(planItem.createdAt)}
                  </summary>
                  <div className="plan-history-preview">
                    <ProseContent content={planItem.text} />
                  </div>
                </details>
              ))}
          </article>
        </div>
      </div>

      {showModal && (
        <Modal
          title={currentTask.title + ' · 實作計畫'}
          eyebrow={
            'IMPLEMENTATION PLAN · 版本 ' +
            (chosenPlan?.revision || currentTask.plan?.revision || '草稿')
          }
          wide
          onClose={closePlanModal}
        >
          <div className="dialog-body plan-modal-body">
            <div className="plan-modal-toolbar">
              <div className="plan-modal-meta">
                <Badge status={currentTask.status} />
                <span className="muted">
                  規劃 AI：{selectionText(currentTask.ai?.planning)} · 基底{' '}
                  {shortCommit(chosenPlan?.baseCommit || currentTask.plan?.baseCommit)}
                </span>
              </div>
              <div className="plan-mode-switch" role="group" aria-label="視窗計畫模式">
                <button
                  type="button"
                  className={modalViewMode === 'preview' ? 'active' : ''}
                  onClick={() => switchModalMode('preview')}
                >
                  📄 結構化計畫格式
                </button>
                {canEdit && (
                  <button
                    type="button"
                    className={modalViewMode === 'edit' ? 'active' : ''}
                    onClick={() => switchModalMode('edit')}
                  >
                    ✏️ 編輯計畫內容
                  </button>
                )}
              </div>
            </div>

            {modalViewMode === 'preview' ? (
              <div ref={modalPreviewRef} className="plan-modal-document">
                {activePlanText.trim() ? (
                  <ProseContent content={activePlanText} />
                ) : (
                  <Empty title="尚無計畫內容" description="可切換至編輯模式撰寫，或關閉視窗由 AI 產生。" />
                )}
              </div>
            ) : (
              <textarea
                ref={modalEditorRef}
                aria-label="視窗編輯計畫內容"
                className="plan-editor plan-modal-editor"
                rows={20}
                readOnly={!canEdit}
                value={activePlanText}
                placeholder="手動撰寫或微調計畫內容…"
                onChange={event => handleDraftChange(event.target.value)}
              />
            )}
          </div>
          <div className="dialog-footer plan-modal-footer">
            <span className="muted">
              {isDirty ? '⚠️ 尚有未保存的修改，請先保存再核准' : '確認無誤後可直接核准並開始實作'}
            </span>
            <div className="button-row">
              {isDirty && (
                <button type="button" className="secondary" onClick={handleResetPlan}>
                  放棄未保存修改
                </button>
              )}
              {editablePlan.includes(currentTask.status) && isDirty && (
                <button
                  type="button"
                  className="secondary"
                  disabled={!canEdit || action.busy}
                  onClick={() => void action.run(handleSavePlan)}
                >
                  保存計畫修改
                </button>
              )}
              {currentTask.status === 'AwaitingApproval' && currentTask.plan && (
                <button
                  type="button"
                  className="warning-button"
                  disabled={isDirty || !isCurrentRevision || action.busy}
                  onClick={() =>
                    void action.run(async () => {
                      await api('/api/tasks/' + currentTask.id + '/approve-plan', 'POST', {
                        expectedVersion: currentTask.version,
                        planRevision: currentTask.plan!.revision,
                      });
                      notice('計畫已核准，實作已排隊。');
                      closePlanModal();
                      onChanged();
                    })
                  }
                >
                  ✓ 核准計畫並開始實作
                </button>
              )}
              <button type="button" className="secondary" onClick={closePlanModal}>
                關閉
              </button>
            </div>
          </div>
        </Modal>
      )}
    </>
  );
}

function ArtifactPanel({ run }: { run: Run }) {
  const loadArtifacts = useCallback(
    (signal: AbortSignal) =>
      api<Artifact[]>('/api/runs/' + run.id + '/artifacts', 'GET', undefined, { signal }),
    [run.id]
  );
  const filesResource = useResource('files:' + run.id, loadArtifacts);
  const notice = useNotice();
  const [selectedPath, setSelectedPath] = useState('');
  const [artifactPage, setArtifactPage] = useState<Omit<ArtifactPage, 'offset'> | null>(null);
  const [loadError, setLoadError] = useState('');
  const [isBusy, setIsBusy] = useState(false);
  const latestRequest = useMemo(() => new LatestRequest(), [run.id]);
  const busyRef = useRef(false);

  useEffect(() => {
    setArtifactPage(null);
    setLoadError('');
    if (!selectedPath) return;
    setIsBusy(true);
    void latestRequest
      .run(signal =>
        api<ArtifactPage>(artifactUrl(run.id, selectedPath), 'GET', undefined, { signal })
      )
      .then(fetchedPage => {
        if (fetchedPage) {
          setArtifactPage(
            appendPage({ path: selectedPath, text: '', nextOffset: 0, hasMore: true }, fetchedPage)
          );
        }
      })
      .catch(error => setLoadError(error.message))
      .finally(() => setIsBusy(false));
    return () => latestRequest.cancel();
  }, [selectedPath, run.id, latestRequest]);

  useEffect(() => () => latestRequest.cancel(), [latestRequest]);

  async function loadMorePage() {
    if (!artifactPage || busyRef.current) return;
    busyRef.current = true;
    setIsBusy(true);
    try {
      const fetchedPage = await latestRequest.run(signal =>
        api<ArtifactPage>(
          artifactUrl(run.id, selectedPath, artifactPage.nextOffset),
          'GET',
          undefined,
          { signal }
        )
      );
      if (fetchedPage) setArtifactPage(appendPage(artifactPage, fetchedPage));
    } catch (error) {
      setLoadError(error instanceof Error ? error.message : '讀取失敗');
    } finally {
      busyRef.current = false;
      setIsBusy(false);
    }
  }

  return (
    <article className="panel artifact-panel">
      <div className="panel-heading">
        <div>
          <h2>執行檔案</h2>
          <p>CLI 輸出、測試報告與驗證摘要。</p>
        </div>
        <button
          className="icon-button"
          aria-label="更新 Artifact 清單"
          onClick={filesResource.refresh}
        >
          ↻
        </button>
      </div>
      {filesResource.error && <div className="error-banner">{filesResource.error}</div>}
      <div className="artifact-list">
        {!filesResource.data?.length && (
          <Empty
            title={filesResource.data ? '尚無保存檔案' : '讀取執行紀錄…'}
            description="CLI 完成階段後會保存輸出。"
          />
        )}
        {filesResource.data?.map(artifactFile => (
          <button
            className={'artifact-file' + (artifactFile.path === selectedPath ? ' active' : '')}
            key={artifactFile.path}
            onClick={() => setSelectedPath(artifactFile.path)}
          >
            <span>{artifactFile.path}</span>
            <span>{formatBytes(artifactFile.bytes)}</span>
          </button>
        ))}
      </div>
      <div className="code-header">
        <span>{selectedPath || '選擇檔案'}</span>
        <button
          className="icon-button"
          disabled={!artifactPage || artifactPage.path !== selectedPath}
          onClick={() => {
            void navigator.clipboard
              .writeText(artifactPage!.text)
              .then(() => notice('已複製目前讀取的內容。'))
              .catch(() => notice('瀏覽器不允許複製，請手動選取內容。', true));
          }}
        >
          複製
        </button>
      </div>
      <pre className="code-view">
        {loadError ||
          (artifactPage?.path === selectedPath
            ? artifactPage.text || '檔案內容為空。'
            : selectedPath
              ? '讀取中…'
              : '選取檔案以讀取內容。')}
      </pre>
      {artifactPage?.path === selectedPath && artifactPage.hasMore && (
        <button className="secondary" disabled={isBusy} onClick={() => void loadMorePage()}>
          讀取下一頁
        </button>
      )}
      <p className="footnote">CLI log 在階段結束後保存，執行中的進度請看事件。</p>
    </article>
  );
}

function ExecutionPanel({ data }: { data: DetailData }) {
  const [selectedRunId, setSelectedRunId] = useState('');
  const [followLatest, setFollowLatest] = useState(true);
  const currentRun =
    data.runs.find(runItem => runItem.id === selectedRunId) || data.runs.at(-1);

  useEffect(() => {
    if (followLatest || !data.runs.some(runItem => runItem.id === selectedRunId)) {
      setSelectedRunId(data.runs.at(-1)?.id || '');
    }
  }, [data.runs, followLatest, selectedRunId]);

  const runId = currentRun?.id || '';
  const loadValidations = useCallback(
    (signal: AbortSignal) =>
      api<Validation[]>('/api/runs/' + runId + '/validations', 'GET', undefined, { signal }),
    [runId]
  );
  const evidenceResource = useResource(
    'evidence:' + runId,
    loadValidations,
    currentRun && !currentRun.finishedAt ? 5000 : 0,
    !!currentRun
  );

  const latestPhaseEvent = data.events
    .filter(eventItem => eventItem.kind === 'PhaseStarted' || eventItem.kind === 'PhaseVerified')
    .at(-1);
  const eventLabels: Record<string, string> = {
    TaskCreated: '任務建立',
    PlanningStarted: '規劃開始',
    PlanningFinished: '規劃結束',
    RunStarted: '實作開始',
    RunFinished: '實作結束',
    PhaseStarted: '階段開始',
    PhaseVerified: '階段驗證完成',
    PlannerGuided: '規劃 AI 規則內自動指導',
    WorkflowPaused: '超出規則／階段確認點暫停',
    WorkflowContinued: '人工確認後繼續執行',
    TaskReviewed: '成果審核',
    TaskRetried: '重新排隊',
    AiAcceptanceStarted: 'AI 驗收開始',
    AiAcceptance: 'AI 驗收結果',
    AiCorrectionQueued: 'AI 修正排隊',
    StorageCleaned: '儲存空間清理',
  };

  return (
    <>
      <article className="panel">
        <div className="panel-heading">
          <div>
            <h2>執行紀錄</h2>
            <p>規劃與實作的每次 Run 都保留紀錄。</p>
          </div>
          <select
            aria-label="選擇執行紀錄"
            value={currentRun?.id || ''}
            onChange={event => {
              setSelectedRunId(event.target.value);
              setFollowLatest(event.target.value === data.runs.at(-1)?.id);
            }}
          >
            {data.runs
              .slice()
              .reverse()
              .map(runItem => (
                <option value={runItem.id} key={runItem.id}>
                  {runItem.kind === 'Planning' ? '規劃' : '實作'} · {formatDate(runItem.startedAt)} ·{' '}
                  {statusNames[runItem.status] || runItem.status}
                </option>
              ))}
          </select>
        </div>
        {!currentRun ? (
          <Empty title="尚無執行紀錄" />
        ) : (
          <>
            <div className="meta-grid">
              <Meta label="Run ID" value={currentRun.id} />
              <Meta label="AI / 模型" value={selectionText(currentRun.selection)} />
              <Meta
                label="基底 / 成果"
                value={shortCommit(currentRun.baseCommit) + ' / ' + shortCommit(currentRun.resultCommit)}
              />
              <Meta label="Worktree" value={currentRun.worktreePath} />
              <Meta label="開始" value={formatDate(currentRun.startedAt)} />
              <Meta label="結束" value={formatDate(currentRun.finishedAt)} />
            </div>
            {(currentRun.error || evidenceResource.error) && (
              <div className="error-banner">{currentRun.error || evidenceResource.error}</div>
            )}
            {currentRun.kind === 'Implementation' && !data.task.workflow?.steps && (
              <div className="phase-grid">
                {(data.task.policy?.tdd === false ? ['Baseline', 'Implementation'] : phases).map(
                  phaseName => {
                    const matchedValidation = evidenceResource.data?.filter(
                      validationItem => validationItem.phase === phaseName && validationItem.origin !== 'StructuredTool'
                    ).at(-1);
                    const stateInfo = validationState(matchedValidation);
                    const isPhaseRunning =
                      !currentRun.finishedAt &&
                      latestPhaseEvent?.kind === 'PhaseStarted' &&
                      latestPhaseEvent.message === phaseName;
                    return (
                      <div
                        className={
                          'phase-card tone-' +
                          (isPhaseRunning ? 'blue' : stateInfo.tone) +
                          (isPhaseRunning ? ' running' : '')
                        }
                        key={phaseName}
                      >
                        <div className="phase-title">
                          <span>{phaseName}</span>
                          <span className={'chip ' + (isPhaseRunning ? 'blue' : stateInfo.tone)}>
                            {isPhaseRunning ? '執行中' : stateInfo.label}
                          </span>
                        </div>
                        <p>
                          {matchedValidation
                            ? matchedValidation.total +
                              ' 個測試 · ' +
                              matchedValidation.failed +
                              ' 個失敗'
                            : '等待測試證據'}
                        </p>
                      </div>
                    );
                  }
                )}
              </div>
            )}
            {data.task.workflow?.steps && (
              <div className="phase-grid">
                {evidenceResource.data?.map(validationItem => (
                  <div
                    className={'phase-card tone-' + validationState(validationItem).tone}
                    key={validationItem.evidencePath}
                  >
                    <strong>
                      {data.task.workflow?.steps?.find(
                        stepItem => stepItem.id === validationItem.stepId
                      )?.name || '驗證'}{' '}
                      · {validationItem.phase}
                    </strong>
                    <p>{validationItem.origin==='StructuredTool'?'AI 工具自行驗證':validationItem.attempt?`修正後驗證 · 第 ${validationItem.attempt} 輪`:'系統驗證'}</p>
                    <p>
                      {validationState(validationItem).label}{validationItem.kind==='Command'?` · 退出碼 ${validationItem.exitCode}`:` · ${validationItem.total} 個測試 · ${validationItem.failed} 個失敗`}
                    </p>
                    {validationItem.error&&<p className="error-text">{validationItem.error}</p>}
                  </div>
                ))}
              </div>
            )}
          </>
        )}
      </article>
      <AiReviewReports run={currentRun} />
      <div className="detail-grid">
        <article className="panel">
          <div className="panel-heading">
            <div>
              <h2>階段事件</h2>
              <p>每五秒更新，保留狀態變更與規劃 AI 指揮紀錄。</p>
            </div>
          </div>
          <div className="timeline">
            {!data.events.length && <Empty title="尚無事件" />}
            {data.events
              .slice()
              .reverse()
              .map(eventItem => (
                <div className="timeline-item" key={eventItem.sequence}>
                  <strong>{eventLabels[eventItem.kind] || eventItem.kind}</strong>
                  <p>{statusNames[eventItem.message] || eventItem.message}</p>
                  <time>{formatDate(eventItem.createdAt)}</time>
                </div>
              ))}
          </div>
        </article>
        {currentRun && <ArtifactPanel key={currentRun.id} run={currentRun} />}
      </div>
    </>
  );
}

function MergeDialog({
  task,
  run,
  project,
  onClose,
  onMerged,
}: {
  task: WorkTask;
  run: Run;
  project?: Project;
  onClose: () => void;
  onMerged: () => void;
}) {
  const [snapshot] = useState({
    taskId: task.id,
    version: task.version,
    runId: run.id,
    resultCommit: run.resultCommit,
  });
  const loadGitState = useCallback(
    (signal: AbortSignal) =>
      api<GitState>('/api/projects/' + task.projectId + '/git-state', 'GET', undefined, { signal }),
    [task.projectId]
  );
  const gitStateResource = useResource('git:' + task.projectId, loadGitState, 0);
  const notice = useNotice();
  const action = useAction(notice);
  const isMergeAllowed =
    gitStateResource.data?.clean && gitStateResource.data.branch === project?.baseBranch;

  return (
    <Modal title="確認本地合併" eyebrow="LOCAL MERGE" onClose={onClose}>
      <div className="dialog-body">
        {gitStateResource.error && <div className="error-banner">{gitStateResource.error}</div>}
        {!gitStateResource.data && !gitStateResource.error && (
          <Empty title="檢查本地目標分支…" />
        )}
        {gitStateResource.data && (
          <>
            <Meta label="專案" value={project?.name} />
            <Meta label="目標分支" value={gitStateResource.data.branch} />
            <Meta label="目前 HEAD" value={gitStateResource.data.headCommit} />
            <Meta label="核准成果" value={snapshot.resultCommit} />
            <Meta
              label="工作目錄"
              value={gitStateResource.data.clean ? '乾淨' : '有未提交的變更'}
            />
            {!isMergeAllowed && (
              <div className="error-banner">
                請切回設定的基底分支，並先處理未提交變更。
              </div>
            )}
          </>
        )}
        <p className="footnote">
          僅進行本地 fast-forward 合併，不推送遠端。HEAD 改變時後端會拒絕合併。
        </p>
        <p>若目標分支已前進，可建立獨立整合任務，重新驗證與審查後再合併。</p>
      </div>
      <div className="dialog-footer">
        <button className="secondary" onClick={onClose}>
          取消
        </button>
        <button className="secondary" disabled={!gitStateResource.data||action.busy||gitStateResource.data.branch!==project?.baseBranch}
          onClick={()=>void action.run(async()=>{
            const created=await api<WorkTask>('/api/tasks/'+snapshot.taskId+'/integrate','POST',{
              expectedVersion:snapshot.version,runId:snapshot.runId,resultCommit:snapshot.resultCommit,expectedTargetCommit:gitStateResource.data!.headCommit,
            });
            notice('整合任務已排隊；原成果保留，完成後需要重新審核。');onMerged();onClose();location.hash='#task/'+created.id;
          })}>建立整合與重驗證任務</button>
        <button
          className="primary"
          disabled={!isMergeAllowed || action.busy}
          onClick={() =>
            void action.run(async () => {
              await api('/api/tasks/' + snapshot.taskId + '/merge', 'POST', {
                runId: snapshot.runId,
                resultCommit: snapshot.resultCommit,
                expectedTargetCommit: gitStateResource.data!.headCommit,
                expectedVersion: snapshot.version,
              });
              notice('成果已合併到本地基底分支。');
              onMerged();
              onClose();
            })
          }
        >
          確認合併
        </button>
      </div>
    </Modal>
  );
}

function ReviewPanel({
  data,
  run,
  projects,
  onChanged,
}: {
  data: DetailData;
  run?: Run;
  projects: Project[];
  onChanged: () => void;
}) {
  const currentTask = data.task;
  const notice = useNotice();
  const action = useAction(notice);
  const [feedback, setFeedback] = useState('');
  const [diffData, setDiffData] = useState<Diff | null>(null);
  const [isMergeDialogOpen, setIsMergeDialogOpen] = useState(false);
  const latestRequest = useMemo(() => new LatestRequest(), [run?.id]);

  useEffect(() => () => latestRequest.cancel(), [latestRequest]);

  async function submitReview(decision: string) {
    if (!run?.resultCommit) throw new Error('尚無可審核成果。');
    if (decision === 'Reject' && !feedback.trim()) {
      throw new Error('請填寫退回修正的原因。');
    }
    await api('/api/tasks/' + currentTask.id + '/reviews', 'POST', {
      runId: run.id,
      resultCommit: run.resultCommit,
      expectedVersion: currentTask.version,
      decision,
      comments: feedback.trim() || null,
    });
    setFeedback('');
    notice(decision === 'Approve' ? '成果已核准。' : '成果已退回。');
    onChanged();
  }

  const isReviewable = currentTask.status === 'AwaitingReview';

  return (
    <>
      <AiReviewReports run={run} />
      <TaskPreview task={currentTask} run={run} project={projects.find(p=>p.id===currentTask.projectId)} />
      <div className={isReviewable ? 'attention-banner' : 'info-banner'}>
        {isReviewable
          ? '⚡ 請先點擊「讀取差異」檢查完整程式碼變更，確認無誤後即可核准並合併回本地分支。'
          : currentTask.status === 'Approved'
            ? '✓ 成果已核准，現在可以檢查本地目標分支並執行合併。'
            : '目前狀態：' + (statusNames[currentTask.status] || currentTask.status)}
      </div>
      <div className="detail-grid review-grid">
        <article className="panel">
          <div className="panel-heading">
            <div>
              <h2>成果差異</h2>
              <p>
                {run?.resultCommit
                  ? shortCommit(run.baseCommit) + ' → ' + shortCommit(run.resultCommit)
                  : '尚無可審核成果'}
              </p>
            </div>
            <button
              className={isReviewable && !diffData ? 'warning-button' : 'secondary'}
              disabled={!run?.resultCommit || action.busy}
              onClick={() =>
                void action.run(async () => {
                  const fetchedDiff = await latestRequest.run(signal =>
                    api<Diff>('/api/runs/' + run!.id + '/diff', 'GET', undefined, { signal })
                  );
                  if (fetchedDiff) setDiffData(fetchedDiff);
                })
              }
            >
              讀取差異
            </button>
          </div>
          <pre className="diff-view">
            {!diffData
              ? '請讀取並檢查成果差異，再提交審核。'
              : !diffData.patch
                ? '此成果沒有檔案差異。'
                : diffData.patch.split('\n').length > 2500
                  ? diffData.patch
                  : diffData.patch.split('\n').map((lineText, lineIndex) => (
                      <span
                        key={lineIndex}
                        className={
                          lineText.startsWith('diff ')
                            ? 'diff-file'
                            : lineText.startsWith('@@')
                              ? 'diff-hunk'
                              : lineText.startsWith('+')
                                ? 'diff-add'
                                : lineText.startsWith('-')
                                  ? 'diff-remove'
                                  : ''
                        }
                      >
                        {lineText + '\n'}
                      </span>
                    ))}
          </pre>
        </article>
        <article className="panel">
          <h2>人工驗收與本地合併</h2>
          <label>
            審核意見
            <textarea
              maxLength={20000}
              rows={5}
              placeholder="輸入驗收備註，若要退回修正請務必說明原因…"
              value={feedback}
              onChange={event => setFeedback(event.target.value)}
            />
          </label>
          <div className="button-row">
            <button
              className="primary"
              disabled={!isReviewable || !diffData || action.busy}
              onClick={() => void action.run(() => submitReview('Approve'))}
            >
              核准成果
            </button>
            <button
              className="danger-button"
              disabled={!isReviewable || action.busy}
              onClick={() => void action.run(() => submitReview('Reject'))}
            >
              退回修正
            </button>
          </div>
          <p className="footnote">
            {isReviewable
              ? diffData
                ? '正在審核 ' + shortCommit(run?.resultCommit)
                : '⚠ 讀取完整成果差異後才能解鎖「核准成果」按鈕。'
              : '任務待成果審核時才能提交決策。'}
          </p>
          {currentTask.status === 'Approved' && run?.resultCommit && (
            <div className="merge-box">
              <h3>本地合併</h3>
              <p className="muted">先檢查目標分支，再進行 fast-forward 合併回本地分支。</p>
              <button className="primary" onClick={() => setIsMergeDialogOpen(true)}>
                檢查並合併
              </button>
            </div>
          )}
          {currentTask.status === 'Merging' && (
            <>
              <p className="footnote">請先確認舊 Git 合併程序已停止，再核對中斷狀態。</p>
              <button
                className="secondary"
                disabled={action.busy}
                onClick={() =>
                  void action.run(async () => {
                    await api('/api/tasks/' + currentTask.id + '/merge/reconcile', 'POST');
                    notice('合併狀態已核對。');
                    onChanged();
                  })
                }
              >
                核對中斷合併
              </button>
            </>
          )}
        </article>
      </div>
      <div className="detail-grid">
        <article className="panel">
          <h2>審核歷史</h2>
          {!data.reviews.length && <Empty title="尚無審核紀錄" />}
          {data.reviews
            .slice()
            .reverse()
            .map(reviewItem => (
              <div className="history-item" key={reviewItem.id}>
                <strong>
                  {reviewItem.decision === 'Approve' ? '核准' : '退回'} ·{' '}
                  {formatDate(reviewItem.createdAt)}
                </strong>
                <p>{reviewItem.comments || '未附加意見'}</p>
                <p>{shortCommit(reviewItem.resultCommit)}</p>
              </div>
            ))}
        </article>
        <article className="panel">
          <h2>合併紀錄</h2>
          {!data.merges.length && <Empty title="尚無合併紀錄" />}
          {data.merges
            .slice()
            .reverse()
            .map(mergeItem => (
              <div className="history-item" key={mergeItem.id}>
                <strong>
                  {mergeItem.state} · {formatDate(mergeItem.startedAt)}
                </strong>
                <p>
                  {shortCommit(mergeItem.expectedTargetCommit)} →{' '}
                  {shortCommit(mergeItem.resultCommit)}
                </p>
                {mergeItem.error && <p>{mergeItem.error}</p>}
              </div>
            ))}
        </article>
      </div>
      {isMergeDialogOpen && run && (
        <MergeDialog
          task={currentTask}
          run={run}
          project={projects.find(projectItem => projectItem.id === currentTask.projectId)}
          onClose={() => setIsMergeDialogOpen(false)}
          onMerged={onChanged}
        />
      )}
    </>
  );
}

export function TaskDetail({
  taskId,
  projects,
  onChanged,
}: {
  taskId: string;
  projects: Project[];
  onChanged: () => void;
}) {
  const eventsRef = useRef<TaskEvent[]>([]);
  const cachedTerminalRef = useRef<DetailData | null>(null);
  const [activeTab, setActiveTab] = useState('plan');
  const [isPlanDirty, setIsPlanDirty] = useState(false);
  const [isRetryOpen, setIsRetryOpen] = useState(false);
  const [isWorkflowOpen, setIsWorkflowOpen] = useState(false);
  const [isModelOpen, setIsModelOpen] = useState(false);
  const [isHeaderPlanModalOpen, setIsHeaderPlanModalOpen] = useState(false);
  const notice = useNotice();
  const action = useAction(notice);

  useEffect(() => {
    cachedTerminalRef.current = null;
  }, [taskId]);

  const loadDetail = useCallback(
    async (signal: AbortSignal): Promise<DetailData> => {
      if (cachedTerminalRef.current) return cachedTerminalRef.current;
      const cursor = eventsRef.current.at(-1)?.sequence || 0;
      const [task, runs, plans, reviews, merges, newEvents] = await Promise.all([
        api<WorkTask>('/api/tasks/' + taskId, 'GET', undefined, { signal }),
        api<DetailData['runs']>('/api/tasks/' + taskId + '/runs', 'GET', undefined, { signal }),
        api<DetailData['plans']>('/api/tasks/' + taskId + '/plans', 'GET', undefined, { signal }),
        api<DetailData['reviews']>('/api/tasks/' + taskId + '/reviews', 'GET', undefined, {
          signal,
        }),
        api<DetailData['merges']>('/api/tasks/' + taskId + '/merges', 'GET', undefined, { signal }),
        api<TaskEvent[]>('/api/tasks/' + taskId + '/events?after=' + cursor, 'GET', undefined, {
          signal,
        }),
      ]);
      if (!signal.aborted) {
        eventsRef.current = [...eventsRef.current, ...newEvents];
      }
      const detailResult = {
        task,
        runs,
        plans,
        reviews,
        merges,
        events: eventsRef.current,
      };
      if (
        !signal.aborted &&
        (task.status === 'Merged' ||
          (task.status === 'Cancelled' && runs.every(runItem => Boolean(runItem.finishedAt))))
      ) {
        cachedTerminalRef.current = detailResult;
      }
      return detailResult;
    },
    [taskId]
  );

  const detailResource = useResource(taskId, loadDetail);
  const data = detailResource.data;
  const titleState = detailResource.error
    ? { label: '連線中斷', problem: true }
    : taskTitleState(data?.task.status);
  useActivityPageTitle(data?.task.title, titleState);

  const handleChanged = () => {
    cachedTerminalRef.current = null;
    detailResource.refresh();
    onChanged();
  };

  if (!data) {
    return (
      <>
        <a className="back-link" href="#tasks">
          ← 返回任務
        </a>
        {detailResource.error ? (
          <div className="error-banner">
            {detailResource.error}
            <button className="secondary" onClick={detailResource.refresh}>
              重試
            </button>
          </div>
        ) : (
          <Empty title="讀取任務…" />
        )}
      </>
    );
  }

  const currentTask = data.task;
  const latestImplementationRun = data.runs
    .filter(runItem => runItem.kind === 'Implementation')
    .at(-1);
  const recommendedTab = ['AwaitingReview', 'Approved', 'Rejected', 'Merging', 'Merged'].includes(
    currentTask.status
  )
    ? 'review'
    : ['Queued', 'Running', 'Failed', 'AwaitingStepApproval'].includes(currentTask.status)
      ? 'execution'
      : 'plan';
  const headerTone = ['Failed', 'PlanningFailed', 'Rejected'].includes(currentTask.status)
    ? 'danger'
    : ['AwaitingApproval', 'AwaitingStepApproval', 'AwaitingReview', 'Approved'].includes(
          currentTask.status
        )
      ? 'amber'
      : ['PlanningQueued', 'Planning', 'Queued', 'Running', 'Merging'].includes(currentTask.status)
        ? 'blue'
        : currentTask.status === 'Merged'
          ? 'success'
          : 'neutral';

  return (
    <>
      <a href="#tasks" className="back-link">
        ← 返回任務
      </a>
      {detailResource.error && <div className="error-banner">{detailResource.error}</div>}
      <div className={'detail-header-card header-tone-' + headerTone}>
        <div className="detail-heading">
          <div>
            <div className="detail-badge-row">
              <Badge status={currentTask.status} />
            </div>
            <div className="title-with-activity">
              <h1>{currentTask.title}</h1>
              <TitleActivity state={titleState} />
            </div>
            <p>
              {projects.find(projectItem => projectItem.id === currentTask.projectId)?.name} ·{' '}
              {formatDate(currentTask.createdAt)} · <span className="mono-id">{currentTask.id}</span>
            </p>
          </div>
          <div className="button-row">
            {currentTask.plan && (
              <button
                className="secondary"
                onClick={() => setIsHeaderPlanModalOpen(true)}
              >
                ⤢ 檢視完整計畫
              </button>
            )}
            {editablePlan.includes(currentTask.status) && (
              <button
                className="secondary"
                disabled={action.busy}
                onClick={() =>
                  void action.run(async () => {
                    await api('/api/tasks/' + currentTask.id + '/generate-plan', 'POST', {
                      expectedVersion: currentTask.version,
                    });
                    notice('規劃已排隊。');
                    handleChanged();
                  })
                }
              >
                {currentTask.status === 'AwaitingPlan' ? 'AI 產生計畫' : '重新規劃'}
              </button>
            )}
            {currentTask.status === 'AwaitingApproval' && currentTask.plan && (
              <button
                className="warning-button"
                disabled={isPlanDirty || action.busy}
                onClick={() =>
                  void action.run(async () => {
                    await api('/api/tasks/' + currentTask.id + '/approve-plan', 'POST', {
                      expectedVersion: currentTask.version,
                      planRevision: currentTask.plan!.revision,
                    });
                    notice('計畫已核准，實作已排隊。');
                    handleChanged();
                  })
                }
              >
                核准計畫並實作
              </button>
            )}
            {['AwaitingReview', 'Approved'].includes(currentTask.status) &&
              activeTab !== 'review' && (
                <button className="warning-button" onClick={() => setActiveTab('review')}>
                  {currentTask.status === 'Approved' ? '前往合併成果 →' : '前往審核成果 →'}
                </button>
              )}
            {['Running', 'Failed'].includes(currentTask.status) && activeTab !== 'execution' && (
              <button className="secondary" onClick={() => setActiveTab('execution')}>
                查看執行紀錄 →
              </button>
            )}
            {cancellable.includes(currentTask.status) && (
              <button
                className="danger-button"
                disabled={action.busy}
                onClick={() =>
                  void action.run(async () => {
                    await api('/api/tasks/' + currentTask.id + '/cancel', 'POST');
                    notice('任務已取消。');
                    handleChanged();
                  })
                }
              >
                取消任務
              </button>
            )}
            {currentTask.status === 'Cancelled' &&
              (data.runs.some(runItem => !runItem.finishedAt) ? (
                <button className="secondary" disabled>
                  等待舊執行停止…
                </button>
              ) : (
                <button
                  className="secondary"
                  disabled={action.busy}
                  onClick={() =>
                    void action.run(async () => {
                      await api('/api/tasks/' + currentTask.id + '/restart', 'POST', {
                        expectedVersion: currentTask.version,
                      });
                      notice('任務已重新開始。');
                      handleChanged();
                    })
                  }
                >
                  重新開始
                </button>
              ))}
          </div>
        </div>
        <div className="meta-strip">
          <Meta label="規劃 AI" value={selectionText(currentTask.ai?.planning)} />
          <Meta
            label="實作 AI"
            value={
              currentTask.workflow?.steps
                ? '依流程步驟設定'
                : selectionText(currentTask.ai?.implementation)
            }
          />
          <Meta label="任務版本" value={'v' + currentTask.version} />
        </div>
      </div>
      <TaskPermissions
        taskId={currentTask.id}
        runId={data.runs.at(-1)?.id}
        active={currentTask.status === 'Planning' || currentTask.status === 'Running'}
      />
      <TaskActivity data={data} error={detailResource.error} />
      {['Failed', 'Rejected', 'PlanningFailed'].includes(currentTask.status) && (
        <FailureRecovery
          error={
            data.runs.at(-1)?.error ||
            currentTask.revisionInstructions ||
            '執行未完成，請查看紀錄。'
          }
          onRecords={() => setActiveTab('execution')}
          onModel={() => {
            if (currentTask.status !== 'PlanningFailed' && currentTask.workflow?.steps) {
              setIsWorkflowOpen(true);
            } else {
              setIsModelOpen(true);
            }
          }}
          onRetry={
            currentTask.plan && currentTask.status !== 'PlanningFailed'
              ? () => setIsRetryOpen(!isRetryOpen)
              : undefined
          }
        />
      )}
      {isRetryOpen &&
        ['Failed', 'Rejected'].includes(currentTask.status) &&
        currentTask.plan && <><RetryPanel task={currentTask} onChanged={handleChanged} />{currentTask.status==='Failed'&&<WorkspaceRecovery task={currentTask} onChanged={handleChanged}/>}</>}
      <TaskContext task={currentTask} onChanged={handleChanged} />
      {isModelOpen && (
        <TaskModelSettings
          task={currentTask}
          onClose={() => setIsModelOpen(false)}
          onChanged={handleChanged}
        />
      )}
      {isWorkflowOpen && currentTask.workflow?.steps && (
        <WorkflowSettings
          task={currentTask}
          onClose={() => setIsWorkflowOpen(false)}
          onChanged={handleChanged}
        />
      )}
      <div className="tabs detail-tabs">
        {[
          ['plan', '01 · 計畫與需求', 'plan'],
          ['execution', '02 · 執行與證據', 'execution'],
          ['review', '03 · 成果審核', 'review'],
        ].map(([tabId, displayLabel, ariaKey]) => (
          <button
            key={tabId}
            aria-label={
              ariaKey === 'plan'
                ? '計畫與需求'
                : ariaKey === 'execution'
                  ? '執行與證據'
                  : '成果審核'
            }
            className={
              (activeTab === tabId ? 'active' : '') +
              (recommendedTab === tabId && activeTab !== tabId ? ' tab-attention' : '')
            }
            aria-pressed={activeTab === tabId}
            onClick={() => setActiveTab(tabId)}
          >
            {displayLabel}
            {recommendedTab === tabId && activeTab !== tabId && (
              <span className="tab-dot">● 待處理</span>
            )}
          </button>
        ))}
      </div>
      <div hidden={activeTab !== 'plan'}>
        <PlanPanel
          data={data}
          onChanged={handleChanged}
          onDirty={setIsPlanDirty}
          modalOpenExternal={isHeaderPlanModalOpen}
          onCloseExternalModal={() => setIsHeaderPlanModalOpen(false)}
        />
      </div>
      <div hidden={activeTab !== 'execution'}>
        <ExecutionPanel data={data} />
      </div>
      <div hidden={activeTab !== 'review'}>
        <ReviewPanel
          key={latestImplementationRun?.id || 'none'}
          data={data}
          run={latestImplementationRun}
          projects={projects}
          onChanged={handleChanged}
        />
      </div>
    </>
  );
}
