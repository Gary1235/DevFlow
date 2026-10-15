import {defineConfig,loadEnv} from 'vite';
import react from '@vitejs/plugin-react';
import {proxyOrigin} from './src/devProxy.ts';
export default defineConfig(({mode})=>{
  const env=loadEnv(mode,process.cwd(),'DEVFLOW_');
  const target=env.DEVFLOW_API_URL||'http://127.0.0.1:5180';
  return {plugins:[react()],server:{host:'127.0.0.1',port:5173,strictPort:true,
    proxy:{'/api':{target,changeOrigin:true,configure(proxy){proxy.on('proxyReq',(out,request)=>{
      const origin=proxyOrigin(request.headers.origin,request.headers.host,target);
      if(origin)out.setHeader('Origin',origin);
    });}}}},preview:{host:'127.0.0.1',port:4173,strictPort:true},
    test:{environment:'jsdom',setupFiles:['./tests/setup.ts'],include:['tests/**/*.test.ts','tests/**/*.test.tsx']}};
});
