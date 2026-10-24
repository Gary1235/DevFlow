# DevFlow v0.7

## CLI 版本檢查與更新

AI 連線頁提供 Codex／Gemini／Claude 的明確版本檢查與手動確認更新。
只管理可驗證為本機 npm 全域安裝的 CLI，套件名稱固定為 `@openai/codex`、`@google/gemini-cli`、
`@anthropic-ai/claude-code`。其他安裝來源顯示原安裝方式更新提示。
Node 與 npm JS 入口以原生程序及 ArgumentList 執行，不使用 shell 拼接命令。
檢查會查詢官方 npm registry，更新會安裝檢查結果中的精確版本、固定原安裝 prefix，
再檢查 DevFlow 實際使用的 CLI 版本；不憑 npm exit code 直接宣告成功。

接口：

- `POST /api/ai/{provider}/cli/check`：目前／最新版本、實際執行檔、可否管理、10 分鐘有效的 check ID。
- `POST /api/ai/{provider}/cli/update`：`{ "checkId": "..." }`，確認安裝與版本未改變後回傳 202 及作業 ID。
- `GET /api/ai/cli/updates/{id}`：Updating／Verifying／Completed／Failed／TimedOut。

兩個 POST 都要求 `X-DevFlow-Action: 1`、loopback 與同源來源檢查。安裝最多十分鐘，
不自動重試、不自動執行付費模型測試，不回傳 npm 原始輸出。
作業 metadata 在 API 記憶體中；API 重啟後舊作業回傳 404，需重新檢查實際版本。
非正常中斷仍需人工確認外部 npm／CLI 程序狀態，不會自動重送安裝。

API 與 Worker 透過 `DevFlow:CliControlRoot/cli-maintenance.lock` 共享執行／更新互斥。
執行中的任務、討論、登入或 probe 會阻擋更新；更新期間 Worker 在領取任務前等待，排隊任務保留。
更新結束後重新辨識 CLI。兩個程序必須使用同一控制目錄（預設 backend/.data/cli-control），
且首次部署此功能時須一併重啟 API 與 Worker。此鎖不控制 DevFlow 之外的 CLI 程序，請先關閉外部 CLI 終端。
預設辨識 Windows 使用者 npm 安裝；自訂部署可用 `DevFlow:NodeExecutable`、`DevFlow:NpmCliPath`、
`DevFlow:NpmGlobalRoot` 設定絕對路徑，NpmGlobalRoot 應指向實際的 node_modules。

以 .NET 10 建立的本機開發調度後端，附開發工作台。AI 調用全部透過本機 CLI。

## v0.7：React 前端工作台

前端新增總覽、任務搜尋／篩選、專案設定、AI 連線，以及任務計畫／執行／審核頁面。
包含計畫歷史與編輯、Run 切換、TDD 階段證據、事件輪詢、Artifact 文字分頁、成果 diff、
成果審核／重試、本地合併预覽與歷史。操作文件見 [frontend README](../frontend/README.md)。
前端改用 React + TypeScript + Vite，開發模式執行 npm run dev，入口為 http://127.0.0.1:5173。
正式部署先於 frontend 執行 npm run build，再建置／publish API，複製 dist 並提供 /assets。原有 HTTP 與 CLI 路由繼續沿用。

手動 plan 接口新增可選 expectedVersion，前端會提供它，過期編輯回傳 409 且不覆蓋計畫。
舊客戶端省略時保留既有行為；建議提供版本以避免過期內容覆蓋。
本次驗證：132 個後端測試通過；前端 React 測試與啟動方式詳見 frontend README。實際瀏覽器排版與原生登入視窗尚未驗收，
瀏覽器工具安全政策拒絕 file:// 驗證頁；不以 DOM 測試替代視覺驗收。

## v0.5：多 AI CLI 與登入

- 規劃與實作分別選擇 Codex CLI、Gemini CLI 或 Claude Code，以及模型 ID；空白代表沿用 CLI 預設。
- 專案保存預設，任務提交時解析覆寫並保存；Run 保存該階段的要求設定。
- Worker 依任務路由 Runner，Red／Green／Refactor 共用實作設定。
- Gemini 使用 sandbox、規劃 plan mode、實作 auto_edit；未安裝或執行失敗會明確失敗，不切換供應商。
- 登入使用官方 CLI 終端。Codex 支援瀏覽器／裝置碼，Gemini 在互動終端選 Google 登入。
- 登入作業互斥、15 分鐘逾時、取消、API 停止時清理所屬終端；不讀取或保存 Token。
- 免額度診斷與主動模型連線測試分開，登入終端 exit 0 只標為 AwaitingVerification，不假定登入成功。

