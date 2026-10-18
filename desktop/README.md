# DevFlow Desktop 0.1 — Windows x64

Electron 桌面版沿用 React 工作台及 .NET API／Worker。開啟應用後自動啟動兩個隱藏的後端程序，檢查就緒才顯示工作台，不需要另開 PowerShell 或 Vite。

## 開發與建置

需要 Windows x64、Node.js（沿用 frontend 的版本要求）、.NET 10 SDK，以及已安裝的 frontend 依賴。

```powershell
cd C:\Gary\SideProject\DevFlow\frontend
npm ci
cd ..\desktop
npm ci
npm run dev
```

`dev` 會先建置 React，再發布 API／Worker 的 Windows x64 自包含執行檔。首次發布需要下載 .NET runtime 套件。前端或後端改動後重新執行 `npm run dev`；只有桌面外殼改動可使用 `npm start`。

```powershell
npm test
npm run smoke
npm run pack
npm run dist
npm run smoke:packaged
```

- `test`：通知去重、導覽限制、服務啟動／驗證／停止順序。
- `smoke`：實際啟動 Electron、React 與兩個 .NET 服務，使用暫存空資料庫，確認 API 驗證、關窗後背景執行及正常停止；不呼叫模型，截圖保存在 `release/smoke.png`。
- `pack`：產生 `release/win-unpacked/DevFlow.exe`，可先檢查解壓版。
- `dist`：產生 `release/DevFlow-0.1.0-Setup.exe`。使用每位使用者的 NSIS 安裝方式，可選安裝目錄，解除安裝保留資料。
- `smoke:packaged`：對 `release/win-unpacked/DevFlow.exe` 執行同一套實際啟動驗證，確認打包後仍能定位前後端資源。

API 和 Worker 隨安裝包附帶 .NET runtime；不需要另外安裝 .NET runtime。但開發專案的測試仍需要對應 SDK；Git、Node（若所選 CLI 需要）與官方 AI CLI 仍由使用者安裝。沒有 AI CLI 時可開啟工作台查看安裝狀態；模型任務需要先安裝及登入。第一版尚無自動安裝 CLI、桌面程式自動更新或程式碼簽章設定。

## 使用方式

關閉視窗會隱藏至系統匣，任務繼續執行；雙擊系統匣圖示或再次啟動 DevFlow 可還原視窗。系統匣可開啟資料目錄與診斷紀錄。選擇「停止服務並結束」並確認後，先通知 Worker 取消工作、清理 CLI／測試子程序，再停止 API。正常停止逾時時可繼續等待，或明確選擇強制停止。

收到停止通知的執行中 Run 會依原有流程記錄失敗或中斷；排隊任務與歷史保留，不自動重跑失敗任務。強制關閉或主機斷電後，下一次 Worker 啟動會沿用原有中斷恢復流程；仍應確認外部 CLI／Git／npm 沒有殘留程序。關閉應用前應等待 CLI 更新完成。

計畫待核准、成果待審核、執行／規劃失敗與成果合併時會發出桌面通知，點擊可開啟對應任務。首次讀取歷史不發通知。通知能否顯示仍受 Windows 通知設定影響。

## 資料與隔離

預設資料目錄為 `%APPDATA%\DevFlow\data`，其中包含 `devflow.db`、`artifacts/`、`worktrees/`、`cli-control/` 與 `logs/`。API／Worker 使用相同絕對路徑；日誌每次啟動覆寫，單個日誌最多保留 4 MiB。原有開發版的 `backend/.data` 不會自動匯入或覆寫。原始 Git Repository 仍在使用者登錄的位置。

若需要自訂儲存位置，可在啟動應用前設定 `DEVFLOW_DATA_DIR` 為完整路徑。請使用專用資料夾；不要讓另一組 API／Worker 同時管理同一份資料。既有資料遷移需完整備份並核對 worktree／artifact 路徑，第一版不提供自動遷移。其他 `DevFlow__*` CLI 路徑設定仍可沿用。

桌面 API 每次使用隨機 loopback 埠與新的會話密鑰，密鑰由 Electron 網路層注入，不提供給 React。外部瀏覽器不能直接操作此會話。Renderer 停用 Node、啟用 sandbox／context isolation，禁止非本機導覽及額外權限。獨立啟動網頁版時，不設定桌面密鑰即保持原有行為。

父程序意外結束時，後端透過標準輸入 EOF 請求停止；桌面版不會接管、終止或自動重啟外部啟動的後端程序。API／Worker 異常結束會顯示錯誤並停止其餘服務，修正後需重新開啟應用。
