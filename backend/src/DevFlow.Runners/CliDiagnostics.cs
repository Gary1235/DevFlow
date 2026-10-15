using DevFlow.Core;
namespace DevFlow.Runners;
public sealed record CliDiagnostic(string Provider, bool Installed, string? Version, string LoginState, string ExecutionState, string Message);
public sealed class CliDiagnostics(IProcessRunner process, CliRegistry registry, string controlRoot)
{
    public async Task<CliDiagnostic> InspectAsync(string provider, CancellationToken ct = default)
    {
        provider = AiSelection.Normalize(new(provider)).Provider;
        if (!registry.List().Single(p => p.Id == provider).Installed)
            return new(provider, false, null, "Unknown", "NotInstalled", "CLI 未安裝或路徑未設定。");
        var command = registry.Require(provider);
        var root = PrepareDirectory();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var version = await process.RunAsync(command.Executable, [.. command.PrefixArguments, "--version"], root, null, timeout.Token);
            if (version.ExitCode != 0) return new(provider, true, null, "Unknown", "Error", "CLI 無法啟動；請在本機終端檢查權限與安裝。");
            // Only accept version-like text; never expose arbitrary CLI diagnostics or credentials.
            var text = version.StandardOutput.Trim();
            var safeVersion = text.Length <= 100 && System.Text.RegularExpressions.Regex.IsMatch(text, @"\A[A-Za-z0-9 ._+()/-]+\z") ? text : null;
            if (provider == "gemini")
                return new(provider, true, safeVersion, "Unknown", "Unchecked", "Gemini CLI 沒有統一的免額度登入狀態命令。請完成官方登入後，按連線測試確認；登入與執行是分開檢查。");
            var login = await process.RunAsync(command.Executable, [.. command.PrefixArguments, .. provider == "claude" ? new[] { "auth", "status" } : new[] { "login", "status" }], root, null, timeout.Token);
            if (provider == "claude")
            {
                var claudeState = "Unknown";
                try
                {
                    using var json = System.Text.Json.JsonDocument.Parse(login.StandardOutput);
                    var loggedIn = json.RootElement.GetProperty("loggedIn").GetBoolean();
                    if (loggedIn && login.ExitCode == 0) claudeState = "LoggedIn";
                    else if (!loggedIn && login.ExitCode == 1) claudeState = "LoggedOut";
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { }
                return new(provider, true, safeVersion, claudeState, "Unchecked", "Claude Code 登入與模型使用資格分開檢查。尚未付費也可保留設定；需要使用時再完成官方登入與確認資格。");
            }
            var combined = login.StandardOutput + login.StandardError;
            var state = login.ExitCode == 0 ? "LoggedIn" : combined.Contains("not logged", StringComparison.OrdinalIgnoreCase) ? "LoggedOut" : "Unknown";
            return new(provider, true, safeVersion, state, "Unchecked", state == "LoggedIn" ? "CLI 已登入；尚未測試模型執行。" : "請使用官方 CLI 登入，或在本機終端檢查登入狀態。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(provider, true, null, "Unknown", "TimedOut", "CLI 檢查逾時。"); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { return new(provider, true, null, "Unknown", "Error", "CLI 啟動遭遇權限或系統錯誤。請在本機終端執行相同 CLI 檢查。"); }
    }
    public async Task<CliDiagnostic> ProbeAsync(AiSelection selection, CancellationToken ct = default)
    {
        selection = AiSelection.Normalize(selection);
        var command = registry.Require(selection.Provider);
        var root = PrepareDirectory();
        List<string> args = selection.Provider switch
        {
            "codex" => ["exec", "--sandbox", "read-only", "--json", "--ephemeral"],
            "claude" => ClaudeCliRunner.Arguments(selection, planning: true, probe: true),
            _ => ["--sandbox", "--approval-mode", "plan", "--output-format", "json", "--prompt", "Reply only DEVFLOW_OK. Do not use tools or modify files."]
        };
        if (selection.Model != null && selection.Provider != "claude") args.AddRange(["--model", selection.Model]);
        if (selection.Provider == "codex") args.AddRange(["--skip-git-repo-check", "-C", root, "-"]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            var result = await process.RunAsync(command.Executable, [.. command.PrefixArguments, .. args], root,
                selection.Provider is "codex" or "claude" ? "Reply only DEVFLOW_OK. Do not use tools or modify files." : null, timeout.Token);
            bool success = result.ExitCode == 0 && result.StandardOutput.Contains("DEVFLOW_OK", StringComparison.Ordinal);
            if (selection.Provider == "codex" && success)
            {
                bool answer = false, completed = false;
                try
                {
                    foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        using var json = System.Text.Json.JsonDocument.Parse(line);
                        var type = json.RootElement.GetProperty("type").GetString();
                        if (type is "turn.failed" or "error") { success = false; break; }
                        if (type == "turn.completed") completed = true;
                        if (type == "item.completed" && json.RootElement.TryGetProperty("item", out var item) &&
                            item.TryGetProperty("type", out var kind) && kind.GetString() == "agent_message" &&
                            item.GetProperty("text").GetString()?.Trim() == "DEVFLOW_OK") answer = true;
                    }
                    success = success && answer && completed;
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { success = false; }
            }
            if (selection.Provider == "gemini" && success)
            {
                try
                {
                    using var json = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
                    success = (!json.RootElement.TryGetProperty("error", out var error) || error.ValueKind == System.Text.Json.JsonValueKind.Null)
                        && json.RootElement.GetProperty("response").GetString()?.Contains("DEVFLOW_OK", StringComparison.Ordinal) == true;
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { success = false; }
            }
            if (selection.Provider == "claude" && success)
            {
                try { success = ClaudeCliRunner.ParseResult(result.StandardOutput).Trim() == "DEVFLOW_OK"; }
                catch (InvalidOperationException) { success = false; }
            }
            return new(selection.Provider, true, null, success ? "VerifiedByExecution" : "Unknown", success ? "Ready" : "Error",
                success ? "指定的 CLI／模型已完成連線測試。" : "CLI／模型執行失敗；可能是登入、模型權限、額度或 Sandbox 問題。請在本機終端確認。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(selection.Provider, true, null, "Unknown", "TimedOut", "模型連線測試逾時。"); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { return new(selection.Provider, true, null, "Unknown", "Error", "模型執行遭遇系統權限錯誤。"); }
    }
    private string PrepareDirectory()
    {
        var directory = Path.GetFullPath(controlRoot); Directory.CreateDirectory(directory); return directory;
    }
}
