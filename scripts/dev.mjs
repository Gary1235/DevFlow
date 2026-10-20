import {spawn} from 'node:child_process';
import {createWriteStream,existsSync} from 'node:fs';
import {mkdir} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

export const repository=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
export function devPaths(root,env={}){
  const database=path.resolve(root,env.DevFlow__DatabasePath||'backend/src/DevFlow.Api/.data/devflow.db');
  const data=path.dirname(database);
  return {database,artifacts:path.resolve(root,env.DevFlow__ArtifactsRoot||path.join(data,'artifacts')),
    worktrees:path.resolve(root,env.DevFlow__WorktreeRoot||(env.DevFlow__DatabasePath?path.join(data,'worktrees'):path.join(root,'backend/src/DevFlow.Worktrees'))),
    cliControl:path.resolve(root,env.DevFlow__CliControlRoot||path.join(data,'cli-control'))};
}
export function sameDatabase(actual,expected){
  if(typeof actual!=='string')return false;
  return process.platform==='win32'?path.resolve(actual).toLowerCase()===path.resolve(expected).toLowerCase():path.resolve(actual)===path.resolve(expected);
}
export async function readStatus(origin){
  const response=await fetch(origin+'/api/worker/status',{signal:AbortSignal.timeout(2000)});
  if(!response.ok)throw new Error(`狀態接口回傳 ${response.status}`);
  const status=await response.json();
  if(typeof status.online!=='boolean'||typeof status.databasePath!=='string')throw new Error('不是可辨識的 DevFlow 狀態接口');
  return status;
}

