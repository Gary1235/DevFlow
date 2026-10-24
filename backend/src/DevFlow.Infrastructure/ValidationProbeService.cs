using DevFlow.Core;
namespace DevFlow.Infrastructure;
public sealed record ValidationProbe(Guid Id, string State, ProjectPreflight? Preflight = null, ValidationEvidence? Evidence = null,
    string? Error = null, string? RetainedWorkspace = null, Guid ProjectId = default, string? ProfileName = null,
    DateTimeOffset CreatedAt = default, DateTimeOffset? FinishedAt = null, string? WorkspacePath = null, int ProjectVersion = 0);
public sealed class ValidationProbeService(SqliteTaskStore store, IProcessRunner process, string worktreeRoot, string artifactsRoot, CancellationToken shutdown)
{
    private readonly object sync = new();
    private Task? running;
    private Guid? activeId;
    private CancellationTokenSource? cancellation;
    public Task WaitForStopAsync(CancellationToken ct) { lock (sync) return running?.WaitAsync(ct) ?? Task.CompletedTask; }
    public ValidationProbe Get(Guid id) => store.GetValidationProbe(id) ?? throw new MissingEntityException("驗證測試不存在。");
    public IReadOnlyList<ValidationProbe> List(Guid projectId) => store.ListValidationProbes(projectId);
    private FileStream AcquireLock() => new(store.DatabasePath + ".probes.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public void RecoverInterrupted()
    {
        lock (sync) {
            if (running is { IsCompleted: false }) return;
            FileStream owner;
            try { owner = AcquireLock(); } catch (IOException) { return; }
            using (owner) store.InterruptValidationProbes();
        }
    }
    public ValidationProbe Cancel(Guid id)
    {
        lock (sync) {
            var probe = Get(id);
            if (probe.State is not ("Running" or "Stopping")) return probe;
            if (activeId != id || cancellation is null) throw new ConflictException("此測試由另一個 API 程序執行，請在原服務停止。");
            var stopped = probe with { State = "Stopping" }; store.SaveValidationProbe(stopped);
            cancellation.Cancel(); return stopped;
        }
    }
    public ValidationProbe Start(ProjectDefinition project, ValidationProfile profile)
    {
        lock (sync) {
        if (shutdown.IsCancellationRequested) throw new ConflictException("API 正在停止，不能開始設定測試。");
        ProjectExecutionSettings.Normalize([profile]);
        FileStream owner;
        try { owner = AcquireLock(); } catch (IOException) { throw new ConflictException("已有設定測試執行中，請稍候。"); }
        var id = Guid.NewGuid(); var probe = new ValidationProbe(id, "Running", ProjectId: project.Id, ProfileName: profile.Name,
            CreatedAt: DateTimeOffset.UtcNow, WorkspacePath: Path.GetFullPath(Path.Combine(worktreeRoot, id.ToString("N"))), ProjectVersion: project.Version);
        try { store.InterruptValidationProbes(); store.SaveValidationProbe(probe); }
        catch { owner.Dispose(); throw; }
        var cancel = new CancellationTokenSource(); cancellation = cancel; activeId = id;
        running = Task.Run(async () => {
            Workspace? workspace = null; var manager = new GitWorkspaceManager(process, worktreeRoot);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown, cancel.Token); timeout.CancelAfter(TimeSpan.FromMinutes(30));
            var outcome = "Failed";
            void Save() {
                lock (sync) {
                    if (cancel.IsCancellationRequested && probe.State == "Running") probe = probe with { State = "Stopping" };
                    store.SaveValidationProbe(probe);
                }
            }
            try {
                var checks = await new ProjectInspector(process).CheckAsync(project, profile, timeout.Token);
                probe = probe with { Preflight = checks }; Save();
                if (!checks.Ready) { probe = probe with { Error = "環境預檢未通過。" }; return; }
                var current = await store.GetProjectAsync(project.Id, timeout.Token);
                if (current?.Version != project.Version) throw new ConflictException("專案版本已變更，請重新測試。");
                workspace = await manager.CreateAsync(project, id, timeout.Token);
                var evidence = await new DotnetValidationRunner(process, artifactsRoot, "SettingsProbe").ValidateAsync(
                    project with { ValidationProfiles = [profile], DefaultValidationProfileId = profile.Id }, workspace, id, TddPhase.Implementation, timeout.Token);
                outcome = evidence.ExitCode == 0 && evidence.Failed == 0 && (evidence.Kind != "DotnetTest" || evidence.Total > 0) ? "Completed" : "Failed";
                probe = probe with { Evidence = evidence }; Save();
            }
            catch (Exception ex) { probe = probe with { Error = ex is OperationCanceledException ? "設定測試已停止或逾時。" : ex.Message }; }
            finally {
                try {
                if (workspace is not null) {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await manager.RemoveScratchAsync(project, workspace, id, cleanup.Token); }
                    catch (Exception) { probe = probe with { RetainedWorkspace = workspace.Path }; }
                }
                else if (Directory.Exists(probe.WorkspacePath)) probe = probe with { RetainedWorkspace = probe.WorkspacePath };
                lock (sync) {
                    probe = probe with { State = cancel.IsCancellationRequested ? "Cancelled" : outcome, FinishedAt = DateTimeOffset.UtcNow };
                    Save();
                }
                }
                finally { lock (sync) { activeId = null; cancellation = null; cancel.Dispose(); owner.Dispose(); } }
            }
        });
        return probe;
        }
    }
}
