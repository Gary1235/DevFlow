using System.Text.Json;
using System.Text.RegularExpressions;
using DevFlow.Core;
namespace DevFlow.Runners;

public sealed record CliVersionCheck(Guid Id, string Provider, string Executable, string? InstalledVersion, string? LatestVersion,
    bool UpdateAvailable, bool CanUpdate, string Message, string ManualCommand, DateTimeOffset ExpiresAt, string? EntryPoint = null);
public sealed record CliUpdateSession(Guid Id, string Provider, string State, string Message, string? InstalledVersion,
    string TargetVersion, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt = null);

public sealed class CliMaintenance : IDisposable
{
    private readonly IProcessRunner process;
    private readonly CliRegistry registry;
    private readonly CliMaintenanceGate gate;
    private readonly Func<CancellationToken, Task<bool>> busy;
    private readonly string directory, node, npm, globalRoot;
    private readonly object sync = new();
    private readonly Dictionary<Guid, CliVersionCheck> checks = [];
    private readonly Dictionary<Guid, CliUpdateSession> sessions = [];
    private readonly CancellationTokenSource shutdown = new();
    private static readonly Regex VersionPattern = new(@"\A\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?\z");

    public CliMaintenance(IProcessRunner process, CliRegistry registry, CliMaintenanceGate gate,
        Func<CancellationToken, Task<bool>> busy, string directory, string? node = null, string? npm = null, string? globalRoot = null)
    {
        this.process = process; this.registry = registry; this.gate = gate; this.busy = busy; this.directory = directory;
        this.node = node ?? FindNode() ?? "";
        this.npm = npm ?? (this.node.Length > 0 ? Path.Combine(Path.GetDirectoryName(this.node)!, "node_modules", "npm", "bin", "npm-cli.js") : "");
        this.globalRoot = Path.GetFullPath(globalRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules"));
    }
    private static string? FindNode() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(p => Path.Combine(p.Trim('"'), OperatingSystem.IsWindows() ? "node.exe" : "node")).FirstOrDefault(File.Exists);
    private static string Package(string provider) => provider switch
    { "codex" => "@openai/codex", "gemini" => "@google/gemini-cli", "claude" => "@anthropic-ai/claude-code", _ => throw new ArgumentException("Unknown CLI provider.") };
    private bool Managed(string provider, CliCommand command)
    {
        if (!File.Exists(node) || !File.Exists(npm)) return false;
        var package = Package(provider);
        var root = Path.GetFullPath(Path.Combine(globalRoot, package.Replace('/', Path.DirectorySeparatorChar)));
        var entry = command.PrefixArguments.FirstOrDefault() ?? command.Executable;
        if (!Path.GetFullPath(entry).StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        try { using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json"))); return json.RootElement.GetProperty("name").GetString() == package; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
    private async Task<string?> ReadVersion(CliCommand command, CancellationToken ct)
    {
        var result = await process.RunAsync(command.Executable, [.. command.PrefixArguments, "--version"], directory, null, ct);
        if (result.ExitCode != 0) return null;
        var match = Regex.Match(result.StandardOutput.Trim(), @"(?:\A|\s)(\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?)(?:\s|\z)");
        return match.Success ? match.Groups[1].Value : null;
    }
    private static bool Newer(string latest, string installed) =>
        Version.TryParse(latest.Split('-')[0], out var next) && Version.TryParse(installed.Split('-')[0], out var current) &&
        (next > current || next == current && installed.Contains('-') && !latest.Contains('-'));

    public async Task<CliVersionCheck> CheckAsync(string provider, CancellationToken ct = default)
    {
        provider = AiSelection.Normalize(new(provider)).Provider;
        if (provider == "antigravity")
        {
            using var agyLease = gate.EnterExecution();
            registry.Refresh();
            var agyCommand = registry.Require(provider);
            Directory.CreateDirectory(directory);
            using var agyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            agyTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            string? version = null;
            try { version = await ReadVersion(agyCommand, agyTimeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            return new(Guid.NewGuid(), provider, agyCommand.Executable, version, null, false, false,
                "DevFlow 目前僅讀取本機版本；請在終端執行 agy update，或使用官方安裝方式更新。",
                "agy update", DateTimeOffset.UtcNow.AddMinutes(10));
        }
        var package = Package(provider);
        using var lease = gate.EnterExecution();
        registry.Refresh();
        var command = registry.Require(provider);
        Directory.CreateDirectory(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        string? installed = null, latest = null; string? error = null;
        try
        {
            installed = await ReadVersion(command, timeout.Token);
            if (File.Exists(node) && File.Exists(npm))
            {
                var response = await process.RunAsync(node, [npm, "view", package, "version", "--json", "--registry=https://registry.npmjs.org"], directory, null, timeout.Token);
                if (response.ExitCode == 0)
                {
                    using var json = JsonDocument.Parse(response.StandardOutput);
                    if (json.RootElement.ValueKind == JsonValueKind.String && json.RootElement.GetString() is { } version && VersionPattern.IsMatch(version)) latest = version;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { error = "版本檢查逾時。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException) { error = "無法讀取 CLI 或 npm 版本，請檢查本機權限及網路。"; }
        var managed = Managed(provider, command);
        var available = installed is not null && latest is not null && Newer(latest, installed);
        var canUpdate = managed && available && error is null;
        var message = error ?? (installed is null ? "無法確認已安裝版本，請使用本機終端檢查。" : latest is null ? "無法取得 npm 最新版本，請檢查網路與 npm。" :
            !managed ? "已顯示 npm 發布版本；目前執行檔不屬於可管理的 npm 安裝，請依原安裝方式手動更新。" : available ? "有可用更新；點擊更新後才會安裝。" : "目前版本沒有較新的 npm 穩定更新。");
        var check = new CliVersionCheck(Guid.NewGuid(), provider, command.Executable, installed, latest, available, canUpdate, message,
            managed ? $"npm install -g {package}@latest" : "請使用此 CLI 原安裝方式更新。", DateTimeOffset.UtcNow.AddMinutes(10), command.PrefixArguments.FirstOrDefault());
        lock (sync)
        {
            foreach (var id in checks.Values.OrderBy(c => c.ExpiresAt).Take(Math.Max(0, checks.Count - 31)).Select(c => c.Id).ToArray()) checks.Remove(id);
            checks.Add(check.Id, check);
        }
        return check;
    }

    public CliUpdateSession Get(Guid id) { lock (sync) return sessions.GetValueOrDefault(id) ?? throw new MissingEntityException("CLI update session not found; API may have restarted. Check the installed version before retrying."); }
    public async Task<CliUpdateSession> StartAsync(string provider, Guid checkId, CancellationToken ct = default)
    {
        if (AiSelection.Normalize(new(provider)).Provider == "antigravity")
            throw new ConflictException("請依 Antigravity CLI 官方安裝方式更新；不支援 npm 更新。");
        Package(provider);
        CliVersionCheck check;
        lock (sync) check = checks.GetValueOrDefault(checkId) ?? throw new ConflictException("請先檢查 CLI 更新。");
        if (check.Provider != provider || !check.CanUpdate || check.ExpiresAt < DateTimeOffset.UtcNow || check.LatestVersion is null)
            throw new ConflictException("更新資訊已過期或此安裝不支援管理，請重新檢查版本。");
        var lease = gate.EnterUpdate();
        try
        {
            if (await busy(ct)) throw new ConflictException("仍有任務、討論或登入作業執行中，請等待結束後更新。");
            registry.Refresh();
            var command = registry.Require(provider);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            if (command.Executable != check.Executable || !Managed(provider, command) || await ReadVersion(command, timeout.Token) != check.InstalledVersion)
                throw new ConflictException("CLI 安裝或版本已改變，請重新檢查更新。");
            var session = new CliUpdateSession(Guid.NewGuid(), provider, "Updating", "正在下載並安裝 CLI 更新…", check.InstalledVersion, check.LatestVersion, DateTimeOffset.UtcNow);
            lock (sync)
            {
                checks.Remove(checkId);
                foreach (var id in sessions.Values.Where(s => s.FinishedAt is not null).OrderBy(s => s.StartedAt).Take(Math.Max(0, sessions.Count - 31)).Select(s => s.Id).ToArray()) sessions.Remove(id);
                sessions.Add(session.Id, session);
            }
            _ = Task.Run(() => UpdateAsync(session, lease));
            return session;
        }
        catch { lease.Dispose(); throw; }
    }
    private void Set(CliUpdateSession session) { lock (sync) sessions[session.Id] = session; }
    private async Task UpdateAsync(CliUpdateSession session, IDisposable lease)
    {
        CliUpdateSession finished;
        using (lease)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            try
            {
                var prefix = Path.GetDirectoryName(globalRoot)!;
                if (!OperatingSystem.IsWindows() && Path.GetFileName(prefix) == "lib") prefix = Path.GetDirectoryName(prefix)!;
                var result = await process.RunAsync(node, [npm, "install", "--global", "--prefix=" + prefix, Package(session.Provider) + "@" + session.TargetVersion, "--registry=https://registry.npmjs.org", "--no-audit", "--no-fund"], directory, null, timeout.Token);
                if (result.ExitCode != 0)
                {
                    finished = session with { State = "Failed", Message = InstallFailure(result), FinishedAt = DateTimeOffset.UtcNow };
                }
                else
                {
                    Set(session with { State = "Verifying", Message = "正在確認更新後的執行檔與版本…" });
                    registry.Refresh();
                    var actual = await ReadVersion(registry.Require(session.Provider), timeout.Token);
                    var verified = actual == session.TargetVersion;
                    finished = session with { State = verified ? "Completed" : "Failed", InstalledVersion = actual,
                        Message = verified ? "CLI 已更新。重新載入模型並手動執行連線測試，即可確認模型使用狀態。" : "安裝程序結束，但 DevFlow 使用的 CLI 版本未符合目標。請檢查路徑後重新確認。", FinishedAt = DateTimeOffset.UtcNow };
                }
            }
            catch (OperationCanceledException) { finished = session with { State = "TimedOut", Message = "更新中斷或逾時；請先檢查實際安裝版本，不會自動重試。", FinishedAt = DateTimeOffset.UtcNow }; }
            catch (Exception)
            { finished = session with { State = "Failed", Message = "更新或版本驗證遭遇本機錯誤，請檢查權限與 CLI 安裝位置。", FinishedAt = DateTimeOffset.UtcNow }; }
        }
        // Release maintenance before publishing completion so model refreshes can run.
        Set(finished);
    }
    private static string InstallFailure(ProcessResult result)
    {
        var text = result.StandardError + "\n" + result.StandardOutput;
        bool Has(params string[] values) => values.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));
        if (Has("EACCES", "EPERM", "EBUSY", "access denied", "permission denied")) return "CLI 更新遇到檔案占用或權限問題。請先關閉外部 CLI 終端，確認此使用者可修改 npm 安裝目錄，再檢查版本後重試。";
        if (Has("ENOTFOUND", "ECONNRESET", "ETIMEDOUT", "ECONNREFUSED", "CERT_")) return "npm 無法連線或憑證驗證失敗。請檢查網路、代理與憑證，再檢查版本後重試。";
        if (Has("EBADENGINE")) return "目前 Node.js 版本不符合新版 CLI 要求。請先更新 Node.js，再重新檢查 CLI 更新。";
        return "CLI 更新失敗（結束代碼 " + result.ExitCode + "）。請檢查 npm 安裝與本機環境，重新確認版本後再試；不會自動重送安裝。";
    }
    public void Dispose() => shutdown.Cancel();
}
