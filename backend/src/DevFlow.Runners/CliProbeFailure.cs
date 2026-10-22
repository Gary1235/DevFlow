using DevFlow.Core;

namespace DevFlow.Runners;

internal static class CliProbeFailure
{
    // Match known diagnostics, but return only fixed text. CLI output may contain credentials,
    // account details or local configuration and must not be sent to the browser verbatim.
    public static (string Code, string Message) Classify(ProcessResult result)
    {
        var errors = result.StandardError;
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var isError = root.TryGetProperty("type", out var type) && type.GetString() is "error" or "turn.failed";
                isError |= root.TryGetProperty("is_error", out var flag) && flag.ValueKind == System.Text.Json.JsonValueKind.True;
                isError |= root.TryGetProperty("event", out var eventType) && eventType.GetString() == "error";
                if (isError) errors += "\n" + root.GetRawText();
                else if (root.TryGetProperty("result", out var eventResult) && eventResult.ValueKind == System.Text.Json.JsonValueKind.Object && eventResult.TryGetProperty("error", out var resultError) && resultError.ValueKind != System.Text.Json.JsonValueKind.Null)
                    errors += "\n" + resultError.GetRawText();
                else if (root.TryGetProperty("error", out var error) && error.ValueKind != System.Text.Json.JsonValueKind.Null)
                    errors += "\n" + error.GetRawText();
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { }
        }
        bool Has(params string[] patterns) => patterns.Any(p => errors.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (Has("unexpected argument", "unknown option", "unrecognized option", "invalid value", "unsupported reasoning", "reasoning effort is not supported"))
            return ("InvalidArguments", "CLI 拒絕參數或推理程度。請確認所選模型支援該程度，或改用「CLI 預設」；若仍失敗，請檢查 CLI 版本。");
        if (Has("insufficient_quota", "quota exceeded", "exceeded your current quota", "usage limit", "credit balance is too low", "insufficient credits"))
            return ("QuotaExceeded", "CLI 回報額度不足或使用上限。請在供應商帳號確認額度與重設時間。");
        if (Has("rate_limit_exceeded", "rate limit", "too many requests"))
            return ("RateLimited", "CLI 回報請求頻率限制。請稍後再試。");
        if (Has("model_not_found", "model not found", "does not exist or you do not have access", "not supported when using", "do not have access to", "not available for your account", "not allowed to use"))
            return ("ModelUnavailable", "CLI 回報所選模型目前不可用。請先檢查 CLI 更新並重新載入模型；更新後仍失敗，再確認登入帳號與模型使用資格。");
        if (Has("not logged in", "authentication required", "authentication failed", "invalid_api_key", "invalid api key", "token has expired", "token expired", "unauthorized", "401 Unauthorized"))
            return ("AuthenticationFailed", "CLI 回報驗證失敗或憑證失效。請透過官方 CLI 重新登入，再重新檢查狀態。");
        if (Has("sandbox initialization failed", "failed to setup sandbox", "failed to initialize sandbox", "sandbox is not supported", "no sandbox backend", "docker is not running", "docker not found"))
            return ("SandboxUnavailable", "CLI 無法啟動 Sandbox。請檢查該 CLI 的本機 Sandbox 環境與所需元件。");
        if (Has("error sending request", "unable to connect", "connection refused", "connection reset", "certificate verify failed", "failed to connect", "dns error", "fetch failed"))
            return ("ConnectionFailed", "CLI 無法連線至服務。請檢查網路、代理伺服器與憑證設定。");
        if (Has("access denied", "permission denied", "os error 5"))
            return ("PermissionDenied", "CLI 回報本機存取權限不足。請確認執行 API 的 Windows 帳號可存取 CLI 的設定及暫存資料夾。");
        return result.ExitCode == 0
            ? ("UnexpectedResponse", "CLI 已結束，但未回傳完整且符合格式的測試結果，無法確認模型可執行。")
            : ("UnknownFailure", "CLI 執行失敗，目前無法辨識原因。請提供供應商、模型、推理程度及下方結束代碼，以便追查。");
    }
}
