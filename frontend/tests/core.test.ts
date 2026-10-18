import {describe,it,expect,vi} from 'vitest';
import {ApiError,createClient,LatestRequest} from '../src/api';
import {appendPage,artifactUrl,validationState,selectableModels} from '../src/domain';
import type {Provider} from '../src/types';
import {proxyOrigin} from '../src/devProxy';
describe('API transport',()=>{
  it('sends one explicit mutation with local-action header and retains conflicts',async()=>{
    const fetcher=vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify({title:'Task changed'}),{status:409}));
    await expect(createClient(fetcher)('/api/tasks/id/plan','POST',{expectedVersion:4})).rejects.toMatchObject({status:409,message:'Task changed'} satisfies Partial<ApiError>);
    expect(fetcher).toHaveBeenCalledTimes(1);expect(fetcher.mock.calls[0][1]).toMatchObject({method:'POST',credentials:'same-origin',headers:{'X-DevFlow-Action':'1'},body:'{"expectedVersion":4}'});
  });
  it('never retries a failed mutation',async()=>{const fetcher=vi.fn<typeof fetch>().mockRejectedValue(new Error('network'));await expect(createClient(fetcher)('/api/tasks','POST',{})).rejects.toThrow('無法連線');expect(fetcher).toHaveBeenCalledTimes(1);});
  it('discards a late result even if the transport ignores cancellation',async()=>{const request=new LatestRequest();let finish!:(s:string)=>void;const first=request.run(()=>new Promise<string>(r=>finish=r));expect(await request.run(async()=> 'new')).toBe('new');finish('old');expect(await first).toBeUndefined();});
});
describe('evidence and pagination',()=>{
  it('sorts model versions newest first, preserves same-version catalog order, and keeps custom IDs last',()=>{
    const provider:Provider={id:'codex',installed:true,executable:'codex',modelsVerified:false,configuredDefaultModel:'gpt-6.1-sol',models:['custom-z','gpt-6-astra','gpt-6-sol','gpt-5.6-sol','gpt-6.10-sol','gpt-6-luna','gpt-5.6-terra','gpt-5.6-luna','gpt-6.1-sol']};
    const original=[...provider.models];
    expect(selectableModels(provider)).toEqual(['gpt-6.10-sol','gpt-6.1-sol','gpt-6-astra','gpt-6-sol','gpt-6-luna','gpt-5.6-sol','gpt-5.6-terra','gpt-5.6-luna','custom-z']);
    expect(provider.models).toEqual(original);
  });
  it('sorts Gemini and Claude version IDs numerically without rearranging model metadata',()=>{
    const base={installed:true,executable:null,modelsVerified:false};
    expect(selectableModels({...base,id:'gemini',models:['pro','gemini-2.5-pro','gemini-3.1-pro','gemini-3-pro']})).toEqual(['gemini-3.1-pro','gemini-3-pro','gemini-2.5-pro','pro']);
    expect(selectableModels({...base,id:'claude',models:['sonnet','claude-opus-4-6','claude-sonnet-4-7','claude-opus-4-7']})).toEqual(['claude-sonnet-4-7','claude-opus-4-7','claude-opus-4-6','sonnet']);
  });
  it('requires actual failing tests in Red',()=>{expect(validationState({phase:'Red',evidencePath:'evidence.json',exitCode:1,total:0,failed:0}).tone).toBe('danger');expect(validationState({phase:'Red',evidencePath:'evidence.json',exitCode:1,total:3,failed:1}).tone).toBe('success');expect(validationState({phase:'Green',evidencePath:'evidence.json',exitCode:0,total:0,failed:0}).tone).toBe('danger');});
  it('uses server UTF-16 cursor and safely encodes artifact paths',()=>{expect(appendPage({path:'a',text:'🙂',nextOffset:2,hasMore:true},{path:'a',text:'done',offset:2,nextOffset:6,hasMore:false}).text).toBe('🙂done');expect(artifactUrl('id','Green/驗證 #1.log',2)).toContain('/Green/%E9%A9%97%E8%AD%89%20%231.log?offset=2');});
  it('rejects mismatched and non-advancing artifact pages',()=>{const current={path:'a',text:'x',nextOffset:1,hasMore:true};expect(()=>appendPage(current,{path:'a',text:'x',offset:0,nextOffset:1,hasMore:true})).toThrow();expect(()=>appendPage(current,{path:'a',text:'',offset:1,nextOffset:1,hasMore:true})).toThrow();});
});
describe('local development proxy',()=>{
  it('rewrites only the configured local Vite origin to local backend origin',()=>{expect(proxyOrigin('http://127.0.0.1:5173','127.0.0.1:5173','http://127.0.0.1:5180')).toBe('http://127.0.0.1:5180');expect(proxyOrigin('http://localhost:5173','localhost:5173','http://localhost:5180')).toBe('http://localhost:5180');});
  it('does not legitimize foreign origins or forged hosts',()=>{expect(proxyOrigin('https://evil.test','127.0.0.1:5173','http://localhost:5180')).toBeUndefined();expect(proxyOrigin('http://evil.test','evil.test','http://localhost:5180')).toBeUndefined();expect(proxyOrigin(undefined,'localhost:5173','http://localhost:5180')).toBeUndefined();});
  it('rejects a remote backend for login actions',()=>{expect(()=>proxyOrigin('http://localhost:5173','localhost:5173','https://remote.test')).toThrow();});
});

