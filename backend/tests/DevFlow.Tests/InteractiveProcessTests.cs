using DevFlow.Infrastructure;
using Xunit;

namespace DevFlow.Tests;

public sealed class InteractiveProcessTests
{
    [Fact] public async Task Process_exit_is_signalled_while_a_protocol_callback_is_waiting_for_user_input()
    {
        if(!OperatingSystem.IsWindows())return;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var script="[Console]::WriteLine('{\"kind\":\"permission\"}'); Start-Sleep -Milliseconds 300; exit 7";
        var result=await new ProcessRunner().RunInteractiveAsync("powershell.exe",["-NoProfile","-NonInteractive","-Command",script],Path.GetTempPath(),async(session,ct)=>{
            Assert.Equal("permission",(await session.ReadAsync(ct))!.Value.GetProperty("kind").GetString());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Task.Delay(Timeout.InfiniteTimeSpan,session.ProcessExited).WaitAsync(ct));
            Assert.False(ct.IsCancellationRequested);
        },timeout.Token);
        Assert.Equal(7,result.ExitCode);
    }
    [Fact]
    public async Task Native_process_keeps_stdin_open_for_multiple_protocol_replies()
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); [Console]::WriteLine('{\"kind\":\"ready\"}'); $first = [Console]::ReadLine(); [Console]::WriteLine($first); $second = [Console]::ReadLine(); [Console]::WriteLine($second)";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await new ProcessRunner().RunInteractiveAsync("powershell.exe",["-NoProfile","-NonInteractive","-Command",script],Path.GetTempPath(),async (session,ct)=>
        {
            Assert.Equal("ready",(await session.ReadAsync(ct))!.Value.GetProperty("kind").GetString());
            await session.SendAsync(new { decision="accept" },ct);
            Assert.Equal("accept",(await session.ReadAsync(ct))!.Value.GetProperty("decision").GetString());
            await session.SendAsync(new { decision="decline" },ct);
            Assert.Equal("decline",(await session.ReadAsync(ct))!.Value.GetProperty("decision").GetString());
        },timeout.Token);
        Assert.Equal(0,result.ExitCode);
        Assert.Contains("accept",result.StandardOutput); Assert.Contains("decline",result.StandardOutput);
    }
}
