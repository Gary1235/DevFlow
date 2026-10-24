using System.Net;
using System.Net.Http.Json;
using DevFlow.Core;
using DevFlow.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace DevFlow.Tests;

public sealed class ExecutionSettingsApiTests : IAsyncLifetime
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"devflow-settings-api-"+Guid.NewGuid());
    private WebApplicationFactory<Program> factory=null!;
    private HttpClient client=null!;
    public Task InitializeAsync() {
        Directory.CreateDirectory(root);
        factory=new WebApplicationFactory<Program>().WithWebHostBuilder(b=>b.ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?> { ["DevFlow:DatabasePath"]=Path.Combine(root,"db"), ["DevFlow:WorktreeRoot"]=Path.Combine(root,"worktrees") })));
        client=factory.CreateClient();return Task.CompletedTask;
    }
    public async Task DisposeAsync(){client.Dispose();await factory.DisposeAsync();Directory.Delete(root,true);}
    [Fact] public async Task Project_settings_round_trip_and_stale_versions_are_rejected_without_starting_a_preview()
    {
        var store=factory.Services.GetRequiredService<SqliteTaskStore>();
        var project=new ProjectDefinition(Guid.NewGuid(),"Demo",root,"main",["test"]);await store.AddProjectAsync(project);
        var input=new {expectedVersion=0,validationProfiles=new[]{new ValidationProfile("web","Web",".","node",["build.js"])},defaultValidationProfileId="web",previewServices=new[]{new PreviewService("web","Web",".","node",["server.js","{port}"])}};
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PutAsJsonAsync($"/api/projects/{project.Id}/execution-settings",input)).StatusCode);
        client.DefaultRequestHeaders.Add("X-DevFlow-Action","1");
        Assert.Equal(HttpStatusCode.OK,(await client.PutAsJsonAsync($"/api/projects/{project.Id}/execution-settings",input)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PutAsJsonAsync($"/api/projects/{project.Id}/execution-settings",input)).StatusCode);
        var saved=await client.GetFromJsonAsync<ProjectDefinition>($"/api/projects/{project.Id}");
        Assert.Equal("web",saved!.DefaultValidationProfileId);Assert.Single(saved.PreviewServices!);
        var task=WorkTask.Create(project.Id,"Task","Build","Pass");await store.AddTaskAsync(task);
        Assert.Equal("Stopped",(await client.GetFromJsonAsync<TaskPreviewView>($"/api/tasks/{task.Id}/preview"))!.State);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync($"/api/tasks/{task.Id}/preview/start",new {runId=Guid.NewGuid(),resultCommit="x",expectedVersion=task.Version,projectVersion=1})).StatusCode);
        Assert.Empty(await store.ListRunsAsync(task.Id));
    }
    [Fact] public async Task Probe_history_is_readable_but_cancellation_requires_an_explicit_same_origin_action()
    {
        var store = factory.Services.GetRequiredService<SqliteTaskStore>();
        var project = new ProjectDefinition(Guid.NewGuid(), "Demo", root, "main", ["test"]); await store.AddProjectAsync(project);
        var probe = new ValidationProbe(Guid.NewGuid(), "Completed", ProjectId: project.Id, ProfileName: "Build", CreatedAt: DateTimeOffset.UtcNow);
        store.SaveValidationProbe(probe);
        var history = await client.GetFromJsonAsync<ValidationProbe[]>($"/api/projects/{project.Id}/execution-probes");
        Assert.Equal(probe.Id, Assert.Single(history!).Id);
        var url = $"/api/execution-probes/{probe.Id}/cancel";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new {})).StatusCode);
        client.DefaultRequestHeaders.Add("X-DevFlow-Action", "1"); client.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new {})).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(url, new {})).StatusCode);
        Assert.Equal("Completed", store.GetValidationProbe(probe.Id)!.State);
    }
    [Fact] public async Task New_diagnostic_and_recovery_actions_require_explicit_same_origin_requests()
    {
        var store=factory.Services.GetRequiredService<SqliteTaskStore>();
        var project=new ProjectDefinition(Guid.NewGuid(),"Demo",root,"main",["test"]);await store.AddProjectAsync(project);
        var input=new {expectedVersion=0,profile=new ValidationProfile("build","Build",".","node",["build.js"])};
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/projects/{project.Id}/execution-preflight",input)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/projects/{project.Id}/execution-probes",input)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/tasks/{Guid.NewGuid()}/recovery/preview",new {})).StatusCode);
        client.DefaultRequestHeaders.Add("X-DevFlow-Action","1");client.DefaultRequestHeaders.Add("Origin","https://foreign.example");
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/projects/{project.Id}/execution-preflight",input)).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync($"/api/projects/{project.Id}/execution-preflight",new {expectedVersion=1,input.profile})).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/projects/{project.Id}/execution-preflight",input)).StatusCode);
        Assert.Empty(await store.ListTasksAsync());
    }
}
