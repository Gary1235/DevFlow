import {it,expect,vi} from 'vitest';
import {render,screen,fireEvent,waitFor,within} from '@testing-library/react';
import {StoragePage} from '../src/components/StoragePage';
import {NoticeProvider} from '../src/components/ui';

const item={key:'run:artifacts',taskId:'task',runId:'run',title:'已完成任務',projectName:'Demo',kind:'artifacts',path:'D:\\artifacts\\run',bytes:1024,files:2,lastActivity:'2026-08-01T00:00:00Z',canClean:true,reason:null};
function setup(fail=false){
  const requests:{path:string;body:any}[]=[];
  vi.stubGlobal('fetch',async(path:string,options:RequestInit)=>{
    const body=options.body?JSON.parse(String(options.body)):null;requests.push({path,body});
    if(path==='/api/storage')return new Response(JSON.stringify({categories:[{id:'database',label:'資料庫與紀錄',path:'D:\\state.db',bytes:2048,files:1,complete:true,warning:null}],scannedAt:'2026-10-16T00:00:00Z'}));
    if(path.endsWith('/preview'))return new Response(JSON.stringify({id:'preview',expiresAt:'2026-10-16T10:00:00Z',olderThanDays:body.olderThanDays,items:[item,{...item,key:'run:worktree',kind:'worktree',canClean:false,reason:'工作副本有未提交變更'}],truncated:false}));
    if(fail)return new Response(JSON.stringify({title:'預覽已過期，請重新預覽。'}),{status:409});
    return new Response(JSON.stringify({items:[{key:item.key,title:item.title,kind:item.kind,bytes:1024,success:true,message:'已清理'}],completedAt:'2026-10-16T09:00:00Z'}));
  });
  render(<NoticeProvider><StoragePage/></NoticeProvider>);return requests;
}
it('shows inventory and requires preview, explicit selection and confirmation before cleanup',async()=>{
  const requests=setup();await screen.findByText('D:\\state.db');
  expect(requests.every(r=>r.path==='/api/storage')).toBe(true);
  fireEvent.change(screen.getByLabelText('保留天數'),{target:{value:'7'}});
  expect(requests.every(r=>r.path==='/api/storage')).toBe(true);
  fireEvent.click(screen.getByRole('button',{name:'預覽可清理項目'}));
  expect(await screen.findByText('工作副本有未提交變更')).toBeTruthy();
  expect((screen.getByLabelText('選取 已完成任務 工作副本與編譯檔案') as HTMLInputElement).disabled).toBe(true);
  expect((screen.getByRole('button',{name:'清理所選項目'}) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByLabelText('選取 已完成任務 日誌與原始測試報告'));
  fireEvent.click(screen.getByRole('button',{name:'清理所選項目'}));
  const modal=screen.getByRole('dialog',{name:'確認清理所選資料'});
  expect(within(modal).getByText(/任務文字、驗收紀錄、測試摘要及 Git 成果會保留/)).toBeTruthy();
  fireEvent.click(within(modal).getByRole('button',{name:'取消'}));
  expect(requests.some(r=>r.path.endsWith('/cleanup'))).toBe(false);
  fireEvent.click(screen.getByRole('button',{name:'清理所選項目'}));
  fireEvent.click(screen.getByRole('button',{name:'確認清理'}));
  await screen.findByText('清理結果');
  expect(requests.find(r=>r.path.endsWith('/preview'))?.body).toEqual({olderThanDays:7,artifacts:true,worktrees:true});
  expect(requests.find(r=>r.path.endsWith('/cleanup'))?.body).toEqual({previewId:'preview',keys:['run:artifacts']});
});
it('keeps the preview visible when the server rejects an expired cleanup and reports the error',async()=>{
  const requests=setup(true);await screen.findByText('D:\\state.db');
  fireEvent.click(screen.getByRole('button',{name:'預覽可清理項目'}));
  fireEvent.click(await screen.findByLabelText('選取 已完成任務 日誌與原始測試報告'));
  fireEvent.click(screen.getByRole('button',{name:'清理所選項目'}));fireEvent.click(screen.getByRole('button',{name:'確認清理'}));
  await screen.findByText('預覽已過期，請重新預覽。');
  await waitFor(()=>expect(screen.queryByRole('dialog')).toBeNull());
  expect(screen.queryByText('清理結果')).toBeNull();
  expect(requests.filter(r=>r.path.endsWith('/cleanup'))).toHaveLength(1);
});
