import {useState} from 'react';
import {it,expect} from 'vitest';
import {render,screen,fireEvent,within} from '@testing-library/react';
import {AiChoice,NoticeProvider} from '../src/components/ui';
import {AiConnections} from '../src/components/AiConnections';
import type {Provider,Selection} from '../src/types';

const provider:Provider={id:'antigravity',installed:false,executable:null,models:[],modelsVerified:false};
const catalog:Provider={...provider,installed:true,models:['claude-opus-5-5-low','claude-opus-5-5-medium','claude-opus-5-5-high','gpt-oss-120b-medium'],modelDetails:[['claude-opus-5-5-low','Claude Opus 5.5 (Low)'],['claude-opus-5-5-medium','Claude Opus 5.5 (Medium)'],['claude-opus-5-5-high','Claude Opus 5.5 (High)'],['gpt-oss-120b-medium','GPT-OSS 120B (Medium)']].map(([id,displayName])=>({id,displayName,reasoningEfforts:[],defaultReasoningEffort:null,isDefault:false}))};
function Choice({data=catalog,initial={provider:'antigravity',model:null} as Selection}:{data?:Provider;initial?:Selection}){const [value,setValue]=useState<Selection|null>(initial);return <><AiChoice providers={[data]} label="前端" value={value} onChange={setValue}/><output data-testid="value">{JSON.stringify(value)}</output></>;}
it('separates real CLI variants into one model and available effort options',()=>{
  render(<Choice/>);
  const initialEffort=screen.getByLabelText('前端推理程度') as HTMLSelectElement;
  expect(initialEffort.disabled).toBe(true);
  expect(within(initialEffort).getAllByRole('option').map(o=>o.textContent)).toEqual(['CLI 預設']);
  const model=screen.getByLabelText('前端模型');
  expect(within(model).getAllByRole('option').map(o=>o.textContent)).toEqual(['CLI 預設','Claude Opus 5.5','GPT-OSS 120B','自訂模型 ID…']);
  fireEvent.change(model,{target:{value:'__agy_family:claude-opus-5-5'}});
  expect((screen.getByLabelText('前端推理程度') as HTMLSelectElement).value).toBe('medium');
  fireEvent.change(screen.getByLabelText('前端推理程度'),{target:{value:'high'}});
  expect(JSON.parse(screen.getByTestId('value').textContent!)).toMatchObject({provider:'antigravity',model:'claude-opus-5-5-high',reasoningEffort:null});
  fireEvent.change(model,{target:{value:'__agy_family:gpt-oss-120b'}});
  expect(within(screen.getByLabelText('前端推理程度')).getAllByRole('option').map(o=>(o as HTMLOptionElement).value)).toEqual(['medium']);
  fireEvent.change(model,{target:{value:''}});
  expect((screen.getByLabelText('前端推理程度') as HTMLSelectElement).value).toBe('');
  expect(JSON.parse(screen.getByTestId('value').textContent!)).toMatchObject({model:null,reasoningEffort:null});
});
it('hides effort controls when variants cannot be identified or the model ID is custom',()=>{
  render(<Choice data={{...provider,models:['model-high']}} initial={{provider:'antigravity',model:'model-high'}}/>);
  expect((screen.getByLabelText('前端模型') as HTMLSelectElement).value).toBe('model-high');
  expect(screen.queryByLabelText('前端推理程度')).toBeNull();
  fireEvent.change(screen.getByLabelText('前端模型'),{target:{value:'__custom'}});
  fireEvent.change(screen.getByLabelText('前端自訂模型'),{target:{value:'custom-model-high'}});
  expect(screen.queryByLabelText('前端推理程度')).toBeNull();
  expect(JSON.parse(screen.getByTestId('value').textContent!).model).toBe('custom-model-high');
});
it('preserves a saved variant and makes removal of an old extra override explicit',()=>{
  render(<Choice initial={{provider:'antigravity',model:'claude-opus-5-5-low',reasoningEffort:'high'}}/>);
  expect((screen.getByLabelText('前端推理程度') as HTMLSelectElement).value).toBe('low');
  expect(JSON.parse(screen.getByTestId('value').textContent!).reasoningEffort).toBe('high');
  fireEvent.click(screen.getByRole('button',{name:'改用模型內建深度'}));
  expect(JSON.parse(screen.getByTestId('value').textContent!)).toMatchObject({model:'claude-opus-5-5-low',reasoningEffort:null});
});
it('explains the missing agy executable and links to official installation',()=>{
  render(<NoticeProvider><AiConnections providers={[provider]}/></NoticeProvider>);
  expect(screen.getByText(/找不到 agy/)).toBeTruthy();
  expect(screen.getByRole('link',{name:'Antigravity CLI 官方安裝說明'}).getAttribute('href')).toContain('antigravity.google');
  expect((screen.getByRole('button',{name:'連線測試 · 使用額度'}) as HTMLButtonElement).disabled).toBe(true);
  expect(screen.queryByText(/npm/)).toBeNull();
});
it('keeps a saved Gemini selection visible until explicitly changed',()=>{
  render(<AiChoice providers={[provider]} label="規劃" value={{provider:'gemini',model:'pro'}} onChange={()=>{}}/>);
  expect((screen.getByLabelText('規劃 AI') as HTMLSelectElement).value).toBe('gemini');
  expect(screen.getByRole('option',{name:/Gemini CLI（舊任務）/})).toBeTruthy();
});
