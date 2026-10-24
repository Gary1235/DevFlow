using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Tests;

public sealed class RoundTwoWorkflowTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-round2-" + Guid.NewGuid());
    private readonly ProcessRunner process = new(); private SqliteTaskStore store = null!; private ProjectDefinition project = null!;
    private string Repo => Path.Combine(root, "repo"); private string Trees => Path.Combine(root, "worktrees"); private string Artifacts => Path.Combine(root, "artifacts");
    public async Task InitializeAsync() {
        Directory.CreateDirectory(Repo); store = new(Path.Combine(root, "tasks.db")); await store.InitializeAsync();
        await Git("init", "-b", "main"); await File.WriteAllTextAsync(Path.Combine(Repo, "feature.txt"), "base\n");
        await File.WriteAllTextAsync(Path.Combine(Repo, ".gitignore"), "node_modules/\n"); await Commit("seed");
        project = new(Guid.NewGuid(), "Workflow", Repo, "main", ["test"], ValidationProfiles: [new("build", "Build", ".", "node", ["-e", "process.stdout.write('probe ok')"])], DefaultValidationProfileId: "build");
        await store.AddProjectAsync(project);
    }
    public Task DisposeAsync() {
        if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("devflow-round2-", StringComparison.Ordinal)) throw new InvalidOperationException();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true); return Task.CompletedTask;
    }
    private async Task<string> Git(params string[] args) {
        var result = await process.RunAsync("git", args, Repo, null, default); Assert.True(result.ExitCode == 0, result.StandardError); return result.StandardOutput.Trim();
    }
    private async Task Commit(string message) { await Git("add", "."); await Git("-c", "user.name=DevFlow", "-c", "user.email=devflow@localhost", "commit", "-m", message); }
    private async Task<WorkTask> Seed() {
        var task = WorkTask.Create(project.Id, "Feature", "Add feature", "Preserve target changes").SetPlan("Implement and validate.").ApprovePlan() with { Policy = new(false) };
        await store.AddTaskAsync(task); return task;
    }
    private ExecutionCoordinator Coordinator(IAgentRunner agent) => new(store, agent, new GitWorkspaceManager(process, Trees), new Validator(), new Reviewer(), new GitResultDiffReader(process), new GitExecutionPolicyGuard(process));
    [Fact] public async Task Diverged_result_is_integrated_validated_reviewed_and_only_then_fast_forwarded()
    {
        var task = await Seed();
        await Coordinator(new Agent(async (_, w) => await File.WriteAllTextAsync(Path.Combine(w.Path, "feature.txt"), "feature\n"))).ExecuteNextAsync();
        var source = (await store.GetTaskAsync(task.Id))!; var run = Assert.Single(await store.ListRunsAsync(task.Id));
        await File.WriteAllTextAsync(Path.Combine(Repo, "feature.txt"), "target\n"); await Commit("target advanced"); var target = await Git("rev-parse", "HEAD");
        var integration = await store.CreateIntegrationTaskAsync(task.Id, source.Version, run.Id, run.ResultCommit!, target, default);
        var sawConflict = false;
        // Ensure the original planner is an independent integration reviewer.
        await store.SaveTaskAsync(integration with { Integration = integration.Integration! with { Reviewers = [new("codex")] }, Version = integration.Version + 1 }, integration.Version);
        await Coordinator(new Agent(async (_, w) => {
            sawConflict = (await File.ReadAllTextAsync(Path.Combine(w.Path, "feature.txt"))).Contains("<<<<<<<", StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(w.Path, "feature.txt"), "feature and target\n");
        })).ExecuteNextAsync();
        Assert.True(sawConflict); Assert.Equal(target, await Git("rev-parse", "HEAD")); Assert.Equal("target\n", await File.ReadAllTextAsync(Path.Combine(Repo, "feature.txt")));
        var current = (await store.GetTaskAsync(integration.Id))!; var integrated = Assert.Single(await store.ListRunsAsync(integration.Id));
        Assert.Equal(Status.AwaitingReview, current.Status); Assert.Equal(target, integrated.BaseCommit); Assert.Equal("Pass", Assert.Single(integrated.AiReviews!).Verdict);
        await Git("merge-base", "--is-ancestor", target, integrated.ResultCommit!); await Git("merge-base", "--is-ancestor", run.ResultCommit!, integrated.ResultCommit!);
        await store.ReviewAsync(current.Id, integrated.Id, integrated.ResultCommit!, ReviewDecision.Approve, null, current.Version);
        current = (await store.GetTaskAsync(current.Id))!;
        var merge = await new MergeCoordinator(store, new GitMergeService(process)).MergeAsync(current.Id, integrated.Id, integrated.ResultCommit!, target, current.Version);
        Assert.Equal(MergeState.Succeeded, merge.State); Assert.Equal("feature and target\n", (await File.ReadAllTextAsync(Path.Combine(Repo, "feature.txt"))).ReplaceLineEndings("\n"));
    }
    [Fact] public async Task Recovery_copies_partial_work_checks_staleness_and_keeps_the_failed_worktree_unchanged()
    {
        var task = await Seed();
        await Coordinator(new Agent(async (_, w) => {
            await File.WriteAllTextAsync(Path.Combine(w.Path, "feature.txt"), "partial\n");
            await File.WriteAllTextAsync(Path.Combine(w.Path, "note.txt"), "untracked evidence");
            throw new InvalidOperationException("interrupted implementation");
        })).ExecuteNextAsync();
        var failed = (await store.GetTaskAsync(task.Id))!; Assert.Equal(Status.Failed, failed.Status);
        var run = Assert.Single(await store.ListRunsAsync(task.Id)); var recovery = new WorkspaceRecoveryService(store, process, Trees, Artifacts);
        var preview = await recovery.PreviewAsync(task.Id, default);
        Assert.Contains("feature.txt", preview.Files); Assert.Contains("note.txt", preview.Files);
        await Assert.ThrowsAsync<ArgumentException>(() => recovery.RecoverAsync(task.Id, preview.Id, false, default));
        await File.AppendAllTextAsync(Path.Combine(run.WorktreePath!, "note.txt"), " changed");
        await Assert.ThrowsAsync<ConflictException>(() => recovery.RecoverAsync(task.Id, preview.Id, true, default));
        preview = await recovery.PreviewAsync(task.Id, default);
        var restored = await recovery.RecoverAsync(task.Id, preview.Id, true, default); var sawPartial = false;
        await Coordinator(new Agent(async (_, w) => {
            sawPartial = (await File.ReadAllTextAsync(Path.Combine(w.Path, "feature.txt"))).ReplaceLineEndings("\n") == "partial\n" && File.Exists(Path.Combine(w.Path, "note.txt"));
            await File.WriteAllTextAsync(Path.Combine(w.Path, "feature.txt"), "repaired\n");
        })).ExecuteNextAsync();
        Assert.True(sawPartial); Assert.Equal("partial\n", await File.ReadAllTextAsync(Path.Combine(run.WorktreePath!, "feature.txt")));
        Assert.Equal(Status.Failed, (await store.GetTaskAsync(task.Id))!.Status);
        var done = (await store.GetTaskAsync(restored.Id))!; Assert.Equal(Status.AwaitingReview, done.Status); Assert.True(done.Recovery!.Applied);
    }
    [Fact] public async Task Settings_probe_executes_in_a_disposable_worktree_and_reports_after_cleanup()
    {
        var target = await Git("rev-parse", "HEAD"); var service = new ValidationProbeService(store, process, Trees, Artifacts, default);
        var probe = service.Start(project, project.ValidationProfiles![0]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (service.Get(probe.Id).State == "Running") await Task.Delay(50, timeout.Token);
        var result = service.Get(probe.Id); Assert.Equal("Completed", result.State); Assert.Null(result.RetainedWorkspace);
        Assert.False(Directory.Exists(Path.Combine(Trees, probe.Id.ToString("N")))); Assert.Equal(target, await Git("rev-parse", "HEAD"));
        Assert.Equal(0, result.Evidence!.ExitCode);
    }
    [Fact] public async Task Git_worktree_aliases_share_repository_identity_and_the_same_queue_lock()
    {
        var task = await Seed(); await store.ClaimNextAsync();
        var alias = await new GitWorkspaceManager(process, Trees).CreateAsync(project, Guid.NewGuid(), default);
        Assert.Equal(RepositoryIdentity.Key(Repo), RepositoryIdentity.Key(alias.Path));
        var other = project with { Id = Guid.NewGuid(), RepositoryPath = alias.Path }; await store.AddProjectAsync(other);
        await store.AddTaskAsync(WorkTask.Create(other.Id, "Alias task", "Build", "Validate").SetPlan("Plan").ApprovePlan());
        Assert.Null(await store.ClaimNextAsync()); Assert.Equal(Status.Running, (await store.GetTaskAsync(task.Id))!.Status);
    }
    [Fact] public async Task Api_shutdown_waits_for_owned_probe_process_and_worktree_cleanup()
    {
        using var shutdown = new CancellationTokenSource();
        var observed = new ObserveValidation(process);
        var service = new ValidationProbeService(store, observed, Trees, Artifacts, shutdown.Token);
        var probe = service.Start(project, project.ValidationProfiles![0] with { Arguments = ["-e", "setInterval(()=>{},1000)"] });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try {
            while (!observed.Started && service.Get(probe.Id).State == "Running") await Task.Delay(50, timeout.Token);
            Assert.True(observed.Started);
        }
        finally { shutdown.Cancel(); await service.WaitForStopAsync(timeout.Token); }
        Assert.Equal("Failed", service.Get(probe.Id).State); Assert.Null(service.Get(probe.Id).RetainedWorkspace);
        Assert.False(Directory.Exists(Path.Combine(Trees, probe.Id.ToString("N"))));
    }
    [Fact] public async Task Probe_results_remain_available_after_reopening_the_service_and_database()
    {
        var service = new ValidationProbeService(store, process, Trees, Artifacts, default);
        var probe = service.Start(project, project.ValidationProfiles![0]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await service.WaitForStopAsync(timeout.Token);
        var reopened = new SqliteTaskStore(store.DatabasePath); await reopened.InitializeAsync();
        var next = new ValidationProbeService(reopened, process, Trees, Artifacts, default); next.RecoverInterrupted();
        var result = next.Get(probe.Id);
        Assert.Equal("Completed", result.State); Assert.NotNull(result.FinishedAt); Assert.Equal(project.Id, result.ProjectId);
        Assert.Equal(0, result.Evidence!.ExitCode); Assert.Equal(probe.Id, Assert.Single(next.List(project.Id)).Id);
    }
    [Fact] public async Task Lost_probe_ownership_is_marked_interrupted_without_deleting_or_rerunning_the_worktree()
    {
        var id = Guid.NewGuid(); var workspace = Path.Combine(Trees, id.ToString("N")); Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "evidence.txt"), "retain");
        store.SaveValidationProbe(new(id, "Running", ProjectId: project.Id, CreatedAt: DateTimeOffset.UtcNow, WorkspacePath: workspace));
        var service = new ValidationProbeService(store, process, Trees, Artifacts, default); service.RecoverInterrupted();
        var result = service.Get(id); Assert.Equal("Interrupted", result.State); Assert.Equal(workspace, result.RetainedWorkspace);
        Assert.NotNull(result.FinishedAt); Assert.Equal("retain", await File.ReadAllTextAsync(Path.Combine(workspace, "evidence.txt")));
        service.RecoverInterrupted(); Assert.Equal(result, service.Get(id));
    }
    [Fact] public async Task Cancelling_a_probe_stops_its_process_cleans_up_and_releases_the_cross_service_lock()
    {
        var observed = new ObserveValidation(process);
        var service = new ValidationProbeService(store, observed, Trees, Artifacts, default);
        var other = new ValidationProbeService(store, process, Trees, Artifacts, default);
        var probe = service.Start(project, project.ValidationProfiles![0] with { Arguments = ["-e", "setInterval(()=>{},1000)"] });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try {
            while (!observed.Started && service.Get(probe.Id).State == "Running") await Task.Delay(50, timeout.Token);
            Assert.True(observed.Started);
            Assert.Throws<ConflictException>(() => other.Start(project, project.ValidationProfiles[0]));
            other.RecoverInterrupted(); Assert.Equal("Running", other.Get(probe.Id).State);
            Assert.Throws<ConflictException>(() => other.Cancel(probe.Id));
            Assert.Equal("Stopping", service.Cancel(probe.Id).State);
        }
        finally { service.Cancel(probe.Id); await service.WaitForStopAsync(timeout.Token); }
        Assert.Equal("Cancelled", service.Get(probe.Id).State); Assert.False(Directory.Exists(Path.Combine(Trees, probe.Id.ToString("N"))));
        var next = other.Start(project, project.ValidationProfiles[0]); await other.WaitForStopAsync(timeout.Token);
        Assert.Equal("Completed", other.Get(next.Id).State);
        Assert.Equal("Cancelled", other.Cancel(probe.Id).State); // Repeated cancellation does not rerun anything.
    }
    private sealed class ObserveValidation(IProcessRunner inner) : IProcessRunner {
        public volatile bool Started;
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string cwd, string? input, CancellationToken ct) {
            if (a.Contains("-e")) Started = true;
            return inner.RunAsync(e, a, cwd, input, ct);
        }
    }
    private sealed class Agent(Func<WorkTask, Workspace, Task> execute) : IAgentRunner {
        public Task ExecuteAsync(WorkTask t, Workspace w, Guid id, TddPhase phase, CancellationToken ct) => execute(t, w);
    }
    private sealed class Validator : IValidationRunner {
        public Task<ValidationEvidence> ValidateAsync(ProjectDefinition p, Workspace w, Guid id, TddPhase phase, CancellationToken ct) => Task.FromResult(new ValidationEvidence(0, 0, 0, [], [], "Command"));
    }
    private sealed class Reviewer : IPlanningRunner {
        public Task<string> PlanAsync(WorkTask t, Workspace w, Guid id, CancellationToken ct) => Task.FromResult("{\"verdict\":\"Pass\",\"summary\":\"Integration preserves both sides and passed validation.\"}");
    }
}
