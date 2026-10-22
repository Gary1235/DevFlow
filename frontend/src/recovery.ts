export interface RecoveryAdvice {kind:string;title:string;next:string;connection?:boolean;model?:boolean}
export function recoveryAdvice(error:string):RecoveryAdvice{
  if(/\[CliInterrupted\]/i.test(error))return {kind:'interrupted',title:'CLI 執行或確認通道已中斷',next:'CLI 已無法接收這次確認，舊確認不會繼續生效。請查看最後活動與執行紀錄，再手動重試。'};
  if(/找不到驗證設定|TDD 需要|工作目錄不存在|工作目錄.*超出/i.test(error))return {kind:'validation-config',title:'驗證設定需要調整',next:'到「專案 → 專案設定」確認工作目錄與驗證指令，再編輯待執行步驟選用的驗證設定；建置指令請使用標準實作模式。'};
  if(/\[Authentication\]|not authenticated|unauthorized|login required|登入.*失效/i.test(error))return {kind:'authentication',title:'需要重新確認登入',next:'到「AI 連線」檢查並重新登入，再回來手動重試。',connection:true};
  if(/\[ModelUnavailable\]|model.not.found|unsupported model|模型不可用/i.test(error))return {kind:'model',title:'目前模型無法使用',next:'重新載入可用模型，選擇另一個模型後再重試。',connection:true,model:true};
  if(/\[Quota\]|rate.limit|quota|額度|頻率受限/i.test(error))return {kind:'quota',title:'額度或請求頻率受限',next:'先檢查帳號額度，或等待限制解除後再重試；不會自動重送。',connection:true};
  if(/\[Permission\]|工具權限|未獲准|拒絕本次工具|permission.denied|approval required/i.test(error))return {kind:'permission',title:'工具操作未獲准',next:'有待確認卡片時，請直接回覆；若本次已結束，先查看權限紀錄與 CLI 設定，再手動重試。'};
  if(/Baseline|Red requires|Red phase|must pass|validation failed|Integration validation|驗證未通過|依賴準備失敗|測試.*失敗|測試未通過/i.test(error))return {kind:'validation',title:'測試或驗證未通過',next:'查看驗證日誌與實際錯誤；若是目錄、指令或套件問題，先調整專案的驗證設定，再選擇重試範圍。'};
  if(/\[Network\]|network|無法連線|connection reset/i.test(error))return {kind:'network',title:'CLI 連線失敗',next:'確認網路與 AI 連線狀態，再手動重試。',connection:true};
  if(/timed out|timeout|逾時|interrupted|已停止|cancelled/i.test(error))return {kind:'interrupted',title:'執行已停止、逾時或中斷',next:'查看最後活動與執行紀錄，確認原因後再選擇接續或重新執行。'};
  return {kind:'unknown',title:'這次執行未完成',next:'目前資訊不足以確定原因。請查看執行紀錄；可檢查連線、調整模型或補充修正指示後手動重試。',connection:true,model:true};
}
