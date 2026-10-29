using System.Text.Json;
using System.Threading.Channels;
using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Runners;
using Xunit;

namespace DevFlow.Tests;

public sealed class StructuredMcpAdapterTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow mcp " + Guid.NewGuid());
    private SqliteTaskStore store = null!;
    private WorkTask task = null!;
    private Guid run;
    private static string Helper => typeof(DevFlow.Api.AntigravityPermissionHook).Assembly.Location;
    public async Task InitializeAsync()
    {
        store = new(Path.Combine(root,"tasks.db")); await store.InitializeAsync();
        var project = new ProjectDefinition(Guid.NewGuid(),"MCP",root,"main",["test"]);
        await store.AddProjectAsync(project);
        task = WorkTask.Create(project.Id,"Task","Read source","Pass").SetPlan("Read source and validate").ApprovePlan();
        await store.AddTaskAsync(task); run=(await store.ClaimNextAsync())!.Run.Id;
        await File.WriteAllTextAsync(Path.Combine(root,"source.txt"),"source evidence via real MCP");
    }
    public Task DisposeAsync() { Directory.Delete(root,true); return Task.CompletedTask; }

    [Theory]
    [InlineData("antigravity",true)][InlineData("antigravity",false)]
    [InlineData("claude",true)][InlineData("claude",false)]
    [InlineData("gemini",true)][InlineData("gemini",false)]
    public async Task Every_adapter_registers_and_executes_real_host_tools_with_shared_audit(string provider,bool planning)
    {
        Directory.CreateDirectory(Path.Combine(root,".agents"));
        var config=Path.Combine(root,".agents","mcp_config.json");
        const string original="{\n  \"mcpServers\": {\"existing\": {\"command\": \"unchanged\"}}\n}\n";
        await File.WriteAllTextAsync(config,original);
        var fixture=new NativeFixture(provider);
        var selection=new AiSelection(provider);
        task=task with {Ai=new(selection,selection)};
        var registry=new CliRegistry(antigravity:Environment.ProcessPath!,claude:Environment.ProcessPath!,gemini:Environment.ProcessPath!);
        var runner=new RoutedCliRunner(fixture,registry,root,store,Helper,structuredTools:new StructuredToolFactory(store,new ProcessRunner(),Path.Combine(root,"artifacts")));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));
        if(planning) Assert.Contains("Read",await runner.PlanAsync(task,new(root,"base"),run,timeout.Token));
        else await runner.ExecuteAsync(task,new(root,"base"),run,TddPhase.Implementation,timeout.Token);
        Assert.Contains("source evidence via real MCP",fixture.ToolOutput);
        Assert.Equal(planning?3:5,fixture.ToolCount);
        var approval=Assert.Single(await store.ListToolApprovalsAsync(task.Id));
        Assert.Equal("read_file",approval.Tool);Assert.Equal(provider,approval.Provider);Assert.Equal("AllowedByPolicy",approval.State);
        Assert.Equal(original,await File.ReadAllTextAsync(config));
        Assert.False(File.Exists(Path.Combine(root,".agents","hooks.json")));
    }

    [Fact]
    public async Task Readonly_mcp_bridge_refuses_writes_unknown_tools_and_outside_targets()
    {
        var operation=new TaskPermissionPlanningRunner.Operation(store,task.Id,run,default,"antigravity",true);
        await using var bridge=new StructuredMcpBridge(new StructuredToolFactory(store,new ProcessRunner(),root).Create(task,new(root,"base"),run,operation),Helper,default);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await new ProcessRunner().RunInteractiveAsync(bridge.Command,bridge.Arguments,root,async(s,ct)=>{
            foreach(var (name,args) in new[]{
                ("devflow_write_file",JsonSerializer.SerializeToElement(new{path="source.txt",content="replaced"})),
                ("devflow_read_file",JsonSerializer.SerializeToElement(new{path="../outside.txt"})),
                ("devflow_run_validation",JsonSerializer.SerializeToElement(new{profileId="unknown"})),
                ("unknown_tool",JsonSerializer.SerializeToElement(new{path="source.txt"}))}) {
                await s.SendAsync(new{jsonrpc="2.0",id=1,method="tools/call",@params=new{name,arguments=args}},ct);
                Assert.True((await s.ReadAsync(ct))!.Value.GetProperty("result").GetProperty("isError").GetBoolean());
            }
        },timeout.Token);
        Assert.Equal("source evidence via real MCP",await File.ReadAllTextAsync(Path.Combine(root,"source.txt")));
        Assert.Empty(await store.ListToolApprovalsAsync(task.Id));
    }

    [Fact]
    public async Task Antigravity_restores_configuration_when_the_cli_fails()
    {
        Directory.CreateDirectory(Path.Combine(root,".agents"));
        var file=Path.Combine(root,".agents","mcp_config.json");
        const string original="{\"mcpServers\":{\"keep\":{\"command\":\"keep\"}}}";
        await File.WriteAllTextAsync(file,original);
        task=task with{Ai=new(new("antigravity"),new("antigravity"))};
        var runner=new RoutedCliRunner(new NativeFixture("antigravity"){Fail=true},new CliRegistry(antigravity:Environment.ProcessPath!),root,store,Helper,
            structuredTools:new StructuredToolFactory(store,new ProcessRunner(),root));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>runner.PlanAsync(task,new(root,"base"),run,default));
        Assert.Equal(original,await File.ReadAllTextAsync(file));
        Assert.False(File.Exists(Path.Combine(root,".agents","hooks.json")));
    }

    [Fact]
    public async Task Server_names_and_tool_capabilities_are_bound_to_one_invocation()
    {
        var operation=new TaskPermissionPlanningRunner.Operation(store,task.Id,run,default,"claude",true);
        var tools=new StructuredToolFactory(store,new ProcessRunner(),root).Create(task,new(root,"base"),run,operation);
        await using var first=new StructuredMcpBridge(tools,Helper,default);
        await using var second=new StructuredMcpBridge(tools,Helper,default);
        Assert.NotEqual(first.PipeName,second.PipeName);Assert.NotEqual(first.Name,second.Name);
        Assert.True(first.OwnsTool($"mcp__{first.Name}__devflow_read_file"));
        Assert.False(first.OwnsTool($"mcp__{second.Name}__devflow_read_file"));
        Assert.False(first.OwnsTool($"mcp__{first.Name}__devflow_write_file"));
        Assert.False(first.OwnsTool($"mcp__{first.Name}__unknown"));
        Assert.True(first.OwnsTool("call_mcp_tool",JsonSerializer.SerializeToElement(new{server_name=first.Name,tool_name="devflow_read_file"})));
        Assert.False(first.OwnsTool("call_mcp_tool",JsonSerializer.SerializeToElement(new{server_name=second.Name,tool_name="devflow_read_file"})));
        Assert.False(first.OwnsTool("call_mcp_tool",JsonSerializer.SerializeToElement(new{server_name=first.Name,tool_name="devflow_write_file"})));
    }

    [Fact]
    public async Task Dropping_a_client_connection_cancels_its_inflight_host_tool()
    {
        var tools=new BlockingTools();
        await using var bridge=new StructuredMcpBridge(tools,Helper,default);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using(var pipe=new System.IO.Pipes.NamedPipeClientStream(".",bridge.PipeName,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous)) {
            await pipe.ConnectAsync(timeout.Token);
            using var writer=new StreamWriter(pipe,leaveOpen:true){AutoFlush=true};
            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"wait\",\"arguments\":{}}}");
            await tools.Started.Task.WaitAsync(timeout.Token);
        }
        await tools.Cancelled.Task.WaitAsync(timeout.Token);
    }

    private sealed class BlockingTools : IStructuredTools
    {
        public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Cancelled=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<StructuredToolDefinition> Definitions=>[];
        public async Task<string> ExecuteAsync(string tool,JsonElement args,CancellationToken ct) {
            Started.SetResult();
            try {await Task.Delay(Timeout.InfiniteTimeSpan,ct);return "unexpected";}
            catch(OperationCanceledException){Cancelled.SetResult();throw;}
        }
    }

    private sealed class NativeFixture(string provider) : IProcessRunner,IInteractiveProcessRunner
    {
        private string? server;
        private JsonElement config;
        public string? ToolOutput;
        public int ToolCount;
        public bool Fail;
        public async Task<ProcessResult> RunAsync(string executable,IReadOnlyList<string> args,string cwd,string? input,CancellationToken ct)
        {
            Assert.Equal("antigravity",provider);
            Assert.Equal(cwd,args[args.ToList().IndexOf("--add-dir")+1]);
            Assert.Contains("Prefer the DevFlow MCP tools",input);
            using var document=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cwd,".agents","mcp_config.json"),ct));
            var entry=document.RootElement.GetProperty("mcpServers").EnumerateObject().Single(p=>p.Name.StartsWith("devflow_",StringComparison.Ordinal));
            server=entry.Name;config=entry.Value.Clone();
            if(Fail)throw new InvalidOperationException("Simulated CLI failure");
            Assert.Equal(-1,config.GetProperty("timeoutSeconds").GetInt32());
            if(OperatingSystem.IsWindows()) {
                using var hooks=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cwd,".agents","hooks.json"),ct));
                var command=hooks.RootElement.EnumerateObject().Single(p=>p.Name.StartsWith("devflow-web-approval-",StringComparison.Ordinal)).Value.GetProperty("PreToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
                var payload=JsonSerializer.Serialize(new{conversationId=Guid.NewGuid(),workspacePaths=new[]{cwd},toolCall=new{name="call_mcp_tool",args=new{server_name=server,tool_name="devflow_read_file",arguments=new{path="source.txt"}}}});
                var hook=await new ProcessRunner().RunAsync("cmd.exe",["/d","/s","/c",command],cwd,payload,ct);
                Assert.Equal(0,hook.ExitCode);
                using var decision=JsonDocument.Parse(hook.StandardOutput);
                Assert.Equal("auto_approve",decision.RootElement.GetProperty("decision").GetString());
            }
            await InvokeHelper(cwd,ct);
            return new(0,"{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"response\":\"Read source and validate.\"}}","");
        }
        private async Task InvokeHelper(string cwd,CancellationToken ct)
        {
            var executable=config.GetProperty("command").GetString()!;
            var args=config.GetProperty("args").EnumerateArray().Select(a=>a.GetString()!).ToArray();
            var result=await new ProcessRunner().RunInteractiveAsync(executable,args,cwd,async(s,token)=>{
                await s.SendAsync(new{jsonrpc="2.0",id=1,method="initialize",@params=new{protocolVersion="2025-06-18",capabilities=new{},clientInfo=new{name="fixture",version="1"}}},token);
                Assert.Equal("DevFlow",(await s.ReadAsync(token))!.Value.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
                await s.SendAsync(new{jsonrpc="2.0",method="notifications/initialized"},token);
                await s.SendAsync(new{jsonrpc="2.0",id=2,method="tools/list"},token);
                ToolCount=(await s.ReadAsync(token))!.Value.GetProperty("result").GetProperty("tools").GetArrayLength();
                await s.SendAsync(new{jsonrpc="2.0",id=3,method="tools/call",@params=new{name="devflow_read_file",arguments=new{path="source.txt"}}},token);
                var result=(await s.ReadAsync(token))!.Value.GetProperty("result");
                Assert.False(result.GetProperty("isError").GetBoolean());
                ToolOutput=result.GetProperty("content")[0].GetProperty("text").GetString();
            },ct);
            Assert.Equal(0,result.ExitCode);
        }
        public async Task<ProcessResult> RunInteractiveAsync(string executable,IReadOnlyList<string> args,string cwd,Func<IProcessSession,CancellationToken,Task> exchange,CancellationToken ct)
        {
            if(provider=="claude") {
                Assert.Contains("--strict-mcp-config",args);Assert.DoesNotContain("mcp__*",args);
                using var json=JsonDocument.Parse(args[args.ToList().IndexOf("--mcp-config")+1]);
                var entry=Assert.Single(json.RootElement.GetProperty("mcpServers").EnumerateObject());server=entry.Name;config=entry.Value.Clone();
            }
            await exchange(new Session(this,provider,cwd),ct);
            return new(0,"fixture","");
        }
        private sealed class Session(NativeFixture owner,string provider,string cwd) : IProcessSession
        {
            private readonly Channel<JsonElement> frames=Channel.CreateUnbounded<JsonElement>();
            public CancellationToken ProcessExited=>default;
            private void Frame(object value)=>frames.Writer.TryWrite(JsonSerializer.SerializeToElement(value));
            public async Task<JsonElement?> ReadAsync(CancellationToken ct)=>await frames.Reader.ReadAsync(ct);
            public async Task SendAsync(object message,CancellationToken ct)
            {
                var frame=JsonSerializer.SerializeToElement(message);
                if(provider=="claude") {
                    var type=frame.GetProperty("type").GetString();
                    if(type=="control_request") Frame(new{type="control_response",response=new{subtype="success",request_id="devflow-initialize"}});
                    if(type=="user") {
                        Assert.Contains("Prefer the DevFlow MCP tools",frame.GetProperty("message").GetProperty("content").GetString());
                        Frame(new{type="control_request",request_id="tool",request=new{subtype="can_use_tool",tool_name=$"mcp__{owner.server}__devflow_read_file",input=new{path="source.txt"}}});
                    }
                    if(type=="control_response") {
                        Assert.Equal("allow",frame.GetProperty("response").GetProperty("response").GetProperty("behavior").GetString());
                        await owner.InvokeHelper(cwd,ct);
                        Frame(new{type="result",subtype="success",is_error=false,result="Read source and validate."});
                    }
                } else {
                    var method=frame.TryGetProperty("method",out var m)?m.GetString():null;
                    if(method=="initialize") Frame(new{jsonrpc="2.0",id=1,result=new{protocolVersion=1}});
                    if(method=="session/new") {
                        Assert.Equal(cwd,frame.GetProperty("params").GetProperty("cwd").GetString());
                        var entry=Assert.Single(frame.GetProperty("params").GetProperty("mcpServers").EnumerateArray());owner.config=entry.Clone();owner.server=entry.GetProperty("name").GetString();
                        Frame(new{jsonrpc="2.0",id=2,result=new{sessionId="session"}});
                    }
                    if(method=="session/prompt") Frame(new{jsonrpc="2.0",id=90,method="session/request_permission",@params=new{sessionId="session",toolCall=new{toolCallId="call",name=$"{owner.server}__devflow_read_file",rawInput=new{path="source.txt"}},options=new[]{new{kind="allow_once",optionId="once"},new{kind="reject_once",optionId="deny"}}}});
                    if(method is null && frame.GetProperty("id").GetInt32()==90) {
                        Assert.Equal("once",frame.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
                        await owner.InvokeHelper(cwd,ct);
                        Frame(new{method="session/update",@params=new{sessionId="session",update=new{sessionUpdate="agent_message_chunk",content=new{type="text",text="Read source and validate."}}}});
                        Frame(new{jsonrpc="2.0",id=3,result=new{stopReason="end_turn"}});
                    }
                }
            }
        }
    }
}
