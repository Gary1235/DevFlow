import {useState} from 'react';
import {it,expect,vi} from 'vitest';
import {fireEvent,render,screen,within} from '@testing-library/react';
import {AiChoice,NoticeProvider} from '../src/components/ui';
import {AiConnections} from '../src/components/AiConnections';
import {selectionText} from '../src/domain';
import type {Provider,Selection} from '../src/types';

const provider:Provider={id:'codex',installed:true,executable:'codex',models:['a','b'],modelsVerified:false,modelDetails:[{id:'a',displayName:'Model A',reasoningEfforts:['low','high'],defaultReasoningEffort:'high',isDefault:true},{id:'b',displayName:'Model B',reasoningEfforts:['medium'],defaultReasoningEffort:'medium',isDefault:false}]};
function Choice({data=provider,initial={provider:'codex',model:'a'} as Selection}:{data?:Provider;initial?:Selection}){
  const [value,setValue]=useState<Selection|null>(initial);
  return <><AiChoice providers={[data]} label="規劃" value={value} onChange={setValue}/><output data-testid="selection">{JSON.stringify(value)}</output></>;
}
it('offers only the selected models levels and clears effort when switching models',()=>{
  render(<Choice/>);const effort=screen.getByLabelText('規劃推理程度');
  expect(within(effort).getAllByRole('option').map(o=>(o as HTMLOptionElement).value)).toEqual(['','low','high']);
  fireEvent.change(effort,{target:{value:'high'}});
  expect(JSON.parse(screen.getByTestId('selection').textContent!)).toMatchObject({model:'a',reasoningEffort:'high'});
  fireEvent.change(screen.getByLabelText('規劃模型'),{target:{value:'b'}});
  expect((screen.getByLabelText('規劃推理程度') as HTMLSelectElement).value).toBe('');
  expect(within(screen.getByLabelText('規劃推理程度')).getAllByRole('option').map(o=>(o as HTMLOptionElement).value)).toEqual(['','medium']);
  expect(JSON.parse(screen.getByTestId('selection').textContent!)).toMatchObject({model:'b',reasoningEffort:null});
});
it('preserves saved levels when metadata is missing and lets users return to CLI default',()=>{
  render(<Choice data={{...provider,modelDetails:[]}} initial={{provider:'codex',model:'a',reasoningEffort:'high'}}/>);
  expect(screen.getByRole('option',{name:/已保存：高/})).toBeTruthy();
  fireEvent.change(screen.getByLabelText('規劃推理程度'),{target:{value:''}});
  expect(JSON.parse(screen.getByTestId('selection').textContent!).reasoningEffort).toBeNull();
});
it('uses CLI defaults for providers without per-run reasoning controls',()=>{
  render(<AiChoice label="討論" providers={[{id:'gemini',installed:true,executable:'gemini',models:['pro'],modelsVerified:false}]} value={{provider:'gemini',model:'pro'}} onChange={()=>{}}/>);
  expect((screen.getByLabelText('討論推理程度') as HTMLSelectElement).disabled).toBe(true);
  expect(screen.getByText('推理設定由 Gemini CLI 管理。')).toBeTruthy();
  expect(selectionText({provider:'codex',model:'a',reasoningEffort:'high'})).toContain('推理 高');
});
it('sends the selected effort only when the user starts a model probe',async()=>{
  const requests:{path:string;body:any}[]=[];
  vi.stubGlobal('fetch',async(path:string,options:RequestInit)=>{const body=options.body?JSON.parse(String(options.body)):null;requests.push({path,body});return new Response(JSON.stringify(path.endsWith('diagnostics')?{loginState:'LoggedIn',executionState:'Unchecked',version:'1.0',message:'已登入'}:{loginState:'VerifiedByExecution',executionState:'Ready',message:'可執行'}));});
  render(<NoticeProvider><AiConnections providers={[provider]}/></NoticeProvider>);
  await screen.findByText('已登入',{selector:'div.provider-status'});
  expect(requests.some(r=>r.path.endsWith('probe'))).toBe(false);
  fireEvent.change(screen.getByLabelText('Codex CLI測試模型'),{target:{value:'a'}});
  fireEvent.change(screen.getByLabelText('Codex CLI測試推理程度'),{target:{value:'high'}});
  fireEvent.click(screen.getByRole('button',{name:'連線測試 · 使用額度'}));
  await screen.findByText(/可執行/,{selector:'div.provider-status'});
  expect(requests.find(r=>r.path.endsWith('probe'))?.body).toEqual({provider:'codex',model:'a',reasoningEffort:'high'});
});
