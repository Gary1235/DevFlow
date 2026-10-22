import {createContext,useContext,useEffect,useId,useRef,useState,type ReactNode} from 'react';
import {tone,statusNames,providerNames,effortNames,selectableModels} from '../domain';
import type {Provider,Selection,ProviderId} from '../types';
import {antigravityFamilies} from '../antigravityModels';
const NoticeContext=createContext<(message:string,error?:boolean)=>void>(()=>{});
export function NoticeProvider({children}:{children:ReactNode}){
  const [items,setItems]=useState<{id:number;message:string;error:boolean}[]>([]),next=useRef(0),timers=useRef<ReturnType<typeof setTimeout>[]>([]);
  useEffect(()=>()=>timers.current.forEach(clearTimeout),[]);
  function notice(message:string,error=false){const id=++next.current;setItems(v=>[...v,{id,message,error}]);if(!error)timers.current.push(setTimeout(()=>setItems(v=>v.filter(n=>n.id!==id)),6000));}
  return <NoticeContext.Provider value={notice}>{children}<div className="toast-stack" role="status" aria-live="polite">{items.map(n=><div key={n.id} className={'toast'+(n.error?' error':'')}><span>{n.message}</span><button aria-label="關閉通知" onClick={()=>setItems(v=>v.filter(i=>i.id!==n.id))}>✕</button></div>)}</div></NoticeContext.Provider>;
}
export const useNotice=()=>useContext(NoticeContext);
export function Badge({status,label}:{status:string;label?:string}){return <span className={'chip '+tone(status)}>{label||statusNames[status]||status}</span>;}
export function Meta({label,value}:{label:string;value?:string|null}){return <div className="meta-item"><span>{label}</span><strong>{value||'—'}</strong></div>;}
export function Empty({title,description,children}:{title:string;description?:string;children?:ReactNode}){return <div className="empty"><div className="empty-icon">◈</div><strong>{title}</strong><p>{description}</p>{children}</div>;}
export function Heading({eyebrow,title,description,children}:{eyebrow:string;title:string;description:string;children?:ReactNode}){return <div className="page-heading"><div><p className="eyebrow">{eyebrow}</p><h1>{title}</h1><p>{description}</p></div>{children}</div>;}
export function Modal({title,eyebrow,onClose,children,wide=false}:{title:string;eyebrow?:string;onClose:()=>void;children:ReactNode;wide?:boolean}){
  const ref=useRef<HTMLDialogElement>(null),id=useId();
  useEffect(()=>{const dialog=ref.current!;dialog.showModal();return()=>{if(dialog.open)dialog.close();};},[]);
  return <dialog ref={ref} className={wide?'wide-dialog':''} aria-labelledby={id} onCancel={e=>{e.preventDefault();onClose();}}><div className="dialog-heading"><div><p className="eyebrow">{eyebrow}</p><h2 id={id}>{title}</h2></div><button className="icon-button" aria-label="關閉視窗" onClick={onClose}>✕</button></div>{children}</dialog>;
}
export function AiChoice({providers,value,onChange,inherit=false,label}:{providers:Provider[];value:Selection|null;onChange:(s:Selection|null)=>void;inherit?:boolean;label:string}){
  const selectedProvider=providers.find(p=>p.id===value?.provider);
  return <fieldset><legend>{label}</legend><label>AI CLI<select aria-label={label+' AI'} value={value?.provider||''} onChange={e=>onChange(e.target.value?{provider:e.target.value as ProviderId,model:null,reasoningEffort:null}:null)}>{inherit&&<option value="">沿用專案預設</option>}{value&&!selectedProvider&&<option value={value.provider}>{providerNames[value.provider]}（已保存）</option>}{providers.map(p=><option key={p.id} value={p.id}>{providerNames[p.id]}{p.installed?'':'（未安裝）'}</option>)}</select></label><ModelChoice key={value?.provider||'inherit'} label={label} provider={selectedProvider} value={value} onChange={s=>onChange(s)} disabled={!value}/></fieldset>;
}
export function ModelChoice({provider,value,onChange,label,disabled=false,modelLabel='模型'}:{provider?:Provider;value:Selection|null;onChange:(s:Selection)=>void;label:string;disabled?:boolean;modelLabel?:string}){
  const models=selectableModels(provider),model=value?.model||'',[customSelected,setCustomSelected]=useState(false);
  const custom=customSelected||!!model&&!models.includes(model);
  const families=provider?.id==='antigravity'?antigravityFamilies(provider):[];
  const family=families.find(f=>f.variants.some(v=>v.id===model)),variant=family?.variants.find(v=>v.id===model);
  function select(id:string){if(value)onChange({...value,model:id||null,reasoningEffort:null});}
  return <><label>{modelLabel}<select aria-label={label+'模型'} disabled={disabled} value={custom?'__custom':family?.key||model} onChange={e=>{const key=e.target.value;setCustomSelected(key==='__custom');if(key==='__custom')return;const group=families.find(f=>f.key===key);select(group?(group.variants.find(v=>v.effort==='medium')||group.variants[0]).id:key);}}><option value="">CLI 預設</option>{provider?.id==='antigravity'?families.map(f=><option key={f.key} value={f.key}>{f.name}</option>):models.map(id=><option key={id} value={id}>{provider?.modelDetails?.find(d=>d.id===id)?.displayName||id}</option>)}<option value="__custom">自訂模型 ID…</option></select></label>
    {custom&&<label>自訂模型 ID<input aria-label={label+'自訂模型'} maxLength={128} value={model} disabled={disabled} onChange={e=>select(e.target.value)}/></label>}
    {provider?.id==='antigravity'?<>{!custom&&(!model||variant?.effort)&&<label>推理程度<select aria-label={label+'推理程度'} disabled={disabled||!variant?.effort} value={variant?.effort||''} onChange={e=>{const next=family?.variants.find(v=>v.effort===e.target.value);if(next)select(next.id);}}>{!variant?.effort&&<option value="">CLI 預設</option>}{family?.variants.map(v=><option key={v.id} value={v.effort!}>{effortNames[v.effort!]||v.effort}（{v.effort}）</option>)}</select></label>}
      <p className="footnote">{!custom&&variant?.effort?'僅顯示此模型實際可用的推理程度。':!model&&!custom?'選擇模型後即可設定推理程度；未指定時沿用 CLI 預設。':'推理深度沿用 CLI 預設或已包含於完整模型 ID。'}</p>
      {value?.reasoningEffort&&<p className="footnote">此設定已保存額外推理程度：{effortNames[value.reasoningEffort]||value.reasoningEffort}。<button type="button" className="text-link" disabled={disabled} onClick={()=>select(model)}>改用模型內建深度</button></p>}</>:<ReasoningChoice label={label+'推理程度'} provider={provider} model={value?.model||null} value={value?.reasoningEffort||null} disabled={disabled} onChange={effort=>value&&onChange({...value,reasoningEffort:effort})}/>}</>;
}
export function ReasoningChoice({provider,model,value,onChange,label,disabled=false}:{provider?:Provider;model:string|null;value:string|null;onChange:(effort:string|null)=>void;label:string;disabled?:boolean}){
  if(provider?.id==='antigravity')return null;
  const detail=model?provider?.modelDetails?.find(m=>m.id===model):undefined;
  const manual=provider?.id==='codex'&&!!model&&model===provider.configuredDefaultModel&&!detail;
  const codexEfforts=['low','medium','high','xhigh','ultra'];
  const reported=detail?.reasoningEfforts||[];
  const efforts=manual?codexEfforts:provider?.id==='codex'?reported.filter(e=>codexEfforts.includes(e)):reported,unverified=!!value&&!efforts.includes(value);
  return <><label>推理程度<select aria-label={label} value={value||''} disabled={disabled||(!efforts.length&&!value)} onChange={e=>onChange(e.target.value||null)}><option value="">CLI 預設</option>{efforts.map(e=><option key={e} value={e}>{effortNames[e]||e}（{e}）</option>)}{unverified&&<option value={value!}>已保存：{effortNames[value!]||value}（支援狀態待確認）</option>}</select></label>{manual?<p className="footnote">手動指定，支援狀態待 CLI 驗證；目前模型目錄未提供此預設模型的推理選項。</p>:provider?.id==='gemini'?<p className="footnote">推理設定由 Gemini CLI 管理。</p>:provider&&detail&&efforts.length>0?<p className="footnote">較高程度通常需要較長時間與較多用量。{provider.id==='claude'&&'實際等級可能由 CLI 依模型或組織限制調整。'}</p>:provider&&<p className="footnote">{model?'尚未取得此模型的推理選項；可沿用 CLI 預設。':'先選擇模型即可設定推理程度；未指定時沿用 CLI 設定。'}</p>}</>;
}

