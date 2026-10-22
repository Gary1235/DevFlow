using System.Net;
using System.Net.Sockets;
using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
namespace DevFlow.Tests;

public sealed class TaskPreviewTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-preview-" + Guid.NewGuid());
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private SqliteTaskStore store = null!;
    private TaskPreviewManager manager = null!;
    private WorkTask task = null!;
    private TaskRun run = null!;
    private ProjectDefinition project = null!;
    private string workspace = null!;
    private static string Node {
        get {
            var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "node", "bin", "node.exe");
            return Environment.GetEnvironmentVariable("DevFlow__NodeExecutable") ?? (File.Exists(bundled) ? bundled : "node");
        }
    }
    private sealed class GitFixture : IProcessRunner
    {
        public Guid RunId;
        public readonly List<string> Commands = [];
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string w, string? input, CancellationToken ct) {
            lock (Commands) Commands.Add(string.Join(' ', a));
            return Task.FromResult(new ProcessResult(0, a[0] == "rev-parse" ? Commit : a[0] == "symbolic-ref" ? "devflow/" + RunId.ToString("N") : "", ""));
        }
    }
    private readonly GitFixture git = new();
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        store = new(Path.Combine(root, "store.db")); await store.InitializeAsync();
        project = new(Guid.NewGuid(), "Preview", root, "main", ["test"], PreviewServices: [new("web", "Web", ".", Node, ["server.cjs", "{port}"])]);
        await store.AddProjectAsync(project);
        task = WorkTask.Create(project.Id, "Preview task", "Build", "Pass").SetPlan("plan").ApprovePlan();
        await store.AddTaskAsync(task);
        var claim = (await store.ClaimNextAsync())!; run = claim.Run; git.RunId = run.Id;
        workspace = Path.Combine(root, "worktrees", run.Id.ToString("N")); Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "server.cjs"), "require('http').createServer((req,res)=>res.end('preview-ok')).listen(Number(process.argv[2]),'127.0.0.1',()=>console.log('READY'));process.on('SIGTERM',()=>process.exit(0));");
        await store.SetWorkspaceAsync(run.Id, new(workspace, "base"));
        await store.CompleteRunAsync(run.Id, true, workspace, "base", Commit, null);
        task = (await store.GetTaskAsync(task.Id))!;
        manager = new(store, git, Path.Combine(root, "worktrees"));
    }
    public async Task DisposeAsync() { await manager.DisposeAsync(); Directory.Delete(root, true); }
    private Task<TaskPreviewView> Start() => manager.StartAsync(task.Id, run.Id, Commit, task.Version, project.Version, default);
    private async Task<TaskPreviewView> WaitFor(string state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true) {
            var view = manager.Get(task.Id);
            if (view.State == state) return view;
            Assert.NotEqual("Failed", state == "Failed" ? "expected" : view.State);
            await Task.Delay(50, timeout.Token);
        }
    }
    [Fact] public async Task Starts_exact_result_serves_url_logs_and_stops_owned_processes()
    {
        await Start(); var view = await WaitFor("Running");
        Assert.Equal(Commit, view.ResultCommit); Assert.Equal(run.Id, view.RunId);
        using var client = new HttpClient(); Assert.Equal("preview-ok", await client.GetStringAsync(view.Services[0].Url));
        Assert.True(manager.HasActiveRun(run.Id));
        await Assert.ThrowsAsync<ConflictException>(() => Start());
        var stopped = await manager.StopAsync(task.Id);
        Assert.Equal("Stopped", stopped.State); Assert.False(manager.HasActiveRun(run.Id));
        Assert.Contains("READY", stopped.Log);
        Assert.Contains(git.Commands, c => c.StartsWith("worktree lock")); Assert.Contains(git.Commands, c => c.StartsWith("worktree unlock"));
        Assert.Equal(task.Status, (await store.GetTaskAsync(task.Id))!.Status);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(view.Services[0].Url));
    }
    [Fact] public async Task Stale_versions_and_wrong_result_never_start_processes()
    {
        await Assert.ThrowsAsync<ConflictException>(() => manager.StartAsync(task.Id, run.Id, Commit, task.Version - 1, project.Version, default));
        await Assert.ThrowsAsync<ConflictException>(() => manager.StartAsync(task.Id, run.Id, "different", task.Version, project.Version, default));
        await Assert.ThrowsAsync<ConflictException>(() => manager.StartAsync(task.Id, run.Id, Commit, task.Version, project.Version + 1, default));
        Assert.Equal("Stopped", manager.Get(task.Id).State);
    }
    [Fact] public async Task Startup_failure_is_reported_and_can_be_restarted_after_correction()
    {
        File.Delete(Path.Combine(workspace, "server.cjs"));
        await Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (manager.HasActiveRun(run.Id)) await Task.Delay(50, timeout.Token);
        var failed = manager.Get(task.Id); Assert.Equal("Failed", failed.State); Assert.NotNull(failed.Error); Assert.Contains("MODULE_NOT_FOUND", failed.Log);
        Assert.Contains(git.Commands, c => c.StartsWith("worktree unlock"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "server.cjs"), "require('http').createServer((q,r)=>r.end('recovered')).listen(Number(process.argv[2]),'127.0.0.1');");
        await Start(); var restarted = await WaitFor("Running");
        using var client = new HttpClient(); Assert.Equal("recovered", await client.GetStringAsync(restarted.Services[0].Url));
        await manager.StopAsync(task.Id);
    }
    [Fact] public async Task Api_shutdown_stops_preview_services_and_can_be_disposed_twice()
    {
        await Start(); var view = await WaitFor("Running");
        await manager.DisposeAsync(); await manager.DisposeAsync();
        Assert.False(manager.HasActiveRun(run.Id));
        using var client = new HttpClient(); await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(view.Services[0].Url));
    }
    [Fact] public async Task Occupied_explicit_port_is_rejected_instead_of_opening_some_other_server()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try {
            var service = project.PreviewServices![0] with { Port = ((IPEndPoint)listener.LocalEndpoint).Port };
            project = await store.SetExecutionSettingsAsync(project.Id, project.Version, [], null, [service], null);
            await Assert.ThrowsAsync<ConflictException>(() => Start());
            Assert.Equal("Stopped", manager.Get(task.Id).State);
        } finally { listener.Stop(); }
    }
}
