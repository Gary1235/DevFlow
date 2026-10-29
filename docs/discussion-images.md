# 對話圖片附件

新對話、既有對話及任務內的計畫討論共用圖片操作：貼上截圖、拖曳檔案或選擇圖片。送出前顯示縮圖，可放大預覽、移除，也能只送圖片。一般文字貼上保持原行為。

只接受 PNG、JPEG、WebP，每張最多 4 MiB、每則最多 4 張、每個對話最多 20 張，圖片合計最多 20 MiB，保留 CLI 傳輸大小的空間。API 會檢查 Base64、檔案標頭、類型與大小，不接受 SVG 或由客戶端指定的磁碟路徑。圖片按伺服器產生的 ID 存在共用 artifacts 目錄的 `discussion-images/<discussionId>/<imageId>`；SQLite 只保存名稱、ID、類型及大小，不重複保存 Base64。備份對話圖片時須連同此目錄備份。現有任務日誌清理不會刪除對話附件；目前沒有自動清除附件功能。

尚未送出的圖片只存在目前頁面的輸入草稿，重新整理可能遺失；送出後才能重新載入。API 接收失敗時，網頁保留輸入；模型回覆失敗時，已保存訊息與圖片可重試。切換模型、繼續聊天或整理計畫時，會依時間順序重送此對話的圖片，並附上原訊息 ID 與名稱對照。圖片與文字都是參考資料，不代表核准操作。確認計畫時，任務保存圖片的對話 ID 與附件 ID；Worker 執行時從共用 artifacts 讀取原圖，透過既有原生圖片通道傳給實作、主管與 AI 驗收模型，不在任務 JSON 重複儲存 Base64。延續修正與整合任務保留圖片參照。圖片任務不會自動分配給只支援文字的 Antigravity 背景模式。

CLI 傳遞方式：

| CLI | 原生輸入 | 行為 |
| --- | --- | --- |
| Codex | app-server `turn/start.input` 的 `image`／data URL | 與文字一起傳入，模型不支援時由 CLI 回報錯誤 |
| Claude | stream-json user message 的 image block，base64 source | 與文字一起傳入，沿用原權限通道 |
| Gemini | ACP image content block | 先要求 `agentCapabilities.promptCapabilities.image` 為 true；未宣告就明確中止，不送文字替代 |
| Antigravity | 現有 stream-json 背景輸入只接受 text | 送出前提示切換 CLI，API 也會拒絕，避免靜默略過圖片 |

Codex 格式依 [官方 app-server 文件](https://learn.chatgpt.com/docs/app-server)。Antigravity 的 [官方 headless 文件](https://www.antigravity.google/docs/cli/headless/) 明訂目前只接受 text block，其他區塊會結束程序；本版沒有用文字路徑或跳過權限來冒充原生圖片輸入。

自動測試涵蓋原生協定封包、附件存取範圍、大小／數量限制、格式拒絕、失敗重試與貼上／拖曳／預覽／移除。這不代表每個帳號、模型及 CLI 版本都已實際看圖驗證；實際視覺回覆仍以所選 CLI 的結果為準。
