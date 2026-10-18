# DevFlow v0.7 React 開發工作台

## CLI 版本管理

在「AI 連線」各張 CLI 卡片下方按「檢查 CLI 更新」，查看目前版本、npm 最新版本與實際執行檔。
版本查詢只在點擊時連線至官方 npm registry，不呼叫模型、不自動安裝。
若有新版且 DevFlow 能確認目前 CLI 屬於可管理的 npm 全域安裝，可按「更新 CLI」；
確認視窗會顯示目標版本及安裝路徑。確認後背景安裝指定版本，再以實際 CLI 的 `--version` 驗證結果。
原生獨立安裝或無法確認來源的安裝只提供手動更新說明，不嘗試覆蓋。

更新期間會顯示安裝／版本驗證進度，重新進入頁面會在同一瀏覽器分頁內恢復追蹤。
完成後自動重新檢查登入狀態與刷新模型清單；模型連線測試仍由使用者自行點擊。
更新失敗、逾時或 API 重啟後，先重新檢查實際版本，不會自動重送安裝。
錯誤會區分網路、檔案占用／權限與 Node.js 版本問題，不顯示 npm 原始輸出。

正在執行的任務、討論、登入與 probe 會阻擋更新；更新時 Worker 保留排隊任務，暫停領取新任務。
請先結束自己另外開啟的 CLI 終端，避免 Windows 鎖住執行檔。
首次套用這次程式變更，需重啟 API 與 Worker；兩者須使用相同 `DevFlow:CliControlRoot`。
一般 npm CLI 更新完成後，API 與 Worker 會重新辨識 CLI 路徑，不需再次重啟。

## 討論、規則與 AI 驗收

登錄專案的「選擇資料夾」預設開啟 Windows 原生資料夾選擇器，選好後自動填入完整路徑，取消則保留原本輸入。API 需在目前登入的 Windows 桌面執行；選擇器使用獨立 STA 執行緒及目前前景視窗作為擁有者，不透過 PowerShell 開啟。關閉頁面或取消請求會關閉原生視窗，三分鐘未完成會逾時。也可明確切換「改用頁面瀏覽」，瀏覽 API 所在電腦的本機磁碟與資料夾。

- 左側「討論」可選專案與模型、保存對話、整理成一至八個任務草稿。建立討論不呼叫模型；送出訊息與整理任務會使用所選 CLI 的模型額度。
- 草稿可編輯名稱、需求、驗收條件、規則與流程；待釐清問題需先確認處理方式。草稿轉任務預設不自動規劃或實作，來源討論與任務互相保留連結。重送同一草稿不會重複建立任務。
- 左側「規則庫」提供新增、編輯與封存，以及各專案的預選規則。任務與討論保存規則版本快照，規則庫後續修改不影響既有任務。
- 新增任務可填本次專屬規則、選擇 TDD 或一般實作，並設定允許編輯的路徑。檔案使用 Repository 相對路徑；資料夾以 `/` 結尾，不使用萬用字元。留空代表整個 Repository。
- 流程範本支援省略 AI 規劃、零至兩個依序執行的 AI 驗收階段，以及零至兩次自動修正。每個驗收階段可選供應商與模型，最終人工驗收與本地合併核准仍保留。
- 模型欄位提供下拉選擇、CLI 預設與自訂模型 ID。Codex 會從官方 CLI app-server 讀取模型目錄並短暫快取；Gemini／Claude 顯示 CLI 別名或設定的模型選項。清單不代表帳號權限已驗證。
- 各階段模型旁可設定推理程度，包含專案預設、討論、規劃、實作、AI 驗收與連線測試。Codex 從模型目錄讀取每個模型支援的等級，透過單次執行的 `model_reasoning_effort` 設定傳入；Claude 使用 `--effort`，實際等級仍可能受模型、CLI 版本及組織限制調整。Gemini 目前沿用 CLI 的模型推理設定。
- 未指定模型／無推理能力資料時沿用 CLI 預設。切換模型會清除本次推理選擇，避免帶到不同模型；舊任務沒有此欄位時沿用 CLI 設定。設定與執行／驗收快照會保存推理程度，修改專案預設不會改寫既有任務。更新後需重啟 API 與 Worker。
- AI 連線頁會自動檢查登入狀態；登入流程結束後重新確認，已登入時隱藏登入控制。Gemini 等 CLI 無法免額度判斷登入狀態時會標示「無法確認」，可手動重新檢查或登入。
- AI 驗收讀取確切 commit 的完整差異、核准計畫、規則及獨立測試摘要；報告與 CLI 輸出在執行紀錄中保留。修正使用新 Run，最終成果差異保留原始基底，涵蓋所有修正。

更新後需重新啟動 API 與 Worker；正式部署需先建置前端再建置 API。既有 SQLite 資料會保留，啟動時自動建立規則與討論資料表。
討論使用 API 所在主機的 CLI 與唯讀 worktree，不依賴 Worker；實作與 AI 驗收需要 Worker。API 中斷的討論會保留已送出的訊息，需明確重試，不會自動重送模型呼叫。
文字規則由模型理解與人工驗收；TDD 有測試證據檢查，允許編輯範圍由 Git 檔案差異檢查。範圍檢查不是作業系統的檔案存取隔離。

React + TypeScript + Vite。API 使用 .NET，AI 執行由 Worker 呼叫官方 CLI。

## 儲存空間管理

左側「儲存空間」顯示 API 實際使用的 SQLite 資料庫（含 WAL／SHM）、執行產物及工作副本路徑、檔案數與容量。容量為檔案內容大小，未包含來源 Repository 或共用 Git 物件；掃描無法完整讀取時會顯示部分容量。