function renderInlineMarkdown(textValue: string): ReactNode[] {
  const inlineParts = textValue.split(/(\*\*[^*]+\*\*|`[^`\n]+`)/g);
  return inlineParts.map((tokenText, tokenIndex) => {
    if (tokenText.startsWith('**') && tokenText.endsWith('**') && tokenText.length > 4) {
      return (
        <strong className="md-bold" key={tokenIndex}>
          {tokenText.slice(2, -2)}
        </strong>
      );
    }
    if (tokenText.startsWith('`') && tokenText.endsWith('`') && tokenText.length > 2) {
      return (
        <code className="md-inline-code" key={tokenIndex}>
          {tokenText.slice(1, -1)}
        </code>
      );
    }
    return tokenText;
  });
}

function renderMarkdownBlocks(
  markdownSection: string,
  sectionKeyPrefix: string,
  baseLineOffset = 0,
  baseCharOffset = 0
): ReactNode[] {
  const normalizedSection = markdownSection.replace(/\r\n/g, '\n');
  const lines = normalizedSection.split('\n');
  const renderedBlocks: ReactNode[] = [];
  let listItemsBuffer: {
    depth: number;
    marker: string;
    content: string;
    lineIndex: number;
    charOffset: number;
  }[] = [];
  let runningCharInSection = 0;

  function flushListBuffer(flushKey: string) {
    if (!listItemsBuffer.length) return;
    const currentItems = listItemsBuffer;
    listItemsBuffer = [];
    const firstItem = currentItems[0];
    renderedBlocks.push(
      <ul
        className="md-list"
        key={flushKey}
        data-source-line={firstItem.lineIndex}
        data-char-offset={firstItem.charOffset}
      >
        {currentItems.map(listItem => (
          <li
            key={listItem.lineIndex}
            className={'md-list-item depth-' + Math.min(listItem.depth, 3)}
            data-marker={listItem.marker}
            data-source-line={listItem.lineIndex}
            data-char-offset={listItem.charOffset}
          >
            {renderInlineMarkdown(listItem.content)}
          </li>
        ))}
      </ul>
    );
  }

  lines.forEach((rawLine, relativeLineIndex) => {
    const globalLineIndex = baseLineOffset + relativeLineIndex;
    const globalCharOffset = baseCharOffset + runningCharInSection;
    runningCharInSection += rawLine.length + 1;

    const trimmedLine = rawLine.trim();
    if (!trimmedLine) {
      flushListBuffer(`${sectionKeyPrefix}-list-${globalLineIndex}`);
      return;
    }

    if (/^(-{3,}|\*{3,}|_{3,})$/.test(trimmedLine)) {
      flushListBuffer(`${sectionKeyPrefix}-list-${globalLineIndex}`);
      renderedBlocks.push(
        <hr
          className="md-divider"
          key={`${sectionKeyPrefix}-hr-${globalLineIndex}`}
          data-source-line={globalLineIndex}
          data-char-offset={globalCharOffset}
        />
      );
      return;
    }

    const headingMatch = /^(#{1,4})\s+(.+)$/.exec(trimmedLine);
    if (headingMatch) {
      flushListBuffer(`${sectionKeyPrefix}-list-${globalLineIndex}`);
      const headingLevel = headingMatch[1].length;
      const headingContent = renderInlineMarkdown(headingMatch[2]);
      if (headingLevel === 1) {
        renderedBlocks.push(
          <h3
            className="md-heading md-h1"
            key={`${sectionKeyPrefix}-h-${globalLineIndex}`}
            data-source-line={globalLineIndex}
            data-char-offset={globalCharOffset}
          >
            {headingContent}
          </h3>
        );
      } else if (headingLevel === 2) {
        renderedBlocks.push(
          <h4
            className="md-heading md-h2"
            key={`${sectionKeyPrefix}-h-${globalLineIndex}`}
            data-source-line={globalLineIndex}
            data-char-offset={globalCharOffset}
          >
            {headingContent}
          </h4>
        );
      } else {
        renderedBlocks.push(
          <h5
            className="md-heading md-h3"
            key={`${sectionKeyPrefix}-h-${globalLineIndex}`}
            data-source-line={globalLineIndex}
            data-char-offset={globalCharOffset}
          >
            {headingContent}
          </h5>
        );
      }
      return;
    }

    const listMatch = /^(\s*)([-*+]|\d+\.)\s+(.+)$/.exec(rawLine);
    if (listMatch) {
      const leadingSpaces = listMatch[1].replace(/\t/g, '  ').length;
      const indentDepth = Math.floor(leadingSpaces / 2);
      listItemsBuffer.push({
        depth: indentDepth,
        marker: listMatch[2],
        content: listMatch[3],
        lineIndex: globalLineIndex,
        charOffset: globalCharOffset,
      });
      return;
    }

    if (trimmedLine.startsWith('>')) {
      flushListBuffer(`${sectionKeyPrefix}-list-${globalLineIndex}`);
      renderedBlocks.push(
        <blockquote
          className="md-quote"
          key={`${sectionKeyPrefix}-q-${globalLineIndex}`}
          data-source-line={globalLineIndex}
          data-char-offset={globalCharOffset}
        >
          {renderInlineMarkdown(trimmedLine.replace(/^>\s*/, ''))}
        </blockquote>
      );
      return;
    }

    flushListBuffer(`${sectionKeyPrefix}-list-${globalLineIndex}`);
    renderedBlocks.push(
      <p
        className="md-paragraph"
        key={`${sectionKeyPrefix}-p-${globalLineIndex}`}
        data-source-line={globalLineIndex}
        data-char-offset={globalCharOffset}
      >
        {renderInlineMarkdown(rawLine)}
      </p>
    );
  });

  flushListBuffer(`${sectionKeyPrefix}-list-end`);
  return renderedBlocks;
}

export function ProseContent({ content }: { content: string }) {
  const [copiedBlockIndex, setCopiedBlockIndex] = useState<number | null>(null);
  const hasStructuredSyntax =
    Boolean(content) &&
    (content.includes('\n') ||
      content.includes('```') ||
      content.includes('**') ||
      content.includes('`') ||
      /(^|\n)\s*(#{1,4}\s|[-*+]\s|\d+\.\s|---)/.test(content));

  if (!content || !hasStructuredSyntax) {
    return (
      <div className="prose" data-source-line={0} data-char-offset={0}>
        {content}
      </div>
    );
  }

  const normalizedContent = content.replace(/\r\n/g, '\n');
  const parts = normalizedContent.split(/(```[\s\S]*?```)/g);
  let cumulativeLineOffset = 0;
  let cumulativeCharOffset = 0;

  return (
    <div className="prose prose-structured plan-formatted-doc">
      {parts.map((segment, segmentIndex) => {
        const segmentStartLine = cumulativeLineOffset;
        const segmentStartChar = cumulativeCharOffset;
        const segmentLineCount = segment.split('\n').length - 1;
        cumulativeLineOffset += segmentLineCount;
        cumulativeCharOffset += segment.length;

        if (segment.startsWith('```') && segment.endsWith('```') && segment.length >= 6) {
          const innerText = segment.slice(3, -3);
          const firstNewlineIndex = innerText.indexOf('\n');
          const languageLabel =
            firstNewlineIndex >= 0 ? innerText.slice(0, firstNewlineIndex).trim() : '';
          const codeText =
            firstNewlineIndex >= 0 ? innerText.slice(firstNewlineIndex + 1) : innerText;
          return (
            <div
              className="prose-code-block"
              key={segmentIndex}
              data-source-line={segmentStartLine}
              data-char-offset={segmentStartChar}
            >
              <div className="prose-code-header">
                <span>{languageLabel || 'CODE'}</span>
                <button
                  type="button"
                  className="prose-copy-button"
                  onClick={() => {
                    void navigator.clipboard?.writeText(codeText).then(() => {
                      setCopiedBlockIndex(segmentIndex);
                      setTimeout(
                        () =>
                          setCopiedBlockIndex(currentIndex =>
                            currentIndex === segmentIndex ? null : currentIndex
                          ),
                        2000
                      );
                    });
                  }}
                >
                  {copiedBlockIndex === segmentIndex ? '✓ 已複製' : '複製'}
                </button>
              </div>
              <pre>
                <code>{codeText}</code>
              </pre>
            </div>
          );
        }
        return segment ? (
          <div className="md-section" key={segmentIndex}>
            {renderMarkdownBlocks(
              segment,
              `seg-${segmentIndex}`,
              segmentStartLine,
              segmentStartChar
            )}
          </div>
        ) : null;
      })}
    </div>
  );
}
