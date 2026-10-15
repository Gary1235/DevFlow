import {afterEach,vi} from 'vitest';
import {cleanup} from '@testing-library/react';
afterEach(()=>{cleanup();vi.useRealTimers();vi.restoreAllMocks();vi.unstubAllGlobals();if(typeof document!=='undefined'){history.replaceState(null,'','/');sessionStorage.clear();}});
if(typeof HTMLDialogElement!=='undefined'){
  HTMLDialogElement.prototype.showModal=function(){this.open=true;};
  HTMLDialogElement.prototype.close=function(){this.open=false;};
}
