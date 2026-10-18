# 後端 TDD 驗證紀錄

測試報告（`.trx`）與診斷 log 是本機產生檔，不納入版控。本摘要保留開發時的 Red／Green 證據與環境修正結果。

| 紀錄 | 總測試 | 通過 | 失敗 | 說明 |
| --- | ---: | ---: | ---: | --- |
| 01-red | 13 | 0 | 13 | 領域、SQLite、HTTP 行為實作前 |
| 02-green | 13 | 13 | 0 | 第一輪實作完成 |
| 03-execution-red | 19 | 13 | 6 | TDD 調度及 TRX 解析實作前 |
| 04-execution-green | 19 | 19 | 0 | 調度及證據驗證完成 |
| 05-infrastructure-red | 25 | 19 | 6 | CLI、程序、Worktree 與中斷恢復實作前 |
| 06-infrastructure-green | 25 | 24 | 1 | Git 測試環境權限造成失敗，並非完整 Green |
| 07-validation-red | 30 | 24 | 6 | 驗證執行器與即時取消實作前，另有 Git 環境失敗 |
| 08-validation-green | 30 | 29 | 1 | Git 唯讀物件清理失敗，並非完整 Green |
| 09-green | 30 | 30 | 0 | 修正測試清理後全數通過 |
| 10-baseline-red | 31 | 30 | 1 | Baseline 守門測試實作前 |
| 11-baseline-green | 31 | 31 | 0 | Baseline 驗證完成 |
| 12-relocation-aborted | 0 | 0 | 0 | 搬移後 testhost 本機連線逾時，測試未執行 |
| 13-relocation-verified | 31 | 31 | 0 | 2026-10-13 在正式 backend 路徑完成 Release 測試 |
| 14-review-red | 40 | 31 | 9 | 審核、版本守門、退回重試與歷史保存實作前 |
| 15-review-green | 40 | 40 | 0 | SQLite 審核與重試交易完成 |
| 16-review-api-red | 44 | 38 | 6 | HTTP 審核、差異與重試交接實作前 |
| 17-review-api-green | 44 | 44 | 0 | HTTP 接口、真實 Git 差異與修正意見交接通過 |
| 18-review-edge-red | 50 | 48 | 2 | 超大差異拒絕顯示與審核決策輸入守門實作前 |
| 19-review-release-green | 50 | 50 | 0 | 完整方案 Release 建置及審核流程驗證通過 |
| 20-merge-red | 58 | 50 | 8 | 合併核准守門、紀錄、互斥與中斷核對實作前 |
| 21-merge-green | 58 | 58 | 0 | 合併調度與 SQLite 狀態交易完成 |
| 22-git-merge-red | 65 | 58 | 7 | 真實 Git 合併及 HTTP 接口實作前 |
| 23-git-merge-green | 65 | 64 | 1 | 合併實作通過；Windows 換行測試預期需修正，並非完整 Green |
| 24-merge-release-green | 66 | 66 | 0 | Release 建置與 Git／HTTP 合併驗證通過 |
| 25-merge-race-red | 15 | 14 | 1 | 僅執行合併測試組；確認執行中核對可能提前解鎖的競態 |
| 26-merge-release-green | 67 | 67 | 0 | 加入執行中互斥與 Pending 等候保護後，完整 Release 測試通過 |
| 27-planning-red | 75 | 67 | 8 | 規劃佇列、取消、版本歷史與中斷恢復實作前 |
| 28-planning-green | 75 | 75 | 0 | 規劃調度及 SQLite 計畫歷史完成 |
| 29-planner-artifact-red | 86 | 75 | 11 | CLI 結構化規劃與 Artifact 讀取實作前 |
| 30-planner-artifact-green | 86 | 86 | 0 | 規劃 Runner、路徑保護、Unicode 分頁與驗證摘要通過 |
| 31-planning-api-red | 89 | 86 | 3 | 自動規劃與紀錄 HTTP 接口實作前 |
| 32-planning-api-green | 89 | 89 | 0 | HTTP 接口及 Worker 規劃調度接入完成 |
| 33-plan-approval-red | 93 | 92 | 1 | 計畫核准尚未綁定人工看過的版本 |
| 34-plan-snapshot-red | 23 | 20 | 3 | 僅執行規劃及實作測試組；核准版本與規劃 commit 守門實作前 |
| 35-planning-release-green | 94 | 94 | 0 | 完整 Release 建置與規劃、版本核准、commit 保存及 Artifact 接口驗證通過 |
| 36-livecheck-release-green | 94 | 94 | 0 | 加入獨立真實 CLI 驗證工具後，完整回歸通過；模型端到端仍受 CLI 啟動權限錯誤阻擋 |
| 37-ai-api-red | 7 | 0 | 7 | AI 設定保存、預設繼承、登入入口與工作台接口實作前 |
| 38-ai-runner-green | 7 | 7 | 0 | 路由、獨立模型參數、Gemini 錯誤輸出及 Run 設定快照通過 |
| 39-ai-api-green | 14 | 7 | 7 | 部分通過；前端靜態資產連結產生舊 Manifest，並非完整 Green |
| 40-cli-connection-red | — | — | — | 測試編譯錯誤（單個 params new 需明確類型），不是行為 Red；沒有 TRX |
| 41-cli-probe-red | 13 | 5 | 8 | 確認 prompt echo 不能算 probe 成功，以及靜態資產 Manifest 問題 |
| 42-cli-api-green | 13 | 13 | 0 | 登入互斥、取消、逾時、免額度診斷、probe 結果守門與 HTTP 接口通過 |
| 43-multi-cli-release-green | 114 | 114 | 0 | v0.5 完整 Release 回歸通過；CLI／登入為測試替身，瀏覽器與模型端到端尚未驗收 |
| 44-login-cleanup-red | 1 | 0 | 1 | 系統拒絕終止登入程序時，不應拋出未處理例外或釋放供應商鎖定 |
| 45-cli-release-green | 115 | 115 | 0 | 加入清理失敗狀態及保留鎖定後，完整 Release 回歸通過；JavaScript 語法檢查通過 |
| 46-claude-red | 2 | 0 | 2 | Claude 備用清單與未登入時的任務設定保存實作前 |
| 47-claude-adapter-red | 14 | 13 | 1 | Claude 路由、模型、工具限制、登入與失敗結果通過；確認 Windows npm 包裝檔誤判為原生程式 |
| 48-claude-release-green | 129 | 129 | 0 | Claude 備用支援與 Windows 原生 CLI 探測修正後，完整 Release 回歸通過；未執行真實付費模型 |
| 49-frontend-assets-red | 2 | 0 | 2 | 前端 ES modules 靜態接口實作前 |
| 50-plan-editor-red | 1 | 0 | 1 | 過期前端計畫編輯可能覆蓋新版計畫 |
| 51-frontend-release-green | 132 | 132 | 0 | ES modules 服務與計畫編輯版本守門完成，完整 Release 回歸通過 |
| 52-react-assets-red | 2 | 0 | 2 | React 正式建置 JS／CSS 未隨 API 提供 |
| 53-react-assets-green | 132 | 132 | 0 | 複製 dist 並服務 /assets，完整 Release 回歸通過 |

