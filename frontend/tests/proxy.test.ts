// @vitest-environment node
import {it,expect} from 'vitest';
import {createServer as createHttpServer,request} from 'node:http';
import type {AddressInfo} from 'node:net';
import {createServer} from 'vite';
it('forwards CLI actions through the actual Vite proxy without accepting foreign origins',async()=>{
  const backend=createHttpServer((req,res)=>{res.setHeader('Content-Type','application/json');res.end(JSON.stringify({origin:req.headers.origin,host:req.headers.host,action:req.headers['x-devflow-action'],method:req.method}));});
  await new Promise<void>(resolve=>backend.listen(0,'127.0.0.1',resolve));
  const target=`http://127.0.0.1:${(backend.address() as AddressInfo).port}`,previous=process.env.DEVFLOW_API_URL;
  process.env.DEVFLOW_API_URL=target;
  // A test server must not replace the dependency cache of a running dev server.
  const vite=await createServer({cacheDir:'node_modules/.vite-proxy-test',server:{port:0,strictPort:false}});
  try{
    await vite.listen();
    const port=(vite.httpServer!.address() as AddressInfo).port;
    // Use an ephemeral socket while preserving the production Host/Origin pair.
    // This allows tests to run alongside the user's Vite server on 5173.
    const post=(origin:string)=>new Promise<Record<string,string>>((resolve,reject)=>{
      const req=request({hostname:'127.0.0.1',port,path:'/api/ai/codex/login',method:'POST',headers:{Host:'127.0.0.1:5173',Origin:origin,'X-DevFlow-Action':'1'}},res=>{let body='';res.on('data',chunk=>body+=chunk);res.on('end',()=>{try{resolve(JSON.parse(body));}catch(error){reject(error);}});});
      req.on('error',reject);req.end();
    });
    expect(await post('http://127.0.0.1:5173')).toEqual({origin:target,host:new URL(target).host,action:'1',method:'POST'});
    expect((await post('https://foreign.test')).origin).toBe('https://foreign.test');
  }finally{await vite.close();backend.closeAllConnections();await new Promise<void>((resolve,reject)=>backend.close(error=>error?reject(error):resolve()));if(previous===undefined)delete process.env.DEVFLOW_API_URL;else process.env.DEVFLOW_API_URL=previous;}
},15000);
