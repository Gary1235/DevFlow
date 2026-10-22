using System.Text.Json;
using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Runners;
using Xunit;
namespace DevFlow.Tests;

public sealed class CliVerificationTests : IAsyncLifetime
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"devflow-verification-"+Guid.NewGuid());
    private string executable="";
    private SqliteTaskStore store=null!;
    private sealed class Fake : IProcessRunner {
        public ProcessResult Result=new(0,"",""); public int Calls;
        public Task<ProcessResult> RunAsync(string e,IReadOnlyList<string> args,string cwd,string? input,CancellationToken ct) {
            Calls++; return Task.FromResult(args.Contains("--version")?new ProcessResult(0,"agy 1.0.0",""):Result);
        }
    }
    private static ProcessResult Success(string text="DEVFLOW_OK")=>new(0,JsonSerializer.Serialize(new{@event="result",result=new{status="SUCCESS",response=text}}),"");
    public async Task InitializeAsync() {
        Directory.CreateDirectory(root); executable=Path.Combine(root,"agy.exe");await File.WriteAllTextAsync(executable,"");
        store=new(Path.Combine(root,"test.db"));await store.InitializeAsync();
    }
    public Task DisposeAsync(){Directory.Delete(root,true);return Task.CompletedTask;}
    [Fact] public async Task Worker_success_is_available_to_a_new_API_instance_without_an_extra_model_call()
    {
        var fake=new Fake{Result=Success("A completed reply")};var registry=new CliRegistry(antigravity:executable);
        var runner=new RoutedCliRunner(fake,registry,root,verifications:store);
        var task=WorkTask.Create(Guid.NewGuid(),"Test","Test","Test") with {Ai=new(new("antigravity","model-test"),new("antigravity")),ReadOnlyInstruction="Discuss only"};
        await runner.PlanAsync(task,new(root,"base"),Guid.NewGuid(),default);
        var apiStore=new SqliteTaskStore(store.DatabasePath);
        var diagnostics=new CliDiagnostics(fake,registry,root,apiStore);
        var result=await diagnostics.InspectAsync("antigravity");
        Assert.Equal("VerifiedByExecution",result.LoginState);Assert.NotNull(result.LastVerifiedAt);Assert.Equal("model-test",result.VerifiedModel);
        Assert.Equal("Task",result.VerificationSource);Assert.Equal(2,fake.Calls);
        Assert.Equal("VerifiedByExecution",(await new CliDiagnostics(fake,registry,root,apiStore).InspectAsync("antigravity")).LoginState);
    }
    [Fact] public async Task Probe_success_persists_quota_does_not_log_out_and_authentication_failure_invalidates()
    {
        var fake=new Fake{Result=Success()};var registry=new CliRegistry(antigravity:executable);
        var diagnostics=new CliDiagnostics(fake,registry,root,store);
        Assert.Equal("Unknown",(await diagnostics.InspectAsync("antigravity")).LoginState);
        var probe=await diagnostics.ProbeAsync(new("antigravity","tested"));Assert.NotNull(probe.LastVerifiedAt);
        Assert.Equal("Probe",(await diagnostics.InspectAsync("antigravity")).VerificationSource);
        fake.Result=new(1,"","quota exceeded");await diagnostics.ProbeAsync(new("antigravity"));
        Assert.Equal("VerifiedByExecution",(await diagnostics.InspectAsync("antigravity")).LoginState);
        fake.Result=new(1,"","401 Unauthorized private-credential");await diagnostics.ProbeAsync(new("antigravity"));
        var failed=await diagnostics.InspectAsync("antigravity");Assert.Equal("LoggedOut",failed.LoginState);Assert.Null(failed.LastVerifiedAt);Assert.DoesNotContain("private-credential",failed.Message);
        fake.Result=Success();await diagnostics.ProbeAsync(new("antigravity"));Assert.Equal("VerifiedByExecution",(await diagnostics.InspectAsync("antigravity")).LoginState);
    }
    [Fact] public async Task Failed_task_authentication_replaces_success_but_invalid_output_never_creates_success()
    {
        var fake=new Fake{Result=new(0,"{}","")};var registry=new CliRegistry(antigravity:executable);
        var runner=new RoutedCliRunner(fake,registry,root,verifications:store);
        var task=WorkTask.Create(Guid.NewGuid(),"Test","Test","Test") with{Ai=new(new("antigravity"),new("antigravity")),ReadOnlyInstruction="Discuss"};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>runner.PlanAsync(task,new(root,"base"),Guid.NewGuid(),default));Assert.Null(await store.GetCliVerificationAsync("antigravity"));
        fake.Result=Success();await runner.PlanAsync(task,new(root,"base"),Guid.NewGuid(),default);
        fake.Result=new(1,"","not logged in");await Assert.ThrowsAsync<InvalidOperationException>(()=>runner.PlanAsync(task,new(root,"base"),Guid.NewGuid(),default));
        Assert.False((await store.GetCliVerificationAsync("antigravity"))!.Successful);
        fake.Result=Success();await runner.PlanAsync(task,new(root,"base"),Guid.NewGuid(),default);
        fake.Result=new(0,"""{"event":"result","result":{"status":"ERROR","error":"401 Unauthorized"}}""","");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>runner.PlanAsync(task,new(root,"base"),Guid.NewGuid(),default));
        Assert.False((await store.GetCliVerificationAsync("antigravity"))!.Successful);
    }
    [Fact] public async Task Evidence_is_bound_to_the_executable_and_older_writes_cannot_overwrite_newer_failures()
    {
        var fake=new Fake{Result=Success()};await new CliDiagnostics(fake,new(antigravity:executable),root,store).ProbeAsync(new("antigravity"));
        var evidence=(await store.GetCliVerificationAsync("antigravity"))!;
        var other=Path.Combine(root,"other.exe");await File.WriteAllTextAsync(other,"");
        Assert.Equal("Unknown",(await new CliDiagnostics(fake,new(antigravity:other),root,store).InspectAsync("antigravity")).LoginState);
        await store.SaveCliVerificationAsync(evidence with{ObservedAt=evidence.ObservedAt.AddMinutes(1),Successful=false});
        await store.SaveCliVerificationAsync(evidence);
        Assert.False((await store.GetCliVerificationAsync("antigravity"))!.Successful);
    }
}
