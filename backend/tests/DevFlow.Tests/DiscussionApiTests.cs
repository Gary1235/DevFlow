using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevFlow.Core;
using DevFlow.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace DevFlow.Tests;

public sealed class DiscussionApiTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-discussion-api-" + Guid.NewGuid());
    private readonly ReadOnlyRunner runner = new();
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private ProjectDefinition project = null!;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["DevFlow:DatabasePath"] = Path.Combine(directory, "api.db") }))
            .ConfigureTestServices(s => { s.AddSingleton<IPlanningRunner>(runner); s.AddSingleton<IWorkspaceManager, ReadOnlyWorkspace>(); }));
        client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-DevFlow-Action", "1");
        project = new(Guid.NewGuid(), "Demo", directory, "main", ["test"]);
        await factory.Services.GetRequiredService<ITaskStore>().AddProjectAsync(project);
    }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); Directory.Delete(directory, true); }
    private async Task<JsonElement> CreateDiscussion()
    {
        var response = await client.PostAsJsonAsync("/api/discussions", new { projectId = project.Id, title = "Improve checkout", selection = new { provider = "gemini", model = "chosen-model" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, runner.Calls);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact] public async Task Discussion_to_editable_draft_to_task_does_not_execute_and_retains_source()
    {
        var d = await CreateDiscussion(); var id = d.GetProperty("id").GetGuid();
        var turn = await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = 0, message = "Consider an audit log, but retention is undecided." });
        Assert.Equal(HttpStatusCode.OK, turn.StatusCode);
        var reply = await turn.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, reply.GetProperty("messages").GetArrayLength());
        Assert.Equal("chosen-model", reply.GetProperty("messages")[1].GetProperty("selection").GetProperty("model").GetString());
        var draftsResponse = await client.PostAsJsonAsync($"/api/discussions/{id}/drafts", new { expectedVersion = reply.GetProperty("version").GetInt32() });
        Assert.Equal(HttpStatusCode.OK, draftsResponse.StatusCode);
        var drafts = await draftsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var draftId = drafts.GetProperty("drafts")[0].GetProperty("id").GetGuid();
        var body = new { projectId = project.Id, title = "Edited audit task", description = "Keep logs for 30 days", acceptanceCriteria = "Retention works", autoPlan = false,
            sourceDiscussionId = id, sourceDraftId = draftId, acknowledgeOpenQuestions = true };
        var unconfirmed = await client.PostAsJsonAsync("/api/tasks", new { projectId = project.Id, title = "Audit", description = "Build", acceptanceCriteria = "Tests", sourceDiscussionId = id, sourceDraftId = draftId });
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        var created = await client.PostAsJsonAsync("/api/tasks", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var task = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AwaitingPlan", task.GetProperty("status").GetString());
        Assert.Equal("Edited audit task", task.GetProperty("title").GetString());
        Assert.Equal(id, task.GetProperty("sourceDiscussionId").GetGuid());
        var duplicate = await client.PostAsJsonAsync("/api/tasks", body);
        Assert.Equal(task.GetProperty("id").GetGuid(), (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(2, runner.Calls);
        Assert.Null(await factory.Services.GetRequiredService<ITaskStore>().ClaimNextAsync());
    }

    [Fact] public async Task Cross_origin_discussion_calls_do_not_start_a_model_or_save_a_message()
    {
        var d = await CreateDiscussion(); var id = d.GetProperty("id").GetGuid();
        client.DefaultRequestHeaders.Add("Origin", "https://other.example");
        var response = await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = 0, message = "Run" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, runner.Calls);
        Assert.Empty((await factory.Services.GetRequiredService<SqliteTaskStore>().GetDiscussionAsync(id, default))!.Messages);
    }

    [Fact] public async Task Failed_reply_is_saved_and_can_be_retried_without_duplicate_user_messages()
    {
        var d = await CreateDiscussion(); var id = d.GetProperty("id").GetGuid(); runner.Fail = true;
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = 0, message = "Hello" })).StatusCode);
        var saved = (await factory.Services.GetRequiredService<SqliteTaskStore>().GetDiscussionAsync(id, default))!;
        Assert.Single(saved.Messages); Assert.Null(saved.ActiveOperation); Assert.NotNull(saved.Error);
        runner.Fail = false;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/discussions/{id}/reply", new { expectedVersion = saved.Version })).StatusCode);
        Assert.Equal(2, (await factory.Services.GetRequiredService<SqliteTaskStore>().GetDiscussionAsync(id, default))!.Messages.Length);
    }
    private sealed class ReadOnlyWorkspace : IWorkspaceManager
    {
        public Task<Workspace> CreateAsync(ProjectDefinition p, Guid id, CancellationToken ct) => Task.FromResult(new Workspace(p.RepositoryPath, "base-commit"));
        public Task AssertUnchangedAsync(Workspace w, CancellationToken ct) => Task.CompletedTask;
        public Task<string> SnapshotAsync(Workspace w, CancellationToken ct) => throw new InvalidOperationException("Discussion must never commit.");
    }
    private sealed class ReadOnlyRunner : IPlanningRunner
    {
        public int Calls; public bool Fail;
        public Task<string> PlanAsync(WorkTask task, Workspace w, Guid id, CancellationToken ct)
        {
            Calls++; if (Fail) throw new InvalidOperationException("Provider unavailable");
            Assert.Equal("chosen-model", task.Ai!.Planning!.Model);
            return Task.FromResult(task.ReadOnlyInstruction!.StartsWith("Convert", StringComparison.Ordinal)
                ? "{\"tasks\":[{\"title\":\"Audit log\",\"description\":\"Add audit logs\",\"acceptanceCriteria\":\"Events persist\",\"openQuestions\":[\"What is retention?\"]}]}"
                : "We can add an audit log. How long should records be kept?");
        }
    }
}
