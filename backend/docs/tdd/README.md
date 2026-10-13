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

測試涵蓋狀態規則、驗收條件、SQLite 持久化與並行領取、HTTP 操作、TDD 守門、程序取消、Git Worktree 隔離及中斷任務保存。
模型呼叫使用測試替身；真實 Codex 模型執行尚未驗證。
