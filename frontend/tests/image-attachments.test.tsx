import {it,expect,vi} from 'vitest';
import {render,screen,fireEvent,waitFor} from '@testing-library/react';
import {Discussions} from '../src/components/Discussions';
import {NoticeProvider} from '../src/components/ui';
import type {BaseData,Discussion} from '../src/types';
const projectId='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',id='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const data:BaseData={projects:[{id:projectId,name:'Demo',repositoryPath:'C:/demo',baseBranch:'main',validationArguments:['build'],version:0,aiDefaults:null}],tasks:[],providers:[{id:'codex',installed:true,modelsVerified:false,executable:'codex',models:[]},{id:'antigravity',installed:true,modelsVerified:false,executable:'agy',models:[]}]};
function setup(existing=false,fail=false){
  let d:Discussion={id,projectId,title:'截圖討論',selection:{provider:'codex',model:null},messages:existing?[{id:'saved',role:'user',content:'查看截圖',createdAt:'2026-10-27T01:00:00Z',images:[{id:'image-id',name:'saved.png',mimeType:'image/png',bytes:4}]}]:[],rules:[],drafts:[],version:existing?2:0,updatedAt:'2026-10-27T01:00:00Z',activeOperation:null,error:null};
  const calls:{path:string;method:string;body:any}[]=[];
  vi.stubGlobal('fetch',async(input:string,options:RequestInit={})=>{
    const path=new URL(input,location.href).pathname,method=options.method||'GET',body=options.body?JSON.parse(String(options.body)):null;calls.push({path,method,body});
    if(path.endsWith('/messages')&&fail)return new Response(JSON.stringify({title:'暫時無法送出'}),{status:502});
    if(path.endsWith('/messages'))d={...d,version:d.version+1,messages:[...d.messages,{id:'sent',role:'user',content:body.message||'請查看附圖。',createdAt:d.updatedAt,images:body.images?.map((i:any)=>({id:'new-image',name:i.name,mimeType:i.mimeType,bytes:4}))}]};
    return new Response(JSON.stringify(path==='/api/discussions'&&method==='GET'?[d]:d),{headers:{'Content-Type':'application/json'}});
  });
  render(<NoticeProvider><Discussions data={data} projectId={projectId} discussionId={existing||fail?id:undefined} onTaskCreated={()=>{}}/></NoticeProvider>);
  return calls;
}
function paste(){const file=new File(['screenshot-bytes'],'shot.png',{type:'image/png'});fireEvent.paste(screen.getByLabelText('討論訊息'),{clipboardData:{items:[{kind:'file',getAsFile:()=>file}]}});}
it('pastes an image, previews it, removes it and sends an image-only first turn',async()=>{
  const calls=setup();paste();
  fireEvent.click(await screen.findByRole('button',{name:'預覽圖片 shot.png'}));expect(screen.getByRole('dialog')).toBeTruthy();
  fireEvent.click(screen.getByRole('button',{name:'關閉視窗'}));
  fireEvent.click(screen.getByRole('button',{name:'移除圖片 shot.png'}));expect(screen.queryByAltText('shot.png')).toBeNull();
  expect(calls.every(c=>c.method==='GET')).toBe(true);
  paste();await screen.findByAltText('shot.png');await waitFor(()=>expect(screen.getByRole('button',{name:'傳送訊息'}).hasAttribute('disabled')).toBe(false));
  fireEvent.click(screen.getByRole('button',{name:'傳送訊息'}));
  await waitFor(()=>expect(calls.some(c=>c.path.endsWith('/messages'))).toBe(true));
  expect(calls.find(c=>c.path.endsWith('/messages'))?.body).toMatchObject({message:'',images:[{name:'shot.png',mimeType:'image/png',data:btoa('screenshot-bytes')}]});
});
it('keeps attachments after a failed send and blocks unsupported provider without losing the draft',async()=>{
  const calls=setup(false,true);await screen.findByRole('heading',{name:'截圖討論'});paste();await screen.findByAltText('shot.png');
  await waitFor(()=>expect(screen.getByRole('button',{name:'傳送訊息'}).hasAttribute('disabled')).toBe(false));fireEvent.click(screen.getByRole('button',{name:'傳送訊息'}));
  await screen.findByText('暫時無法送出');expect(screen.getByAltText('shot.png')).toBeTruthy();
  fireEvent.click(screen.getByRole('button',{name:/Codex CLI · CLI 預設/}));fireEvent.change(screen.getByLabelText('對話 AI'),{target:{value:'antigravity'}});fireEvent.click(screen.getByRole('button',{name:'完成'}));
  expect(screen.getByRole('button',{name:'傳送訊息'}).hasAttribute('disabled')).toBe(true);expect(screen.getByText(/Antigravity 背景模式目前只接受文字/)).toBeTruthy();
  expect(screen.getByAltText('shot.png')).toBeTruthy();expect(calls.filter(c=>c.path.endsWith('/messages')).length).toBe(1);
});
it('shows persisted thumbnails and preserves normal text paste',async()=>{
  setup(true);const saved=await screen.findByAltText('saved.png');expect(saved.getAttribute('src')).toBe('/api/discussions/'+id+'/images/image-id');
  const cancelled=fireEvent.paste(screen.getByLabelText('討論訊息'),{clipboardData:{items:[{kind:'string'}]}});expect(cancelled).toBe(true);
});
it('accepts dragged files and rejects oversized or unsupported uploads locally',async()=>{
  setup();const field=screen.getByLabelText('討論訊息');
  fireEvent.drop(field,{dataTransfer:{files:[new File(['abc'],'drop.webp',{type:'image/webp'})]}});await screen.findByAltText('drop.webp');
  fireEvent.change(screen.getByLabelText('選擇圖片'),{target:{files:[new File(['<svg/>'],'bad.svg',{type:'image/svg+xml'}),new File([new Uint8Array(4*1024*1024+1)],'large.png',{type:'image/png'})]}});
  await screen.findAllByText('請使用 PNG、JPEG 或 WebP，每張上限 4 MB。');expect(screen.queryByAltText('bad.svg')).toBeNull();expect(screen.queryByAltText('large.png')).toBeNull();
});
