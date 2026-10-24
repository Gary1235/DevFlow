# 第二輪改善與驗證紀錄

對應 [原始盤點](audit-2026-10-23.md) 與 [第一輪紀錄](audit-implementation-2026-10-23.md)。本檔保留第二輪驗收快照；後續的設定測試持久化、取消與證據覆寫修正見 [第三輪紀錄](audit-round-three.md)。

## 已完成

| 盤點項目 | 第二輪行為 |
|---|---|
| A1 開發版通知 | Windows 啟動器支援 `npm run dev -- --notify`，或 `DEVFLOW_PERMISSION_NOTIFICATIONS=1`。獨立系統匣程序每五秒取得待核准清單，以 request ID 去重；瀏覽器關閉後仍可通知，點擊開啟 5173 網頁的任務／討論。啟動器結束時退出。通知失敗不會中止 API／Worker。 |
| A2 repository 互斥 | 統一路徑連結與 Git common-dir 身分。同一資料庫下，不同專案 ID、不同 Git worktree 指向同一 Repository 時仍共用排程／合併互斥。 |
| A3、A5 結構化能力 | Codex 的 app-server 註冊讀取、列檔、文字搜尋、寫入、執行已儲存驗證設定五種主程序工具。工具不經 shell 字串解析；唯讀階段不提供寫入／驗證。所有操作仍經現有權限紀錄、修改範圍與控制檔保護；驗證比對設定版本，等待核准後再次檢查版本。 |
| A4 搜尋 | 新結構化搜尋使用 Git 已追蹤與未忽略檔案集合，不掃描被忽略的依賴；不跟隨連結。回傳檔案／文字搜尋截斷旗標。原生 shell 搜尋仍使用各自的既有邊界檢查。 |
| B1、B3 設定診斷 | 專案設定可偵測真實 .NET 方案、測試專案、Web 專案及 npm scripts，手動加入表單。提供目錄、目標、script、lockfile、SDK／Node／npm 版本預檢；另可在可清除的隔離副本執行表單中的驗證與準備，不呼叫 AI。API 關閉會取消並等待測試清理。 |
| B2、B3 驗證可見性 | 原驗證與每次修復的 evidence 同時可查；摘要採最新系統驗證，明確區分系統修復與 AI 自行呼叫的驗證，避免修復通過後仍只顯示第一次失敗。 |
| C2 核准紀錄 | 日常輪詢只讀所有 Pending 加最近 100 筆；歷史依 task／run、rowid 游標分頁，介面按需載入，每頁最多 200 筆。增加索引，不再反覆傳輸完整歷史。 |
| C4 分歧整合 | 合併視窗可建立「整合最新基底」新任務。固定來源成果與最新目標 commit，在隔離副本整合、處理衝突、驗證原流程使用的設定，再完整重驗並執行已設定的 AI 審查。成果仍需人工審核，最後維持明確的 fast-forward 合併。 |
| C4 失敗副本恢復 | 失敗任務可先預覽未提交檔案，確認舊 CLI／測試程序停止後，將快照複製到新的恢復任務。重新檢查原副本／版本與指紋，保留原始副本；從乾淨基底驗證後套用 checkpoint，修正並保存 baseline／Red 的既有測試 ID。 |

## 在另一台開發主機使用

1. 更新程式碼後，停止並重啟 API、Worker 與開發啟動器；正在執行的舊程序不會自行載入新功能。
2. Windows 需要網頁關閉後通知時，執行 `npm run dev -- --notify`。啟動器與通知程序仍須保持執行；Windows 通知設定、勿擾模式可能抑制氣泡顯示。僅 `--backend-only` 不會啟動網頁，點擊通知仍使用 5173，須另有開發網頁服務。
3. 在「專案設定 → 偵測與測試設定」加入適合的目標，先預檢，再執行隔離測試。測試使用基底分支的已提交檔案；表單草稿可直接測試，但要儲存設定才供正式任務使用。
4. Codex 主程序工具預設啟用，須使用支援實驗性 dynamic tools 的 CLI。若舊 CLI 在建立 thread 時拒絕此能力，可設定 `DevFlow__StructuredToolsEnabled=false` 並重啟 API／Worker，回到原生工具轉接。沒有自動重送已失敗工具或擴張授權。
5. 成果與主分支分歧時，在合併視窗建立整合任務；失敗實作想保留未提交內容時，在失敗任務預覽並建立恢復任務。兩者都新增任務，不取代來源證據。

