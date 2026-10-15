using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Tests;

public sealed class MergeTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-merge-" + Guid.NewGuid());
    private SqliteTaskStore Store => new(Path.Combine(directory, "tasks.db"));
    private static string Base => new('a', 40);
    private static string Result => new('b', 40);
    public Task InitializeAsync() => Store.InitializeAsync();
    public Task DisposeAsync() { Directory.Delete(directory, true); return Task.CompletedTask; }
    private async Task<(WorkTask Task, TaskRun Run)> Seed(bool approve = true)
    {
        var p = new ProjectDefinition(Guid.NewGuid(), "Demo", directory, "main", ["test"]);
        await Store.AddProjectAsync(p);
        var task = WorkTask.Create(p.Id, "API", "Build", "Tests").SetPlan("TDD").ApprovePlan();
        await Store.AddTaskAsync(task);
        var claim = (await Store.ClaimNextAsync())!;
        await Store.CompleteRunAsync(claim.Run.Id, true, directory, Base, Result, null);
        task = (await Store.GetTaskAsync(task.Id))!;
        if (approve) await Store.ReviewAsync(task.Id, claim.Run.Id, Result, ReviewDecision.Approve, null, task.Version);
        return ((await Store.GetTaskAsync(task.Id))!, (await Store.ListRunsAsync(task.Id)).Single());
    }
    private sealed class FakeGit(Exception? error = null, MergeObservation observation = MergeObservation.Applied) : IGitMergeService
    {
        public int Calls { get; private set; }
        public Task<RepositoryState> InspectAsync(ProjectDefinition p, CancellationToken ct = default) => Task.FromResult(new RepositoryState("main", Base, true));
        public Task<string> MergeAsync(ProjectDefinition p, TaskMerge m, CancellationToken ct = default)
        { Calls++; if (error != null) throw error; return Task.FromResult(Result); }
        public Task<MergeObservation> ObserveAsync(ProjectDefinition p, TaskMerge m, CancellationToken ct = default) => Task.FromResult(observation);
    }
    private sealed class BlockingGit : IGitMergeService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RepositoryState> InspectAsync(ProjectDefinition p, CancellationToken ct = default) => Task.FromResult(new RepositoryState("main", Base, true));
        public async Task<string> MergeAsync(ProjectDefinition p, TaskMerge m, CancellationToken ct = default)
        { Started.SetResult(); return await Finish.Task.WaitAsync(ct); }
        public Task<MergeObservation> ObserveAsync(ProjectDefinition p, TaskMerge m, CancellationToken ct = default) => Task.FromResult(MergeObservation.NotApplied);
    }
    private sealed class LaterClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddMinutes(3);
    }
    [Fact] public async Task Reconcile_cannot_unlock_a_merge_that_may_still_be_running()
    {
        var (task, run) = await Seed(); var git = new BlockingGit();
        var coordinator = new MergeCoordinator(Store, git, new LaterClock());
        var running = coordinator.MergeAsync(task.Id, run.Id, Result, Base, task.Version);
        try
        {
            await git.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<ConflictException>(() => coordinator.ReconcileAsync(task.Id));
            Assert.Equal(Status.Merging, (await Store.GetTaskAsync(task.Id))!.Status);
        }
        finally { git.Finish.SetResult(Result); await running; }
    }
    [Fact] public async Task Approved_merge_records_result_and_repeated_request_is_idempotent()
    {
        var (task, run) = await Seed(); var git = new FakeGit();
        var coordinator = new MergeCoordinator(Store, git);
        var merge = await coordinator.MergeAsync(task.Id, run.Id, Result, Base, task.Version);
        Assert.Equal(MergeState.Succeeded, merge.State);
        Assert.Equal(Result, merge.MergedCommit);
        Assert.Equal(Status.Merged, (await Store.GetTaskAsync(task.Id))!.Status);
        Assert.Equal(merge, await coordinator.MergeAsync(task.Id, run.Id, Result, Base, task.Version));
        Assert.Equal(1, git.Calls);
    }
    [Fact] public async Task Unapproved_task_never_calls_git()
    {
        var (task, run) = await Seed(false); var git = new FakeGit();
        await Assert.ThrowsAsync<ConflictException>(() => new MergeCoordinator(Store, git).MergeAsync(task.Id, run.Id, Result, Base, task.Version));
        Assert.Equal(0, git.Calls);
        Assert.Empty(await Store.ListMergesAsync(task.Id));
    }
    [Fact] public async Task Wrong_result_or_stale_task_version_is_rejected()
    {
        var (task, run) = await Seed();
        await Assert.ThrowsAsync<ConflictException>(() => Store.BeginMergeAsync(task.Id, run.Id, new string('c', 40), Base, task.Version));
        await Assert.ThrowsAsync<ConflictException>(() => Store.BeginMergeAsync(task.Id, run.Id, Result, Base, task.Version - 1));
    }
    [Fact] public async Task Known_rejection_restores_approval_and_keeps_failed_attempt()
    {
        var (task, run) = await Seed();
        await Assert.ThrowsAsync<ConflictException>(() => new MergeCoordinator(Store, new FakeGit(new ConflictException("Dirty worktree")))
            .MergeAsync(task.Id, run.Id, Result, Base, task.Version));
        Assert.Equal(Status.Approved, (await Store.GetTaskAsync(task.Id))!.Status);
        Assert.Equal(MergeState.Failed, (await Store.ListMergesAsync(task.Id)).Single().State);
    }
    [Fact] public async Task Unknown_outcome_blocks_retries_until_reconciled()
    {
        var (task, run) = await Seed(); var git = new FakeGit(new OperationCanceledException());
        var coordinator = new MergeCoordinator(Store, git);
        await Assert.ThrowsAsync<MergeUncertainException>(() => coordinator.MergeAsync(task.Id, run.Id, Result, Base, task.Version));
        Assert.Equal(Status.Merging, (await Store.GetTaskAsync(task.Id))!.Status);
        Assert.Equal(MergeState.Uncertain, (await Store.ListMergesAsync(task.Id)).Single().State);
        await Assert.ThrowsAsync<ConflictException>(() => coordinator.MergeAsync(task.Id, run.Id, Result, Base, task.Version));
        Assert.Equal(MergeState.Succeeded, (await coordinator.ReconcileAsync(task.Id)).State);
        Assert.Equal(Status.Merged, (await Store.GetTaskAsync(task.Id))!.Status);
    }
    [Fact] public async Task Interrupted_attempt_can_be_reconciled_without_repeating_git_merge()
    {
        var (task, run) = await Seed();
        await Store.BeginMergeAsync(task.Id, run.Id, Result, Base, task.Version);
        var git = new FakeGit(observation: MergeObservation.NotApplied);
        var reconciled = await new MergeCoordinator(Store, git, new LaterClock()).ReconcileAsync(task.Id);
        Assert.Equal(MergeState.Failed, reconciled.State);
        Assert.Equal(Status.Approved, (await Store.GetTaskAsync(task.Id))!.Status);
        Assert.Equal(0, git.Calls);
    }
    [Fact] public async Task Same_repository_registered_twice_cannot_merge_concurrently()
    {
        var (first, run1) = await Seed();
        var (second, run2) = await Seed();
        await Store.BeginMergeAsync(first.Id, run1.Id, Result, Base, first.Version);
        await Assert.ThrowsAsync<ConflictException>(() => Store.BeginMergeAsync(second.Id, run2.Id, Result, Base, second.Version));
    }
    [Fact] public async Task Merged_and_merging_tasks_cannot_be_cancelled()
    {
        var (task, run) = await Seed();
        var merge = await Store.BeginMergeAsync(task.Id, run.Id, Result, Base, task.Version);
        var merging = (await Store.GetTaskAsync(task.Id))!;
        Assert.Throws<ConflictException>(() => merging.Cancel());
        await Store.FinishMergeAsync(merge.Id, MergeState.Succeeded, Result, null);
        var merged = (await Store.GetTaskAsync(task.Id))!;
        Assert.Throws<ConflictException>(() => merged.Cancel());
    }
}
