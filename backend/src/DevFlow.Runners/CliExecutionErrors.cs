using DevFlow.Core;

namespace DevFlow.Runners;

public static class CliExecutionErrors
{
    public static InvalidOperationException Failed(string provider, string phase, ProcessResult result)
    {
        var evidence = (result.StandardError + "\n" + result.StandardOutput).ToLowerInvariant();
        string code, message;
        if (Has(evidence, "not authenticated", "authentication failed", "unauthorized", "login required", "not logged in", "token expired", "invalid_api_key", "401"))
            (code, message) = ("Authentication", "登入已失效或尚未登入，請到 AI 連線重新登入。");
        else if (Has(evidence, "model_not_found", "model not found", "unsupported model", "model is not supported", "model is not available", "does not have access to model"))
            (code, message) = ("ModelUnavailable", "所選模型不可用，請重新載入模型或更換模型。");
        else if (Has(evidence, "rate_limit", "rate limit", "quota exceeded", "insufficient_quota", "resource_exhausted", "429"))
            (code, message) = ("Quota", "請求頻率或額度受限，請稍後重試或檢查帳號額度。");
        else if (Has(evidence, "permission denied", "permission_denied", "tool permission", "denied_actions", "approval required"))
            (code, message) = ("Permission", "工具權限遭拒，請檢查 CLI 權限設定與執行紀錄。");
        else if (Has(evidence, "econnrefused", "enotfound", "network error", "connection reset", "connection timed out", "fetch failed"))
            (code, message) = ("Network", "CLI 無法連線，請檢查網路後再重試。");
        else (code, message) = ("CliFailure", "CLI 執行未成功，請查看執行紀錄確認原因。");
        // Keep raw provider output in artifacts, never concatenate it into the user-facing error.
        return new($"[{code}] {provider} · {phase}：{message}（exit {result.ExitCode}）詳見執行紀錄。");
    }
    private static bool Has(string value, params string[] markers) => markers.Any(value.Contains);
}