### Claude Code 備用支援

provider ID 為 `claude`。畫面提供 Claude Code、sonnet／opus／haiku 別名與自訂模型 ID。
可先保存專案或任務設定，無須現在登入或付費；模型清單不代表帳號已取得使用權限。
不會自動登入、呼叫模型、開通訂閱或切換到 Claude 作為 fallback。
只有你提交使用 Claude 的自動任務、核准實作，或按下連線測試時才會呼叫它。

安裝官方 CLI 後，API／Worker 會尋找 PATH 或使用者的 .local/bin 原生執行檔；
也支援設定 npm JavaScript entry point。必要時在 API／Worker 兩個視窗設定：

```powershell
$env:DevFlow__ClaudeExecutable = 'C:\Users\your-user\.local\bin\claude.exe'
# 可選清單；畫面仍允許自訂模型 ID
$env:DevFlow__Models__Claude__0 = 'sonnet'
```

官方登入使用 `claude auth login`，不加入 Console API billing 參數；
免額度狀態查詢使用 `claude auth status`，只輸出登入狀態，不暴露 email 或憑證。
登入成功不代表帳號有模型使用資格，資格與額度由官方 CLI 確認。

目前工作台的正式路徑由 RoutedCliRunner／PermissionProcess 轉成 stream-json 雙向確認，
使用 --print、--model、--no-session-persistence，移除直接 Runner 的 --restricted。
規劃提供 Read／Glob／Grep／Bash，實作另提供 Edit／Write；工具請求交由 DevFlow 權限政策判斷。
直接使用 ClaudeCliRunner 的獨立模式仍採 restricted 行為，不能拿它代表工作台的實際權限。
DevFlow 政策、CLI 原生限制與 OS 權限是不同層級；工作副本不是額外的 OS sandbox。
Run 保存要求設定、prompt、Claude JSON、stderr 與 TDD 證據；非零 exit code、is_error 或不完整結果都視為失敗。

