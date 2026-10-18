using DevFlow.Core;
using System.Diagnostics;
namespace DevFlow.Runners;
public sealed record CliDiagnostic(string Provider, bool Installed, string? Version, string LoginState, string ExecutionState, string Message,
    string? ErrorCode = null, int? ExitCode = null);
public sealed class CliDiagnostics(IProcessRunner process, CliRegistry registry, string controlRoot)
{
    private readonly SemaphoreSlim modelListLock = new(1, 1);
    private CliModelOption[]? codexModelCache;
    private DateTimeOffset codexModelCacheUntil;
    private string? configuredDefaultModel;
    private string? catalogState;
    private string? catalogMessage;

    public async Task<IReadOnlyList<CliProvider>> ListProvidersAsync(CancellationToken ct = default, bool refresh = false)
    {
        var providers = registry.List().ToArray();
        var codexIndex = Array.FindIndex(providers, p => p.Id == "codex" && p.Installed);
        if (codexIndex < 0) return providers;
        var models = await DiscoverCodexModelsAsync(ct, refresh);
        providers[codexIndex] = providers[codexIndex] with { Models = models.Select(m => m.Id).ToArray(), ModelDetails = models, ModelsVerified = false,
            ConfiguredDefaultModel = configuredDefaultModel, ModelCatalogState = catalogState, ModelCatalogMessage = catalogMessage };
        return providers;
    }

    private async Task<CliModelOption[]> DiscoverCodexModelsAsync(CancellationToken ct, bool refresh)
    {
        await modelListLock.WaitAsync(ct);
        try
        {
            if (!refresh && codexModelCache is not null && codexModelCacheUntil > DateTimeOffset.UtcNow) return codexModelCache;
            Process? child = null;
            try
            {
                var command = registry.Require("codex");
                var start = new ProcessStartInfo(command.Executable)
                {
                    WorkingDirectory = PrepareDirectory(), UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8
                };
                foreach (var argument in command.PrefixArguments.Append("app-server")) start.ArgumentList.Add(argument);
                child = new Process { StartInfo = start };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                child.Start();
                var stderr = child.StandardError.ReadToEndAsync(timeout.Token);
                _ = stderr.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                await child.StandardInput.WriteLineAsync("{\"method\":\"initialize\",\"id\":0,\"params\":{\"clientInfo\":{\"name\":\"devflow\",\"title\":\"DevFlow\",\"version\":\"0.7\"}}}".AsMemory(), timeout.Token);
                await child.StandardInput.FlushAsync(timeout.Token);
                using var initialized = await ReadRpcResultAsync(child, 0, timeout.Token);
                await child.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}".AsMemory(), timeout.Token);
                await child.StandardInput.FlushAsync(timeout.Token);

                // Read only the effective model ID; never return the rest of the CLI configuration.
                await child.StandardInput.WriteLineAsync("{\"method\":\"config/read\",\"id\":1001,\"params\":{\"includeLayers\":false}}".AsMemory(), timeout.Token);
                await child.StandardInput.FlushAsync(timeout.Token);
                try
                {
                    using var config = await ReadRpcResultAsync(child, 1001, timeout.Token);
                    configuredDefaultModel = config.RootElement.TryGetProperty("config", out var settings) &&
                        settings.TryGetProperty("model", out var model) && model.ValueKind == System.Text.Json.JsonValueKind.String
                        ? AiSelection.Normalize(new("codex", model.GetString())).Model : null;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { configuredDefaultModel = null; }

                var models = new List<CliModelOption>();
                string? cursor = null;
                for (var page = 0; page < 10; page++)
                {
                    var id = page + 1;
                    var requestParams = new Dictionary<string, object?> { ["limit"] = 100, ["includeHidden"] = true };
                    if (cursor is not null) requestParams["cursor"] = cursor;
                    var request = System.Text.Json.JsonSerializer.Serialize(new { method = "model/list", id, @params = requestParams });
                    await child.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token);
                    await child.StandardInput.FlushAsync(timeout.Token);
                    using var result = await ReadRpcResultAsync(child, id, timeout.Token);
                    models.AddRange(CodexModelCatalog.Read(result.RootElement, configuredDefaultModel));
                    cursor = result.RootElement.TryGetProperty("nextCursor", out var next) && next.ValueKind == System.Text.Json.JsonValueKind.String ? next.GetString() : null;
                    if (string.IsNullOrEmpty(cursor)) break;
                }
                codexModelCache = models.DistinctBy(m => m.Id).ToArray();
                catalogState = codexModelCache.Length > 0 ? "Ready" : "Empty";
                catalogMessage = codexModelCache.Length > 0 ? "已讀取 CLI 模型目錄；實際使用權限仍以執行結果為準。" : "CLI 模型目錄為空，請重新載入，或輸入帳號可用的模型 ID。";
                codexModelCacheUntil = DateTimeOffset.UtcNow.AddMinutes(5);
                // A valid RPC result does not depend on app-server shutting down promptly.
                child.StandardInput.Close();
                return codexModelCache;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                // The picker remains usable with configured/custom model IDs when app-server discovery is unavailable.
                codexModelCache ??= registry.List().Single(p => p.Id == "codex").ModelDetails ?? [];
                catalogState = "Unavailable";
                catalogMessage = ex is OperationCanceledException ? "讀取 CLI 模型目錄逾時。可按「重新載入模型」重試。" : "無法讀取 CLI 模型目錄。已保留現有選項，可按「重新載入模型」重試。";
                codexModelCacheUntil = DateTimeOffset.UtcNow.AddMinutes(1);
                return codexModelCache;
            }
            finally
            {
                if (child is not null)
                {
                    try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    child.Dispose();
                }
            }
        }
        finally { modelListLock.Release(); }
    }

    private static async Task<System.Text.Json.JsonDocument> ReadRpcResultAsync(Process child, int expectedId, CancellationToken ct)
    {
        while (true)
        {
            var line = await child.StandardOutput.ReadLineAsync(ct) ?? throw new IOException("Codex app-server closed before returning a model catalog.");
            if (line.Length > 1024 * 1024) throw new IOException("Codex app-server response exceeded the expected size.");
            using var message = System.Text.Json.JsonDocument.Parse(line);
            if (!message.RootElement.TryGetProperty("id", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.Number || id.GetInt32() != expectedId) continue;
            if (message.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("Codex app-server rejected model discovery.");
            if (!message.RootElement.TryGetProperty("result", out var result)) throw new InvalidOperationException("Codex app-server returned no result.");
            return System.Text.Json.JsonDocument.Parse(result.GetRawText());
        }
    }

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
        if (selection.Provider != "claude") CliReasoning.AddArguments(args, selection);
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
            if (!success)
            {
                var failure = CliProbeFailure.Classify(result);
                return new(selection.Provider, true, null, "Unknown", "Error", failure.Message, failure.Code, result.ExitCode);
            }
            return new(selection.Provider, true, null, "VerifiedByExecution", "Ready", "指定的 CLI／模型已完成連線測試。", ExitCode: result.ExitCode);
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