v0.7 前端改為 Vitest／React Testing Library／jsdom，另驗證實際 Vite 代理的同源 CLI 動作與外部 Origin 保留。詳見 frontend/tests。
它們驗證 DOM 與操作流程，不代表實際瀏覽器排版、官方登入視窗或真實模型端到端驗收。

測試涵蓋狀態規則、驗收條件、SQLite 持久化與並行領取、HTTP 操作、TDD 守門、程序取消、Git Worktree 隔離及中斷任務保存。
上述回歸測試的模型呼叫使用測試替身；真實 CLI 嘗試與環境限制另見 [驗證紀錄](live-cli-2026-10-14.md)。


2026-10-16 推理選單修正：先確認隱藏的目前預設模型被過濾（1 個行為測試失敗），再改為請求完整目錄並僅保留目前預設的隱藏模型及其能力。完整回歸 175 項中 174 通過，1 項既有 StorageTests Git 清理測試因沙箱無法寫入暫存 .git/config 而失敗，並非完整 Green。前端 32 項通過（排除會占用開發中 5173 的代理測試）；新增標籤與手動深度測試先失敗再通過。未執行真實模型驗證。

2026-10-16 模型排序：版本排序測試先失敗，再修正為版本號降冪，同版本保留目錄順序；設定補入模型也參與排序，未修改原始模型與能力資料。前端 34 項通過，排除占用 5173 的代理測試，TypeScript 與正式建置通過。真實 gpt-6.1-sol probe 在工具沙箱中結束碼 1、stdout 空白，stderr 分類為本機權限錯誤；尚未取得使用者 ModelUnavailable 的原始訊息，未確認帳號權限或模型可用性。

## 2026-10-17：CLI 版本檢查與手動更新

- Red：新增 check／update 本機明確操作保護測試，兩項因接口未實作回傳 404 而失敗；前端兩項因缺少版本檢查與確認更新按鈕而失敗。
- 實作 npm 安裝來源驗證、精確套件版本與 prefix、check ID 時效、背景更新／版本驗證、重複更新／安裝變動守門，以及 API／Worker 共用檔案鎖。
- 回歸涵蓋忙碌作業、並行調用、安裝失敗、版本不符、取消／逾時、不安全版本字串、同源限制及無效 session；不執行真實 npm 全域安裝或付費模型。
- 原先使用系統 Temp 的 StorageTests Git 測試受沙箱寫入 .git/config 權限阻擋。將測試程序 TEMP／TMP 指向 backend/.test-work/maintenance-temp 後，保留原始測試與斷言，完整 195 項測試通過。結果：cli-maintenance-release.trx。
- Release solution 建置零警告／錯誤；React 全部 37 項測試、TypeScript 與正式建置通過。代理測試改用臨時埠並保留實際 Host／Origin 條件，可與開發中的 5173 共存。
- 尚未驗收真實全域 CLI 更新與瀏覽器視覺操作。部署後需重啟 API／Worker，UI 更新必須由使用者確認；API 重啟後先重新檢查實際版本，不重送安裝。
