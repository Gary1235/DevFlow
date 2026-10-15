using DevFlow.Core;
using DevFlow.Runners;
using Xunit;
namespace DevFlow.Tests;

public sealed class PlanningRunnerTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-plan-runner-" + Guid.NewGuid());
    public Task InitializeAsync() { Directory.CreateDirectory(directory); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(directory, true); return Task.CompletedTask; }
    private sealed class OutputProcess(string json, int exit = 0) : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public string? Input { get; private set; }
        public async Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string w, string? input, CancellationToken ct)
        {
            Arguments = a; Input = input;
            var outputIndex = a.ToList().IndexOf("--output-last-message");
            if (outputIndex >= 0) await File.WriteAllTextAsync(a[outputIndex + 1], json, ct);
            return new(exit, "{\"type\":\"turn.completed\"}", "diagnostics");
        }
    }
    [Fact] public async Task Planner_uses_read_only_sandbox_and_reads_structured_final_output()
    {
        var process = new OutputProcess("{\"plan\":\"Write failing tests, implement and refactor\"}");
        var task = WorkTask.Create(Guid.NewGuid(), "API", "Build", "Tests");
        var id = Guid.NewGuid();
        var plan = await new CodexPlanningRunner(process, "codex.exe", directory).PlanAsync(task, new(directory, "base"), id, CancellationToken.None);
        Assert.Contains("read-only", process.Arguments);
        Assert.Contains("--output-schema", process.Arguments);
        Assert.Equal("-", process.Arguments.Last());
        Assert.Contains("Write failing tests", plan);
        Assert.Contains("Tests", process.Input);
        Assert.True(File.Exists(Path.Combine(directory, id.ToString("N"), "Planning-agent.jsonl")));
    }
    [Theory]
    [InlineData("{\"plan\":\"\"}", 0)]
    [InlineData("invalid json", 0)]
    [InlineData("{\"plan\":\"TDD\"}", 1)]
    public async Task Invalid_or_failed_planning_output_is_rejected(string json, int exit)
    {
        var task = WorkTask.Create(Guid.NewGuid(), "API", "Build", "Tests");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CodexPlanningRunner(new OutputProcess(json, exit), "codex.exe", directory)
            .PlanAsync(task, new(directory, "base"), Guid.NewGuid(), CancellationToken.None));
    }
}
