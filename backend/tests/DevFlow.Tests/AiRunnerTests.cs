using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Runners;
using System.Text.Json;
using Xunit;
namespace DevFlow.Tests;
public sealed class AiRunnerTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-routing-" + Guid.NewGuid());
    public Task InitializeAsync() { Directory.CreateDirectory(directory); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(directory, true); return Task.CompletedTask; }
    private sealed class Capture : IProcessRunner
    {
        public string Executable = ""; public string? Input; public string[] Arguments = [];
        public string Output = "{\"response\":\"Write behavior tests, implement and refactor\"}";
        public int ExitCode;
        public async Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string w, string? input, CancellationToken ct)
        {
            Executable = e; Arguments = a.ToArray(); Input = input;
            var index = Array.IndexOf(Arguments, "--output-last-message");
            if (index >= 0) await File.WriteAllTextAsync(Arguments[index + 1], "{\"plan\":\"TDD\"}", ct);
            return new(ExitCode, Output, "");
        }
    }
    [Fact] public async Task Gemini_plans_and_codex_implements_with_independent_models()
    {
        var codex = Path.Combine(directory, "codex.exe"); var gemini = Path.Combine(directory, "gemini.exe");
        await File.WriteAllTextAsync(codex, ""); await File.WriteAllTextAsync(gemini, "");
        var registry = new CliRegistry(codex, gemini); var process = new Capture();
        var runner = new RoutedCliRunner(process, registry, directory);
        var task = WorkTask.Create(Guid.NewGuid(), "Trim", "Implement", "Tests") with { Ai = new(new("gemini", "pro"), new("codex", "my-model")) };
        var id = Guid.NewGuid();
        Assert.Contains("behavior", await runner.PlanAsync(task, new(directory, "base"), id, CancellationToken.None));
        Assert.Equal(gemini, process.Executable); Assert.Contains("plan", process.Arguments); Assert.Contains("--sandbox", process.Arguments);
        Assert.Equal("pro", process.Arguments[Array.IndexOf(process.Arguments, "--model") + 1]);
        await runner.ExecuteAsync(task, new(directory, "base"), id, TddPhase.Red, CancellationToken.None);
        Assert.Equal(codex, process.Executable); Assert.Contains("workspace-write", process.Arguments);
        Assert.Equal("my-model", process.Arguments[Array.IndexOf(process.Arguments, "--model") + 1]);
        Assert.Contains("Do not implement", process.Input);
    }
    [Theory]
    [InlineData("{\"error\":{\"message\":\"bad model\"},\"response\":\"fake success\"}", 0)]
    [InlineData("not JSON", 0)]
    [InlineData("{\"response\":\"\"}", 0)]
    [InlineData("{\"response\":\"plan\"}", 1)]
    public async Task Gemini_errors_cannot_be_accepted_as_a_plan(string output, int exit)
    {
        var runner = new GeminiCliRunner(new Capture { Output = output, ExitCode = exit }, "gemini", directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.PlanAsync(WorkTask.Create(Guid.NewGuid(), "T", "D", "A"), new(directory, "base"), Guid.NewGuid(), CancellationToken.None));
    }
    [Fact] public async Task Store_freezes_both_run_selections_and_rejects_stale_project_changes()
    {
        var store = new SqliteTaskStore(Path.Combine(directory, "ai.db")); await store.InitializeAsync();
        var config = new AiConfiguration(new("gemini", "pro"), new("codex", "chosen"));
        var project = new ProjectDefinition(Guid.NewGuid(), "P", directory, "main", ["test"], config);
        await store.AddProjectAsync(project);
        var task = WorkTask.Create(project.Id, "T", "D", "A") with { Ai = config };
        await store.AddTaskAsync(task.RequestPlanning());
        var planning = (await store.ClaimPlanningAsync())!;
        Assert.Equal(config.Planning, planning.Run.Selection);
        await store.CompletePlanningAsync(planning.Run.Id, "TDD", null);
        task = (await store.GetTaskAsync(task.Id))!;
        await store.SaveTaskAsync(task.ApprovePlan(), task.Version);
        Assert.Equal(config.Implementation, (await store.ClaimNextAsync())!.Run.Selection);
        await store.SetProjectAiAsync(project.Id, new(), 0);
        await Assert.ThrowsAsync<ConflictException>(() => store.SetProjectAiAsync(project.Id, config, 0));
        Assert.Equal(config, (await store.GetTaskAsync(task.Id))!.Ai);
    }
    [Fact] public void Legacy_json_and_cli_default_model_are_compatible()
    {
        var task = JsonSerializer.Deserialize<WorkTask>(JsonSerializer.Serialize(WorkTask.Create(Guid.NewGuid(), "T", "D", "A")))!;
        Assert.Equal(new AiSelection(), AiSelection.Normalize(task.Ai?.Implementation));
    }
}
