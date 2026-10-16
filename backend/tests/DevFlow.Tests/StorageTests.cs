using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
using Status = DevFlow.Core.TaskStatus;

namespace DevFlow.Tests;

public sealed class StorageTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-storage-" + Guid.NewGuid());
    private SqliteTaskStore store = null!;
    private string artifacts = "", worktrees = "", source = "";
    private readonly Clock clock = new();
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow.AddDays(60); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NoGit : IProcessRunner
    {
        public int Calls;
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string directory, string? input, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("Git must not run in this test."); }
    }
    public async Task InitializeAsync()
    {
        artifacts = Path.Combine(root, "artifacts"); worktrees = Path.Combine(root, "worktrees"); source = Path.Combine(root, "source");
        Directory.CreateDirectory(source); Directory.CreateDirectory(artifacts); Directory.CreateDirectory(worktrees);
        store = new(Path.Combine(root, "state.db")); await store.InitializeAsync();
    }
    public Task DisposeAsync()
    {
        Assert.StartsWith(Path.GetTempPath(), Path.GetFullPath(root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true); return Task.CompletedTask;
    }
    private StorageManager Manager(IProcessRunner? process = null) => new(store, process ?? new NoGit(), artifacts, worktrees, clock);

    private async Task<(ProjectDefinition Project, WorkTask Task, TaskRun Run)> Seed(Status status = Status.Merged, bool active = false, string? error = null)
    {
        var project = new ProjectDefinition(Guid.NewGuid(), "Demo", source, "HEAD", ["test"]);
        await store.AddProjectAsync(project);
        var task = WorkTask.Create(project.Id, "Storage task", "Requirement", "Criteria") with { Status = Status.Queued };
        await store.AddTaskAsync(task);
        var claim = (await store.ClaimNextAsync())!;
        var runPath = Path.Combine(worktrees, claim.Run.Id.ToString("N"));
        await store.SetWorkspaceAsync(claim.Run.Id, new(runPath, new string('a', 40)), default);
        if (!active) await store.CompleteRunAsync(claim.Run.Id, true, runPath, new string('a', 40), new string('a', 40), error);
        var current = (await store.GetTaskAsync(task.Id))!;
        await store.SaveTaskAsync(current with { Status = status, Version = current.Version + 1 }, current.Version);
        return (project, (await store.GetTaskAsync(task.Id))!, (await store.GetRunAsync(claim.Run.Id))!);
    }
    private async Task<string> Log(TaskRun run)
    {
        var directory = Path.Combine(artifacts, run.Id.ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Baseline"));
        await File.WriteAllTextAsync(Path.Combine(directory, "stdout.log"), "large raw log");
        await File.WriteAllTextAsync(Path.Combine(directory, "Baseline", "evidence.json"), "{\"ExitCode\":0,\"Total\":2,\"Failed\":0}");
        return directory;
    }

    [Fact] public async Task Artifact_cleanup_preserves_validation_summaries_database_and_task_history()
    {
        var seeded = await Seed(); var directory = await Log(seeded.Run); var manager = Manager();
        var overview = await manager.OverviewAsync(default);
        Assert.True(overview.Categories.Single(c => c.Id == "database").Bytes > 0);
        Assert.True(overview.Categories.Single(c => c.Id == "artifacts").Bytes > 0);
        var preview = await manager.PreviewAsync(30, true, false, default);
        var item = Assert.Single(preview.Items); Assert.True(item.CanClean); Assert.Equal(1, item.Files);
        var report = await manager.CleanAsync(preview.Id, [item.Key], default);
        Assert.True(Assert.Single(report.Items).Success);
        Assert.False(File.Exists(Path.Combine(directory, "stdout.log")));
        Assert.True(File.Exists(Path.Combine(directory, "Baseline", "evidence.json")));
        Assert.Equal(seeded.Task, await store.GetTaskAsync(seeded.Task.Id));
        Assert.NotNull(await store.GetRunAsync(seeded.Run.Id));
        Assert.Contains(await store.GetEventsAsync(seeded.Task.Id), e => e.Kind == "StorageCleaned");
        Assert.Single(await new RunArtifactReader(artifacts).ValidationsAsync(seeded.Run.Id));
        Assert.Empty((await manager.PreviewAsync(30, true, false, default)).Items);
        await Assert.ThrowsAsync<ConflictException>(() => manager.CleanAsync(preview.Id, [item.Key], default));
    }

    [Theory]
    [InlineData(Status.AwaitingReview, false)]
    [InlineData(Status.Failed, false)]
    [InlineData(Status.Cancelled, true)]
    public async Task Reviewable_failed_and_still_running_cancelled_tasks_are_protected(Status status, bool active)
    {
        var seeded = await Seed(status, active); var directory = await Log(seeded.Run);
        Assert.Empty((await Manager().PreviewAsync(0, true, false, default)).Items);
        Assert.True(File.Exists(Path.Combine(directory, "stdout.log")));
    }

    [Fact] public async Task Retention_and_expiry_and_invalid_keys_cannot_trigger_deletion()
    {
        var seeded = await Seed(); var directory = await Log(seeded.Run); var manager = Manager();
        Assert.Empty((await manager.PreviewAsync(90, true, false, default)).Items);
        var preview = await manager.PreviewAsync(30, true, false, default);
        await Assert.ThrowsAsync<ArgumentException>(() => manager.CleanAsync(preview.Id, ["../../source"], default));
        clock.Now = clock.Now.AddMinutes(6);
        await Assert.ThrowsAsync<ConflictException>(() => manager.CleanAsync(preview.Id, [preview.Items[0].Key], default));
        Assert.True(File.Exists(Path.Combine(directory, "stdout.log")));
    }

    [Fact] public async Task Interrupted_worker_runs_are_preserved_for_orphan_process_inspection()
    {
        var seeded = await Seed(Status.Cancelled, error: "Worker interrupted; inspect retained worktree and any orphan process before retrying.");
        var directory = await Log(seeded.Run);
        Assert.Empty((await Manager().PreviewAsync(0, true, false, default)).Items);
        Assert.True(File.Exists(Path.Combine(directory, "stdout.log")));
    }

    [Fact] public async Task Files_changed_after_preview_are_skipped_and_no_files_are_removed()
    {
        var seeded = await Seed(); var directory = await Log(seeded.Run); var manager = Manager();
        var preview = await manager.PreviewAsync(30, true, false, default);
        await File.WriteAllTextAsync(Path.Combine(directory, "new.log"), "new evidence");
        var report = await manager.CleanAsync(preview.Id, [preview.Items[0].Key], default);
        Assert.False(Assert.Single(report.Items).Success);
        Assert.True(File.Exists(Path.Combine(directory, "stdout.log")));
        Assert.True(File.Exists(Path.Combine(directory, "new.log")));
    }

    [Fact] public async Task Source_repository_and_paths_outside_managed_worktree_root_are_blocked_before_git()
    {
        var seeded = await Seed(); var noGit = new NoGit();
        // Even a stored worktree path cannot redirect cleanup to a source directory.
        await store.SetWorkspaceAsync(seeded.Run.Id, new(source, new string('a', 40)), default);
        var item = Assert.Single((await Manager(noGit).PreviewAsync(30, false, true, default)).Items);
        Assert.False(item.CanClean); Assert.Equal(0, noGit.Calls);
        Assert.True(Directory.Exists(source));
    }

    [Fact] public async Task Artifact_directory_registered_as_a_source_repository_is_never_cleanable()
    {
        var seeded = await Seed(); var directory = await Log(seeded.Run);
        await store.AddProjectAsync(new(Guid.NewGuid(), "Protected source", directory, "main", ["test"]));
        var preview = await Manager().PreviewAsync(30, true, false, default);
        Assert.False(Assert.Single(preview.Items).CanClean);
        Assert.True(File.Exists(Path.Combine(directory, "stdout.log")));
    }

    [Fact] public async Task Changed_task_state_invalidates_the_confirmed_item()
    {
        var seeded = await Seed(); var directory = await Log(seeded.Run); var manager = Manager();
        var preview = await manager.PreviewAsync(30, true, false, default);
        var current = (await store.GetTaskAsync(seeded.Task.Id))!;
        await store.SaveTaskAsync(current with { Status = Status.Running, Version = current.Version + 1 }, current.Version);
        var result = await manager.CleanAsync(preview.Id, [preview.Items[0].Key], default);
        Assert.False(Assert.Single(result.Items).Success);
        Assert.True(File.Exists(Path.Combine(directory, "stdout.log")));
    }

    [Fact] public async Task Real_git_worktree_cleanup_rejects_changes_and_retains_the_result_branch()
    {
        var runner = new ProcessRunner();
        async Task<string> Git(params string[] args)
        {
            var result = await runner.RunAsync("git", args, source, null, default);
            Assert.True(result.ExitCode == 0, result.StandardError); return result.StandardOutput.Trim();
        }
        await Git("init");
        await File.WriteAllTextAsync(Path.Combine(source, "app.txt"), "original");
        await File.WriteAllTextAsync(Path.Combine(source, ".gitignore"), "bin/\n");
        await Git("add", "app.txt", ".gitignore");
        await Git("-c", "user.name=DevFlow", "-c", "user.email=devflow@localhost", "commit", "-m", "initial");
        var seeded = await Seed();
        var workspace = await new GitWorkspaceManager(runner, worktrees).CreateAsync(seeded.Project, seeded.Run.Id, default);
        await store.SetWorkspaceAsync(seeded.Run.Id, workspace, default);
        // Replace the fixture's placeholder result with this repository's real commit.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open(); using var update = connection.CreateCommand();
            var run = (await store.GetRunAsync(seeded.Run.Id))! with { ResultCommit = workspace.BaseCommit };
            update.CommandText = "UPDATE Runs SET Payload=$payload WHERE Id=$id";
            update.Parameters.AddWithValue("$payload", System.Text.Json.JsonSerializer.Serialize(run)); update.Parameters.AddWithValue("$id", run.Id.ToString()); update.ExecuteNonQuery();
        }
        var manager = Manager(runner);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "app.txt"), "user edits");
        var dirty = Assert.Single((await manager.PreviewAsync(30, false, true, default)).Items);
        Assert.False(dirty.CanClean); Assert.True(File.Exists(Path.Combine(workspace.Path, "app.txt")));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "app.txt"), "original");
        Directory.CreateDirectory(Path.Combine(workspace.Path, "bin"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "bin", "generated.cache"), "ignored build output");
        var preview = await manager.PreviewAsync(30, false, true, default);
        var clean = Assert.Single(preview.Items); Assert.True(clean.CanClean, clean.Reason);
        var result = await manager.CleanAsync(preview.Id, [clean.Key], default);
        Assert.True(Assert.Single(result.Items).Success, result.Items[0].Message);
        Assert.False(Directory.Exists(workspace.Path));
        Assert.True(File.Exists(Path.Combine(source, "app.txt")));
        await Git("show-ref", "--verify", "refs/heads/devflow/" + seeded.Run.Id.ToString("N"));
    }
}
