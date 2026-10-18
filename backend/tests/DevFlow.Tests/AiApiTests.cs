using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace DevFlow.Tests;

public sealed class AiApiTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-ai-" + Guid.NewGuid());
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["DevFlow:DatabasePath"] = Path.Combine(directory, "ai.db") })));
        client = factory.CreateClient(); return Task.CompletedTask;
    }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); Directory.Delete(directory, true); }
    private async Task<Guid> Project(object? aiDefaults = null)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Fixture", repositoryPath = directory, baseBranch = "main", validationArguments = new[] { "test" }, aiDefaults });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private Task<HttpResponseMessage> Create(Guid projectId, object? planning = null, object? implementation = null) =>
        client.PostAsJsonAsync("/api/tasks", new { projectId, title = "AI", description = "Build", acceptanceCriteria = "Tests", planning, implementation });
    [Theory]
    [InlineData("check")]
    [InlineData("update")]
    public async Task Cli_maintenance_requires_explicit_local_action(string operation)
    {
        var response = await client.PostAsJsonAsync($"/api/ai/codex/cli/{operation}", new { checkId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Theory]
    [InlineData("check")]
    [InlineData("update")]
    public async Task Cli_maintenance_rejects_foreign_origins_before_running_commands(string operation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/ai/codex/cli/{operation}")
        { Content = JsonContent.Create(new { checkId = Guid.NewGuid() }) };
        request.Headers.Add("X-DevFlow-Action", "1"); request.Headers.Add("Origin", "https://foreign.example");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }
    [Fact] public async Task Unknown_update_session_is_not_restarted()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/ai/cli/updates/{Guid.NewGuid()}")).StatusCode);
    }
    [Fact] public async Task Legacy_submission_resolves_and_persists_codex_defaults()
    {
        var response = await Create(await Project());
        var task = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("codex", task.GetProperty("ai").GetProperty("planning").GetProperty("provider").GetString());
        var read = await client.GetFromJsonAsync<JsonElement>("/api/tasks/" + task.GetProperty("id").GetGuid());
        Assert.Equal(task.GetProperty("ai").ToString(), read.GetProperty("ai").ToString());
    }
    [Fact] public async Task Overrides_and_project_defaults_are_frozen_in_task_and_run()
    {
        var project = await Project(new { planning = new { provider = "gemini", model = "pro" }, implementation = new { provider = "codex", model = "custom-model" } });
        var task = await (await Create(project, implementation: new { provider = "gemini", model = "flash" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pro", task.GetProperty("ai").GetProperty("planning").GetProperty("model").GetString());
        Assert.Equal("flash", task.GetProperty("ai").GetProperty("implementation").GetProperty("model").GetString());
        var update = await client.PutAsJsonAsync($"/api/projects/{project}/ai-defaults", new { expectedVersion = 0, aiDefaults = new { planning = new { provider = "codex" }, implementation = new { provider = "codex" } } });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var id = task.GetProperty("id").GetGuid();
        var read = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}");
        Assert.Equal("pro", read.GetProperty("ai").GetProperty("planning").GetProperty("model").GetString());
    }
    [Theory]
    [InlineData("unknown", "model")]
    [InlineData("codex", "--dangerously-bypass-approvals-and-sandbox")]
    [InlineData("gemini", "model\nsecond")]
    public async Task Invalid_provider_or_model_is_rejected(string provider, string model)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(await Project(), new { provider, model })).StatusCode);
    }
    [Fact] public async Task Provider_catalog_and_local_ui_are_available_without_model_calls()
    {
        var response = await client.GetAsync("/api/ai/providers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var providers = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(providers.EnumerateArray(), p => p.GetProperty("id").GetString() == "gemini");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }
    [Fact] public async Task Login_requires_explicit_local_action_and_unknown_sessions_return_404()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/ai/codex/login", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/ai/logins/{Guid.NewGuid()}")).StatusCode);
    }
    [Fact] public async Task Claude_is_listed_as_backup_without_login_or_model_execution()
    {
        var providers = await client.GetFromJsonAsync<JsonElement>("/api/ai/providers");
        var claude = Assert.Single(providers.EnumerateArray(), p => p.GetProperty("id").GetString() == "claude");
        Assert.Contains(claude.GetProperty("models").EnumerateArray(), m => m.GetString() == "sonnet");
        Assert.False(claude.GetProperty("modelsVerified").GetBoolean());
    }
    [Fact] public async Task Claude_selection_can_be_saved_even_when_account_is_not_ready()
    {
        var response = await Create(await Project(), new { provider = "claude", model = "sonnet" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("claude", task.GetProperty("ai").GetProperty("planning").GetProperty("provider").GetString());
        Assert.Equal("sonnet", task.GetProperty("ai").GetProperty("planning").GetProperty("model").GetString());
    }
    [Theory]
    [InlineData("script", "src", "javascript")]
    [InlineData("link", "href", "text/css")]
    public async Task React_production_assets_are_served(string tag, string attribute, string mediaType)
    {
        var html = await client.GetStringAsync("/");
        var match = System.Text.RegularExpressions.Regex.Match(html, $"<{tag}[^>]*{attribute}=\"(/assets/[^\"]+)\"");
        Assert.True(match.Success, "React production asset was not present in the index.");
        var response = await client.GetAsync(match.Groups[1].Value);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(mediaType, response.Content.Headers.ContentType?.MediaType ?? "");
    }
    [Fact] public async Task Stale_frontend_plan_editor_cannot_overwrite_a_newer_plan()
    {
        var task = await (await Create(await Project())).Content.ReadFromJsonAsync<JsonElement>();
        var id = task.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/tasks/{id}/plan", new { text = "New plan", expectedVersion = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/tasks/{id}/plan", new { text = "Stale editor", expectedVersion = 0 })).StatusCode);
        var read = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}");
        Assert.Equal("New plan", read.GetProperty("plan").GetProperty("text").GetString());
    }
}
