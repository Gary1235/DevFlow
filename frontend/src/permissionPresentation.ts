// Presentation only: approval replies always refer to the original server-side arguments.
export function permissionArguments(value:unknown):unknown {
  if(Array.isArray(value))return value.map(permissionArguments);
  if(value&&typeof value==='object')return Object.fromEntries(Object.entries(value).map(([key,item])=>[
    key,/password|passwd|secret|token|api[_-]?key|authorization|connectionstring/i.test(key)?'[已隱藏機敏值]':permissionArguments(item),
  ]));
  if(typeof value==='string')return value
    .replace(/((?:password|passwd|pwd|token|api[_-]?key|secret)\s*[=:]\s*)(?:"[^"]*"|'[^']*'|[^;\s]+)/gi,'$1[已隱藏機敏值]')
    .replace(/(--(?:password|token|api-key|secret)\s+)(?:"[^"]*"|'[^']*'|\S+)/gi,'$1[已隱藏機敏值]')
    .replace(/(\b(?:postgres(?:ql)?|mysql|mongodb(?:\+srv)?)?:\/\/[^\s/:]+:)[^\s@]+@/gi,'$1[已隱藏機敏值]@');
  return value;
}
