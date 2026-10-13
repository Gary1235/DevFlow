# DevFlow 後端 v0.1

以 .NET 10 建立的本機開發調度後端。Web UI 尚未實作，目前可透過 HTTP API 操作。

## 已實作

- 專案登錄、任務提交與查詢、計畫提交／修改、人工核准計畫、取消任務。
- SQLite WAL 儲存；任務與事件以交易提交；版本檢查防止覆蓋並行更新。
- 原子領取任務，每次執行建立獨立 Run 與 Git Worktree。
- 獨立 Worker、Codex CLI Adapter、程序逾時／取消、執行紀錄與 TRX 證據。
- TDD 流程：Baseline → Red → Green → Refactor → 待審核。
- 單一 Worker 檔案鎖；重啟後將中斷 Run 標為失敗，保留 Worktree 與歷史。

## 專案結構

```text
src/
  DevFlow.Api/             HTTP API
  DevFlow.Worker/          背景執行服務
  DevFlow.Core/            模型、狀態規則、TDD 調度與介面
  DevFlow.Contracts/       HTTP 請求型別
  DevFlow.Infrastructure/  SQLite、Git、程序、TRX 與驗證執行器
  DevFlow.Runners/         Codex CLI Adapter
tests/
  DevFlow.Tests/           領域、SQLite、HTTP、調度與基礎設施測試
docs/tdd/                 開發時保存的 Red／Green 測試紀錄
```

## 建置與測試

需要 .NET 10 SDK；Git 測試需要 Git，程序取消測試在 Windows 使用 PowerShell。
請從 DevFlow 根目錄執行：

```powershell
dotnet restore DevFlow.slnx
dotnet build DevFlow.slnx -c Release -m:1 /nr:false
dotnet test DevFlow.slnx -m:1 /nr:false
```

SDK 使用 global.json 的版本及 latestFeature roll-forward。測試使用真正的 SQLite、HTTP TestServer、
子程序及臨時 Git Repository；Agent 調用採測試替身，不消耗模型額度。

## 啟動 API

```powershell
dotnet run --project src/DevFlow.Api
```

預設位址為 http://127.0.0.1:5180，健康檢查為 GET /health。
API 與 Worker 都要從 DevFlow 根目錄啟動，或使用相同的絕對資料庫設定。
目前為本機可信使用者版本，沒有帳號權限與遠端部署設定。

## 啟動 Worker

先安裝並登入 Codex CLI，設定原生執行檔路徑。Windows npm 安裝產生的 codex.ps1／codex.cmd
是啟動包裝器；此版本需指定套件內的 codex.exe，Linux/macOS 可指定原生 codex 路徑。
不要將憑證寫入 Repository。

```powershell
$env:DevFlow__CodexExecutable = 'C:\path\to\codex.exe'
$env:DevFlow__DatabasePath = 'C:\path\to\DevFlow\.data\devflow.db'
$env:DevFlow__ArtifactsRoot = 'C:\path\to\DevFlow\.data\artifacts'
$env:DevFlow__WorktreeRoot = 'C:\path\to\DevFlow.Worktrees'
$env:DevFlow__ExecutionTimeoutSeconds = '1800'
dotnet run --project src/DevFlow.Worker
```

