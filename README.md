# DevFlow

本機 AI 開發工作台：需求討論、規劃、隔離實作、AI／人工驗收與本地 Git 合併。

在專案根目錄執行 `npm run dev`，會一起啟動網頁（5173）、API（5180）與 Worker。首次使用先在 `frontend` 執行 `npm ci`；根目錄啟動器沒有額外 npm 套件。需要 .NET 10 SDK、Node.js 與已安裝的 AI CLI。

啟動器會沿用 `backend/src/DevFlow.Api/.data/devflow.db`，保留既有任務；API 與 Worker 共用資料庫、執行紀錄、工作副本與 CLI 控制目錄。桌面版繼續使用自己的資料。按 Ctrl+C 會停止本次啟動的服務；已在執行且資料庫相同的服務會沿用，不會被關閉。紀錄位於資料庫旁的 `dev-logs`。

只啟動後端可用 `npm run dev:backend`。若啟動器發現佔用埠的是舊版 API 或不同資料庫，會停止並提示先關閉舊開發服務，不會自動合併或覆蓋資料。

- [Windows Electron 桌面版](desktop/README.md)：一鍵啟動、系統匣背景執行、任務通知、安裝包建置。
- [React 工作台](frontend/README.md)：網頁開發模式與功能操作。
- [.NET API／Worker](backend/README.md)：執行流程、CLI 設定、資料與 API。

桌面第一版支援 Windows x64，保留現有前後端架構；AI 呼叫仍由本機官方 CLI 執行。
