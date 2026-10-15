using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevFlow.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace DevFlow.Tests;

public sealed class ReviewApiTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-review-api-" + Guid.NewGuid());
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private ITaskStore Store => factory.Services.GetRequiredService<ITaskStore>();
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> {
                ["DevFlow:DatabasePath"] = Path.Combine(directory, "api.db") }));
            b.ConfigureServices(s => s.AddSingleton<IResultDiffReader, FakeDiff>());
            b.ConfigureServices(s => s.AddSingleton<IGitMergeService, FakeMerge>());
        });
        client = factory.CreateClient();
        return Task.CompletedTask;
    }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); Directory.Delete(directory, true); }
    private sealed class FakeDiff : IResultDiffReader
    {
        public Task<ResultDiff> ReadAsync(ProjectDefinition p, TaskRun r, CancellationToken ct = default)
            => Task.FromResult(new ResultDiff(r.Id, r.BaseCommit!, r.ResultCommit!, "diff --git a/feature.cs b/feature.cs"));
    }
    private sealed class FakeMerge : IGitMergeService
    {
        public bool Interrupted { get; set; }
        public Task<RepositoryState> InspectAsync(ProjectDefinition p, CancellationToken ct = default) =>
            Task.FromResult(new RepositoryState("main", new string('a', 40), true));
        public Task<string> MergeAsync(ProjectDefinition p, TaskMerge m, CancellationToken ct = default)
        {
            if (Interrupted) throw new OperationCanceledException("Simulated interruption.");
            return Task.FromResult(m.ResultCommit);
        }
        public Task<MergeObservation> ObserveAsync(ProjectDefinition p, TaskMerge m, CancellationToken ct = default) => Task.FromResult(MergeObservation.Applied);
    }
    private async Task<(WorkTask Task, TaskRun Run)> Completed()
    {
        var project = new ProjectDefinition(Guid.NewGuid(), "Demo", directory, "main", ["test"]);
        await Store.AddProjectAsync(project);
        var task = WorkTask.Create(project.Id, "API", "Build", "Tests").SetPlan("TDD").ApprovePlan();
        await Store.AddTaskAsync(task);
        var claim = (await Store.ClaimNextAsync())!;
        await Store.CompleteRunAsync(claim.Run.Id, true, directory, new string('a', 40), new string('b', 40), null);
        return ((await Store.GetTaskAsync(task.Id))!, (await Store.ListRunsAsync(task.Id)).Single());
    }
    [Fact] public async Task Review_and_retry_roundtrip_returns_versioned_task_and_history()
    {
        var (task, run) = await Completed();
        var response = await client.PostAsJsonAsync($"/api/tasks/{task.Id}/reviews",
            new { runId = run.Id, resultCommit = run.ResultCommit, decision = "Reject", comments = "Fix missing IDs", expectedVersion = task.Version });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(response.Headers.Location)).StatusCode);
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{task.Id}/reviews");
        Assert.Equal("Reject", history[0].GetProperty("decision").GetString());
        var rejected = (await Store.GetTaskAsync(task.Id))!;
        var retry = await client.PostAsJsonAsync($"/api/tasks/{task.Id}/retry", new { expectedVersion = rejected.Version });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("Queued", (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Single(await Store.ListRunsAsync(task.Id));
    }
    [Fact] public async Task Review_rejects_unknown_decision_and_mismatched_commit()
    {
        var (task, run) = await Completed();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/tasks/{task.Id}/reviews",
            new { runId = run.Id, resultCommit = run.ResultCommit, decision = "Merge", expectedVersion = task.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/tasks/{task.Id}/reviews",
            new { runId = run.Id, resultCommit = run.ResultCommit, decision = "0", expectedVersion = task.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/tasks/{task.Id}/reviews",
            new { runId = run.Id, resultCommit = new string('c', 40), decision = "Approve", expectedVersion = task.Version })).StatusCode);
    }
    [Fact] public async Task Diff_reads_exact_recorded_result_and_unknown_run_returns_404()
    {
        var (_, run) = await Completed();
        var response = await client.GetAsync($"/api/runs/{run.Id}/diff");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var diff = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(run.ResultCommit, diff.GetProperty("resultCommit").GetString());
        Assert.Contains("diff --git", diff.GetProperty("patch").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/runs/{run.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/runs/{Guid.NewGuid()}/diff")).StatusCode);
    }
    [Fact] public async Task Approval_then_merge_returns_result_and_merge_history()
    {
        var (task, run) = await Completed();
        await Store.ReviewAsync(task.Id, run.Id, run.ResultCommit!, ReviewDecision.Approve, null, task.Version);
        var approved = (await Store.GetTaskAsync(task.Id))!;
        var state = await client.GetAsync($"/api/projects/{task.ProjectId}/git-state");
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        var response = await client.PostAsJsonAsync($"/api/tasks/{task.Id}/merge", new {
            runId = run.Id, resultCommit = run.ResultCommit, expectedTargetCommit = run.BaseCommit, expectedVersion = approved.Version });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Succeeded", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/tasks/{task.Id}/merges")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/tasks/{task.Id}/merge/reconcile", null)).StatusCode);
    }
    [Fact] public async Task Interrupted_merge_returns_503_and_can_be_reconciled_without_remerging()
    {
        var (task, run) = await Completed();
        await Store.ReviewAsync(task.Id, run.Id, run.ResultCommit!, ReviewDecision.Approve, null, task.Version);
        var approved = (await Store.GetTaskAsync(task.Id))!;
        ((FakeMerge)factory.Services.GetRequiredService<IGitMergeService>()).Interrupted = true;
        var response = await client.PostAsJsonAsync($"/api/tasks/{task.Id}/merge", new {
            runId = run.Id, resultCommit = run.ResultCommit, expectedTargetCommit = run.BaseCommit, expectedVersion = approved.Version });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(MergeState.Uncertain, (await Store.ListMergesAsync(task.Id)).Single().State);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/tasks/{task.Id}/merge/reconcile", null)).StatusCode);
        Assert.Equal(DevFlow.Core.TaskStatus.Merged, (await Store.GetTaskAsync(task.Id))!.Status);
    }
}
