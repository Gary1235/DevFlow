# 各 CLI 的結構化工具接入

共同執行器仍是 `backend/src/DevFlow.Infrastructure/StructuredToolExecutor.cs`。提供讀檔、列檔、字面文字搜尋、寫檔與執行已儲存驗證設定五項能力；規劃、討論及審查只提供前三項。

| CLI | 註冊方式 |
| --- | --- |
| Codex | 保留 app-server 的 dynamic tools 及 `item/tool/call`。 |
| Antigravity | 在本次任務副本 `.agents/mcp_config.json` 臨時加入獨立 MCP server，明確傳入 `--add-dir`，結束時還原。`call_mcp_tool` 只轉交名稱符合本次 server 與已提供工具的呼叫。 |
| Claude | 使用本次啟動的 `--mcp-config` JSON 與 `--strict-mcp-config`；只解除本次 DevFlow server 的入口攔截，工具實際參數仍由主程序審核。 |
| Gemini | 在 ACP `session/new.mcpServers` 傳入本次 stdio server，單次原生確認只轉交此 server 的工具。 |

## MCP 生命週期

`StructuredMcpBridge` 為每次執行建立隨機 server 名稱與 current-user-only named pipe。API／Worker 的 `--devflow-mcp` 模式只負責標準輸入／輸出 JSON-RPC 轉送，不啟動另一套 API 或 Worker，也不接收資料庫、工作區或 executable 覆寫參數。真正工具呼叫留在持有當前任務、Run、修改範圍與權限 operation 的主程序。

支援 initialize、ping、tools/list、tools/call 與取消通知。管線中斷或本次 operation 取消會取消進行中的工具；沒有另外設定網頁確認期限。Antigravity 本次 MCP 設定使用 `timeoutSeconds: -1`，避免它的預設 MCP 逾時提前結束人工確認。

只有本次註冊的工具入口可直接轉交；它不代表檔案或驗證操作已核准。讀寫與驗證仍經原執行器的路徑、連結、唯讀、修改範圍、保護設定檔、專案版本及權限紀錄檢查。外部目錄、其他 MCP 與原生 Shell 不會因此取得額外權限。結構化驗證仍不執行依賴準備。

Antigravity 的工作區 MCP 設定保留既有 server；結束或失敗時恢復原始內容。若使用者同時修改設定，只移除仍屬於本次操作的 server，保留其他變更。拒絕在符號連結的設定位置寫入。不更動任何 CLI 的全域 MCP、登入或永久允許設定。

## 啟用與限制

預設啟用 `DevFlow:StructuredToolsEnabled`，重啟 API／Worker 後的新 CLI 執行才會接入。既有 CLI 版本必須支援對應的 MCP／dynamic tools 能力。關閉此設定會保留原生工具權限轉接。模型仍可能選擇原生工具；未知 Shell 語法仍需原有解析或確認，不會因新增工具而整段放行。

自動化測試以模擬 CLI 搭配真正的 API helper 子程序、named pipe 與 SQLite 執行器，覆蓋三個新增轉接的唯讀／實作呼叫、政策稽核、越界拒絕、CLI 失敗還原、server 身分隔離及中斷取消。沒有呼叫付費模型。額外本機 Antigravity 無提問啟動測試能完成初始化，但未觀察到 MCP 探索；它不能證明模型回合一定會載入工具。各已安裝 CLI 的真實模型選用及版本相容性仍須使用者重啟後在任務中確認，不把協定 fixture 當成付費端到端實測。

參考：[Antigravity 工作區 MCP 設定](https://antigravity.google/docs/mcp)、[Claude MCP 設定](https://code.claude.com/docs/en/mcp)、[ACP session/new](https://agentclientprotocol.com/protocol/session-setup)、[MCP stdio 協定](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports)。
