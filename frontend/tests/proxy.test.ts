// @vitest-environment node
import {it,expect} from 'vitest';
import {createServer as createHttpServer} from 'node:http';
import type {AddressInfo} from 'node:net';
import {createServer} from 'vite';
it('forwards CLI actions through the actual Vite proxy without accepting foreign origins',async()=>{
  const backend=createHttpServer((req,res)=>{res.setHeader('Content-Type','application/json');res.end(JSON.stringify({origin:req.headers.origin,host:req.headers.host,action:req.headers['x-devflow-action'],method:req.method}));});
  await new Promise<void>(resolve=>backend.listen(0,'127.0.0.1',resolve));
  const target=`http://127.0.0.1:${(backend.address() as AddressInfo).port}`,previous=process.env.DEVFLOW_API_URL;
  process.env.DEVFLOW_API_URL=target;
  const vite=await createServer();
  try{
    await vite.listen();
    const action=await fetch('http://127.0.0.1:5173/api/ai/codex/login',{method:'POST',headers:{Origin:'http://127.0.0.1:5173','X-DevFlow-Action':'1'}});
    expect(await action.json()).toEqual({origin:target,host:new URL(target).host,action:'1',method:'POST'});
    const foreign=await fetch('http://127.0.0.1:5173/api/ai/codex/login',{method:'POST',headers:{Origin:'https://foreign.test','X-DevFlow-Action':'1'}});
    expect((await foreign.json()).origin).toBe('https://foreign.test');
  }finally{await vite.close();backend.closeAllConnections();await new Promise<void>((resolve,reject)=>backend.close(error=>error?reject(error):resolve()));if(previous===undefined)delete process.env.DEVFLOW_API_URL;else process.env.DEVFLOW_API_URL=previous;}
},15000);
