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
            .ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["DevFlow:DatabasePath"] = Path.Combine(directory, "api.db"), ["DevFlow:ArtifactsRoot"] = Path.Combine(directory, "artifacts") }))
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

    [Fact] public async Task Latest_failed_discussion_artifacts_are_available_without_starting_another_model()
    {
        var discussion = await CreateDiscussion(); var id = discussion.GetProperty("id").GetGuid();
        var store = factory.Services.GetRequiredService<SqliteTaskStore>();
        var started = await store.BeginDiscussionAsync(id, 0, "Read project", default);
        var operation = started.ActiveOperation!.Value;
        var artifacts = Path.Combine(directory, "artifacts", operation.ToString("N"));
        Directory.CreateDirectory(artifacts);
        await File.WriteAllTextAsync(Path.Combine(artifacts, "Planning-agent.stderr.log"), "test diagnostic");
        await store.FinishDiscussionAsync(id, operation, null, null, null, "test failure", default);
        var files = await client.GetFromJsonAsync<JsonElement>($"/api/discussions/{id}/artifacts");
        Assert.Single(files.EnumerateArray());
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/discussions/{id}/artifacts/Planning-agent.stderr.log");
        Assert.Equal("test diagnostic", page.GetProperty("text").GetString());
        Assert.Equal(0, runner.Calls);
        Assert.Equal(operation, (await store.GetDiscussionAsync(id, default))!.LastOperationId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/discussions/{Guid.NewGuid()}/artifacts/Planning-agent.stderr.log")).StatusCode);
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

    [Fact] public async Task Project_only_creation_names_from_first_question_and_switches_models_without_losing_history()
    {
        var created = await client.PostAsJsonAsync("/api/discussions", new { projectId = project.Id });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.Equal(0, runner.Calls);
        var d = await created.Content.ReadFromJsonAsync<JsonElement>(); var id = d.GetProperty("id").GetGuid();
        var question = "如何改善訂單 API？\n希望先討論架構";
        var first = await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = 0, message = question, selection = new { provider = "codex", model = "first", reasoningEffort = "high" } });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var saved = (await store().GetDiscussionAsync(id, default))!;
        Assert.Equal("如何改善訂單 API？", saved.Title); Assert.Equal("first", saved.Messages[1].Selection!.Model);
        var second = await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = saved.Version, message = "換個角度分析", selection = new { provider = "antigravity", model = "second", reasoningEffort = "low" } });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        saved = (await store().GetDiscussionAsync(id, default))!;
        Assert.Equal(4, saved.Messages.Length); Assert.Equal("first", saved.Messages[1].Selection!.Model);
        Assert.Equal("antigravity", saved.Messages[3].Selection!.Provider); Assert.Equal("low", saved.Messages[3].Selection!.ReasoningEffort);
        Assert.Contains(question, runner.Transcript); Assert.Contains(saved.Messages[1].Content, runner.Transcript);
        Assert.Equal("如何改善訂單 API？", saved.Title);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/discussions/{id}/title", new { title = "訂單架構", expectedVersion = saved.Version })).StatusCode);
        saved = (await store().GetDiscussionAsync(id, default))!; Assert.False(saved.AutoTitle); Assert.Equal("訂單架構", saved.Title);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/discussions/{id}/title", new { title = "過期操作", expectedVersion = 0 })).StatusCode);
    }

    [Fact] public async Task Background_reply_can_be_stopped_and_stale_operations_cannot_stop_another_turn()
    {
        var d = await CreateDiscussion(); var id = d.GetProperty("id").GetGuid(); runner.Block = true;
        var response = await client.PostAsJsonAsync($"/api/discussions/{id}/messages?background=1", new { expectedVersion = 0, message = "等待回覆", selection = new { provider = "antigravity" } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var active = (await store().GetDiscussionAsync(id, default))!;
        Assert.Single(active.Messages); Assert.NotNull(active.ActiveOperation);
        var progress = await client.GetFromJsonAsync<JsonElement>($"/api/discussions/{id}/progress");
        Assert.Equal("正在分析", progress.GetProperty("content").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/discussions/{id}/stop", new { operationId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = active.Version, message = "重複" })).StatusCode);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/discussions/{id}/stop", new { operationId = active.ActiveOperation })).StatusCode);
        for (var i = 0; i < 100; i++) { active = (await store().GetDiscussionAsync(id, default))!; if (active.ActiveOperation is null) break; await Task.Delay(20); }
        Assert.Null(active.ActiveOperation); Assert.Contains("已停止", active.Error);
        Assert.Contains("回覆已停止", active.Messages.Last().Content);
        runner.Block = false;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = active.Version, message = "繼續" })).StatusCode);
        Assert.Equal(2, runner.Calls);
    }

    [Fact] public async Task Invalid_selection_does_not_save_a_message_or_start_a_model()
    {
        var d = await CreateDiscussion(); var id = d.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = 0, message = "Hello", selection = new { provider = "invalid" } })).StatusCode);
        Assert.Empty((await store().GetDiscussionAsync(id, default))!.Messages); Assert.Equal(0, runner.Calls);
    }
    [Fact] public async Task Web_permission_reply_requires_same_origin_and_exact_active_operation()
    {
        var d = await CreateDiscussion(); var id = d.GetProperty("id").GetGuid();
        var operations = factory.Services.GetRequiredService<DevFlow.Api.DiscussionOperations>();
        var operation = operations.Start(id, Guid.NewGuid(), new("antigravity"), default);
        try
        {
            using var args = JsonDocument.Parse("{\"CommandLine\":\"Get-ChildItem -Directory\"}");
            var waiting = operation.RequestPermissionAsync("run_command", args.RootElement, directory, default);
            var request = operation.Permissions.Single();
            var progress = await client.GetFromJsonAsync<JsonElement>($"/api/discussions/{id}/progress");
            Assert.Equal(request.Id, progress.GetProperty("permissions")[0].GetProperty("id").GetGuid());
            var route = $"/api/discussions/{id}/permissions/{request.Id}";
            client.DefaultRequestHeaders.Remove("X-DevFlow-Action");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, new { operationId = operation.Id, allow = true })).StatusCode);
            client.DefaultRequestHeaders.Add("X-DevFlow-Action", "1");
            client.DefaultRequestHeaders.Add("Origin", "https://other.example");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, new { operationId = operation.Id, allow = true })).StatusCode);
            client.DefaultRequestHeaders.Remove("Origin");
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route, new { operationId = Guid.NewGuid(), allow = true })).StatusCode);
            Assert.False(waiting.IsCompleted);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(route, new { operationId = operation.Id, allow = false })).StatusCode);
            Assert.False(await waiting);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route, new { operationId = operation.Id, allow = true })).StatusCode);
        }
        finally { operations.Finish(id, operation); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Background_discussion_waits_for_web_decision_and_resumes_the_same_hook(bool allow)
    {
        var d = await CreateDiscussion(); var id = d.GetProperty("id").GetGuid(); runner.UseHook = true;
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/discussions/{id}/messages?background=1",
            new { expectedVersion = 0, message = "確認工具請求", selection = new { provider = "antigravity" } })).StatusCode);
        JsonElement progress = default; Guid requestId = Guid.Empty, operationId = Guid.Empty;
        for (var i = 0; i < 300; i++)
        {
            progress = await client.GetFromJsonAsync<JsonElement>($"/api/discussions/{id}/progress");
            if (progress.GetProperty("permissions").GetArrayLength() > 0)
            {
                requestId = progress.GetProperty("permissions")[0].GetProperty("id").GetGuid();
                operationId = progress.GetProperty("operationId").GetGuid(); break;
            }
            await Task.Delay(20);
        }
        Assert.NotEqual(Guid.Empty, requestId);
        Assert.Single((await store().GetDiscussionAsync(id, default))!.Messages);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/discussions/{id}/permissions/{requestId}", new { operationId, allow })).StatusCode);
        Discussion saved = null!;
        for (var i = 0; i < 300; i++)
        {
            saved = (await store().GetDiscussionAsync(id, default))!;
            if (saved.ActiveOperation is null) break; await Task.Delay(20);
        }
        Assert.Null(saved.ActiveOperation); Assert.Equal(1, runner.Calls);
        if (allow) { Assert.Equal(2, saved.Messages.Length); Assert.Null(saved.Error); }
        else { Assert.Single(saved.Messages); Assert.Contains("你已拒絕", saved.Error); }
        Assert.False(File.Exists(Path.Combine(directory, ".agents", "hooks.json")));
    }

    private SqliteTaskStore store() => factory.Services.GetRequiredService<SqliteTaskStore>();
    private sealed class ReadOnlyWorkspace : IWorkspaceManager
    {
        public Task<Workspace> CreateAsync(ProjectDefinition p, Guid id, CancellationToken ct) => Task.FromResult(new Workspace(p.RepositoryPath, "base-commit"));
        public Task AssertUnchangedAsync(Workspace w, CancellationToken ct) => Task.CompletedTask;
        public Task<string> SnapshotAsync(Workspace w, CancellationToken ct) => throw new InvalidOperationException("Discussion must never commit.");
    }
    private sealed class ReadOnlyRunner : IPlanningRunner
    {
        public int Calls; public bool Fail, Block, UseHook; public string? Transcript;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> PlanAsync(WorkTask task, Workspace w, Guid id, CancellationToken ct)
        {
            Calls++; if (Fail) throw new InvalidOperationException("Provider unavailable");
            Transcript = task.Description;
            if (UseHook)
            {
                // Hook installation now belongs to RoutedCliRunner. This fake runner exercises
                // that same hook with the operation supplied by the API's common permission scope.
                await using var permissionHook = await DevFlow.Runners.NativePermissionHook.CreateAsync(
                    CliPermissions.Current!, w.Path, Path.Combine(w.Path, id + "-permissions.jsonl"), ct,
                    typeof(DevFlow.Api.AntigravityPermissionHook).Assembly.Location);
                using var hooks = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(w.Path, ".agents", "hooks.json"), ct));
                var command = hooks.RootElement.EnumerateObject().Single(p => p.Name.StartsWith("devflow-web-approval-")).Value.GetProperty("PreToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
                var input = JsonSerializer.Serialize(new { conversationId = Guid.NewGuid(), workspacePaths = new[] { w.Path }, stepIdx = 1,
                    toolCall = new { name = "run_command", args = new { CommandLine = "Get-ChildItem -Directory", Cwd = w.Path } } });
                var result = OperatingSystem.IsWindows()
                    ? await new ProcessRunner().RunAsync("cmd.exe", ["/d", "/s", "/c", command], w.Path, input, ct)
                    : await new ProcessRunner().RunAsync("/bin/sh", ["-c", command], w.Path, input, ct);
                using var decision = JsonDocument.Parse(result.StandardOutput);
                if (decision.RootElement.GetProperty("decision").GetString() != "auto_approve") throw new InvalidOperationException("工具權限被拒絕");
                return "已收到本次工具核准。";
            }
            if (Block)
            {
                ProcessOutput.Observer?.Invoke("{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"agent_response\",\"text_delta\":\"正在分析\"}}\n");
                Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return task.ReadOnlyInstruction!.StartsWith("Convert", StringComparison.Ordinal)
                ? "{\"tasks\":[{\"title\":\"Audit log\",\"description\":\"Add audit logs\",\"acceptanceCriteria\":\"Events persist\",\"openQuestions\":[\"What is retention?\"]}]}"
                : "We can add an audit log. How long should records be kept?";
        }
    }
}
