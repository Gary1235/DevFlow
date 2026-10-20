using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevFlow.Core;
using DevFlow.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace DevFlow.Tests;

public sealed class WorkflowApiTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-workflow-api-" + Guid.NewGuid());
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["DevFlow:DatabasePath"] = Path.Combine(root, "api.db") })));
        client = factory.CreateClient(); return Task.CompletedTask;
    }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); Directory.Delete(root, true); }
    [Fact] public async Task Retry_preview_is_read_only_and_stale_confirmation_cannot_queue_work()
    {
        var store = factory.Services.GetRequiredService<SqliteTaskStore>();
        var project = new ProjectDefinition(Guid.NewGuid(), "Demo", root, "main", ["test"]);
        await store.AddProjectAsync(project);
        var task = WorkTask.Create(project.Id, "Failed task", "Build", "Pass").SetPlan("approved") with
        { Status = DevFlow.Core.TaskStatus.Failed, ExecutionBaseCommit = "base" };
        await store.AddTaskAsync(task);
        var preview = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{task.Id}/retry-preview?scope=Restart");
        Assert.Equal("Restart", preview.GetProperty("scope").GetString());
        Assert.Equal(DevFlow.Core.TaskStatus.Failed, (await store.GetTaskAsync(task.Id))!.Status);
        Assert.Empty(await store.ListRunsAsync(task.Id));
        var model = await client.PutAsJsonAsync($"/api/tasks/{task.Id}/model", new { expectedVersion = task.Version, stage = "Implementation", selection = new { provider = "antigravity", model = "chosen-model" } });
        Assert.Equal(HttpStatusCode.OK, model.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/tasks/{task.Id}/retry", new { expectedVersion = task.Version, scope = "Restart", instructions = "retry" })).StatusCode);
        var current = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(DevFlow.Core.TaskStatus.Failed, current.Status);
        Assert.Equal("antigravity", current.Ai!.Implementation!.Provider);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/tasks/{task.Id}/retry-preview?scope=Invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/tasks/{task.Id}/retry", new { expectedVersion = current.Version, scope = "Restart", instructions = "retry" })).StatusCode);
        current = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(DevFlow.Core.TaskStatus.Queued, current.Status);
        Assert.Equal("base", current.RevisionCommit);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/tasks/{task.Id}/model", new { expectedVersion = current.Version, stage = "Implementation", selection = new { provider = "codex" } })).StatusCode);
    }
    [Fact] public async Task Creates_ordered_steps_edits_pending_work_and_requires_current_version_to_continue()
    {
        var createdProject = await client.PostAsJsonAsync("/api/projects", new { name = "Demo", repositoryPath = root, baseBranch = "main", validationArguments = new[] { "test" } });
        var projectId = (await createdProject.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var step = new WorkflowStep(Guid.NewGuid(), "後端 API", Selection: new("claude"), WaitForApproval: true);
        var flow = new WorkflowDefinition(PlanningEnabled: false, Steps: [step]);
        var created = await client.PostAsJsonAsync("/api/tasks", new { projectId, title = "Feature", description = "中文需求", acceptanceCriteria = "Tests", workflow = flow, autoPlan = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = json.GetProperty("id").GetGuid();
        Assert.Equal("claude", json.GetProperty("workflow").GetProperty("steps")[0].GetProperty("selection").GetProperty("provider").GetString());
        var store = factory.Services.GetRequiredService<SqliteTaskStore>();
        var claim = (await store.ClaimNextAsync())!;
        await store.SetWorkspaceAsync(claim.Run.Id, new(root, "base"));
        await store.BeginWorkflowStepAsync(claim.Run.Id);
        await store.RecordWorkflowStepAsync(claim.Run.Id, new(step.Id, step.Name, step.Kind, "Completed", claim.Run.Id,
            "result", "API ready", step.Selection, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await store.PauseWorkflowAsync(claim.Run.Id);
        var paused = (await store.GetTaskAsync(id))!;
        var next = paused.Workflow! with { Steps = [paused.Workflow!.Steps![0], new(Guid.NewGuid(), "前端", Selection: new("gemini"))] };
        var edited = await client.PutAsJsonAsync($"/api/tasks/{id}/workflow", new { expectedVersion = paused.Version, workflow = next });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/tasks/{id}/workflow/continue", new { expectedVersion = paused.Version })).StatusCode);
        var version = (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();
        var continued = await client.PostAsJsonAsync($"/api/tasks/{id}/workflow/continue", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, continued.StatusCode);
        Assert.Equal("Queued", (await continued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        var saved = await client.PostAsJsonAsync("/api/workflow-templates", new WorkflowTemplate("API first", next));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Single((await client.GetFromJsonAsync<WorkflowTemplate[]>("/api/workflow-templates"))!);
    }
}
