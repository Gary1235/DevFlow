export interface RecoveryAdvice {kind:string;title:string;next:string;connection?:boolean;model?:boolean}
export function recoveryAdvice(error:string):RecoveryAdvice{
  if(/\[Authentication\]|not authenticated|unauthorized|login required|登入.*失效/i.test(error))return {kind:'authentication',title:'需要重新確認登入',next:'到「AI 連線」檢查並重新登入，再回來手動重試。',connection:true};
  if(/\[ModelUnavailable\]|model.not.found|unsupported model|模型不可用/i.test(error))return {kind:'model',title:'目前模型無法使用',next:'重新載入可用模型，選擇另一個模型後再重試。',connection:true,model:true};
  if(/\[Quota\]|rate.limit|quota|額度|頻率受限/i.test(error))return {kind:'quota',title:'額度或請求頻率受限',next:'先檢查帳號額度，或等待限制解除後再重試；不會自動重送。',connection:true};
  if(/\[Permission\]|工具權限|未獲准|拒絕本次工具|permission.denied|approval required/i.test(error))return {kind:'permission',title:'工具操作未獲准',next:'有待確認卡片時，請直接回覆；若本次已結束，先查看權限紀錄與 CLI 設定，再手動重試。'};
  if(/Baseline|Red requires|Red phase|must pass|validation failed|Integration validation|測試.*失敗|測試未通過/i.test(error))return {kind:'validation',title:'測試或驗證未通過',next:'查看失敗測試與執行紀錄，在修正指示中說明要處理的問題，再選擇重試範圍。'};
  if(/\[Network\]|network|無法連線|connection reset/i.test(error))return {kind:'network',title:'CLI 連線失敗',next:'確認網路與 AI 連線狀態，再手動重試。',connection:true};
  if(/timed out|timeout|逾時|interrupted|已停止|cancelled/i.test(error))return {kind:'interrupted',title:'執行已停止、逾時或中斷',next:'查看最後活動與執行紀錄，確認原因後再選擇接續或重新執行。'};
  return {kind:'unknown',title:'這次執行未完成',next:'目前資訊不足以確定原因。請查看執行紀錄；可檢查連線、調整模型或補充修正指示後手動重試。',connection:true,model:true};
}
