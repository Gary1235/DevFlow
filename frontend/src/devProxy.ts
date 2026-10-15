export function proxyOrigin(origin:string|undefined,host:string|undefined,target:string):string|undefined {
  if(!origin||origin!==`http://${host}`||!['http://127.0.0.1:5173','http://localhost:5173'].includes(origin))return undefined;
  const backend=new URL(target);
  if(backend.protocol!=='http:'||!['localhost','127.0.0.1','[::1]'].includes(backend.hostname))throw new Error('DevFlow proxy must target a local HTTP API.');
  return backend.origin;
}
