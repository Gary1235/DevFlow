using System.Text.Json;
using System.Threading.Channels;
using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Runners;
using Xunit;

namespace DevFlow.Tests;

public sealed class UnifiedPermissionTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-unified-" + Guid.NewGuid());
    private SqliteTaskStore store = null!;
    private ProjectDefinition project = null!;
    public async Task InitializeAsync()
    {
        store = new(Path.Combine(root, "tasks.db")); await store.InitializeAsync();
        project = new(Guid.NewGuid(), "Test", root, "main", ["test"]); await store.AddProjectAsync(project);
    }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }
    private sealed class NativeFixture(string provider, bool turnGrant = false) : IProcessRunner, IInteractiveProcessRunner
    {
        public bool TurnGrant { get; } = turnGrant;
        public List<JsonElement> Sent { get; } = [];
        public List<string[]> Arguments { get; } = [];
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string cwd, string? input, CancellationToken ct) => throw new InvalidOperationException("Must use native duplex protocol.");
        public async Task<ProcessResult> RunInteractiveAsync(string e, IReadOnlyList<string> a, string cwd, Func<IProcessSession, CancellationToken, Task> exchange, CancellationToken ct)
        {
            Arguments.Add(a.ToArray()); var session = new Session(this, provider, cwd);
            await exchange(session, ct); return new(0, "native fixture artifacts", "");
        }
        private sealed class Session(NativeFixture owner, string provider, string cwd) : IProcessSession
        {
            private readonly Channel<JsonElement> frames = Channel.CreateUnbounded<JsonElement>();
            private void Frame(object value) => frames.Writer.TryWrite(JsonSerializer.SerializeToElement(value));
            private string Plan => provider == "codex" ? "{\"plan\":\"Update shipment layout and run npm run build.\"}" : "Update shipment layout and run npm run build.";
            public async Task<JsonElement?> ReadAsync(CancellationToken ct) => await frames.Reader.ReadAsync(ct);
            public Task SendAsync(object message, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested(); var json = JsonSerializer.SerializeToElement(message); owner.Sent.Add(json);
                var method = json.TryGetProperty("method", out var m) ? m.GetString() : null;
                if (provider == "codex")
                {
                    if (method == "initialize") Frame(new { id = 1, result = new { userAgent = "fixture" } });
                    if (method == "thread/start") Frame(new { id = 2, result = new { thread = new { id = "thread" } } });
                    if (method == "turn/start")
                    {
                        Frame(new { id = 3, result = new { turn = new { id = "turn", status = "inProgress" } } });
                        if(owner.TurnGrant) Frame(new {id=90,method="item/permissions/requestApproval",@params=new{threadId="thread",turnId="turn",permissions=new{network=true}}});
                        else Frame(new { id = 90, method = "item/commandExecution/requestApproval", @params = new { threadId = "thread", turnId = "turn", itemId = "command", command = "git diff --stat", cwd } });
                    }
                    if (method is null && json.TryGetProperty("id", out var id) && id.GetInt32() == 90)
                    {
                        Frame(new { method = "item/completed", @params = new { threadId = "thread", turnId = "turn", item = new { id = "answer", type = "agentMessage", text = Plan } } });
                        Frame(new { method = "turn/completed", @params = new { threadId = "thread", turn = new { id = "turn", status = "completed" } } });
                    }
                }
                if (provider == "claude")
                {
                    var type = json.GetProperty("type").GetString();
                    if (type == "control_request") Frame(new { type = "control_response", response = new { subtype = "success", request_id = "devflow-initialize", response = new { } } });
                    if (type == "user") Frame(new { type = "control_request", request_id = "native-request", request = new { subtype = "can_use_tool", tool_name = "Bash", input = new { command = "git diff --stat" } } });
                    if (type == "control_response") Frame(new { type = "result", subtype = "success", is_error = false, result = Plan });
                }
                if (provider == "gemini")
                {
                    if (method == "initialize") Frame(new { jsonrpc = "2.0", id = 1, result = new { protocolVersion = 1 } });
                    if (method == "session/new") Frame(new { jsonrpc = "2.0", id = 2, result = new { sessionId = "session" } });
                    if (method == "session/prompt")
                    {
                        Frame(new { method = "session/update", @params = new { sessionId = "session", update = new { sessionUpdate = "tool_call", toolCallId = "tool", name = "run_shell_command", kind = "execute", rawInput = new { command = "git diff --stat" } } } });
                        Frame(new { id = 90, method = "session/request_permission", @params = new { sessionId = "session", toolCall = new { toolCallId = "tool" }, options = new[] { new { optionId = "once", kind = "allow_once" }, new { optionId = "never", kind = "reject_once" }, new { optionId = "always", kind = "allow_always" } } } });
                    }
                    if (method is null && json.TryGetProperty("id", out var id) && id.GetInt32() == 90)
                    {
                        Frame(new { method = "session/update", @params = new { sessionId = "session", update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = Plan } } } });
                        Frame(new { id = 3, result = new { stopReason = "end_turn" } });
                    }
                }
                return Task.CompletedTask;
            }
        }
    }
    private async Task<TaskToolApproval> Pending(Guid taskId)
    {
        for (var i = 0; i < 200; i++)
        {
            var request = (await store.ListToolApprovalsAsync(taskId)).LastOrDefault(r => r.State == "Pending");
            if (request is not null) return request; await Task.Delay(10);
        }
        throw new TimeoutException("No unified approval request.");
    }
    private RoutedCliRunner Runner(string provider, NativeFixture process)
    {
        // The fixture is the transport; only the registry's existence validation uses this executable.
        var executable = Environment.ProcessPath!;
        return new(process, new CliRegistry(codex: executable, claude: executable, gemini: executable), root, store);
    }
    [Theory]
    [InlineData("codex", true)] [InlineData("claude", true)] [InlineData("gemini", true)]
    [InlineData("codex", false)] [InlineData("claude", false)] [InlineData("gemini", false)]
    public async Task Native_protocols_wait_for_the_same_web_permission_and_reply_once(string provider, bool allow)
    {
        var task = WorkTask.Create(project.Id, "Feature", "Build", "Test") with { Ai = new(new(provider), new(provider)) };
        await store.AddTaskAsync(task); await store.QueuePlanningAsync(task.Id, task.Version); var claim = (await store.ClaimPlanningAsync())!;
        var process = new NativeFixture(provider); var runner = Runner(provider, process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = runner.PlanAsync(claim.Task, new(root, "base"), claim.Run.Id, timeout.Token);
        var pending = await Pending(task.Id); Assert.False(running.IsCompleted); Assert.Equal(provider, pending.Provider);
        Assert.Equal("run_command", pending.Tool); Assert.Equal("git diff --stat", pending.Arguments.GetProperty("CommandLine").GetString());
        Assert.False(pending.Risk.BatchAllowed);
        await Assert.ThrowsAsync<ArgumentException>(()=>store.ReplyToolApprovalWithRuleAsync(task.Id,pending.Id,claim.Run.Id,true,new("Task","GeneralRead",".")));
        await store.ReplyToolApprovalWithRuleAsync(task.Id, pending.Id, claim.Run.Id, allow, null);
        if (allow)
        {
            Assert.Contains("shipment", await running);
            Assert.Empty(await store.ListPermissionRulesAsync());
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => running);
        Assert.DoesNotContain(process.Arguments.SelectMany(x => x), x => x.Contains("dangerously") || x == "--yolo");
        if (provider == "codex") Assert.All(process.Sent.Where(x => x.TryGetProperty("result", out _) && x.TryGetProperty("id", out var id) && id.GetInt32() == 90), x => Assert.Equal(allow ? "accept" : "decline", x.GetProperty("result").GetProperty("decision").GetString()));
        if (provider == "claude") Assert.All(process.Sent.Where(x => x.TryGetProperty("type", out var t) && t.GetString() == "control_response"), x => {
            var response = x.GetProperty("response").GetProperty("response"); Assert.Equal(allow ? "allow" : "deny", response.GetProperty("behavior").GetString()); Assert.False(response.TryGetProperty("updatedPermissions", out _));
        });
        if (provider == "gemini") Assert.All(process.Sent.Where(x => x.TryGetProperty("result", out _) && x.TryGetProperty("id", out var id) && id.GetInt32() == 90), x => Assert.Equal(allow ? "once" : "never", x.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString()));
    }
    [Theory] [InlineData("codex")] [InlineData("claude")] [InlineData("gemini")]
    public async Task Implementation_uses_the_same_permission_queue(string provider)
    {
        var task = WorkTask.Create(project.Id, "Feature", "Build", "Test").SetPlan("Implement then run tests.").ApprovePlan() with { Ai = new(new(provider), new(provider)) };
        await store.AddTaskAsync(task); var claim = (await store.ClaimNextAsync())!;
        var process = new NativeFixture(provider);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = Runner(provider, process).ExecuteAsync(claim.Task, new(root, "base"), claim.Run.Id, TddPhase.Implementation, timeout.Token);
        var pending = await Pending(task.Id); await store.ReplyToolApprovalAsync(task.Id, pending.Id, claim.Run.Id, true);
        await running; Assert.Equal("Allowed", (await store.ListToolApprovalsAsync(task.Id)).Single().State);
    }
    [Theory] [InlineData("codex")] [InlineData("claude")] [InlineData("gemini")]
    public async Task Cancellation_expires_the_native_permission_instead_of_approving_it(string provider)
    {
        var task=WorkTask.Create(project.Id,"Feature","Build","Test") with {Ai=new(new(provider),new(provider))};
        await store.AddTaskAsync(task);await store.QueuePlanningAsync(task.Id,task.Version);var claim=(await store.ClaimPlanningAsync())!;
        var process=new NativeFixture(provider);using var cancellation=new CancellationTokenSource();
        var running=Runner(provider,process).PlanAsync(claim.Task,new(root,"base"),claim.Run.Id,cancellation.Token);
        await Pending(task.Id);cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>running);
        Assert.Equal("Expired",(await store.ListToolApprovalsAsync(task.Id)).Single().State);
    }
    [Fact] public async Task Turn_wide_native_permission_grants_are_never_presented_as_single_use()
    {
        var task=WorkTask.Create(project.Id,"Feature","Build","Test") with {Ai=new(new("codex"),new("codex"))};
        await store.AddTaskAsync(task);await store.QueuePlanningAsync(task.Id,task.Version);var claim=(await store.ClaimPlanningAsync())!;
        var process=new NativeFixture("codex",turnGrant:true);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>Runner("codex",process).PlanAsync(claim.Task,new(root,"base"),claim.Run.Id,default));
        Assert.Contains("整個回合",error.Message);Assert.Empty(await store.ListToolApprovalsAsync(task.Id));
        var decision=Assert.Single(process.Sent,x=>x.TryGetProperty("id",out var id)&&id.GetInt32()==90);
        Assert.Empty(decision.GetProperty("result").GetProperty("permissions").EnumerateObject());
    }
}
