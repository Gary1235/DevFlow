using System.Diagnostics;
using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Runners;
using Xunit;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Tests;

public sealed class InfrastructureTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Environment.GetEnvironmentVariable("DEVFLOW_GIT_TEST_ROOT") ?? Path.GetTempPath(), "devflow-infra-" + Guid.NewGuid());
    public Task InitializeAsync() { Directory.CreateDirectory(directory); return Task.CompletedTask; }
    public Task DisposeAsync()
    {
        var allowedRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("DEVFLOW_GIT_TEST_ROOT") ?? Path.GetTempPath());
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(allowedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(target).StartsWith("devflow-infra-", StringComparison.Ordinal))
            throw new InvalidOperationException("Test cleanup target is outside its scratch directory.");
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(target, true);
        return Task.CompletedTask;
    }
    [Fact] public async Task Process_runner_captures_output_without_shell()
    {
        var result = await new ProcessRunner().RunAsync("dotnet", ["--version"], directory, null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("10.", result.StandardOutput);
    }
    [Fact] public async Task Process_runner_cancellation_terminates_child_promptly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(
            "powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"], directory, null, cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }
    [Fact] public async Task Worktree_snapshot_preserves_original_checkout()
    {
        var process = new ProcessRunner();
        async Task Git(params string[] args)
        {
            var r = await process.RunAsync("git", args, directory, null, CancellationToken.None);
            Assert.True(r.ExitCode == 0, r.StandardError);
        }
        await Git("init", "-b", "main");
        await Git("config", "user.name", "DevFlow Tests");
        await Git("config", "user.email", "tests@devflow.local");
        await File.WriteAllTextAsync(Path.Combine(directory, "feature.txt"), "before");
        await Git("add", ".");
        await Git("commit", "-m", "initial");
        // The workspace root is outside the repository to avoid changing its working tree.
        var root = Path.Combine(directory, "..", "devflow-worktrees-" + Guid.NewGuid());
        try
        {
            var manager = new GitWorkspaceManager(process, root);
            var workspace = await manager.CreateAsync(new(Guid.NewGuid(), "Demo", directory, "main", ["test"]), Guid.NewGuid(), CancellationToken.None);
            await manager.AssertUnchangedAsync(workspace, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "feature.txt"), "after");
            await Assert.ThrowsAsync<ConflictException>(() => manager.AssertUnchangedAsync(workspace, CancellationToken.None));
            var result = await manager.SnapshotAsync(workspace, CancellationToken.None);
            await Assert.ThrowsAsync<ConflictException>(() => manager.AssertUnchangedAsync(workspace, CancellationToken.None));
            Assert.NotEqual(workspace.BaseCommit, result);
            Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(directory, "feature.txt")));
            Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "feature.txt")));
            var diff = await new GitResultDiffReader(process).ReadAsync(new(Guid.NewGuid(), "Demo", directory, "main", ["test"]),
                new(Guid.NewGuid(), Guid.NewGuid(), "AwaitingReview", workspace.Path, workspace.BaseCommit, result, null,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            Assert.Contains("-before", diff.Patch);
            Assert.Contains("+after", diff.Patch);
            await Git("worktree", "remove", workspace.Path);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class CaptureProcess(int exitCode = 0) : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public string? Input { get; private set; }
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string w, string? input, CancellationToken ct)
        { Arguments = a; Input = input; return Task.FromResult(new ProcessResult(exitCode, "{}", "diagnostic")); }
    }
    [Fact] public async Task Codex_runner_passes_prompt_via_stdin_and_preserves_sandbox()
    {
        var process = new CaptureProcess();
        var task = WorkTask.Create(Guid.NewGuid(), "API", "Build", "Tests").SetPlan("TDD").ApprovePlan()
            with { RevisionInstructions = "Fix missing IDs" };
        await new CodexCliRunner(process, "codex.exe", directory).ExecuteAsync(task, new(directory, "base"), Guid.NewGuid(), TddPhase.Red, CancellationToken.None);
        Assert.Contains("workspace-write", process.Arguments);
        Assert.Contains("--json", process.Arguments);
        Assert.Equal("-", process.Arguments.Last());
        Assert.Contains("Red", process.Input);
        Assert.Contains("Tests", process.Input);
        Assert.Contains("Fix missing IDs", process.Input);
    }
    [Fact] public async Task Codex_nonzero_exit_is_failure()
    {
        var task = WorkTask.Create(Guid.NewGuid(), "API", "Build", "Tests").SetPlan("TDD").ApprovePlan();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CodexCliRunner(new CaptureProcess(1), "codex.exe", directory)
            .ExecuteAsync(task, new(directory, "base"), Guid.NewGuid(), TddPhase.Green, CancellationToken.None));
    }
    [Fact] public async Task Restart_marks_interrupted_run_failed_without_requeueing()
    {
        var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
        await store.InitializeAsync();
        var project = new ProjectDefinition(Guid.NewGuid(), "Demo", directory, "main", ["test"]);
        await store.AddProjectAsync(project);
        var task = WorkTask.Create(project.Id, "API", "Build", "Tests").SetPlan("TDD").ApprovePlan();
        await store.AddTaskAsync(task);
        var claim = (await store.ClaimNextAsync())!;
        await store.SetWorkspaceAsync(claim.Run.Id, new(directory, "base"));
        await store.RecoverInterruptedAsync();
        Assert.Equal(Status.Failed, (await store.GetTaskAsync(task.Id))!.Status);
        var run = (await store.ListRunsAsync(task.Id)).Single();
        Assert.Equal(directory, run.WorktreePath);
        Assert.NotNull(run.FinishedAt);
        Assert.Contains("interrupted", run.Error);
        Assert.Null(await store.ClaimNextAsync());
    }
}
