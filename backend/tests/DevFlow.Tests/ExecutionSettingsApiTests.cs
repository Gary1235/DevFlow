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
}
