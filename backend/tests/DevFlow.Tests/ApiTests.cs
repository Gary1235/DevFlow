using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace DevFlow.Tests;

public sealed class ApiTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-api-" + Guid.NewGuid());
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["DevFlow:DatabasePath"] = Path.Combine(directory, "api.db") })));
        client = factory.CreateClient();
        return Task.CompletedTask;
    }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); Directory.Delete(directory, true); }
    private async Task<Guid> CreateProject()
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Demo", repositoryPath = directory, baseBranch = "main", validationArguments = new[] { "test" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    [Fact] public async Task Submit_plan_approve_cancel_and_replay_events()
    {
        var projectId = await CreateProject();
        var response = await client.PostAsJsonAsync("/api/tasks", new { projectId, title = "API", description = "Build API", acceptanceCriteria = "Tests pass" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var url = $"/api/tasks/{id}";
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url + "/approve-plan", new { expectedVersion = 0, planRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(url + "/plan", new { text = "Write failing tests first" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(url + "/approve-plan", new { expectedVersion = 1, planRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(url + "/cancel", null)).StatusCode);
        var task = await client.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal("Cancelled", task.GetProperty("status").GetString());
        Assert.Equal(4, (await client.GetFromJsonAsync<JsonElement>(url + "/events")).GetArrayLength());
    }
    [Fact] public async Task Invalid_input_and_unknown_project_return_client_errors()
    {
        var projectId = await CreateProject();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/tasks", new { projectId, title = "API", description = "Build", acceptanceCriteria = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/tasks", new { projectId = Guid.NewGuid(), title = "API", description = "Build", acceptanceCriteria = "Tests" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/tasks/{Guid.NewGuid()}/events?after=-1")).StatusCode);
    }
}