依據 [Claude Code CLI 官方文件](https://code.claude.com/docs/en/cli-reference) 與
[非互動執行文件](https://code.claude.com/docs/en/headless)。
本機 Claude Code 尚未安裝，這次僅以測試替身驗證 Adapter／登入，不進行真實模型呼叫。

從 backend 開兩個 PowerShell 視窗：

```powershell
# 第一個視窗
dotnet run --project src/DevFlow.Api
# 第二個視窗
dotnet run --project src/DevFlow.Worker
```

使用瀏覽器開啟 http://127.0.0.1:5180。畫面包含 AI 連線、專案預設、任務建立、計畫核准、
執行摘要、成果 diff、成果審核、重試與本地合併。前端原始檔位於 ../frontend；先 npm run build，才隨 API 建置部署。开发模式用 npm run dev 開啟 5173。

API／Worker 應以同一位 Windows 使用者、相同 backend 工作目錄與 CLI 路徑設定執行，才能沿用同一組官方登入。
Windows 會自動尋找 PATH 的原生 CLI 與標準 npm 安裝位置。必要時在兩個視窗設定：

```powershell
$env:DevFlow__CodexExecutable = 'C:\path\to\codex.exe'
$env:DevFlow__GeminiExecutable = 'C:\path\to\@google\gemini-cli\dist\index.js'
$env:DevFlow__NodeExecutable = 'C:\Program Files\nodejs\node.exe'
# 可選模型清單；只是選項，不保證帳號可用。畫面也允許輸入自訂模型 ID。
$env:DevFlow__Models__Codex__0 = 'your-model-id'
$env:DevFlow__Models__Gemini__0 = 'pro'
```

原生程式直接啟動；.js 透過 Node 啟動；不接受 .cmd／.ps1 作為非互動 Runner。
登入畫面會在 API 所在的 Windows 桌面開啟可見終端，不適用無桌面的 Windows Service。
Gemini 完成 Google 登入後輸入 /quit，再按連線測試；退出 Gemini 終端不等於登出。
取消登入不刪除 CLI 已保存的凭證，已開啟的瀏覽器頁面需自行關閉。
未安裝的 CLI 顯示未安裝；此版不自動安裝或更新 CLI。

| Method | Path | 用途 |
| --- | --- | --- |
| GET | /api/ai/providers | CLI 安裝狀態與模型目錄，modelDetails 含推理選項；Codex 由 app-server 讀取並快取，其他 CLI 使用別名／設定清單 |
| GET | /api/storage | 實際資料路徑、資料庫／執行產物／工作副本容量，需要本機同源動作標頭 |
| POST | /api/storage/preview | 依 olderThanDays、artifacts、worktrees 產生五分鐘有效的清理預覽，不刪除資料 |
| POST | /api/storage/cleanup | 以 previewId 與 keys 確認清理，最多 100 項，重新核對狀態與路徑並保留摘要紀錄 |
| POST | /api/projects/pick-folder | 在 API 的 Windows 桌面開啟原生資料夾選擇器；選擇回傳 path，取消回傳 null，需要本機同源動作標頭 |
| GET | /api/ai/{provider}/diagnostics | CLI 版本與免額度登入診斷 |
| POST | /api/ai/{provider}/probe | 明確呼叫模型的連線測試，使用模型額度；選擇可含 reasoningEffort（null 沿用 CLI 預設） |
| POST | /api/ai/{provider}/login | 開啟官方登入終端；body 為 {"mode":"browser"} 或 Codex device |
| GET | /api/ai/logins/{id} | 登入作業狀態 |
| POST | /api/ai/logins/{id}/cancel | 取消所屬登入終端 |
| PUT | /api/projects/{id}/ai-defaults | 保存預設；提供 expectedVersion 與 aiDefaults |

login／probe／cancel 需要 X-DevFlow-Action: 1、自 localhost 存取，瀏覽器需同源。
登入作業僅保存於 API 記憶體；API 重啟後舊 Session ID 回傳 404。
Gemini 缺少統一的免額度登入查詢命令，所以 diagnostics 的 LoginState 為 Unknown；probe 成功才顯示 VerifiedByExecution。
CLI 診斷不回傳原始錯誤／憑證；只回傳固定訊息。詳細任務 log 仍保存於本機 Artifact。

建立專案的 aiDefaults，以及更新預設的格式：

```json
{"planning":{"provider":"gemini","model":"pro"},"implementation":{"provider":"codex","model":null}}
```

建立任務可以省略覆寫以沿用專案預設，或提供：

```json
{"planning":{"provider":"gemini","model":"flash"},"implementation":{"provider":"codex","model":"your-model-id"}}
```

省略整個階段代表繼承；明確選 provider 並將 model 設 null 代表使用該 CLI 預設。
舊任務沒有 AI 欄位時仍使用 Codex 預設。既有任務不會因專案預設更新而改變，重試沿用任務設定。
Run.Selection 與 *-selection.json 是要求的 AI／模型，不代表已解析的實際模型。
沿用 CLI 預設及模型別名可能因 CLI 設定／路由而改變；DevFlow 不主動 fallback，但 CLI 自身可能路由其他模型。
原始 CLI JSON 保留其回報資訊，尚未提供跨 CLI 統一的實際模型摘要。

官方依據：[Codex CLI](https://learn.chatgpt.com/docs/cli/reference)、
[Gemini 認證](https://geminicli.com/docs/get-started/authentication/)、
[Gemini 參數](https://geminicli.com/docs/cli/cli-reference/)、
[Gemini Sandbox](https://geminicli.com/docs/cli/sandbox/)。

驗證：129 個自動測試通過；登入／模型呼叫採替身。Codex 本機版本與登入狀態已確認；
本次 Desktop 工具環境中的模型執行仍受存取拒絕限制，Gemini CLI 尚未安裝。
瀏覽器連到工具啟動的本機 API 逾時，UI 的實際瀏覽器操作與真實跨 AI TDD 尚未完成驗收。
若系統拒絕停止登入終端，Session 會標為 CleanupFailed 並保留供應商鎖定；請手動關閉該終端後重查。

## 已實作

- 專案登錄、任務提交與查詢、計畫提交／修改、人工核准計畫、取消任務。
- SQLite WAL 儲存；任務與事件以交易提交；版本檢查防止覆蓋並行更新。
- 原子領取任務，每次執行建立獨立 Run 與 Git Worktree。
- 獨立 Worker、Codex CLI Adapter、程序逾時／取消、執行紀錄與 TRX 證據。
- TDD 流程：Baseline → Red → Green → Refactor → 待審核。
- 單一 Worker 檔案鎖；重啟後將中斷 Run 標為失敗，保留 Worktree 與歷史。
- 人工審核核准／退回、審核歷史、版本衝突檢查，以及失败／退回任務重試。
- 成果 diff 以保存的基底與成果 commit 產生；退回重試沿用成果 commit 與修正意見。
- 核准後 fast-forward 合併、合併歷史、同 Repository 互斥，以及中斷後狀態核對。
- Codex CLI 唯讀自動規劃、計畫版本歷史、版本核准與規劃 commit 保存。
- Run Artifact 清單、log／JSON／TRX 文字分頁與各 TDD 階段驗證摘要。

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
請從本地專案的 backend 目錄（`C:\Gary\SideProject\DevFlow\backend`）執行：

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
API 與 Worker 都要從 backend 目錄啟動，或使用相同的絕對資料庫及 Artifact 設定。
目前為本機可信使用者版本，沒有帳號權限與遠端部署設定。

## 啟動 Worker

先安裝並登入要使用的 CLI；下面是 Codex 的設定範例。Windows npm 安裝產生的 codex.ps1／codex.cmd
是啟動包裝器；此版本需指定套件內的 codex.exe，Linux/macOS 可指定原生 codex 路徑。
不要將憑證寫入 Repository。

```powershell
$env:DevFlow__CodexExecutable = 'C:\path\to\codex.exe'
$env:DevFlow__DatabasePath = 'C:\path\to\DevFlow\.data\devflow.db'
$env:DevFlow__ArtifactsRoot = 'C:\path\to\DevFlow\.data\artifacts'
$env:DevFlow__WorktreeRoot = 'C:\path\to\DevFlow.Worktrees'
$env:DevFlow__ExecutionTimeoutSeconds = '0' # 0 不設任務期限；正數可自訂秒數
dotnet run --project src/DevFlow.Worker
```

API 也使用相同的 DevFlow__DatabasePath 與 DevFlow__ArtifactsRoot。WorktreeRoot 必須在被調度的 Repository 外。
Worker 自動尋找或使用設定的 CLI；所有供應商都不可用時明確停止啟動。
Codex 正式權限路徑每個階段使用獨立 app-server thread，傳入需求與核准計畫；規劃使用 read-only sandbox，實作使用 workspace-write sandbox。預設註冊實驗性 dynamic tools，提供主程序的結構化檔案操作與已儲存驗證設定；舊 CLI 可用 `DevFlow__StructuredToolsEnabled=false` 關閉此擴充。其他供應商仍沿用各自原生權限協定。
依據 [OpenAI Docs：非互動模式](https://learn.chatgpt.com/docs/non-interactive-mode) 建立 Adapter；
已核對本機 CLI help。真實 CLI 驗證工具已加入，但在本次 Desktop 工具環境中，
CLI 啟動回傳 Windows 存取拒絕；尚未完成真實模型的規劃／實作驗證。

## 真實 CLI 驗證工具

```powershell
dotnet run --project tools/DevFlow.LiveCheck -c Release -- --run-synthetic-fixture 'C:\path\to\codex.exe'
```

這個命令會使用模型額度。它在 backend/.test-work 下建立全新的測試 Repository、SQLite、
Worktree 與 Artifact；只針對 GreetingService.Greet 的 trim 行為執行規劃與 Baseline → Red → Green → Refactor。
測試資料的計畫會自動核准，正式任務仍使用原有人工核准接口。
成功停在 AwaitingReview，保存 summary.json 與 result.diff，供人工審核；不會自動合併。
失敗回傳非零 exit code，保存任務狀態與 Run 錯誤；CLI log 位於 artifacts。
每次使用獨立測試目錄，保留證據；不修改 DevFlow source 或既有 Repository。
若明確設定 NUGET_PACKAGES，測試資料只從該本機快取還原，需要先具備測試套件。
這個工具直接執行調度器，不包含 HTTP API、Worker hosted loop 與人工審核／合併的端到端驗證。
本次環境結果詳見 [CLI 驗證紀錄](docs/tdd/live-cli-2026-10-14.md)。

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
$current = Invoke-RestMethod "$base/api/tasks/$($task.id)"
Invoke-RestMethod "$base/api/tasks/$($task.id)/approve-plan" -Method Post -ContentType 'application/json' -Body (@{
    expectedVersion = $current.version
    planRevision = $current.plan.revision
} | ConvertTo-Json)
Invoke-RestMethod "$base/api/tasks/$($task.id)"
Invoke-RestMethod "$base/api/tasks/$($task.id)/runs"
Invoke-RestMethod "$base/api/tasks/$($task.id)/events?after=0"
```

/plan 接收人工提供的計畫。自動規劃使用 autoPlan 或 generate-plan，詳見下節。
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
| GET、POST | /api/tasks/{id}/reviews | 列出／提交審核決策 |
| GET | /api/tasks/{id}/reviews/{reviewId} | 審核紀錄 |
| POST | /api/tasks/{id}/retry | 失敗或退回後重新排隊 |
| GET | /api/runs/{id} | 單次執行詳情 |
| GET | /api/runs/{id}/diff | 基底與成果 commit 的差異 |
| GET | /api/projects/{id}/git-state | 目前分支、HEAD 與工作目錄是否乾淨 |
| POST | /api/tasks/{id}/merge | 合併指定已核准成果 |
| GET | /api/tasks/{id}/merges | 合併嘗試歷史 |
| POST | /api/tasks/{id}/merge/reconcile | 核對中斷合併的實際結果 |
| POST | /api/tasks/{id}/generate-plan | 將 CLI 規劃加入佇列 |
| GET | /api/tasks/{id}/plans | 計畫版本歷史 |
| GET | /api/runs/{id}/artifacts | 列出該 Run 的保存檔案 |
| GET | /api/runs/{id}/artifacts/{path}?offset=0&limit=65536 | 分頁讀取文字紀錄 |
| GET | /api/runs/{id}/validations | TDD 階段的驗證摘要 |

輸入錯誤回傳 400，找不到資料回傳 404，狀態或版本衝突回傳 409。

## CLI 自動規劃

建立任務時加上 `"autoPlan": true`，即可進入 PlanningQueued；
省略時保留手動規劃方式。既有待規劃、待確認或規劃失敗任務，
可呼叫 generate-plan 並提供目前 expectedVersion，回傳 202。

```json
{"expectedVersion": 0}
```

Worker 交替優先處理規劃與實作，規劃建立獨立 Run／Worktree。
Codex 以 read-only sandbox 與 JSON Schema 產出計畫文字，保存 prompt、JSONL、stderr 及結構化結果。
Orchestrator 再檢查 Worktree 的 HEAD 與檔案變更，成功後進入 AwaitingApproval。
規劃失敗進入 PlanningFailed，可重新規劃或手動提交計畫；取消与重啟恢復皆保留 Run 歷史。

approve-plan 現在必須提供任務 expectedVersion 與 planRevision：

```json
{"expectedVersion": 3, "planRevision": 1}
```

這是核准接口的變更，舊的空 body 呼叫需更新。核准後才排入實作。
自動規劃保存分析時的 baseCommit，實作從這個 commit 開始；
手動修改自動計畫保留該 commit，退回重試優先沿用被退回成果。
手動起始計畫沒有分析 commit 時，仍從專案 baseBranch 建立 Worktree。
計畫中的假設與未解問題需由人工確認或修改；澄清問題接口仍待擴充。
既有資料庫啟動時補建 Plans 表並保存目前計畫為 Legacy，已存在的歷史不覆蓋。

## 執行紀錄與驗證查詢

先取得 Run ID，使用 artifacts 接口列出檔案，再讀取相對路徑。
文字頁回傳 path、text、offset、nextOffset、hasMore；後續請直接使用 nextOffset，
游標採 UTF-16 字元位置並保持 Unicode 代理字元對完整。
每頁 limit 為 1～65536，單個文字檔最多顯示 16 MiB。
僅支援 log、txt、json、jsonl 與 trx；拒絕越界路徑、symlink 與 junction。
檔案寫入中或證據不完整時回傳 409，可稍後重試。

validations 回傳 Baseline／Red／Green／Refactor 的 exitCode、total、failed 與 evidencePath。
CLI 完整 log 於各階段程序結束後保存；執行中的活動摘要與部分 CLI 輸出由轉接器回報，
工作台透過 API 輪詢讀取。尚未使用 SignalR。全域待確認由 GET /api/permissions/pending 提供。

## 人工審核與重試

審核請求必須提供最新 Run 的 `runId`、`resultCommit` 與目前任務的 `expectedVersion`。
decision 使用 `Approve` 或 `Reject`；退回必須提供非空白 comments。
審核與狀態更新以同一個 SQLite 交易保存，並行審核只有一個能成功。

```json
{
  "runId": "取得 runs 接口回傳的 ID",
  "resultCommit": "取得 Run 的成果 commit",
  "expectedVersion": 4,
  "decision": "Reject",
  "comments": "補上未知 ID 回傳 404 的行為"
}
```

Approve 將任務標為 Approved，之後可透過 merge 接口合併。
Reject 將任務標為 Rejected，保留修正意見與成果 commit，不立即執行。
呼叫 retry 時傳入最新 expectedVersion，可附上 instructions。
退回任務可直接沿用審核意見；失敗任務首次重試需提供修正指示。
新 Run 在新的 Worktree 執行，退回任務從上次成果 commit 繼續，舊 Run 與審核歷史不覆蓋。

差異接口讀取不可變 commit，不依賴目前工作目錄的修改，並停用外部 diff driver。
超過 4 MiB 的差異回傳 409，避免將截斷內容當成完整成果。
既有資料庫啟動時會補建 Reviews 表；舊任務 JSON 與既有狀態數值保持相容。

## 核准後合併

先讀取 git-state 與最新任務版本，再呼叫 POST /api/tasks/{id}/merge：

```json
{
  "runId": "已核准 Run 的 ID",
  "resultCommit": "已核准成果的完整 commit ID",
  "expectedTargetCommit": "git-state 回傳的 headCommit",
  "expectedVersion": 5
}
```

目標 Repository 必須位於專案設定的根目錄、已切換到設定的 baseBranch、工作目錄乾淨，
且 HEAD 與 expectedTargetCommit 相同。只有最新已核准的 Run 與成果 commit 可合併。
直接合併只允許 fast-forward。分歧時可呼叫 `/api/tasks/{id}/integrate`，傳入與合併相同的版本、Run、成果與目標 commit 欄位，建立隔離整合、重驗證及重新審核的新任務；原成果與原副本保留。
使用 --no-autostash 與 --no-overwrite-ignore，保護本機未提交與被忽略的檔案。
詳見 [Git merge 官方文件](https://git-scm.com/docs/git-merge)。

SQLite 先保存 Pending 合併紀錄及 Merging 任務狀態，同一 Repository 只能有一個未結束合併。
已知未合併的失敗回傳 409，保留失敗紀錄並恢復 Approved，可在修正環境後重試。
成功後保存成果 commit 並標記 Merged；重複提交同一成功請求會回傳既有結果。
合併不會推送到遠端。

Git 與 SQLite 無法共用同一個交易。若程序中斷或結果無法確認，回傳 503 並保留 Pending／Uncertain 紀錄，
任務維持 Merging，防止直接重跑。reconcile 只讀取 Git 狀態，不重新合併：

- 已核准成果是目標 HEAD 的祖先：記錄成功。
- HEAD 仍是原本的目標 commit，且未包含成果：記錄未合併，恢復 Approved。
- 分支錯誤、Git 操作未結束或其他無法判定狀態：繼續阻擋，交由人工檢查。

啟動 API／Worker 不會自動重跑未確認的合併，重啟後仍可使用 reconcile。
執行中的合併禁止 reconcile；重啟後的 Pending 紀錄需等待建立時間滿兩分鐘，
且人工確認舊 Git 程序已停止，才進行核對。已保存的 Uncertain 紀錄可在確認程序停止後立即核對。
DevFlow 的 Repository 互斥僅協調此資料庫內的操作；合併期間請避免在其他工具切換分支或修改同一 Repository。
同一資料庫會統一路徑連結與 Git common-dir 身分；不同資料庫管理同一 Repository 的互斥仍待擴充。

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

- 澄清問題、超長差異分批審查、孤立 CLI 重連與跨資料庫互斥。
- SignalR 即時推送、列表分頁、資料库 migration。
- Gemini Runner、前後端步驟依賴、整合驗證、前端預覽與 MCP。

目前版本完成 CLI 自動規劃、版本核准、後端自動 TDD、紀錄查詢、人工成果審核、修正重試、本地 fast-forward 合併，以及隔離分歧整合／失敗副本恢復。設定診斷、歷史分頁與新增 API 的使用邊界見 [第二輪改善紀錄](../docs/audit-round-two.md)。

