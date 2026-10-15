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

public sealed class PlanningArtifactApiTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-planning-api-" + Guid.NewGuid());
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private ITaskStore Store => factory.Services.GetRequiredService<ITaskStore>();
    private string Artifacts => Path.Combine(directory, "artifacts");
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["DevFlow:DatabasePath"] = Path.Combine(directory, "api.db"),
                ["DevFlow:ArtifactsRoot"] = Artifacts })));
        client = factory.CreateClient(); return Task.CompletedTask;
    }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); Directory.Delete(directory, true); }
    private async Task<WorkTask> CreateTask(bool autoPlan = false)
    {
        var project = new ProjectDefinition(Guid.NewGuid(), "Demo", directory, "main", ["test"]);
        await Store.AddProjectAsync(project);
        var response = await client.PostAsJsonAsync("/api/tasks", new { projectId = project.Id, title = "API", description = "Build", acceptanceCriteria = "Tests", autoPlan });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return (await Store.GetTaskAsync(id))!;
    }
    [Fact] public async Task Auto_plan_creation_queues_planning_without_implementation()
    {
        var task = await CreateTask(autoPlan: true);
        Assert.Equal(DevFlow.Core.TaskStatus.PlanningQueued, task.Status);
        Assert.Null(await Store.ClaimNextAsync());
        var claim = (await Store.ClaimPlanningAsync())!;
        await Store.CompletePlanningAsync(claim.Run.Id, "TDD plan", null);
        var response = await client.GetAsync($"/api/tasks/{task.Id}/plans");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("TDD plan", (await response.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("text").GetString());
    }
    [Fact] public async Task Generate_plan_rejects_stale_requests_and_returns_202()
    {
        var task = await CreateTask();
        var request = new { expectedVersion = task.Version };
        var response = await client.PostAsJsonAsync($"/api/tasks/{task.Id}/generate-plan", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(response.Headers.Location)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/tasks/{task.Id}/generate-plan", request)).StatusCode);
    }
    [Fact] public async Task Logs_and_validation_results_are_scoped_to_an_existing_run()
    {
        var task = await CreateTask();
        await Store.QueuePlanningAsync(task.Id, task.Version);
        var claim = (await Store.ClaimPlanningAsync())!;
        var root = Path.Combine(Artifacts, claim.Run.Id.ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Green"));
        await File.WriteAllTextAsync(Path.Combine(root, "Planning-agent.jsonl"), "planning output");
        await File.WriteAllTextAsync(Path.Combine(root, "Green", "evidence.json"), "{\"ExitCode\":0,\"Total\":2,\"Failed\":0}");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/runs/{claim.Run.Id}/artifacts")).StatusCode);
        var page = await client.GetAsync($"/api/runs/{claim.Run.Id}/artifacts/Planning-agent.jsonl?offset=0&limit=8");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("planning", (await page.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString());
        var validations = await client.GetAsync($"/api/runs/{claim.Run.Id}/validations");
        Assert.Equal(HttpStatusCode.OK, validations.StatusCode);
        Assert.Equal(2, (await validations.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/runs/{Guid.NewGuid()}/artifacts")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/runs/{claim.Run.Id}/artifacts/Planning-agent.jsonl?limit=0")).StatusCode);
    }
    [Fact] public async Task Approval_must_match_the_plan_revision_the_user_reviewed()
    {
        var task = await CreateTask();
        var first = task.SetPlan("First plan");
        await Store.SaveTaskAsync(first, task.Version);
        var second = first.SetPlan("Revised plan");
        await Store.SaveTaskAsync(second, first.Version);
        var url = $"/api/tasks/{task.Id}/approve-plan";
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, new {
            expectedVersion = first.Version, planRevision = first.Plan!.Revision })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, new {
            expectedVersion = second.Version, planRevision = first.Plan!.Revision })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(url, new {
            expectedVersion = second.Version, planRevision = second.Plan!.Revision })).StatusCode);
    }
}
