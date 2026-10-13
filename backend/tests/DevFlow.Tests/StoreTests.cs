using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Tests;

public sealed class StoreTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-tests-" + Guid.NewGuid());
    private SqliteTaskStore Store => new(Path.Combine(directory, "tasks.db"));
    public Task InitializeAsync() => Store.InitializeAsync();
    public Task DisposeAsync() { Directory.Delete(directory, true); return Task.CompletedTask; }
    private async Task<WorkTask> SeedAsync(bool queued = true)
    {
        var project = new ProjectDefinition(Guid.NewGuid(), "Demo", directory, "main", ["test"]);
        await Store.AddProjectAsync(project);
        var task = WorkTask.Create(project.Id, "API", "Build", "Tests");
        if (queued) task = task.SetPlan("TDD").ApprovePlan();
        await Store.AddTaskAsync(task);
        return task;
    }
    [Fact] public async Task Data_survives_new_store_instance()
    {
        var task = await SeedAsync();
        Assert.Equal(task, await Store.GetTaskAsync(task.Id));
        Assert.Single(await Store.ListProjectsAsync());
    }
    [Fact] public async Task Concurrent_workers_cannot_claim_same_task()
    {
        var task = await SeedAsync();
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Store.ClaimNextAsync())));
        Assert.Single(claims, x => x != null);
        Assert.Equal(Status.Running, (await Store.GetTaskAsync(task.Id))!.Status);
        Assert.Single(await Store.ListRunsAsync(task.Id));
    }
    [Fact] public async Task Unapproved_task_is_not_claimed()
    {
        await SeedAsync(false);
        Assert.Null(await Store.ClaimNextAsync());
    }
    [Fact] public async Task Stale_update_cannot_overwrite_newer_task()
    {
        var task = await SeedAsync(false);
        await Store.SaveTaskAsync(task.SetPlan("TDD"), task.Version);
        await Assert.ThrowsAsync<ConflictException>(() => Store.SaveTaskAsync(task.Cancel(), task.Version));
    }
    [Fact] public async Task Completion_preserves_cancel_and_events_can_be_replayed()
    {
        var task = await SeedAsync();
        var claim = (await Store.ClaimNextAsync())!;
        await Store.SaveTaskAsync(claim.Task.Cancel(), claim.Task.Version);
        await Store.CompleteRunAsync(claim.Run.Id, true, "worktree", "base", "result", null);
        Assert.Equal(Status.Cancelled, (await Store.GetTaskAsync(task.Id))!.Status);
        Assert.Equal("Cancelled", (await Store.ListRunsAsync(task.Id)).Single().Status);
        var events = await Store.GetEventsAsync(task.Id);
        Assert.True(events.Count >= 3);
        Assert.All(await Store.GetEventsAsync(task.Id, events[0].Sequence), e => Assert.True(e.Sequence > events[0].Sequence));
    }
}
