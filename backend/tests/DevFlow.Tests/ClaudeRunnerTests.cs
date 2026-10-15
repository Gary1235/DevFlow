using DevFlow.Core;
using DevFlow.Runners;
using Xunit;
namespace DevFlow.Tests;
public sealed class ClaudeRunnerTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-claude-" + Guid.NewGuid());
    private string executable = "";
    public async Task InitializeAsync() { Directory.CreateDirectory(directory); executable = Path.Combine(directory, "claude.exe"); await File.WriteAllTextAsync(executable, ""); }
    public Task DisposeAsync() { Directory.Delete(directory, true); return Task.CompletedTask; }
    private sealed class Capture : IProcessRunner
    {
        public string[] Arguments = []; public string? Input; public string Executable = "";
        public string Output = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Write behavior tests and implement\"}";
        public int ExitCode;
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string w, string? input, CancellationToken ct)
        { Executable = e; Arguments = a.ToArray(); Input = input; return Task.FromResult(new ProcessResult(ExitCode, Output, "")); }
    }
    [Fact] public async Task Routed_claude_uses_selected_model_and_bounded_file_tools()
    {
        var process = new Capture(); var registry = new CliRegistry(claude: executable);
        var runner = new RoutedCliRunner(process, registry, directory);
        var task = WorkTask.Create(Guid.NewGuid(), "T", "D", "A") with { Ai = new(new("claude", "sonnet"), new("claude", "opus")) };
        var id = Guid.NewGuid(); var workspace = new Workspace(directory, "base");
        Assert.Contains("behavior", await runner.PlanAsync(task, workspace, id, CancellationToken.None));
        Assert.Equal(executable, process.Executable); Assert.Contains("--restricted", process.Arguments); Assert.Contains("plan", process.Arguments);
        Assert.Equal("Read,Glob,Grep", process.Arguments[Array.IndexOf(process.Arguments, "--tools") + 1]);
        Assert.Equal("sonnet", process.Arguments[Array.IndexOf(process.Arguments, "--model") + 1]);
        await runner.ExecuteAsync(task, workspace, id, TddPhase.Red, CancellationToken.None);
        Assert.Contains("acceptEdits", process.Arguments); Assert.Equal("opus", process.Arguments[Array.IndexOf(process.Arguments, "--model") + 1]);
        Assert.Contains("Do not implement", process.Input); Assert.DoesNotContain("Bash", string.Join(" ", process.Arguments));
        Assert.DoesNotContain("--dangerously-skip-permissions", process.Arguments);
        Assert.True(File.Exists(Path.Combine(directory, id.ToString("N"), "Red-selection.json")));
    }
    [Theory]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"No subscription\"}", 0)]
    [InlineData("{\"type\":\"result\",\"subtype\":\"error_max_turns\",\"is_error\":false,\"result\":\"Partial\"}", 0)]
    [InlineData("{\"result\":\"No login\"}", 0)]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"\"}", 0)]
    [InlineData("not JSON", 0)]
    [InlineData("{}", 1)]
    public async Task Invalid_or_unpaid_results_never_become_a_valid_plan(string output, int exit)
    {
        var runner = new ClaudeCliRunner(new Capture { Output = output, ExitCode = exit }, executable, directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.PlanAsync(WorkTask.Create(Guid.NewGuid(), "T", "D", "A"), new(directory, "base"), Guid.NewGuid(), CancellationToken.None));
    }
    [Fact] public async Task Windows_extensionless_npm_wrapper_is_not_a_native_executable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var wrapper = Path.Combine(directory, "codex"); await File.WriteAllTextAsync(wrapper, "#!/bin/sh\n");
        Assert.False(new CliRegistry(codex: wrapper).List().Single(p => p.Id == "codex").Installed);
    }
}
