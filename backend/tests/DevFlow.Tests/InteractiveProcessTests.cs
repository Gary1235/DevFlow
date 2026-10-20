using DevFlow.Infrastructure;
using Xunit;

namespace DevFlow.Tests;

public sealed class InteractiveProcessTests
{
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
