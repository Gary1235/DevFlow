import {useCallback,useEffect,useRef,useState} from 'react';
export function useResource<T>(key:string,load:(signal:AbortSignal)=>Promise<T>,interval=5000,enabled=true){
  const [snapshot,setSnapshot]=useState<{key:string;data?:T;error?:string}>({key:''});
  const [revision,setRevision]=useState(0);
  const refresh=useCallback(()=>setRevision(v=>v+1),[]);
  useEffect(()=>{
    if(!enabled)return;
    let alive=true,busy=false;const controller=new AbortController();
    async function fetchData(){if(busy)return;busy=true;try{const data=await load(controller.signal);if(alive)setSnapshot({key,data});}
      catch(error){if(alive&&!controller.signal.aborted)setSnapshot(old=>({key,data:old.key===key?old.data:undefined,error:error instanceof Error?error.message:'讀取失敗'}));}finally{busy=false;}}
    void fetchData();const timer=interval>0?setInterval(()=>{if(!document.hidden)void fetchData();},interval):undefined;
    return()=>{alive=false;controller.abort();if(timer)clearInterval(timer);};
  },[key,load,interval,revision,enabled]);
  return {data:enabled&&snapshot.key===key?snapshot.data:undefined,error:enabled&&snapshot.key===key?snapshot.error:undefined,refresh};
}
export function useAction(notice:(message:string,error?:boolean)=>void){
  const [busy,setBusy]=useState(false),pending=useRef(false),mounted=useRef(true);
  useEffect(()=>{mounted.current=true;return()=>{mounted.current=false;};},[]);
  const run=async(work:()=>Promise<void>)=>{if(pending.current)return;pending.current=true;setBusy(true);try{await work();}catch(error){notice(error instanceof Error?error.message:'操作失敗',true);}finally{pending.current=false;if(mounted.current)setBusy(false);}};
  return {busy,run};
}
