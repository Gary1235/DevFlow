using System.Text.Json;
using DevFlow.Api;
using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
namespace DevFlow.Tests;

public sealed class PermissionHookTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-hook-" + Guid.NewGuid());
    public Task InitializeAsync() { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }
    private object Payload(string name = "run_command", object? args = null, string? workspace = null) => new
    { conversationId = "8b958694-c9ee-446a-a35f-2278dafb7f6a", workspacePaths = new[] { workspace ?? root }, stepIdx = 2,
        toolCall = new { name, args = args ?? new { CommandLine = "Get-ChildItem -Directory", Cwd = root } } };
    private string Command()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".agents", "hooks.json")));
        return json.RootElement.EnumerateObject().Single(p => p.Name.StartsWith("devflow-web-approval-")).Value.GetProperty("PreToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
    }
    private async Task<ProcessResult> Client(object payload, CancellationToken ct)
    {
        var command = Command();
        var result = OperatingSystem.IsWindows()
            ? await new ProcessRunner().RunAsync("cmd.exe", ["/d", "/s", "/c", command], root, JsonSerializer.Serialize(payload), ct)
            : await new ProcessRunner().RunAsync("/bin/sh", ["-c", command], root, JsonSerializer.Serialize(payload), ct);
        Assert.True(result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardError);
        return result;
    }
    private static async Task<ToolApproval> Pending(DiscussionOperations.Operation operation)
    {
        for (var i = 0; i < 300; i++)
        {
            var pending = operation.Permissions.LastOrDefault(p => p.State == "Pending");
            if (pending is not null) return pending;
            await Task.Delay(20);
        }
        throw new TimeoutException("Hook did not submit a web approval.");
    }

    [Fact] public async Task Real_hook_helper_waits_for_exact_approval_and_restores_existing_configuration()
    {
        Directory.CreateDirectory(Path.Combine(root, ".agents"));
        var original = "{\n  \"existing\": {\"enabled\": false}\n}\n";
        var path = Path.Combine(root, ".agents", "hooks.json"); await File.WriteAllTextAsync(path, original);
        using var operation = new DiscussionOperations.Operation(Guid.NewGuid(), new("antigravity"), default);
        var audit = Path.Combine(root, "audit", "permissions.jsonl");
        await using (await AntigravityPermissionHook.CreateAsync(operation, root, audit, default))
        {
            Assert.DoesNotContain("dangerously-skip-permissions", Command());
            using(var settings=JsonDocument.Parse(await File.ReadAllTextAsync(path)))
                Assert.False(settings.RootElement.EnumerateObject().Single(p=>p.Name.StartsWith("devflow-web-approval-")).Value.GetProperty("PreToolUse")[0].GetProperty("hooks")[0].TryGetProperty("timeout",out _));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var first = Client(Payload(), timeout.Token); var pending = await Pending(operation);
            Assert.False(first.IsCompleted); Assert.Equal("run_command", pending.Tool);
            Assert.Equal("Get-ChildItem -Directory", pending.Arguments.GetProperty("CommandLine").GetString());
            Assert.Equal(root, pending.Workspace);
            operation.ReplyPermission(pending.Id, true);
            var result = await first;
            Assert.Equal(0, result.ExitCode);
            using var response = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("auto_approve", response.RootElement.GetProperty("decision").GetString());
            Assert.Throws<ConflictException>(() => operation.ReplyPermission(pending.Id, true));
            // The same command requires another approval; there is no persistent allow rule.
            var second = Client(Payload(), timeout.Token); var next = await Pending(operation);
            Assert.NotEqual(pending.Id, next.Id); operation.ReplyPermission(next.Id, false);
            using var denied = JsonDocument.Parse((await second).StandardOutput);
            Assert.Equal("deny", denied.RootElement.GetProperty("decision").GetString());
        }
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        var records = await File.ReadAllLinesAsync(audit); Assert.Equal(2, records.Length);
        Assert.Contains("auto_approve", records[0]); Assert.Contains("deny", records[1]);
    }

    [Fact] public async Task Read_only_local_tools_keep_native_policy_and_writes_or_wrong_workspaces_are_denied()
    {
        using var operation = new DiscussionOperations.Operation(Guid.NewGuid(), new("antigravity"), default);
        await using (await AntigravityPermissionHook.CreateAsync(operation, root, Path.Combine(root, "audit.jsonl"), default))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var reading = Client(Payload("list_dir", new { DirectoryPath = root }), timeout.Token);
            var pending = await Pending(operation); Assert.True(pending.Risk.BatchAllowed);
            operation.ReplyPermission(pending.Id,true);
            using var read = JsonDocument.Parse((await reading).StandardOutput);
            Assert.Equal("auto_approve", read.RootElement.GetProperty("decision").GetString());
            using var write = JsonDocument.Parse((await Client(Payload("write_to_file", new { TargetFile = Path.Combine(root, "file.txt") }), timeout.Token)).StandardOutput);
            Assert.Equal("deny", write.RootElement.GetProperty("decision").GetString());
            using var edit = JsonDocument.Parse((await Client(Payload("edit_file", new { Path = Path.Combine(root, "file.txt"), Target = "old", Replacement = "new" }), timeout.Token)).StandardOutput);
            Assert.Equal("deny", edit.RootElement.GetProperty("decision").GetString());
            using var wrong = JsonDocument.Parse((await Client(Payload(workspace: Path.GetTempPath()), timeout.Token)).StandardOutput);
            Assert.Equal("deny", wrong.RootElement.GetProperty("decision").GetString());
            Assert.Single(operation.Permissions);
        }
        Assert.False(Directory.Exists(Path.Combine(root, ".agents")));
    }

    [Theory]
    [InlineData("Select-String -Pattern 'please delete file' -Path src/a.json -Culture en-US")]
    [InlineData("Select-String -Path (Get-ChildItem -Path src -Recurse -Include *.json).FullName -Pattern 'please delete file' -List | Select-Object -First 30 Path,LineNumber,Line")]
    public async Task Readonly_hook_uses_shared_operation_semantics_instead_of_keywords_in_search_text(string command)
    {
        if(!OperatingSystem.IsWindows())return;
        Directory.CreateDirectory(Path.Combine(root,"src"));File.WriteAllText(Path.Combine(root,"src","a.json"),"{}");
        var store=new SqliteTaskStore(Path.Combine(root,"tasks.db"));await store.InitializeAsync();
        var project=new ProjectDefinition(Guid.NewGuid(),"Test",root,"main",["test"]);await store.AddProjectAsync(project);
        var discussion=new Discussion(Guid.NewGuid(),project.Id,"Test",new("antigravity"),[],[],[],0,DateTimeOffset.UtcNow);await store.AddDiscussionAsync(discussion,default);
        using var operation=new DiscussionOperations.Operation(Guid.NewGuid(),new("antigravity"),default);
        operation.ConfigurePermissions(store,project.Id,discussion.Id);
        await using(await AntigravityPermissionHook.CreateAsync(operation,root,Path.Combine(root,"audit.jsonl"),default)) {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result=await Client(Payload(args:new{CommandLine=command}),timeout.Token);
            using var response=JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("auto_approve",response.RootElement.GetProperty("decision").GetString());
            var request=Assert.Single(operation.Permissions);Assert.Equal("AllowedByPolicy",request.State);
            Assert.Equal("powershell",request.Arguments.GetProperty("Shell").GetString());
        }
    }
    [Fact] public async Task Stopping_or_disposing_an_operation_never_implies_approval()
    {
        var operation = new DiscussionOperations.Operation(Guid.NewGuid(), new("antigravity"), default);
        using var arguments = JsonDocument.Parse("{\"CommandLine\":\"whoami\"}");
        var waiting = operation.RequestPermissionAsync("run_command", arguments.RootElement, root, default);
        var request = await Pending(operation);
        operation.Stop(); Assert.False(await waiting);
        Assert.Equal("Expired", operation.Permissions.Single().State);
        Assert.Throws<ConflictException>(() => operation.ReplyPermission(request.Id, true)); operation.Dispose();
        var second = new DiscussionOperations.Operation(Guid.NewGuid(), new("antigravity"), default);
        var other = second.RequestPermissionAsync("run_command", arguments.RootElement, root, default);
        second.Dispose(); Assert.False(await other);
    }

    [Fact] public async Task Native_hook_client_disconnect_invalidates_the_web_request_without_stopping_the_whole_operation()
    {
        using var operation=new DiscussionOperations.Operation(Guid.NewGuid(),new("antigravity"),default);
        await using(await AntigravityPermissionHook.CreateAsync(operation,root,Path.Combine(root,"audit.jsonl"),default))
        {
            using var clientLifetime=new CancellationTokenSource();
            var client=Client(Payload(),clientLifetime.Token);var request=await Pending(operation);
            Assert.Null(request.ExpiresAt);clientLifetime.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client);
            for(var i=0;i<300&&operation.Permissions.Single().State=="Pending";i++)await Task.Delay(20);
            Assert.False(operation.Token.IsCancellationRequested);Assert.Equal("Expired",operation.Permissions.Single().State);
            Assert.Contains("CliInterrupted",operation.Failure);
            Assert.Throws<ConflictException>(()=>operation.ReplyPermission(request.Id,true));
        }
    }
    [Fact] public async Task Stopping_a_waiting_native_hook_returns_deny_and_removes_the_temporary_hook()
    {
        using var operation = new DiscussionOperations.Operation(Guid.NewGuid(), new("antigravity"), default);
        await using (await AntigravityPermissionHook.CreateAsync(operation, root, Path.Combine(root, "audit.jsonl"), default))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var client = Client(Payload(), timeout.Token); await Pending(operation); operation.Stop();
            using var result = JsonDocument.Parse((await client).StandardOutput);
            Assert.Equal("deny", result.RootElement.GetProperty("decision").GetString());
        }
        Assert.False(Directory.Exists(Path.Combine(root, ".agents")));
    }
}
