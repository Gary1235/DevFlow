using DevFlow.Core;
using DevFlow.Runners;
using Xunit;
namespace DevFlow.Tests;
public sealed class CliConnectionTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-cli-" + Guid.NewGuid());
    private string executable = "";
    public async Task InitializeAsync() { Directory.CreateDirectory(directory); executable = Path.Combine(directory, "fake.exe"); await File.WriteAllTextAsync(executable, ""); }
    public Task DisposeAsync() { Directory.Delete(directory, true); return Task.CompletedTask; }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Terminal : ILoginTerminal
    {
        public bool HasExited { get; set; }
        public int ExitCode { get; set; }
        public bool Stopped, Disposed, FailStop;
        public void Stop() { if (FailStop) throw new System.ComponentModel.Win32Exception(5); Stopped = true; HasExited = true; }
        public void Dispose() => Disposed = true;
    }
    private sealed class Launcher : ILoginTerminalLauncher
    {
        public readonly Terminal Terminal = new(); public string[] Arguments = []; public int Calls;
        public ILoginTerminal Start(CliCommand command, string[] arguments, string workingDirectory) { Arguments = arguments; Calls++; return Terminal; }
    }
    private sealed class Capture(params ProcessResult[] results) : IProcessRunner
    {
        public readonly List<string[]> Calls = [];
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string w, string? input, CancellationToken ct)
        { Calls.Add(a.ToArray()); return Task.FromResult(results[Math.Min(Calls.Count - 1, results.Length - 1)]); }
    }
    [Fact] public void Login_is_single_per_provider_and_exit_does_not_claim_authentication()
    {
        var launcher = new Launcher();
        using var manager = new CliLoginManager(new(executable, executable), launcher, directory);
        var session = manager.Start("codex", "device");
        Assert.Equal(new[] { "login", "--device-auth" }, launcher.Arguments);
        Assert.Throws<ConflictException>(() => manager.Start("codex"));
        Assert.Equal(1, launcher.Calls);
        launcher.Terminal.HasExited = true;
        Assert.Equal("AwaitingVerification", manager.Get(session.Id).State);
        Assert.True(launcher.Terminal.Disposed);
    }
    [Fact] public void Cancel_kills_only_owned_login_terminal_and_is_idempotent()
    {
        var launcher = new Launcher(); using var manager = new CliLoginManager(new(executable, executable), launcher, directory);
        var session = manager.Start("gemini");
        Assert.Empty(launcher.Arguments);
        Assert.Equal("Cancelled", manager.Cancel(session.Id).State);
        Assert.Equal("Cancelled", manager.Cancel(session.Id).State);
        Assert.True(launcher.Terminal.Stopped);
        Assert.True(launcher.Terminal.Disposed);
        Assert.Throws<ArgumentException>(() => manager.Start("gemini", "device"));
    }
    [Fact] public void Timeout_and_server_disposal_clean_up_owned_processes()
    {
        var clock = new Clock(); var launcher = new Launcher();
        using var manager = new CliLoginManager(new(executable), launcher, directory, clock);
        var session = manager.Start("codex"); clock.Now = clock.Now.AddMinutes(16); manager.Sweep();
        Assert.Equal("TimedOut", manager.Get(session.Id).State); Assert.True(launcher.Terminal.Stopped);
        var second = new Launcher(); var other = new CliLoginManager(new(executable), second, directory); other.Start("codex"); other.Dispose(); Assert.True(second.Terminal.Stopped);
    }
    [Fact] public async Task Diagnostics_distinguish_login_from_execution_without_exposing_cli_output()
    {
        var process = new Capture(new(0, "codex 1.2.3", ""), new(0, "Logged in using ChatGPT", ""), new(1, "", "secret-token access denied"));
        var diagnostics = new CliDiagnostics(process, new(executable), directory);
        Assert.Equal("LoggedIn", (await diagnostics.InspectAsync("codex")).LoginState);
        var probe = await diagnostics.ProbeAsync(new("codex", "chosen-model"));
        Assert.Equal("Error", probe.ExecutionState); Assert.DoesNotContain("secret-token", probe.Message);
        Assert.Contains("--skip-git-repo-check", process.Calls.Last());
        Assert.Equal("chosen-model", process.Calls.Last()[Array.IndexOf(process.Calls.Last(), "--model") + 1]);
    }
    [Fact] public async Task Gemini_status_is_unknown_until_a_successful_model_probe()
    {
        var process = new Capture(new(0, "1.2.3", ""), new(0, "{\"response\":\"DEVFLOW_OK\"}", ""));
        var diagnostics = new CliDiagnostics(process, new(executable, executable), directory);
        Assert.Equal("Unknown", (await diagnostics.InspectAsync("gemini")).LoginState);
        Assert.Single(process.Calls);
        Assert.Equal("Ready", (await diagnostics.ProbeAsync(new("gemini"))).ExecutionState);
        Assert.Contains("plan", process.Calls.Last());
    }
    [Fact] public async Task Prompt_echo_is_not_a_successful_codex_probe()
    {
        var process = new Capture(new ProcessResult(0, "{\"type\":\"turn.failed\",\"error\":\"DEVFLOW_OK requested but unavailable\"}", ""));
        var diagnostic = await new CliDiagnostics(process, new(executable), directory).ProbeAsync(new("codex"));
        Assert.Equal("Error", diagnostic.ExecutionState);
    }
    [Fact] public void Failed_process_cleanup_keeps_provider_locked_without_crashing_timer()
    {
        var launcher = new Launcher(); using var manager = new CliLoginManager(new(executable), launcher, directory);
        var session = manager.Start("codex"); launcher.Terminal.FailStop = true;
        try
        {
            Assert.Equal("CleanupFailed", manager.Cancel(session.Id).State);
            Assert.Throws<ConflictException>(() => manager.Start("codex"));
            Assert.False(launcher.Terminal.Disposed);
        }
        finally { launcher.Terminal.FailStop = false; }
        Assert.Equal("Cancelled", manager.Cancel(session.Id).State);
    }
    [Fact] public void Claude_login_uses_official_auth_command_without_console_billing_flag()
    {
        var launcher = new Launcher(); using var manager = new CliLoginManager(new(claude: executable), launcher, directory);
        Assert.Equal("WaitingForUser", manager.Start("claude").State);
        Assert.Equal(new[] { "auth", "login" }, launcher.Arguments);
        Assert.DoesNotContain("--console", launcher.Arguments);
        Assert.Throws<ArgumentException>(() => manager.Start("claude", "device"));
    }
    [Fact] public async Task Claude_auth_status_does_not_imply_paid_model_access_or_expose_account_data()
    {
        var process = new Capture(new(0, "2.1.300", ""), new(0, "{\"loggedIn\":true,\"email\":\"private@example.com\",\"subscriptionType\":null}", ""));
        var result = await new CliDiagnostics(process, new(claude: executable), directory).InspectAsync("claude");
        Assert.Equal(new[] { "auth", "status" }, process.Calls.Last()); Assert.Equal("LoggedIn", result.LoginState);
        Assert.Equal("Unchecked", result.ExecutionState); Assert.DoesNotContain("private@example.com", result.Message);
    }
    [Theory]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"DEVFLOW_OK\"}", "Ready")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"DEVFLOW_OK failed\"}", "Error")]
    public async Task Claude_probe_checks_structured_result_and_disables_tools(string output, string expected)
    {
        var process = new Capture(new ProcessResult(0, output, ""));
        var result = await new CliDiagnostics(process, new(claude: executable), directory).ProbeAsync(new("claude", "haiku"));
        Assert.Equal(expected, result.ExecutionState);
        Assert.Equal("", process.Calls.Last()[Array.IndexOf(process.Calls.Last(), "--tools") + 1]);
        Assert.Contains("--restricted", process.Calls.Last());
    }
}
