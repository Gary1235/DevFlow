import {selectableModels} from './domain';
import type {Provider} from './types';

export interface ModelFamily {key:string;name:string;variants:{id:string;effort:string|null}[]}
// Group only when the real CLI slug and its display label agree on the effort.
// Selection always resolves to an ID actually returned by the CLI.
export function antigravityFamilies(provider:Provider):ModelFamily[]{
  const families=new Map<string,ModelFamily>();
  for(const id of selectableModels(provider)){
    const name=provider.modelDetails?.find(m=>m.id===id)?.displayName||id;
    const slug=/^(.+)-(low|medium|high|xhigh|max)$/.exec(id),label=/^(.+?)\s+\((low|medium|high|xhigh|max)\)$/i.exec(name);
    const grouped=!!slug&&!!label&&slug[2]===label[2].toLowerCase();
    const key=grouped?'__agy_family:'+slug![1]:id;
    const family=families.get(key)||{key,name:grouped?label![1]:name,variants:[]};
    family.variants.push({id,effort:grouped?slug![2]:null});families.set(key,family);
  }
  const order=['low','medium','high','xhigh','max'];
  return [...families.values()].map(f=>({...f,variants:f.variants.sort((a,b)=>order.indexOf(a.effort||'')-order.indexOf(b.effort||''))}));
}
