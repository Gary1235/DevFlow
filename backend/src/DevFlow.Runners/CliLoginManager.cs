using DevFlow.Core;
using System.Diagnostics;
using System.Text;
namespace DevFlow.Runners;

public interface ILoginTerminal : IDisposable
{
    bool HasExited { get; }
    int ExitCode { get; }
    void Stop();
}
public interface ILoginTerminalLauncher
{
    ILoginTerminal Start(CliCommand command, string[] arguments, string workingDirectory);
}
public sealed class WindowsLoginTerminalLauncher : ILoginTerminalLauncher
{
    public ILoginTerminal Start(CliCommand command, string[] arguments, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new ConflictException("Interactive login launcher currently supports Windows. Run the provider CLI login in your terminal.");
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = "& " + Quote(command.Executable) + " " + string.Join(" ", command.PrefixArguments.Concat(arguments).Select(Quote)) + "; exit $LASTEXITCODE";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { WorkingDirectory = workingDirectory, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        // A visible terminal is intentional: this action explicitly requests interactive login.
        return new Terminal(Process.Start(start) ?? throw new ConflictException("Unable to open login terminal."));
    }
    private sealed class Terminal(Process process) : ILoginTerminal
    {
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public void Stop()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        public void Dispose() => process.Dispose();
    }
}
public sealed record CliLoginSession(Guid Id, string Provider, string Mode, string State, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt, string Message);
public sealed class CliLoginManager(CliRegistry registry, ILoginTerminalLauncher launcher, string controlRoot, TimeProvider? clock = null) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Entry> sessions = [];
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private Timer? cleanup;
    private bool disposed;
    private sealed class Entry(CliLoginSession session, ILoginTerminal terminal)
    { public CliLoginSession Session = session; public ILoginTerminal? Terminal = terminal; }
    public CliLoginSession Start(string provider, string mode = "browser")
    {
        provider = AiSelection.Normalize(new(provider)).Provider;
        if (mode is not ("browser" or "device") || provider != "codex" && mode != "browser") throw new ArgumentException("Unsupported login mode.");
        var command = registry.Require(provider);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Refresh();
            if (sessions.Values.Any(e => e.Session.Provider == provider && e.Terminal != null)) throw new ConflictException("A login for this provider is already running.");
            // Keep bounded metadata; active entries cannot be pruned.
            foreach (var old in sessions.Where(p => p.Value.Terminal == null).OrderBy(p => p.Value.Session.StartedAt).Take(Math.Max(0, sessions.Count - 63)).Select(p => p.Key).ToArray()) sessions.Remove(old);
            Directory.CreateDirectory(controlRoot);
            string[] arguments = provider switch
            {
                "codex" => mode == "device" ? ["login", "--device-auth"] : ["login"],
                "claude" => ["auth", "login"],
                _ => []
            };
            ILoginTerminal terminal;
            try { terminal = launcher.Start(command, arguments, Path.GetFullPath(controlRoot)); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            { throw new ConflictException("Cannot open the CLI login terminal; check local permissions."); }
            var now = time.GetUtcNow();
            var session = new CliLoginSession(Guid.NewGuid(), provider, mode, "WaitingForUser", now, now.AddMinutes(15),
                provider == "gemini" ? "請在開啟的 Gemini 終端選擇 Google 登入，完成後輸入 /quit，再執行連線測試。" : "請在開啟的官方 CLI 終端／瀏覽器完成登入。登入結束後重新檢查狀態。");
            sessions.Add(session.Id, new(session, terminal));
            cleanup ??= new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            return session;
        }
    }
    public CliLoginSession Get(Guid id) { lock (gate) { Refresh(); return Find(id).Session; } }
    public bool HasActiveLogins { get { lock (gate) { Refresh(); return sessions.Values.Any(e => e.Terminal is not null); } } }
    public CliLoginSession Cancel(Guid id)
    {
        lock (gate)
        {
            Refresh(); var entry = Find(id);
            if (entry.Terminal != null) End(entry, "Cancelled", "登入已取消；不會登出或刪除 CLI 已保存的憑證。", stop: true);
            return entry.Session;
        }
    }
    private Entry Find(Guid id) => sessions.TryGetValue(id, out var entry) ? entry : throw new MissingEntityException("Login session not found or expired after server restart.");
    private void Refresh()
    {
        foreach (var entry in sessions.Values.Where(e => e.Terminal != null))
        {
            if (entry.Terminal!.HasExited)
                End(entry, entry.Terminal.ExitCode == 0 ? "AwaitingVerification" : "Failed", "登入終端已結束；請重新檢查登入或執行連線測試。");
            else if (time.GetUtcNow() >= entry.Session.ExpiresAt) End(entry, "TimedOut", "登入逾時。", stop: true);
        }
    }
    private static void End(Entry entry, string state, string message, bool stop = false)
    {
        if (stop)
        {
            try { entry.Terminal!.Stop(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                entry.Session = entry.Session with { State = "CleanupFailed", Message = "無法停止登入終端；請手動關閉該終端後重查。此供應商仍鎖定，避免重複登入。" };
                return;
            }
        }
        entry.Terminal!.Dispose(); entry.Terminal = null;
        entry.Session = entry.Session with { State = state, Message = message };
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true; cleanup?.Dispose();
            foreach (var entry in sessions.Values.Where(e => e.Terminal != null)) End(entry, "Cancelled", "Server stopped.", stop: true);
        }
    }
    public void Sweep() { lock (gate) { if (!disposed) Refresh(); } }
}
