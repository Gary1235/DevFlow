using System.Text.Json;
using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Runners;
using Xunit;

namespace DevFlow.Tests;

public sealed class ReasoningTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-effort-" + Guid.NewGuid());
    private string executable = "";
    public async Task InitializeAsync() { Directory.CreateDirectory(root); executable = Path.Combine(root, "fake.exe"); await File.WriteAllTextAsync(executable, ""); }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }
    private sealed class Capture : IProcessRunner
    {
        public string[] Arguments = [];
        public string Output = "{}";
        public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, string directory, string? input, CancellationToken ct)
        {
            Arguments = args.ToArray();
            var final = Array.IndexOf(Arguments, "--output-last-message");
            if (final >= 0) await File.WriteAllTextAsync(Arguments[final + 1], "{\"plan\":\"Plan\"}", ct);
            return new(0, Output, "");
        }
    }

    [Fact] public void Catalog_keeps_each_models_supported_efforts_and_filters_hidden_or_invalid_entries()
    {
        using var json = JsonDocument.Parse("""
            {"data":[
              {"id":"a","model":"model-a","displayName":"Model A","isDefault":true,"defaultReasoningEffort":"medium","supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"medium"}]},
              {"id":"b","supportedReasoningEfforts":[{"reasoningEffort":"high"},{"reasoningEffort":"xhigh"},{"reasoningEffort":"max"},{"reasoningEffort":"invalid-command"}]},
              {"id":"hidden","hidden":true}, {"id":"--invalid"} ]}
            """);
        var models = CodexModelCatalog.Read(json.RootElement);
        Assert.Equal(2, models.Length); Assert.Equal("model-a", models[0].Id);
        Assert.Equal(new[] { "low", "medium" }, models[0].ReasoningEfforts);
        Assert.Equal("medium", models[0].DefaultReasoningEffort); Assert.True(models[0].IsDefault);
        Assert.Equal(new[] { "high", "xhigh", "max" }, models[1].ReasoningEfforts);
    }

    [Theory]
    [InlineData("codex", "high\";evil")]
    [InlineData("codex", "unknown")]
    [InlineData("claude", "ultracode")]
    [InlineData("gemini", "high")]
    public void Unsupported_or_malformed_efforts_are_rejected(string provider, string effort) =>
        Assert.Throws<ArgumentException>(() => AiSelection.Normalize(new(provider, "chosen", effort)));

    [Fact] public void Legacy_selections_default_to_cli_settings_and_efforts_are_normalized()
    {
        Assert.Null(JsonSerializer.Deserialize<AiSelection>("{\"Provider\":\"codex\",\"Model\":\"chosen\"}")!.ReasoningEffort);
        Assert.Equal("high", AiSelection.Normalize(new("codex", "chosen", " HIGH ")).ReasoningEffort);
    }

    [Fact] public async Task Codex_planning_implementation_and_probe_apply_the_selected_effort()
    {
        var capture = new Capture();
        var task = WorkTask.Create(Guid.NewGuid(), "T", "D", "A") with { Ai = new(new("codex", "chosen", "medium"), new("codex", "chosen", "xhigh")) };
        await new CodexPlanningRunner(capture, executable, root).PlanAsync(task, new(root, "base"), Guid.NewGuid(), default);
        Assert.Contains("model_reasoning_effort=\"medium\"", capture.Arguments);
        await new CodexCliRunner(capture, executable, root).ExecuteAsync(task, new(root, "base"), Guid.NewGuid(), TddPhase.Implementation, default);
        Assert.Contains("model_reasoning_effort=\"xhigh\"", capture.Arguments);
        capture.Output = "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"DEVFLOW_OK\"}}\n{\"type\":\"turn.completed\"}";
        var result = await new CliDiagnostics(capture, new(executable), root).ProbeAsync(new("codex", "chosen", "high"));
        Assert.Equal("Ready", result.ExecutionState); Assert.Contains("model_reasoning_effort=\"high\"", capture.Arguments);
        await new CodexCliRunner(capture, executable, root).ExecuteAsync(task with { Ai = new(new("codex"), new("codex")) }, new(root, "base"), Guid.NewGuid(), TddPhase.Green, default);
        Assert.DoesNotContain("--config", capture.Arguments);
    }

    [Fact] public async Task Claude_planning_implementation_and_probe_pass_a_session_effort_flag()
    {
        var capture = new Capture { Output = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"DEVFLOW_OK\"}" };
        var runner = new ClaudeCliRunner(capture, executable, root);
        var task = WorkTask.Create(Guid.NewGuid(), "T", "D", "A") with { Ai = new(new("claude", "sonnet", "medium"), new("claude", "opus", "max")) };
        await runner.PlanAsync(task, new(root, "base"), Guid.NewGuid(), default);
        Assert.Equal("medium", capture.Arguments[Array.IndexOf(capture.Arguments, "--effort") + 1]);
        await runner.ExecuteAsync(task, new(root, "base"), Guid.NewGuid(), TddPhase.Green, default);
        Assert.Equal("max", capture.Arguments[Array.IndexOf(capture.Arguments, "--effort") + 1]);
        await new CliDiagnostics(capture, new(claude: executable), root).ProbeAsync(new("claude", "opus", "high"));
        Assert.Equal("high", capture.Arguments[Array.IndexOf(capture.Arguments, "--effort") + 1]);
        Assert.Equal(1, capture.Arguments.Count(a => a == "--effort"));
    }

    [Fact] public void Claude_model_options_distinguish_published_model_capabilities()
    {
        var provider = new CliRegistry(claude: executable, claudeModels: ["claude-opus-4-6", "claude-opus-4-7", "claude-haiku-4-5"]).List().Single(p => p.Id == "claude");
        Assert.DoesNotContain("xhigh", provider.ModelDetails![0].ReasoningEfforts);
        Assert.Contains("xhigh", provider.ModelDetails![1].ReasoningEfforts);
        Assert.Empty(provider.ModelDetails![2].ReasoningEfforts);
    }

    [Fact] public async Task Project_task_run_and_ai_review_selections_keep_independent_effort_snapshots()
    {
        var store = new SqliteTaskStore(Path.Combine(root, "state.db")); await store.InitializeAsync();
        var config = new AiConfiguration(new("codex", "chosen", "medium"), new("codex", "chosen", "high"));
        var project = new ProjectDefinition(Guid.NewGuid(), "Demo", root, "main", ["test"], config);
        await store.AddProjectAsync(project);
        var task = WorkTask.Create(project.Id, "T", "D", "A") with { Ai = config };
        await store.AddTaskAsync(task.RequestPlanning());
        var planning = (await store.ClaimPlanningAsync())!;
        Assert.Equal("medium", planning.Run.Selection!.ReasoningEffort);
        await store.CompletePlanningAsync(planning.Run.Id, "Plan", null);
        var planned = (await store.GetTaskAsync(task.Id))!;
        await store.SaveTaskAsync(planned.ApprovePlan(), planned.Version);
        var run = (await store.ClaimNextAsync())!.Run;
        Assert.Equal("high", run.Selection!.ReasoningEffort);
        var reviewSelection = new AiSelection("claude", "opus", "max");
        await store.RecordAiReviewAsync(run.Id, new(Guid.NewGuid(), 0, 1, reviewSelection, new string('a', 40), "Pass", "Reviewed", DateTimeOffset.UtcNow));
        await store.SetProjectAiAsync(project.Id, new(), 0);
        var reopened = new SqliteTaskStore(store.DatabasePath);
        Assert.Equal("high", (await reopened.GetTaskAsync(task.Id))!.Ai!.Implementation!.ReasoningEffort);
        Assert.Equal(reviewSelection, (await reopened.GetRunAsync(run.Id))!.AiReviews!.Single().Selection);
    }
}
