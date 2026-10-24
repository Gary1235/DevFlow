# 第三輪：設定測試生命週期與證據可靠性

本輪延續 [第二輪](audit-round-two.md)，優先修正新增功能的狀態持久化、取消及證據保存，不擴張工具授權範圍。

## 修正內容

| 缺口 | 調整後 |
|---|---|
| 關閉設定視窗後找不到正在執行的設定測試；API 重啟遺失紀錄 | SQLite 新增 ValidationProbes 表與索引，保存專案、設定名稱、設定版本、時間、預檢、驗證結果與副本路徑。專案設定提供「載入最近設定測試」，可檢視最近二十筆或追蹤尚未完成的測試。 |
| 設定測試無法單獨取消 | 新增取消 API 與介面。先標記 Stopping，取消所屬程序並等待副本清理，才回報 Cancelled；不會把停止要求當成已完成清理。已結束的測試重複取消是唯讀回覆。 |
| 不同 API 程序可能同時執行設定測試 | 以資料庫旁的 `.probes.lock` 排他檔案鎖協調同一資料庫的設定測試。第二個服務不能開始或取消另一服務擁有的測試，也不能把仍有持有者的測試改成中斷。 |
| API 異常退出留下無主的 Running 狀態 | 啟動時取得鎖後，將未完成紀錄標為 Interrupted。保留可能的副本位置，不重跑、不自動清除，明示先確認舊驗證程序已停止。正常關閉仍會取消並等待清理。 |
| 路徑含中括號時，Git 將字面目錄名當 pathspec 模式 | 主程序列檔／搜尋使用 `git --literal-pathspecs`，只列出使用者指定目錄的檔案；不改動原生 shell 搜尋規則。 |
| 讀檔游標可落在 UTF-16 代理字元中間，或超出 EOF | 拒絕無效游標，保留正常分頁與 emoji 邊界處理。 |
| 同一 Run 不同 AI 階段的主程序驗證互相覆寫證據 | 每次結構化驗證使用獨立 GUID 路徑，artifact reader 同時相容第二輪的數字編號與新路徑。 |
| 新預檢失敗時可能仍顯示上次檢查資訊 | 開始新的操作前清除舊預檢／結果，避免舊成功訊息誤導判斷。 |

## 操作與 API

更新後重啟 API／Worker。在專案設定點「載入最近設定測試」，歷史顯示測試時的專案版本；「檢視結果」不執行指令，「追蹤進度」只輪詢既有測試。執行中可點「取消設定測試」。結果可能來自較舊表單或基底，不能直接當成目前設定已驗證。

- GET `/api/projects/{id}/execution-probes`：最近二十筆，包含進行中與已結束紀錄。
- GET `/api/execution-probes/{id}`：持續提供既有查詢，可跨 API 重啟讀取結果。
- POST `/api/execution-probes/{id}/cancel`：需要 `X-DevFlow-Action: 1` 及現有來源檢查。

資料表在既有初始化流程以 `CREATE TABLE IF NOT EXISTS` 建立，不覆寫原任務。只保留設定名稱、版本與證據，未額外保存整份執行環境或憑證。第一、二輪已完成且只存在舊 API 記憶體的設定測試，無法追溯匯入。

## 驗證範圍與限制

新增測試包含：資料庫／服務重新開啟後讀取結果、無主持有的測試轉 Interrupted 且副本保留、兩個服務共用鎖、取消真實 Node 程序後清理並釋放鎖、取消端點的明確操作及來源檢查、中括號目錄的真實 Git 列檔、Unicode 無效游標，以及同一 Run 的不同驗證工具實例保存各自證據。前端覆蓋重新檢視中斷紀錄、重新追蹤與取消。

本輪不會恢復已死去的 CLI 對話，檔案鎖也不協調不同資料庫。異常中斷後舊子程序可能仍存活，保留路徑須人工確認；設定測試的紀錄及日誌目前沒有自動保留期限。記錄保存與最近二十筆查詢是不同概念，舊紀錄不因列表上限而被刪除。

沒有呼叫付費模型、沒有在另一台主機操作、沒有推送、沒有建立安裝包；Windows 系統通知的真實顯示仍待使用主機確認。

## 最終驗證

- `dotnet test backend/DevFlow.slnx --no-restore --verbosity minimal`：962 通過，0 失敗、0 略過。
- `frontend: npm test -- --run`：23 檔、119 通過。
- `dotnet build backend/DevFlow.slnx --no-restore --verbosity minimal`：0 警告、0 錯誤，包含 Worker 與 LiveCheck。
- `frontend: npm run build`：TypeScript、Vite 建置成功。
- `node --test scripts/runtime.test.mjs desktop/test/*.test.cjs`：13 通過、0 略過，含真實 API／Worker 啟停。
- `git diff --check`：通過；目前 checkout 的換行提示不影響結果。
