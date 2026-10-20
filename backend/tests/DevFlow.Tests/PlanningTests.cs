using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Tests;

public sealed class PlanningTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-planning-" + Guid.NewGuid());
    private SqliteTaskStore Store => new(Path.Combine(directory, "tasks.db"));
    private WorkTask task = null!;
    public async Task InitializeAsync()
    {
        await Store.InitializeAsync();
        var project = new ProjectDefinition(Guid.NewGuid(), "Demo", directory, "main", ["test"]);
        await Store.AddProjectAsync(project);
        task = WorkTask.Create(project.Id, "API", "Build", "Tests");
        await Store.AddTaskAsync(task);
    }
    public Task DisposeAsync() { Directory.Delete(directory, true); return Task.CompletedTask; }
    private sealed class FakePlanner(Exception? error = null) : IPlanningRunner
    {
        public Task<string> PlanAsync(WorkTask t, Workspace w, Guid r, CancellationToken ct)
        { if (error != null) throw error; return Task.FromResult("Write tests, implement API, refactor."); }
    }
    private sealed class FakeWorkspace(bool changed = false) : IWorkspaceManager
    {
        public Task<Workspace> CreateAsync(ProjectDefinition p, Guid r, CancellationToken ct) => Task.FromResult(new Workspace("worktree", "base"));
        public Task<string> SnapshotAsync(Workspace w, CancellationToken ct) => throw new InvalidOperationException("Planning must not snapshot code.");
        public Task AssertUnchangedAsync(Workspace w, CancellationToken ct)
        { if (changed) throw new ConflictException("Planner changed files."); return Task.CompletedTask; }
    }
    private sealed class BlockingPlanner : IPlanningRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> PlanAsync(WorkTask t, Workspace w, Guid r, CancellationToken ct)
        { Started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return "Late plan"; }
    }
    [Fact] public async Task Cancelling_an_active_planner_stops_it_without_publishing()
    {
        await Store.QueuePlanningAsync(task.Id, task.Version);
        var planner = new BlockingPlanner();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = new PlanningCoordinator(Store, planner, new FakeWorkspace()).ExecuteNextAsync(cts.Token);
        try
        {
            await planner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var active = (await Store.GetTaskAsync(task.Id))!;
            await Store.SaveTaskAsync(active.Cancel(), active.Version);
            await running.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(Status.Cancelled, (await Store.GetTaskAsync(task.Id))!.Status);
            Assert.Empty(await Store.ListPlansAsync(task.Id));
        }
        finally { cts.Cancel(); await running; }
    }
    [Fact] public async Task Progress_only_reply_is_failed_not_published_and_historical_placeholder_cannot_be_approved()
    {
        await Store.QueuePlanningAsync(task.Id,task.Version);
        await new PlanningCoordinator(Store,new ProgressPlanner(),new FakeWorkspace()).ExecuteNextAsync();
        Assert.Equal(Status.PlanningFailed,(await Store.GetTaskAsync(task.Id))!.Status);
        Assert.Empty(await Store.ListPlansAsync(task.Id));
        var old=task.SetPlan("正在分析儲存庫與現有檔案，以擬定計畫。");
        Assert.NotNull(old.Plan!.Problem);
        Assert.Throws<ArgumentException>(()=>old.ApprovePlan());
        Assert.Null(task.SetPlan("1. 修改出貨列表的表格樣式。\n2. 執行 npm run build 驗證。").Plan!.Problem);
    }
    private sealed class ProgressPlanner : IPlanningRunner
    {
        public Task<string> PlanAsync(WorkTask t,Workspace w,Guid id,CancellationToken ct)=>Task.FromResult("正在分析儲存庫與現有檔案，以擬定計畫。");
    }
    [Fact] public async Task Generated_plan_waits_for_human_approval_and_preserves_revision()
    {
        await Store.QueuePlanningAsync(task.Id, task.Version);
        Assert.True(await new PlanningCoordinator(Store, new FakePlanner(), new FakeWorkspace()).ExecuteNextAsync());
        var planned = (await Store.GetTaskAsync(task.Id))!;
        Assert.Equal(Status.AwaitingApproval, planned.Status);
        Assert.Contains("Write tests", planned.Plan!.Text);
        Assert.Equal("base", planned.Plan.BaseCommit);
        var run = Assert.Single(await Store.ListRunsAsync(task.Id));
        Assert.Equal(RunKind.Planning, run.Kind);
        Assert.Equal("worktree", run.WorktreePath);
        Assert.Equal(run.Id, Assert.Single(await Store.ListPlansAsync(task.Id)).RunId);
        Assert.Null(await Store.ClaimNextAsync());
    }
    [Fact] public async Task Concurrent_workers_claim_planning_only_once()
    {
        await Store.QueuePlanningAsync(task.Id, task.Version);
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Store.ClaimPlanningAsync())));
        Assert.Single(claims, c => c != null);
    }
    [Fact] public async Task Manual_edits_and_regeneration_keep_old_plan_versions()
    {
        var manual = task.SetPlan("First manual plan");
        await Store.SaveTaskAsync(manual, task.Version);
        await Store.QueuePlanningAsync(task.Id, manual.Version);
        await new PlanningCoordinator(Store, new FakePlanner(), new FakeWorkspace()).ExecuteNextAsync();
        var planned = (await Store.GetTaskAsync(task.Id))!;
        var edited = planned.SetPlan("Edited generated plan");
        await Store.SaveTaskAsync(edited, planned.Version);
        Assert.Equal(new[] { 1, 2, 3 }, (await Store.ListPlansAsync(task.Id)).Select(p => p.Revision));
        Assert.Equal("First manual plan", (await Store.ListPlansAsync(task.Id))[0].Text);
    }
    [Fact] public async Task Planner_failure_or_changes_do_not_publish_a_plan()
    {
        await Store.QueuePlanningAsync(task.Id, task.Version);
        await new PlanningCoordinator(Store, new FakePlanner(), new FakeWorkspace(changed: true)).ExecuteNextAsync();
        Assert.Equal(Status.PlanningFailed, (await Store.GetTaskAsync(task.Id))!.Status);
        Assert.Empty(await Store.ListPlansAsync(task.Id));
        Assert.NotNull((await Store.ListRunsAsync(task.Id)).Single().Error);
    }
    [Fact] public async Task Planning_errors_can_requeue_but_stale_requests_are_rejected()
    {
        await Store.QueuePlanningAsync(task.Id, task.Version);
        await new PlanningCoordinator(Store, new FakePlanner(new InvalidOperationException("CLI failed")), new FakeWorkspace()).ExecuteNextAsync();
        var failed = (await Store.GetTaskAsync(task.Id))!;
        await Assert.ThrowsAsync<ConflictException>(() => Store.QueuePlanningAsync(task.Id, task.Version));
        Assert.Equal(Status.PlanningQueued, (await Store.QueuePlanningAsync(task.Id, failed.Version)).Status);
    }
    [Fact] public async Task Cancelled_plan_completion_cannot_overwrite_cancel_or_add_history()
    {
        await Store.QueuePlanningAsync(task.Id, task.Version);
        var claim = (await Store.ClaimPlanningAsync())!;
        await Store.SaveTaskAsync(claim.Task.Cancel(), claim.Task.Version);
        await Store.CompletePlanningAsync(claim.Run.Id, "Late plan", null);
        Assert.Equal(Status.Cancelled, (await Store.GetTaskAsync(task.Id))!.Status);
        Assert.Empty(await Store.ListPlansAsync(task.Id));
        Assert.Equal("Cancelled", (await Store.GetRunAsync(claim.Run.Id))!.Status);
    }
    [Fact] public async Task Manual_edit_cannot_replace_an_active_generated_plan()
    {
        var queued = await Store.QueuePlanningAsync(task.Id, task.Version);
        Assert.Throws<ConflictException>(() => queued.SetPlan("Manual race"));
        var running = (await Store.ClaimPlanningAsync())!.Task;
        Assert.Throws<ConflictException>(() => running.SetPlan("Manual race"));
    }
    [Fact] public async Task Restart_recovers_planning_runs_as_planning_failures()
    {
        await Store.QueuePlanningAsync(task.Id, task.Version);
        var claim = (await Store.ClaimPlanningAsync())!;
        await Store.RecoverInterruptedAsync();
        Assert.Equal(Status.PlanningFailed, (await Store.GetTaskAsync(task.Id))!.Status);
        Assert.NotNull((await Store.GetRunAsync(claim.Run.Id))!.FinishedAt);
    }
}