API 也使用相同的 DevFlow__DatabasePath。WorktreeRoot 必須在被調度的 Repository 外。
未設定 CodexExecutable 時 Worker 會明確停止啟動。
每個階段使用獨立 codex exec，透過 stdin 提供需求與核准計畫，使用 workspace-write sandbox。
依據 [OpenAI Docs：非互動模式](https://learn.chatgpt.com/docs/non-interactive-mode) 建立 Adapter；
已核對本機 CLI help，但尚未執行真實模型任務。

## 操作範例

```powershell
$base = 'http://127.0.0.1:5180'
$project = Invoke-RestMethod "$base/api/projects" -Method Post -ContentType 'application/json' -Body (@{
    name = 'MyBackend'
    repositoryPath = 'C:\repos\MyBackend'
    baseBranch = 'main'
    validationArguments = @('test', 'MyBackend.sln', '-m:1', '/nr:false')
} | ConvertTo-Json)

$task = Invoke-RestMethod "$base/api/tasks" -Method Post -ContentType 'application/json' -Body (@{
    projectId = $project.id
    title = '新增任務查詢 API'
    description = '依 ID 回傳任務，找不到時回傳 404。'
    acceptanceCriteria = '有效 ID 回傳 200；未知 ID 回傳 404；既有測試保持通過。'
} | ConvertTo-Json)

Invoke-RestMethod "$base/api/tasks/$($task.id)/plan" -Method Post -ContentType 'application/json' -Body (@{
    text = '先新增行為測試，再實作查詢服務與端點，最後重構並驗證。'
} | ConvertTo-Json)
Invoke-RestMethod "$base/api/tasks/$($task.id)/approve-plan" -Method Post
Invoke-RestMethod "$base/api/tasks/$($task.id)"
Invoke-RestMethod "$base/api/tasks/$($task.id)/runs"
Invoke-RestMethod "$base/api/tasks/$($task.id)/events?after=0"
```

目前 /plan 接收人工提供的計畫，不會自動呼叫 CLI 產生規劃。
validationArguments 是 dotnet test 的參數；logger 與 results-directory 由系統加入。
事件一次最多回傳 1000 筆，使用最後一筆 sequence 作為下一次 after。

## HTTP 接口

| Method | Path | 用途 |
| --- | --- | --- |
| GET | /health | 健康檢查 |
| GET、POST | /api/projects | 列出／登錄專案 |
| GET | /api/projects/{id} | 專案詳情 |
| GET、POST | /api/tasks | 列出／提交任務 |
| GET | /api/tasks/{id} | 任務詳情 |
| POST | /api/tasks/{id}/plan | 提交或修改計畫 |
| POST | /api/tasks/{id}/approve-plan | 核准並排隊 |
| POST | /api/tasks/{id}/cancel | 取消任務 |
| GET | /api/tasks/{id}/runs | 執行歷史 |
| GET | /api/tasks/{id}/events?after=0 | 重播狀態與階段事件 |

輸入錯誤回傳 400，找不到資料回傳 404，狀態或版本衝突回傳 409。

## TDD 驗證與保存方式

1. Baseline 必須成功，避免原本已失敗的測試被當成新增功能的 Red 證據。
2. Red 必須有非零結束碼、实际執行的測試及 TRX 失敗測試；編譯錯誤不算。
3. Green 與 Refactor 必須成功；Baseline 通過與 Red 失敗的測試 ID 必須出現在通過清單。
4. 驗證完成才建立成果 Commit，任務進入 AwaitingReview。

Artifact 目錄以 Run ID 命名，保存階段 prompt、Agent JSONL、stderr、驗證輸出、TRX 與 evidence.json。
程序輸出每個 stream 最多保留 4 MiB，超出會標記截斷。Worktree 保留供人工檢查。
TRX 與測試 ID 驗證能擋下刪除／略過已失敗測試，但無法判定測試斷言是否被弱化；仍需審核差異。

取消會在背景輪詢後傳到執行中的程序，嘗試終止整個程序樹。
主機或 Worker 非正常退出可能留下孤立 CLI 程序；重啟不會自動重跑，應先確認程序已停止。
Worktree 隔離 Git 變更，程序權限則由 CLI sandbox 與主機設定控制。

## 後續功能

- CLI 自動規劃、澄清問題與計畫版本歷史。
- 審核／退回／重試接口、成果差異與 Artifact 讀取接口、核准後合併。
- SignalR 即時推送、列表分頁、資料库 migration。
- Gemini Runner、前後端步驟依賴、整合驗證、前端預覽與 MCP。

目前版本完成從人工計畫核准到後端自動 TDD 執行並進入待審核的核心。