可依 7／30／90／180 天或所有已結束任務產生清理預覽。保留天數依任務最後活動與執行完成時間計算；僅納入已合併或已取消、所有 Run 均已結束的任務。預覽五分鐘有效，每次最多確認 100 項。此版由使用者手動預覽與確認，不會自動清理。

確認時會重新核對任務版本與檔案清單。有未提交／未追蹤檔案、符號連結／junction、子模組或其他 Git Repository 的工作副本會保留。工作副本僅透過不含 force 的 `git worktree remove` 移除，保留 DevFlow 分支與成果 commit；不處理不符合設定目錄或無法歸屬到任務的資料。

清理執行產物會保留各階段 `evidence.json`，因此任務中的測試摘要仍可查看；SQLite 內的任務、規則、討論與 AI／人工驗收紀錄保留。原始日誌與 TRX 報告刪除後無法由 DevFlow 還原。清理結果逐項顯示，並寫入任務事件；中斷連線後已確認的清理仍會完成結果紀錄。

## 開發啟動

需要 Node.js 24.15 以上（或 26 以上）與 .NET 10 SDK。
在三個 PowerShell 視窗分別執行：

```powershell
# 視窗 1：API
cd C:\Gary\SideProject\DevFlow\backend
dotnet run --project src\DevFlow.Api
```

```powershell
# 視窗 2：Worker
cd C:\Gary\SideProject\DevFlow\backend
dotnet run --project src\DevFlow.Worker
```

```powershell
# 視窗 3：React（首次啟動或套件有更新時先 npm ci）
cd C:\Gary\SideProject\DevFlow\frontend
npm ci
npm run dev
```

開啟 http://127.0.0.1:5173。Vite 代理 `/api` 至 http://127.0.0.1:5180。
API／Worker 需使用相同資料庫、Artifact 路徑與 Windows 使用者。
如需改變 API 埠，在前端視窗設定 `$env:DEVFLOW_API_URL='http://127.0.0.1:其他埠'`。
官方登入與 probe 維持本機來源檢查，開發代理只重寫符合 Vite 同源位址的 Origin。

## 功能

| 畫面 | 功能 |
| --- | --- |
| 總覽 | 任務統計、待人工處理事項、最近任務 |
| 任務工作台 | 專案／狀態篩選、搜尋、建立任務、指定規劃／實作 AI 與模型 |
| 專案 | 登錄 Repository、基底分支、測試參數、AI 預設 |
| AI 連線 | Codex、Gemini、Claude Code 安裝與登入狀態、官方登入／取消、診斷、模型連線測試 |
| 計畫 | 編輯、歷史版本、AI 規劃、指定版本核准 |
| 執行 | Run 切換、Worktree／commit、TDD 階段證據、事件、Artifact 分頁 |
| 審核 | diff、核准／退回、修正重試、本地合併預覽與歷史、中斷核對 |

每五秒輪詢；頁面隱藏時暫停。CLI log 在階段結束後保存。
React state 保留輪詢與頁籤切換中的編輯；離開任務會清除未保存的草稿。
計畫保存使用 expectedVersion，衝突會保留輸入，不自動重送。
歷史計畫唯讀；未保存的計畫不能核准。審核需先讀取 diff，傳送精確 Run、commit 與版本。
合併先顯示目標 HEAD，只做本地 fast-forward，不推送遠端。
Artifact 使用伺服器游標及文字顯示；較晚完成的旧請求會被丟棄。

首頁載入與輪詢不呼叫模型。自動規劃、核准實作及明確按下連線測試可能使用額度。
Claude 可先保存備用設定；憑證由官方 CLI 管理。

Codex 推理選單顯示低（low）、中（medium）、高（high）、極高（xhigh）、Ultra（ultra），
依所選模型回傳的能力縮減選項。若 CLI 目前預設模型缺少能力資料，允許手動指定這五項，
並標示支援狀態待 CLI 驗證。CLI 預設選項仍可保留原設定；連線測試才會實際驗證選定組合。

模型選單依可解析的版本號由新到舊排列（例如 GPT 6.1 在 GPT 6、GPT 5.6 之前），
同版本保留 CLI 目錄順序；未標版本的別名／自訂 ID 放在版本模型之後。
這是版本排序，不是模型發布日期或能力排名。CLI 預設選項固定置頂，自訂輸入入口置底。
目前設定中的模型若未提供目錄能力資料，畫面會顯示提示；已登入不代表指定模型可執行。

## 正式部署

先建置前端，再建置或發布 API；API 會將 `dist` 複製至發布目錄的 `ui`。

```powershell
cd C:\Gary\SideProject\DevFlow\frontend
npm ci
npm run build
cd ..\backend
dotnet publish src\DevFlow.Api -c Release
```

正式環境由 API 提供首頁與 `/assets`，開啟 http://127.0.0.1:5180。
`npm run preview` 只預覽建置版的靜態畫面，完整操作請啟動 API 或使用 `npm run dev`。
`dist`、`node_modules`、套件快取與測試輸出不納入版本控制。

## 驗證與程式結構

```powershell
npm run check
npm test
npm run build
```

`src/App.tsx` 管理導覽，`src/components` 分別處理任務、專案、登入、計畫、執行與審核。
`src/api.ts` 封裝請求與取消，`src/hooks.ts` 管理輪詢，`src/domain.ts` 保存流程規則。
Vitest + React Testing Library + jsdom 驗證操作、輪詢保留草稿、版本衝突、模型繼承、
精確成果審核、Unicode 分頁、文字注入防護及開發代理來源限制。
後端測試驗證 React 正式 JS／CSS 服務與既有調度行為。
測試使用替身；實際瀏覽器排版、原生登入視窗及真實模型端到端仍需人工驗收。