async function main(){
  const flags=new Set(process.argv.slice(2));
  for(const flag of flags)if(!['--backend-only','--no-build'].includes(flag))throw new Error('不支援的參數：'+flag);
  const paths=devPaths(repository,process.env);
  const origin=process.env.DevFlow__ApiUrl||'http://127.0.0.1:5180';
  const parsed=new URL(origin);
  if(parsed.protocol!=='http:'||!['127.0.0.1','localhost','[::1]'].includes(parsed.hostname))throw new Error('開發啟動器僅支援本機 HTTP API。');
  const web='http://127.0.0.1:5173';
  const vite=path.join(repository,'frontend/node_modules/vite/bin/vite.js');
  if(!flags.has('--backend-only')&&!existsSync(vite))throw new Error('請先到 frontend 執行 npm ci，再回專案根目錄執行 npm run dev。');
  const env={...process.env,DevFlow__DatabasePath:paths.database,DevFlow__ArtifactsRoot:paths.artifacts,
    DevFlow__WorktreeRoot:paths.worktrees,DevFlow__CliControlRoot:paths.cliControl,DevFlow__ApiUrl:origin,
    DEVFLOW_API_URL:origin,DevFlow__LauncherManaged:'true',ASPNETCORE_ENVIRONMENT:'Development',DOTNET_ENVIRONMENT:'Development',
    Logging__LogLevel__Microsoft:'Warning'};
  delete env.DevFlow__DesktopManaged;delete env.DevFlow__DesktopToken;
  const children=[];
  const logRoot=path.join(path.dirname(paths.database),'dev-logs');
  await mkdir(logRoot,{recursive:true});
  let stopping=false;
  async function stop(code=0){
    if(stopping)return;stopping=true;
    process.stdin.pause();
    for(const name of ['Worker','Web','Api']){
      const entry=children.find(c=>c.name===name);
      if(!entry||!entry.child.pid||entry.child.exitCode!==null||entry.child.signalCode!==null)continue;
      const exited=new Promise(resolve=>entry.child.once('exit',resolve));
      if(name==='Web')entry.child.kill();
      else if(entry.child.stdin.writable)entry.child.stdin.end('shutdown\n');
      const timer=setTimeout(()=>{if(entry.child.exitCode===null)entry.child.kill();},15000);
      await exited;clearTimeout(timer);
    }
    process.exitCode=code;
  }
  function launch(name,executable,args,cwd){
    const log=createWriteStream(path.join(logRoot,`${name.toLowerCase()}-${process.pid}.log`),{flags:'a'});
    const child=spawn(executable,args,{cwd,env,windowsHide:true,stdio:['pipe','pipe','pipe']});
    child.stdin.on('error',()=>{});
    child.stdout.pipe(log,{end:false});child.stderr.pipe(log,{end:false});
    children.push({name,child});
    child.on('error',error=>{console.error(`${name} 啟動失敗：${error.message}`);log.end();void stop(1);});
    child.on('exit',code=>{log.end();if(!stopping){console.error(`${name} 已結束（${code}），請查看 ${logRoot}`);void stop(1);}});
    return child;
  }
  const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
  async function waitFor(check,label){
    const deadline=Date.now()+45000;
    while(Date.now()<deadline&&!stopping){if(await check())return;await sleep(500);}
    throw new Error(label+'啟動未完成，請查看 '+logRoot);
  }
  async function existingApi(){
    try{
      const response=await fetch(origin+'/health',{signal:AbortSignal.timeout(1500)});
      if(!response.ok)throw new Error('API 埠已被其他服務占用');
    }catch(error){if(error.cause?.code==='ECONNREFUSED')return null;throw error;}
    let status;
    try{status=await readStatus(origin);}catch{throw new Error('偵測到舊版或其他 API，請先停止佔用 '+origin+' 的開發服務，再重新啟動。');}
    if(!sameDatabase(status.databasePath,paths.database))throw new Error('此 API 使用不同資料庫，請先停止舊開發服務；啟動器不會合併或覆蓋資料。');
    return status;
  }
  async function build(project){
    await new Promise((resolve,reject)=>{
      const child=spawn('dotnet',['build',path.join(repository,`backend/src/DevFlow.${project}`),'-c','Release','--nologo','-m:1','/nr:false'],{cwd:repository,env,windowsHide:true,stdio:'inherit'});
      child.once('error',reject);child.once('exit',code=>code===0?resolve():reject(new Error(project+' 建置失敗')));
    });
  }
  process.on('SIGINT',()=>{void stop();});process.on('SIGTERM',()=>{void stop();});
  process.stdin.setEncoding('utf8');
  let input='';
  process.stdin.on('data',chunk=>{input+=chunk;if(input.split(/\r?\n/).includes('shutdown'))void stop();if(input.length>4096)input='';});
  process.stdin.on('end',()=>{void stop();});
  try{
    const existing=await existingApi();
    if(!flags.has('--no-build')){
      if(!existing)await build('Api');
      if(stopping)return;
      if(!existing?.online)await build('Worker');
    }
    if(stopping)return;
    const binary=name=>path.join(repository,`backend/src/DevFlow.${name}/bin/Release/net10.0/DevFlow.${name}${process.platform==='win32'?'.exe':''}`);
    if(!existing){
      launch('Api',binary('Api'),[],path.join(repository,'backend/src/DevFlow.Api'));
      await waitFor(async()=>{try{return sameDatabase((await readStatus(origin)).databasePath,paths.database);}catch{return false;}},'API');
    }else console.log('沿用同一資料庫的開發 API。');
    if(!(await readStatus(origin)).online){
      launch('Worker',binary('Worker'),[],path.join(repository,'backend/src/DevFlow.Worker'));
      await waitFor(async()=>{try{return (await readStatus(origin)).online;}catch{return false;}},'Worker');
    }else console.log('沿用同一資料庫的 Worker。');
    if(!flags.has('--backend-only')){
      let reuseWeb=false;
      try{reuseWeb=sameDatabase((await readStatus(web)).databasePath,paths.database);}catch{}
      if(!reuseWeb){launch('Web',process.execPath,[vite,'--host','127.0.0.1','--port','5173','--strictPort'],path.join(repository,'frontend'));await waitFor(async()=>{try{return sameDatabase((await readStatus(web)).databasePath,paths.database);}catch{return false;}},'網頁');}
    }
    console.log(`DevFlow 已就緒：${flags.has('--backend-only')?origin:web}\n資料庫：${paths.database}\nWorker 已連線。按 Ctrl+C 關閉本次啟動的服務。\n紀錄：${logRoot}`);
    if(children.length===0)process.stdin.pause();
  }catch(error){console.error(error.message);await stop(1);}
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))await main();
