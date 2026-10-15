# DevFlow v0.7 React 開發工作台

React + TypeScript + Vite。API 使用 .NET，AI 執行由 Worker 呼叫官方 CLI。

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
| AI 連線 | Codex、Gemini、Claude Code 安裝狀態、官方登入／取消、診斷、模型連線測試 |
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
