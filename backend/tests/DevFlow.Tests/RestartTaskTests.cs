using System.Net;
using System.Net.Http.Json;
using DevFlow.Core;
using DevFlow.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
using Status = DevFlow.Core.TaskStatus;

namespace DevFlow.Tests;

public sealed class RestartTaskTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-restart-" + Guid.NewGuid());
    private SqliteTaskStore store = null!;
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private WorkTask original = null!;
    public async Task InitializeAsync()
    {
        store = new(Path.Combine(root, "tasks.db")); await store.InitializeAsync();
        var project = new ProjectDefinition(Guid.NewGuid(), "Test", root, "main", ["test"]); await store.AddProjectAsync(project);
        original = WorkTask.Create(project.Id, "Restart", "Build", "Test"); await store.AddTaskAsync(original);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["DevFlow:DatabasePath"] = store.DatabasePath })));
        client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-DevFlow-Action", "1");
    }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); Directory.Delete(root, true); }
    private Task<HttpResponseMessage> Restart(int version) => client.PostAsJsonAsync($"/api/tasks/{original.Id}/restart", new { expectedVersion = version });

    [Fact] public async Task Cancelled_planning_waits_for_shutdown_then_requeues_without_losing_history()
    {
        var manual = original.SetPlan("Historical plan"); await store.SaveTaskAsync(manual, original.Version);
        await store.QueuePlanningAsync(original.Id, manual.Version);
        var first = (await store.ClaimPlanningAsync())!;
        var cancelled = first.Task.Cancel(); await store.SaveTaskAsync(cancelled, first.Task.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await Restart(cancelled.Version)).StatusCode);
        Assert.Equal(Status.Cancelled, (await store.GetTaskAsync(original.Id))!.Status);
        // Late output while cancellation finishes cannot publish a plan.
        await store.CompletePlanningAsync(first.Run.Id, "Late output", null);
        Assert.Single(await store.ListPlansAsync(original.Id));
        Assert.Equal(HttpStatusCode.OK, (await Restart(cancelled.Version)).StatusCode);
        var current = (await store.GetTaskAsync(original.Id))!;
        Assert.Equal(Status.PlanningQueued, current.Status); Assert.Equal("Historical plan", current.Plan!.Text);
        Assert.Equal(cancelled.Version + 1, current.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await Restart(cancelled.Version)).StatusCode);
        var second = (await store.ClaimPlanningAsync())!; Assert.NotEqual(first.Run.Id, second.Run.Id);
        await store.CompletePlanningAsync(second.Run.Id, "Change shipment list layout; run npm run build.", null);
        var planned = (await store.GetTaskAsync(original.Id))!;
        Assert.Equal(Status.AwaitingApproval, planned.Status); Assert.Equal(2, planned.Plan!.Revision);
        Assert.Equal(2, (await store.ListRunsAsync(original.Id)).Count); Assert.Equal(2, (await store.ListPlansAsync(original.Id)).Count);
        Assert.Null(await store.ClaimNextAsync());
        Assert.Contains(await store.GetEventsAsync(original.Id), e => e.Kind == "TaskRestarted");
    }

    [Fact] public async Task Queued_cancellation_can_restart_without_an_existing_run_but_stale_or_non_cancelled_requests_cannot()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await Restart(original.Version)).StatusCode);
        var queued = await store.QueuePlanningAsync(original.Id, original.Version);
        var cancelled = queued.Cancel(); await store.SaveTaskAsync(cancelled, queued.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await Restart(queued.Version)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Restart(cancelled.Version)).StatusCode);
        Assert.Empty(await store.ListRunsAsync(original.Id));
        Assert.Equal(Status.PlanningQueued, (await store.GetTaskAsync(original.Id))!.Status);
    }

    [Fact] public async Task Cancelled_implementation_resets_continuation_but_keeps_the_old_run_and_requires_approval_when_planning_is_disabled()
    {
        var configured = original.SetPlan("Implement layout; run build.").ApprovePlan() with { Workflow = new(PlanningEnabled: false) };
        // Set up the queued implementation as a distinct task fixture.
        var other = configured with { Id = Guid.NewGuid() }; await store.AddTaskAsync(other);
        var claim = (await store.ClaimNextAsync())!;
        var cancelled = claim.Task.Cancel() with { RevisionCommit = "old-result", RevisionInstructions = "old feedback", ExecutionBaseCommit = "old-base", AutoReviewAttempt = 2,
            StepResults = [new(Guid.NewGuid(), "Completed backend", "Implementation", "Completed", claim.Run.Id, "old-result", "Done", new("codex"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)] };
        await store.SaveTaskAsync(cancelled, claim.Task.Version);
        await Assert.ThrowsAsync<ConflictException>(() => store.RestartCancelledAsync(other.Id, cancelled.Version));
        await store.CompleteRunAsync(claim.Run.Id, false, "old-worktree", "old-base", "old-result", "Cancelled.");
        var restarted = await store.RestartCancelledAsync(other.Id, cancelled.Version);
        Assert.Equal(Status.AwaitingApproval, restarted.Status); Assert.Equal(configured.Plan, restarted.Plan);
        Assert.Null(restarted.RevisionCommit); Assert.Null(restarted.RevisionInstructions); Assert.Null(restarted.ExecutionBaseCommit);
        Assert.Empty(restarted.StepResults!); Assert.Equal(0, restarted.AutoReviewAttempt); Assert.Null(restarted.ActiveStepId);
        Assert.Equal("old-result", (await store.GetRunAsync(claim.Run.Id))!.ResultCommit);
        Assert.Null(await store.ClaimNextAsync());
    }

    [Fact] public async Task Restart_requires_an_explicit_same_origin_action()
    {
        var cancelled = original.Cancel(); await store.SaveTaskAsync(cancelled, original.Version);
        client.DefaultRequestHeaders.Remove("X-DevFlow-Action");
        Assert.Equal(HttpStatusCode.BadRequest, (await Restart(cancelled.Version)).StatusCode);
        client.DefaultRequestHeaders.Add("X-DevFlow-Action", "1"); client.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
        Assert.Equal(HttpStatusCode.BadRequest, (await Restart(cancelled.Version)).StatusCode);
        Assert.Equal(Status.Cancelled, (await store.GetTaskAsync(original.Id))!.Status);
    }
    [Fact] public void Missing_or_incomplete_plan_without_ai_planning_requires_a_new_manual_plan()
    {
        var missing = original.Cancel() with { Workflow = new(PlanningEnabled: false) };
        Assert.Equal(Status.AwaitingPlan, missing.RestartCancelled().Status);
        var incomplete = missing with { Plan = new("正在分析相關檔案，以擬定計畫。") };
        Assert.Equal(Status.AwaitingPlan, incomplete.RestartCancelled().Status);
    }
}
