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
    private sealed class NativeFixture(string provider, bool turnGrant = false, string commandText = "git diff --stat", string? toolName = null, object? toolInput = null, bool imageSupport=true) : IProcessRunner, IInteractiveProcessRunner
    {
        public string? ToolName { get; } = toolName;
        public object? ToolInput { get; } = toolInput;
        public string CommandText {get;}=commandText;
        public bool TurnGrant { get; } = turnGrant;
        public bool ImageSupport {get;}=imageSupport;
        public CancellationTokenSource Exited { get; } = new();
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
            public CancellationToken ProcessExited => owner.Exited.Token;
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
                        else Frame(new { id = 90, method = "item/commandExecution/requestApproval", @params = new { threadId = "thread", turnId = "turn", itemId = "command", command = owner.CommandText, cwd } });
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
                    if (type == "user") Frame(new { type = "control_request", request_id = "native-request", request = new { subtype = "can_use_tool", tool_name = owner.ToolName ?? "Bash", input = owner.ToolInput ?? new { command = owner.CommandText } } });
                    if (type == "control_response") Frame(new { type = "result", subtype = "success", is_error = false, result = Plan });
                }
                if (provider == "gemini")
                {
                    if (method == "initialize") Frame(new { jsonrpc = "2.0", id = 1, result = new { protocolVersion = 1,agentCapabilities=new{promptCapabilities=new{image=owner.ImageSupport}} } });
                    if (method == "session/new") Frame(new { jsonrpc = "2.0", id = 2, result = new { sessionId = "session" } });
                    if (method == "session/prompt")
                    {
                        Frame(new { method = "session/update", @params = new { sessionId = "session", update = new { sessionUpdate = "tool_call", toolCallId = "tool", name = owner.ToolName ?? "run_shell_command", kind = owner.ToolName is null ? "execute" : "search", rawInput = owner.ToolInput ?? new { command = owner.CommandText } } } });
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
    [Theory]
    [InlineData("claude", "Glob")][InlineData("claude", "Grep")]
    [InlineData("gemini", "glob")][InlineData("gemini", "list_directory")][InlineData("gemini", "grep_search")]
    public async Task Native_search_protocols_auto_approve_the_same_worktree_scope(string provider, string tool)
    {
        var task=WorkTask.Create(project.Id,"Feature","Build","Test") with {Ai=new(new(provider),new(provider))};
        await store.AddTaskAsync(task);await store.QueuePlanningAsync(task.Id,task.Version);var claim=(await store.ClaimPlanningAsync())!;
        object input=provider=="claude"?new{pattern="synthetic"}:(object)new{pattern="synthetic",dir_path=root};
        var process=new NativeFixture(provider,toolName:tool,toolInput:input);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Runner(provider,process).PlanAsync(claim.Task,new(root,"base"),claim.Run.Id,timeout.Token);
        var approval=Assert.Single(await store.ListToolApprovalsAsync(task.Id));
        Assert.Equal("AllowedByPolicy",approval.State);
        if(provider=="claude") {
            var reply=Assert.Single(process.Sent,x=>x.TryGetProperty("type",out var t)&&t.GetString()=="control_response");
            var response=reply.GetProperty("response").GetProperty("response");
            Assert.Equal("allow",response.GetProperty("behavior").GetString());
            Assert.Equal(root,response.GetProperty("updatedInput").GetProperty("path").GetString());
        }
    }
    [Theory][InlineData("codex")][InlineData("claude")][InlineData("gemini")]
    public async Task Implementation_cli_reuses_exact_project_validation_grants(string provider)
    {
        await store.SetExecutionSettingsAsync(project.Id, 0, [new("build", "Build", ".", "dotnet", ["build"])], "build", [], null);
        var task=WorkTask.Create(project.Id,"Feature","Implement","Validate").SetPlan("Implement changes and run tests.").ApprovePlan() with { Ai=new(new(provider),new(provider)) };
        await store.AddTaskAsync(task);var claim=(await store.ClaimNextAsync())!;
        var process=new NativeFixture(provider,commandText:"dotnet build");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Runner(provider,process).ExecuteAsync(claim.Task,new(root,"base"),claim.Run.Id,TddPhase.Implementation,timeout.Token);
        var approval=Assert.Single(await store.ListToolApprovalsAsync(task.Id));
        Assert.Equal("AllowedByPolicy",approval.State);Assert.Equal("Execution",approval.Risk.Category);
        Assert.Contains("build",approval.Risk.Reason);Assert.Contains("專案版本 1",approval.Risk.Reason);
    }
    private RoutedCliRunner Runner(string provider, NativeFixture process)
    {
        // The fixture is the transport; only the registry's existence validation uses this executable.
        var executable = Environment.ProcessPath!;
        return new(process, new CliRegistry(codex: executable, claude: executable, gemini: executable), root, store);
    }
    [Theory][InlineData("codex")][InlineData("claude")][InlineData("gemini")]
    public async Task Images_are_sent_as_native_multimodal_content_not_as_plain_text_paths(string provider)
    {
        await store.SavePermissionPolicyAsync(new(AutomationEnabled:true),0);
        var task=WorkTask.Create(project.Id,"Image","Inspect","Describe") with{Ai=new(new(provider)),InputImages=[new("image.png","image/png","aGVsbG8=")]};
        await store.AddTaskAsync(task);await store.QueuePlanningAsync(task.Id,task.Version);var claim=(await store.ClaimPlanningAsync())!;
        var process=new NativeFixture(provider,commandText:"git status --short");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Runner(provider,process).PlanAsync(claim.Task with{InputImages=task.InputImages},new(root,"base"),claim.Run.Id,timeout.Token);
        var message=provider=="claude"?process.Sent.Single(s=>s.TryGetProperty("type",out var t)&&t.GetString()=="user"):
            process.Sent.Single(s=>s.TryGetProperty("method",out var m)&&m.GetString()==(provider=="codex"?"turn/start":"session/prompt"));
        var content=provider=="claude"?message.GetProperty("message").GetProperty("content"):message.GetProperty("params").GetProperty(provider=="codex"?"input":"prompt");
        var image=content.EnumerateArray().Single(i=>i.GetProperty("type").GetString()=="image");
        Assert.Equal(provider=="codex"?"data:image/png;base64,aGVsbG8=":"aGVsbG8=",provider=="codex"?image.GetProperty("url").GetString():provider=="claude"?image.GetProperty("source").GetProperty("data").GetString():image.GetProperty("data").GetString());
    }
    [Fact] public async Task Gemini_without_image_capability_fails_before_sending_the_prompt()
    {
        var task=WorkTask.Create(project.Id,"Image","Inspect","Describe") with{Ai=new(new("gemini")),InputImages=[new("image.png","image/png","aGVsbG8=")]};
        await store.AddTaskAsync(task);var process=new NativeFixture("gemini",imageSupport:false);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Runner("gemini",process).PlanAsync(task,new(root,"base"),Guid.NewGuid(),default));
        Assert.DoesNotContain(process.Sent,s=>s.TryGetProperty("method",out var m)&&m.GetString()=="session/prompt");
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
    [Theory] [InlineData("codex")] [InlineData("claude")] [InlineData("gemini")]
    public async Task Native_CLI_exit_invalidates_pending_web_approval_without_waiting_for_a_web_deadline(string provider)
    {
        var task=WorkTask.Create(project.Id,"Feature","Build","Test") with {Ai=new(new(provider),new(provider))};
        await store.AddTaskAsync(task);await store.QueuePlanningAsync(task.Id,task.Version);var claim=(await store.ClaimPlanningAsync())!;
        var process=new NativeFixture(provider);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running=Runner(provider,process).PlanAsync(claim.Task,new(root,"base"),claim.Run.Id,timeout.Token);
        var pending=await Pending(task.Id);Assert.Null(pending.ExpiresAt);
        process.Exited.Cancel();
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>running);
        Assert.Contains("CliInterrupted",error.Message);Assert.False(timeout.IsCancellationRequested);
        Assert.Equal("Expired",(await store.ListToolApprovalsAsync(task.Id)).Single().State);
        await Assert.ThrowsAsync<ConflictException>(()=>store.ReplyToolApprovalAsync(task.Id,pending.Id,claim.Run.Id,true));
    }
    [Theory][InlineData("codex")][InlineData("gemini")]
    public async Task Unknown_shell_read_requests_reach_shared_policy_without_keyword_based_readonly_denial(string provider)
    {
        var task=WorkTask.Create(project.Id,"Feature","Build","Test") with {Ai=new(new(provider),new(provider))};
        await store.AddTaskAsync(task);await store.QueuePlanningAsync(task.Id,task.Version);var claim=(await store.ClaimPlanningAsync())!;
        var process=new NativeFixture(provider,commandText:"Select-String -Pattern 'please delete file' -Path src/a.json -Culture en-US");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running=Runner(provider,process).PlanAsync(claim.Task,new(root,"base"),claim.Run.Id,timeout.Token);
        var pending=await Pending(task.Id);
        Assert.Equal("unknown",pending.Arguments.GetProperty("Shell").GetString());Assert.True(pending.Risk.FixedBoundary);
        await store.ReplyToolApprovalAsync(task.Id,pending.Id,claim.Run.Id,true);await running;
    }
    [Fact] public async Task Bash_service_command_never_inherits_PowerShell_alias_auto_approval()
    {
        if(!OperatingSystem.IsWindows())return;
        var task=WorkTask.Create(project.Id,"Feature","Build","Test").SetPlan("Implement changes.").ApprovePlan() with {Ai=new(new("claude"),new("claude"))};
        await store.AddTaskAsync(task);var claim=(await store.ClaimNextAsync())!;
        var process=new NativeFixture("claude",commandText:"sc stop Spooler");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running=Runner("claude",process).ExecuteAsync(claim.Task,new(root,"base"),claim.Run.Id,TddPhase.Implementation,timeout.Token);
        var pending=await Pending(task.Id);Assert.False(pending.Risk.AutoAllowed);
        Assert.Equal("bash",pending.Arguments.GetProperty("Shell").GetString());
        await store.ReplyToolApprovalAsync(task.Id,pending.Id,claim.Run.Id,false);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>running);
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
