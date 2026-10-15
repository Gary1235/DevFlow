export class ApiError extends Error {
  constructor(message:string,public status=0){super(message);this.name='ApiError';}
}
export interface RequestOptions {signal?:AbortSignal;timeout?:number}
export function createClient(fetcher:typeof fetch=(...args)=>globalThis.fetch(...args)){
  return async function request<T>(path:string,method='GET',body?:unknown,options:RequestOptions={}):Promise<T>{
    const controller=new AbortController(),abort=()=>controller.abort(options.signal?.reason);
    if(options.signal?.aborted)abort();else options.signal?.addEventListener('abort',abort,{once:true});
    const timer=setTimeout(()=>controller.abort(),options.timeout??25000);
    try{
      const response=await fetcher(path,{method,signal:controller.signal,credentials:'same-origin',headers:{'X-DevFlow-Action':'1',...(body===undefined?{}:{'Content-Type':'application/json'})},...(body===undefined?{}:{body:JSON.stringify(body)})});
      const text=await response.text();let result:unknown;
      try{result=text?JSON.parse(text):null;}catch{result=text;}
      if(!response.ok)throw new ApiError(typeof result==='object'&&result!==null&&'title' in result?String(result.title):`操作失敗 (${response.status})`,response.status);
      return result as T;
    }catch(error){
      if(options.signal?.aborted)throw error;
      if(controller.signal.aborted)throw new ApiError('請求逾時，操作可能已保存。請更新狀態後再試。');
      if(error instanceof ApiError)throw error;
      throw new ApiError('無法連線至 DevFlow，請確認本地 API 已啟動。');
    }finally{clearTimeout(timer);options.signal?.removeEventListener('abort',abort);}
  };
}
export const api=createClient();
export class LatestRequest {
  private sequence=0;
  private controller:AbortController|null=null;
  cancel(){this.sequence++;this.controller?.abort();this.controller=null;}
  async run<T>(work:(signal:AbortSignal)=>Promise<T>):Promise<T|undefined>{
    this.cancel();const ticket=this.sequence,controller=new AbortController();this.controller=controller;
    try{const result=await work(controller.signal);return ticket===this.sequence?result:undefined;}
    catch(error){if(ticket!==this.sequence||controller.signal.aborted)return undefined;throw error;}
    finally{if(ticket===this.sequence)this.controller=null;}
  }
}
