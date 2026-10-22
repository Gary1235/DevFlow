using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DevFlow.Core;
using Status = DevFlow.Core.TaskStatus;

namespace DevFlow.Infrastructure;

public sealed record PreviewProcessView(string Id, string Name, string State, string Url, int? ExitCode);
public sealed record TaskPreviewView(Guid TaskId, Guid? RunId, string? ResultCommit, string State,
    PreviewProcessView[] Services, string Log, string? Error);

public sealed class TaskPreviewManager(SqliteTaskStore store, IProcessRunner git, string worktreeRoot) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Session> sessions = new();
    private bool disposed;
    private sealed class Service(PreviewService settings, int port)
    {
        public readonly PreviewService Settings = settings;
        public readonly int Port = port;
        public Process? Process;
        public IDisposable? Job;
        public Task? Output, ErrorOutput;
        public string State = "Starting";
        public int? ExitCode;
    }
    private sealed class Session(Guid taskId, Guid runId, string commit, string workspace)
    {
        public readonly Guid TaskId = taskId, RunId = runId;
        public readonly string Commit = commit, Workspace = workspace;
        public readonly CancellationTokenSource Stop = new();
        public readonly List<Service> Services = [];
        public readonly StringBuilder Log = new();
        public Task? Work;
        public string State = "Starting";
        public string? Error;
        public bool WorktreeLocked;
    }
    public bool HasActiveRun(Guid id) => sessions.Values.Any(s => s.RunId == id && (s.Work is { IsCompleted: false } || s.Services.Any(p => p.Process is not null)));
    public TaskPreviewView Get(Guid taskId)
    {
        if (!sessions.TryGetValue(taskId, out var s)) return new(taskId, null, null, "Stopped", [], "", null);
        lock (s) return new(taskId, s.RunId, s.Commit, s.State,
            s.Services.Select(p => new PreviewProcessView(p.Settings.Id, p.Settings.Name, p.State, $"http://127.0.0.1:{p.Port}", p.ExitCode)).ToArray(), s.Log.ToString(), s.Error);
    }
    public async Task<TaskPreviewView> StartAsync(Guid taskId, Guid runId, string commit, int expectedVersion, int projectVersion, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sessions.TryGetValue(taskId, out var previous) && (previous.Work is { IsCompleted: false } || previous.Services.Any(p => p.Process is not null)))
                throw new ConflictException("此任務已有預覽或仍在停止中，請先停止。");
            var task = await store.GetTaskAsync(taskId, ct) ?? throw new MissingEntityException("Task not found.");
            if (task.Version != expectedVersion || task.Status is not (Status.AwaitingReview or Status.Approved or Status.Rejected or Status.Failed or Status.AwaitingStepApproval))
                throw new ConflictException("請等待任務結束或暫停，並重新讀取成果後啟動預覽。");
            var run = await store.GetRunAsync(runId, ct) ?? throw new MissingEntityException("Run not found.");
            if (run.TaskId != taskId || run.Kind != RunKind.Implementation || run.FinishedAt is null || run.ResultCommit != commit || string.IsNullOrWhiteSpace(commit) || run.WorktreePath is null)
                throw new ConflictException("預覽成果與執行紀錄不一致。");
            var project = await store.GetProjectAsync(task.ProjectId, ct) ?? throw new MissingEntityException("Project not found.");
            if (project.Version != projectVersion) throw new ConflictException("專案設定已更新，請重新讀取。");
            var definitions = ProjectExecutionSettings.Normalize(project.PreviewServices);
            if (definitions.Length == 0) throw new ArgumentException("請先到專案設定新增本地預覽服務。");
            var expectedPath = Path.GetFullPath(Path.Combine(worktreeRoot, runId.ToString("N")));
            var actualPath = Path.GetFullPath(run.WorktreePath);
            if (!actualPath.Equals(expectedPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ConflictException("不是受管理的任務工作副本。");
            ProjectExecutionSettings.ResolveDirectory(actualPath, ".");
            var head = await git.RunAsync("git", ["rev-parse", "HEAD"], actualPath, null, ct);
            var branch = await git.RunAsync("git", ["symbolic-ref", "--short", "HEAD"], actualPath, null, ct);
            if (head.ExitCode != 0 || head.StandardOutput.Trim() != commit || branch.StandardOutput.Trim() != "devflow/" + runId.ToString("N"))
                throw new ConflictException("副本分支或提交已改變，無法預覽指定成果。");
            var dirty = await git.RunAsync("git", ["status", "--porcelain", "--untracked-files=no"], actualPath, null, ct);
            if (dirty.ExitCode != 0 || !string.IsNullOrWhiteSpace(dirty.StandardOutput)) throw new ConflictException("副本含未提交修改，請先確認成果版本。");
            var s = new Session(taskId, runId, commit, actualPath);
            var reservations = new List<TcpListener>();
            try {
                foreach (var definition in definitions) {
                    ProjectExecutionSettings.ResolveDirectory(actualPath, definition.WorkingDirectory);
                    var listener = new TcpListener(IPAddress.Loopback, definition.Port);
                    try { listener.Start(); } catch (SocketException) { throw new ConflictException($"預覽連接埠 {definition.Port} 已被占用，請改用 0 自動分配或選擇其他連接埠。"); }
                    reservations.Add(listener);
                    s.Services.Add(new(definition, ((IPEndPoint)listener.LocalEndpoint).Port));
                }
            } finally { foreach (var listener in reservations) listener.Stop(); }
            var locked = await git.RunAsync("git", ["worktree", "lock", "--reason", "DevFlow local preview", actualPath], project.RepositoryPath, null, ct);
            if (locked.ExitCode != 0) throw new ConflictException("工作副本已鎖定或無法鎖定，請確認沒有其他作業使用中。");
            s.WorktreeLocked = true;
            if (previous is not null) { previous.Stop.Dispose(); }
            sessions[taskId] = s;
            s.Work = RunSession(s);
            return Get(taskId);
        }
        finally { gate.Release(); }
    }
    private async Task RunSession(Session s)
    {
        try
        {
            foreach (var service in s.Services)
            {
                if (service.Settings.PrepareArguments is { Length: > 0 } prepare) {
                    lock (s) service.State = "Preparing";
                    Add(s, service, "執行依賴準備指令…");
                    await Execute(s, service, prepare, true);
                }
                lock (s) service.State = "Starting";
                await Execute(s, service, service.Settings.Arguments, false);
            }
            lock (s) s.State = "Running";
            Add(s, null, "所有服務已就緒。預覽使用測試資料環境；資料庫不會由 worktree 自動隔離。");
            while (!s.Stop.IsCancellationRequested) {
                if (s.Services.Any(p => p.Process?.HasExited != false)) throw new InvalidOperationException("預覽程序已結束，請查看日誌。");
                await Task.Delay(500, s.Stop.Token);
            }
        }
        catch (OperationCanceledException) when (s.Stop.IsCancellationRequested) { }
        catch (Exception ex) { lock (s) { s.Error = ex.Message; s.State = "Failed"; } Add(s, null, ex.Message); }
        finally
        {
            foreach (var service in s.Services) await Kill(s, service);
            if (s.WorktreeLocked && s.Services.All(p => p.Process is null)) {
                try {
                    var unlocked = await git.RunAsync("git", ["worktree", "unlock", s.Workspace], s.Workspace, null, CancellationToken.None);
                    if (unlocked.ExitCode != 0) throw new IOException("Git 拒絕解除預覽工作副本鎖定。");
                    s.WorktreeLocked = false;
                } catch (Exception ex) { lock (s) { s.State = "Failed"; s.Error = ex.Message; } Add(s, null, ex.Message); }
            }
            lock (s) if (s.State != "Failed") s.State = "Stopped";
        }
    }
    private string Expand(Session s, Service service, string text)
    {
        text = text.Replace("{port}", service.Port.ToString());
        foreach (var other in s.Services) text = text.Replace("{port:" + other.Settings.Id + "}", other.Port.ToString());
        if (text.Contains("{port:")) throw new ArgumentException("預覽指令引用了不存在的服務連接埠。");
        return text;
    }
    private async Task Execute(Session s, Service service, string[] arguments, bool prepare)
    {
        s.Stop.Token.ThrowIfCancellationRequested();
        var resolved = LocalExecutable.Resolve(service.Settings.Executable, arguments.Select(a => Expand(s, service, a)));
        var start = new ProcessStartInfo(resolved.Executable) { WorkingDirectory = ProjectExecutionSettings.ResolveDirectory(s.Workspace, service.Settings.WorkingDirectory),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in resolved.Arguments) start.ArgumentList.Add(arg);
        start.Environment["BROWSER"] = "none";
        foreach (var variable in service.Settings.Environment ?? []) start.Environment[variable.Key] = Expand(s, service, variable.Value);
        var process = new Process { StartInfo = start };
        try { process.Start(); } catch { process.Dispose(); throw; }
        service.Process = process;
        service.Job = PreviewProcessJob.Attach(process);
        service.Output = Pump(s, service, process.StandardOutput);
        service.ErrorOutput = Pump(s, service, process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(s.Stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(prepare ? 600 : 60));
        try {
            if (prepare) {
                await process.WaitForExitAsync(deadline.Token);
                service.Job?.Dispose(); service.Job = null;
                await Task.WhenAll(service.Output, service.ErrorOutput).WaitAsync(deadline.Token);
                if (process.ExitCode != 0) throw new InvalidOperationException(service.Settings.Name + " 的依賴準備失敗，請查看日誌。");
                process.Dispose(); service.Process = null;
            }
            else {
                while (true) {
                    if (process.HasExited) throw new InvalidOperationException(service.Settings.Name + " 啟動失敗，退出碼 " + process.ExitCode + "，請查看日誌。");
                    using var client = new TcpClient();
                    try { await client.ConnectAsync(IPAddress.Loopback, service.Port, deadline.Token); break; }
                    catch (SocketException) { await Task.Delay(250, deadline.Token); }
                }
                lock (s) service.State = "Running";
            }
        }
        catch (OperationCanceledException) when (!s.Stop.IsCancellationRequested) { throw new InvalidOperationException(service.Settings.Name + (prepare ? " 依賴準備逾時。" : " 啟動逾時，請確認指令有使用 {port} 並監聽本機。")); }
    }
    private static async Task Pump(Session s, Service service, StreamReader reader)
    {
        var buffer = new char[2048]; int read;
        while ((read = await reader.ReadAsync(buffer)) > 0) Add(s, service, new string(buffer, 0, read));
    }
    private static void Add(Session s, Service? service, string text)
    {
        lock (s) {
            s.Log.Append('[').Append(DateTimeOffset.Now.ToString("HH:mm:ss")).Append(' ').Append(service?.Settings.Name ?? "預覽").Append("] ").AppendLine(text);
            if (s.Log.Length > 65536) s.Log.Remove(0, s.Log.Length - 65536);
        }
    }
    private static async Task Kill(Session s, Service service)
    {
        var process = service.Process;
        if (process is not null) {
            var stopped = false;
            try {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                service.Job?.Dispose(); service.Job = null;
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                stopped = true;
                service.ExitCode = process.ExitCode;
                await Task.WhenAll(service.Output ?? Task.CompletedTask, service.ErrorOutput ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            } catch (Exception ex) { Add(s, service, "停止程序時發生問題：" + ex.Message); lock (s) { s.Error = "部分預覽程序無法確認已停止，請查看日誌。"; s.State = "Failed"; } }
            finally { service.Job?.Dispose(); service.Job = null; if (stopped) { process.Dispose(); service.Process = null; } }
        }
        lock (s) service.State = s.State == "Failed" ? "Failed" : "Stopped";
    }
    public async Task<TaskPreviewView> StopAsync(Guid taskId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try {
            if (sessions.TryGetValue(taskId, out var s)) {
                lock (s) s.State = "Stopping";
                s.Stop.Cancel();
                if (s.Work is not null) await s.Work;
                foreach (var service in s.Services.Where(p => p.Process is not null)) await Kill(s, service);
                if (s.WorktreeLocked && s.Services.All(p => p.Process is null)) {
                    var unlocked = await git.RunAsync("git", ["worktree", "unlock", s.Workspace], s.Workspace, null, ct);
                    if (unlocked.ExitCode == 0) s.WorktreeLocked = false;
                }
            }
            return Get(taskId);
        } finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try {
            if (disposed) return;
            disposed = true;
            foreach (var s in sessions.Values) s.Stop.Cancel();
            await Task.WhenAll(sessions.Values.Select(s => s.Work ?? Task.CompletedTask));
            foreach (var s in sessions.Values) s.Stop.Dispose();
        } finally { gate.Release(); }
    }
}
