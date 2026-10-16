import {it,expect,vi} from 'vitest';
import {fireEvent,render,screen,waitFor} from '@testing-library/react';
import {ProjectDialog} from '../src/components/TaskDialogs';
import {NoticeProvider} from '../src/components/ui';

function renderProject(){const created=vi.fn();render(<NoticeProvider><ProjectDialog onClose={()=>{}} onCreated={created}/></NoticeProvider>);return created;}
it('opens Windows selection without an extra modal and aborts the request when the project dialog unmounts',async()=>{
  const requests:string[]=[];let aborted=false;
  vi.stubGlobal('fetch',(path:string,options:RequestInit)=>{requests.push(path);return new Promise((_resolve,reject)=>options.signal?.addEventListener('abort',()=>{aborted=true;reject(new DOMException('Aborted','AbortError'));},{once:true}));});
  const view=render(<NoticeProvider><ProjectDialog onClose={()=>{}} onCreated={()=>{}}/></NoticeProvider>);fireEvent.click(screen.getByRole('button',{name:'選擇資料夾'}));
  expect(screen.queryByRole('dialog',{name:'選擇專案資料夾'})).toBeNull();
  expect(screen.getAllByRole('dialog')).toHaveLength(1);
  expect(screen.getByRole('button',{name:'選擇資料夾'}).hasAttribute('disabled')).toBe(true);
  await waitFor(()=>expect(requests).toEqual(['/api/projects/pick-folder']));
  expect(screen.getByRole('button',{name:'登錄專案'}).hasAttribute('disabled')).toBe(false);
  view.unmount();
  expect(aborted).toBe(true);
});
it('fills the native Unicode folder result and passes the current path as the initial folder',async()=>{
  const requests:unknown[]=[];
  vi.stubGlobal('fetch',async(_url:string,options:RequestInit)=>{requests.push(JSON.parse(String(options.body)));return new Response(JSON.stringify({path:'D:\\專案\\測試'}));});
  const created=renderProject();fireEvent.change(screen.getByLabelText('Repository 絕對路徑'),{target:{value:'D:\\專案'}});
  fireEvent.click(screen.getByRole('button',{name:'選擇資料夾'}));
  await waitFor(()=>expect((screen.getByLabelText('Repository 絕對路徑') as HTMLInputElement).value).toBe('D:\\專案\\測試'));
  expect(requests).toEqual([{path:'D:\\專案'}]);
  expect(screen.queryByRole('dialog',{name:'選擇專案資料夾'})).toBeNull();expect(created).not.toHaveBeenCalled();
});
it('preserves the previous path when Windows selection is cancelled',async()=>{
  vi.stubGlobal('fetch',async()=>new Response(JSON.stringify({path:null})));
  renderProject();fireEvent.change(screen.getByLabelText('Repository 絕對路徑'),{target:{value:'C:\\original'}});fireEvent.click(screen.getByRole('button',{name:'選擇資料夾'}));
  await waitFor(()=>expect(screen.getByRole('button',{name:'選擇資料夾'}).hasAttribute('disabled')).toBe(false));
  expect((screen.getByLabelText('Repository 絕對路徑') as HTMLInputElement).value).toBe('C:\\original');
});
it('shows native errors and lets the user explicitly switch to in-page browsing',async()=>{
  vi.stubGlobal('fetch',async(url:string)=>url.endsWith('pick-folder')?new Response(JSON.stringify({title:'Windows 選擇器無法開啟'}),{status:409}):new Response(JSON.stringify({path:'C:\\project',parentPath:'C:\\',folders:[]})));
  renderProject();fireEvent.click(screen.getByRole('button',{name:'選擇資料夾'}));
  expect(await screen.findByRole('alert')).toHaveProperty('textContent','Windows 選擇器無法開啟');
  fireEvent.click(screen.getByRole('button',{name:'改用頁面瀏覽'}));
  fireEvent.click(await screen.findByRole('button',{name:'選擇此資料夾'}));
  expect((screen.getByLabelText('Repository 絕對路徑') as HTMLInputElement).value).toBe('C:\\project');
});