## 新增 API

| Method／Path | 輸入與結果 |
|---|---|
| GET `/api/projects/{id}/execution-discovery` | 回傳可加入表單的 validations、previews、warnings。 |
| POST `/api/projects/{id}/execution-preflight` | `{expectedVersion, profile}`；只檢查目標與固定工具版本。 |
| POST `/api/projects/{id}/execution-probes` | 同上；202 回傳測試 ID，背景執行草稿設定。 |
| GET `/api/execution-probes/{id}` | 查詢 Running／Completed／Failed、preflight、evidence、error、retainedWorkspace。 |
| GET `/api/tasks/{id}/permissions/history?runId=...&before=...&limit=100` | 回傳 items、nextBefore；首頁省略 before，直到 nextBefore 為 null。 |
| POST `/api/tasks/{id}/integrate` | `{expectedVersion, runId, resultCommit, expectedTargetCommit}`；201 回傳新任務。 |
| POST `/api/tasks/{id}/recovery/preview` | 無 body；回傳限時預覽 ID、檔案與大小。 |
| POST `/api/tasks/{id}/recovery` | `{previewId, oldProcessStopped:true}`；201 回傳新任務。 |

新增 POST 均要求現有的 `X-DevFlow-Action: 1` 明確操作標頭及來源檢查；版本／狀態衝突回傳 409。恢復預覽與設定測試狀態不跨 API 重啟保存。

## 邊界與保留項目

- 結構化橋接目前只接 Codex；Claude／Gemini 等仍使用原生工具與現有轉接器。未知 shell、外部存取、安裝、部署、資料庫指令仍可能需要核准。已儲存設定不是任意腳本的永久放行。
- 結構化驗證不執行依賴準備；由 Worker 的既有準備階段處理。唯讀操作不等於作業系統沙箱，也不防止其他外部程序同時變更檔案。
- 偵測最多 5000 項、深度六層；預檢不能替代真正建置、套件版本相容性、每種 CLI 的真實模型驗證或新主機環境還原。自訂執行程式以隔離測試確認。
- 設定測試同時最多一個，最長三十分鐘，最近二十筆狀態在 API 記憶體中，重啟後不保留查詢狀態；日誌仍寫入 artifacts。清理失敗會回報保留副本路徑。
- 恢復僅支援尚未完成的 Implementation 步驟；排除被忽略檔案、連結、受保護控制檔、未解衝突，以及超過 200 檔／20 MiB 的內容。預覽十分鐘失效。無法自動證明遠端或孤立 CLI 已停止；必須先確認。恢復 checkpoint 的 Git ref 會保留以避免遺失成果。
- 整合沿用已設定的審查者；無審查者的舊任務仍需人工審核。AI 審查回報問題或差異過大時不自動合併。超長 diff 自動分批審查、舊 CLI 重連、跨資料庫 repository 鎖與事件推送仍未實作。

## 驗證結果

- `dotnet test backend/DevFlow.slnx --no-restore --verbosity minimal`：955 通過，0 失敗、0 略過。
- `frontend: npm test -- --run`：22 檔、117 通過。
- `dotnet build backend/DevFlow.slnx --no-restore --verbosity minimal`：包含 API、Worker、LiveCheck，0 警告、0 錯誤。
- `frontend: npm run build`：TypeScript 與 Vite 建置通過。
- `node --test scripts/runtime.test.mjs desktop/test/*.test.cjs`：13 通過、0 略過，含真 API／Worker 共用資料庫、待核准端點與正常停止。
- Windows 通知腳本完成 PowerShell 語法解析；未在本次工具環境實際點擊 Windows 通知。未呼叫付費模型、未產生安裝包、未推送或執行遠端 CI。

新增回歸覆蓋真實 Git 衝突整合／人工核准後合併、未提交內容恢復與過期快照、不同 worktree 的共用鎖、實際 Node 設定測試與關閉清理、歷史分頁、中文大量寫入與稽核上限、跨來源／過期版本 API 保護、Codex dynamic tool 協定與錯誤 thread／turn 拒絕，以及前端診斷／恢復操作。

Codex 協定依 [官方 app-server 文件](https://learn.chatgpt.com/docs/app-server) 與本機 CLI 產生的 JSON schema 核對：`initialize.capabilities.experimentalApi`、`thread/start.dynamicTools`、`item/tool/call`、`success/contentItems`。協定 fixture 並不代表另一台主機的 CLI／模型已完成實測。
