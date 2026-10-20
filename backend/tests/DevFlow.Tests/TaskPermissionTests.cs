using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevFlow.Api;
using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Runners;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
using Status = DevFlow.Core.TaskStatus;

namespace DevFlow.Tests;

public sealed class TaskPermissionTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-task-permissions-" + Guid.NewGuid());
    private SqliteTaskStore store = null!;
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private WorkTask task = null!;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "tasks.db");
        store = new(database); await store.InitializeAsync();
        var project = new ProjectDefinition(Guid.NewGuid(), "Test", root, "main", ["test"]);
        await store.AddProjectAsync(project);
        task = WorkTask.Create(project.Id, "Plan", "Inspect", "Validate") with { Ai = new(new("antigravity"), new("codex")) };
        await store.AddTaskAsync(task); await store.QueuePlanningAsync(task.Id, task.Version);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["DevFlow:DatabasePath"] = database })));
        client = factory.CreateClient();
    }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); Directory.Delete(root, true); }

    private async Task<TaskToolApproval> Pending()
    {
        for (var i = 0; i < 300; i++)
        {
            var request = (await client.GetFromJsonAsync<TaskToolApproval[]>($"/api/tasks/{task.Id}/permissions"))!.LastOrDefault(r => r.State == "Pending");
            if (request != null) return request;
            await Task.Delay(30);
        }
        throw new TimeoutException("No task approval appeared.");
    }
    private async Task<HttpResponseMessage> Reply(TaskToolApproval request, bool allow, Guid? operationId = null, string? origin = null, bool explicitAction = true, ToolPermissionChoice? rule = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/tasks/{task.Id}/permissions/{request.Id}")
        { Content = JsonContent.Create(new { operationId = operationId ?? request.OperationId, allow, rule }) };
        if (explicitAction) message.Headers.Add("X-DevFlow-Action", "1");
        if (origin != null) message.Headers.Add("Origin", origin);
        return await client.SendAsync(message);
    }
    private sealed class WorkspaceManager(string root) : IWorkspaceManager
    {
        public Task<Workspace> CreateAsync(ProjectDefinition p, Guid id, CancellationToken ct) => Task.FromResult(new Workspace(root, "base"));
        public Task<string> SnapshotAsync(Workspace w, CancellationToken ct) => throw new InvalidOperationException();
        public Task AssertUnchangedAsync(Workspace w, CancellationToken ct)
        { Assert.False(File.Exists(Path.Combine(root, ".agents", "hooks.json"))); return Task.CompletedTask; }
    }
    // Exercises the actual hook command/helper process without calling an AI or executing the requested command.
    private sealed class HookPlanner(bool readCode = false) : IPlanningRunner
    {
        public async Task<string> PlanAsync(WorkTask task, Workspace w, Guid id, CancellationToken ct)
        {
            using var hooks = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(w.Path, ".agents", "hooks.json"), ct));
            var command = hooks.RootElement.EnumerateObject().Single().Value.GetProperty("PreToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
            var payload = JsonSerializer.Serialize(new { conversationId = Guid.NewGuid(), workspacePaths = new[] { w.Path }, stepIdx = 1,
                toolCall = new { name = readCode ? "read_file" : "run_command", args = readCode ? (object)new { Path = Path.Combine(w.Path,"source.cs") } : new { CommandLine = "Get-ChildItem -Directory", Cwd = w.Path } } });
            var result = OperatingSystem.IsWindows()
                ? await new ProcessRunner().RunAsync("cmd.exe", ["/d", "/s", "/c", command], w.Path, payload, ct)
                : await new ProcessRunner().RunAsync("/bin/sh", ["-c", command], w.Path, payload, ct);
            Assert.Equal(0, result.ExitCode);
            using var answer = JsonDocument.Parse(result.StandardOutput);
            if (answer.RootElement.GetProperty("decision").GetString() != "auto_approve") throw new InvalidOperationException("Tool was blocked.");
            return "1. Update the shipment list layout.\n2. Run npm run build and check the list page.";
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_planning_hook_waits_beyond_three_seconds_for_web_reply(bool allow)
    {
        var planner = new TaskPermissionPlanningRunner(new HookPlanner(), store, Path.Combine(root, "artifacts"), typeof(AntigravityPermissionHook).Assembly.Location);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = new PlanningCoordinator(store, planner, new WorkspaceManager(root)).ExecuteNextAsync(timeout.Token);
        try
        {
            var request = await Pending();
            await Task.Delay(3500, timeout.Token);
            Assert.False(running.IsCompleted);
            Assert.Equal(Status.Planning, (await store.GetTaskAsync(task.Id))!.Status);
            Assert.Equal("Pending", (await store.ListToolApprovalsAsync(task.Id)).Single().State);
            Assert.Equal("Get-ChildItem -Directory", request.Arguments.GetProperty("CommandLine").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await Reply(request, true, explicitAction: false)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Reply(request, true, origin: "https://foreign.example")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await Reply(request, true, Guid.NewGuid())).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Reply(request, allow)).StatusCode);
            await running.WaitAsync(timeout.Token);
            Assert.Equal(allow ? Status.AwaitingApproval : Status.PlanningFailed, (await store.GetTaskAsync(task.Id))!.Status);
            Assert.Equal(allow ? "Allowed" : "Denied", (await store.ListToolApprovalsAsync(task.Id)).Single().State);
            Assert.Equal(HttpStatusCode.Conflict, (await Reply(request, true)).StatusCode);
            Assert.False(File.Exists(Path.Combine(root, ".agents", "hooks.json")));
            if (!allow) Assert.Empty(await store.ListPlansAsync(task.Id));
        }
        finally { timeout.Cancel(); await running; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancelled_or_expired_approval_cannot_be_approved_later(bool cancel)
    {
        var claim = (await store.ClaimPlanningAsync())!;
        var now = DateTimeOffset.UtcNow;
        using var args = JsonDocument.Parse("{\"CommandLine\":\"example\"}");
        var request = new TaskToolApproval(Guid.NewGuid(), task.Id, claim.Run.Id, "run_command", args.RootElement, root, now, now.AddMilliseconds(100));
        await store.AddToolApprovalAsync(request);
        if (cancel) await store.SaveTaskAsync(claim.Task.Cancel(), claim.Task.Version);
        else await Task.Delay(150);
        Assert.Equal("Expired", (await store.ListToolApprovalsAsync(task.Id)).Single().State);
        Assert.Equal(HttpStatusCode.Conflict, (await Reply(request, true)).StatusCode);
    }

    [Fact] public async Task Cancelling_the_operation_expires_its_pending_request()
    {
        var claim = (await store.ClaimPlanningAsync())!;
        using var token = new CancellationTokenSource();
        var operation = new TaskPermissionPlanningRunner.Operation(store, task.Id, claim.Run.Id, token.Token);
        using var args = JsonDocument.Parse("{\"CommandLine\":\"example\"}");
        var waiting = operation.RequestPermissionAsync("run_command", args.RootElement, root, token.Token);
        var request = await Pending(); token.Cancel();
        Assert.False(await waiting);
        Assert.Equal("Expired", (await store.ListToolApprovalsAsync(task.Id)).Single().State);
        Assert.Equal(HttpStatusCode.Conflict, (await Reply(request, true)).StatusCode);
    }

    private TaskToolApproval Request(Guid taskId, Guid runId, string command = "Get-ChildItem -Directory") =>
        new(Guid.NewGuid(), taskId, runId, "run_command", JsonSerializer.SerializeToElement(new { CommandLine = command, Cwd = root }), root, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(2));

    [Fact]
    public async Task General_read_batch_is_bound_to_task_and_never_covers_sensitive_requests()
    {
        var claim = (await store.ClaimPlanningAsync())!;
        TaskToolApproval Read(string file)=>new(Guid.NewGuid(),task.Id,claim.Run.Id,"read_file",JsonSerializer.SerializeToElement(new {Path=Path.Combine(root,file)}),root,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(2));
        var request = Read("first.ts"); await store.AddToolApprovalAsync(request);
        var earlierPending = Read("second.scss"); await store.AddToolApprovalAsync(earlierPending);
        Assert.Equal(HttpStatusCode.BadRequest,(await Reply(request,true,rule:new("Project","GeneralRead","."))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Reply(request, true, rule: new("Task", "GeneralRead", "."))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Reply(earlierPending, true, rule: new("Task", "GeneralRead", "."))).StatusCode);
        var rules = await client.GetFromJsonAsync<ToolPermissionRule[]>("/api/tool-permission-rules");
        var rule = Assert.Single(rules!); Assert.Equal(task.ProjectId, rule.ProjectId);
        Assert.Equal(task.Id, rule.TaskId);
        var next = Read("third.cs");
        // Another store instance represents the separate Worker process.
        await new SqliteTaskStore(store.DatabasePath).AddToolApprovalAsync(next);
        var approved = (await store.ListToolApprovalsAsync(task.Id)).Single(r => r.Id == next.Id);
        Assert.Equal("AllowedByRule", approved.State); Assert.Equal(rule.Id, approved.MatchedRuleId);
        var compound = Request(task.Id, claim.Run.Id, "Get-ChildItem; whoami"); await store.AddToolApprovalAsync(compound);
        Assert.Equal("Pending", (await store.ListToolApprovalsAsync(task.Id)).Single(r => r.Id == compound.Id).State);
        foreach(var file in new[]{".env","data.db","appsettings.json"}){
            var sensitive=Read(file);await store.AddToolApprovalAsync(sensitive);
            Assert.Equal("Pending",(await store.ListToolApprovalsAsync(task.Id)).Single(r=>r.Id==sensitive.Id).State);
            Assert.Equal(HttpStatusCode.BadRequest,(await Reply(sensitive,true,rule:new("Task","GeneralRead","."))).StatusCode);
        }
        await store.CompletePlanningAsync(claim.Run.Id, "Update layout and run build.", null);
        var second = WorkTask.Create(task.ProjectId, "Other task", "Build", "Test");
        await store.AddTaskAsync(second); await store.QueuePlanningAsync(second.Id, second.Version);
        var otherClaim = (await store.ClaimPlanningAsync())!;
        var other = Request(second.Id, otherClaim.Run.Id); await store.AddToolApprovalAsync(other);
        Assert.Equal("Pending", (await store.ListToolApprovalsAsync(second.Id)).Single().State);
    }

    [Fact] public async Task Rule_settings_support_edit_disable_delete_and_reject_stale_or_foreign_changes()
    {
        var rule = await store.SavePermissionRuleAsync(new(Guid.NewGuid(), task.ProjectId, null, "run_command", "CommandPrefix", "Get-ChildItem"), null);
        var path = "/api/tool-permission-rules/" + rule.Id;
        object Input(bool enabled, int version, string pattern = "Get-ChildItem") => new { rule.ProjectId, rule.TaskId, rule.Tool, rule.Kind, pattern, enabled, expectedVersion = version };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, Input(false, 0))).StatusCode);
        client.DefaultRequestHeaders.Add("X-DevFlow-Action", "1");
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path, Input(false, 0, "Get-ChildItem -Name"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, Input(true, 0))).StatusCode);
        var saved = Assert.Single((await client.GetFromJsonAsync<ToolPermissionRule[]>("/api/tool-permission-rules"))!);
        Assert.False(saved.Enabled); Assert.Equal("Get-ChildItem -Name", saved.Pattern);
        var claim = (await store.ClaimPlanningAsync())!;
        var request = Request(task.Id, claim.Run.Id, "Get-ChildItem -Name"); await store.AddToolApprovalAsync(request);
        Assert.Equal("Pending", (await store.ListToolApprovalsAsync(task.Id)).Single().State);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(path + "?expectedVersion=0")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(path + "?expectedVersion=1")).StatusCode);
        Assert.Empty(await store.ListPermissionRulesAsync());
    }

    [Fact] public async Task Native_planning_hook_uses_saved_rule_without_a_web_reply()
    {
        var rule = await store.SavePermissionRuleAsync(new(Guid.NewGuid(), task.ProjectId, task.Id, "general_read", "GeneralRead", "."), null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var planner = new TaskPermissionPlanningRunner(new HookPlanner(true), store, Path.Combine(root, "artifacts"), typeof(AntigravityPermissionHook).Assembly.Location);
        Assert.True(await new PlanningCoordinator(store, planner, new WorkspaceManager(root)).ExecuteNextAsync(timeout.Token));
        Assert.Equal(Status.AwaitingApproval, (await store.GetTaskAsync(task.Id))!.Status);
        var request = Assert.Single(await store.ListToolApprovalsAsync(task.Id));
        Assert.Equal("AllowedByRule", request.State); Assert.Equal(rule.Id, request.MatchedRuleId);
        Assert.False(File.Exists(Path.Combine(root, ".agents", "hooks.json")));
    }

    [Fact] public async Task Stale_permission_reply_cannot_create_a_permanent_rule()
    {
        var claim = (await store.ClaimPlanningAsync())!;
        var request = Request(task.Id, claim.Run.Id); await store.AddToolApprovalAsync(request);
        await store.SaveTaskAsync(claim.Task.Cancel(), claim.Task.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await Reply(request, true, rule: new("Project", "CommandPrefix", "Get-ChildItem"))).StatusCode);
        Assert.Empty(await store.ListPermissionRulesAsync());
    }
    [Fact] public async Task Settings_api_rejects_sensitive_and_permanent_grants_even_when_posted_directly()
    {
        client.DefaultRequestHeaders.Add("X-DevFlow-Action","1");
        foreach(var input in new[]{
            new { ProjectId=task.ProjectId,TaskId=(Guid?)task.Id,Tool="write_file",Kind="WorkspacePath",Pattern="." },
            new { ProjectId=task.ProjectId,TaskId=(Guid?)task.Id,Tool="run_command",Kind="Exact",Pattern="{}" },
            new { ProjectId=task.ProjectId,TaskId=(Guid?)null,Tool="general_read",Kind="GeneralRead",Pattern="." }
        }) Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/tool-permission-rules",input)).StatusCode);
        Assert.Empty(await store.ListPermissionRulesAsync());
        Assert.Equal(HttpStatusCode.Created,(await client.PostAsJsonAsync("/api/tool-permission-rules",new {task.ProjectId,TaskId=task.Id,Tool="general_read",Kind="GeneralRead",Pattern=".",Provider="*"})).StatusCode);
    }
}
