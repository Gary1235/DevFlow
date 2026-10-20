import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {mkdtemp,rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {spawn} from 'node:child_process';
import path from 'node:path';
import {devPaths,readStatus,repository,sameDatabase} from './dev.mjs';

test('one development database is reused and explicit environment paths stay independent',()=>{
  const defaults=devPaths(repository);
  assert.equal(defaults.database,path.join(repository,'backend/src/DevFlow.Api/.data/devflow.db'));
  const custom=devPaths(repository,{DevFlow__DatabasePath:path.join(tmpdir(),'separate-dev.db')});
  assert.equal(custom.artifacts,path.join(tmpdir(),'artifacts'));
  assert.equal(sameDatabase(defaults.database,custom.database),false);
  assert.equal(sameDatabase(null,defaults.database),false);
});
test('launcher refuses an unrelated status response',async()=>{
  const server=createServer((_request,response)=>{response.setHeader('Content-Type','application/json');response.end('{}');});
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  try{await assert.rejects(readStatus(`http://127.0.0.1:${server.address().port}`),/不是可辨識/);}
  finally{await new Promise(resolve=>server.close(resolve));}
});
test('real API and Worker share an empty test database and stop together',{skip:process.env.DEVFLOW_TEST_RUNTIME!=='1',timeout:60000},async()=>{
  const root=await mkdtemp(path.join(tmpdir(),'devflow-launcher-'));
  const reserve=createServer();await new Promise(resolve=>reserve.listen(0,'127.0.0.1',resolve));
  const origin=`http://127.0.0.1:${reserve.address().port}`;await new Promise(resolve=>reserve.close(resolve));
  const env={...process.env,DevFlow__ApiUrl:origin,DevFlow__DatabasePath:path.join(root,'test.db'),DevFlow__ArtifactsRoot:path.join(root,'artifacts'),DevFlow__WorktreeRoot:path.join(root,'worktrees'),DevFlow__CliControlRoot:path.join(root,'control')};
  const child=spawn(process.execPath,[path.join(repository,'scripts/dev.mjs'),'--backend-only','--no-build'],{cwd:tmpdir(),env,windowsHide:true,stdio:['pipe','pipe','pipe']});
  let output='';child.stdout.on('data',chunk=>output+=chunk);child.stderr.on('data',chunk=>output+=chunk);
  const exited=new Promise(resolve=>child.once('exit',resolve));
  try{
    const deadline=Date.now()+35000;
    while(!output.includes('DevFlow 已就緒')&&child.exitCode===null&&Date.now()<deadline)await new Promise(resolve=>setTimeout(resolve,200));
    assert.match(output,/DevFlow 已就緒/,output);
    const presence=await readStatus(origin);assert.equal(presence.online,true);assert.equal(presence.databasePath,env.DevFlow__DatabasePath);
    assert.deepEqual(await (await fetch(origin+'/api/tasks')).json(),[]);
    child.stdin.end('shutdown\n');assert.equal(await exited,0);
    await assert.rejects(fetch(origin+'/health'));
  }finally{
    if(child.exitCode===null){child.stdin.end('shutdown\n');await exited;}
    await rm(root,{recursive:true,force:true});
  }
});
