using DevFlow.Core;
using DevFlow.Runners;
using System.Text.Json;
using Xunit;
namespace DevFlow.Tests;

public sealed class AntigravityTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-agy-" + Guid.NewGuid());
    private string executable = "";
    public async Task InitializeAsync() { Directory.CreateDirectory(root); executable = Path.Combine(root, "agy.exe"); await File.WriteAllTextAsync(executable, ""); }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }
    private sealed class Capture(string output, int exit = 0) : IProcessRunner
    {
        public string[] Arguments = []; public string? Input; public int Calls;
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> args, string cwd, string? input, CancellationToken ct)
        { Calls++; Arguments = args.ToArray(); Input = input; return Task.FromResult(new ProcessResult(exit, output, "")); }
    }
    private static string Result(string status = "SUCCESS", string response = "規劃內容") => JsonSerializer.Serialize(new { @event = "result", result = new { status, response } });
    private WorkTask TaskDefinition() => WorkTask.Create(Guid.NewGuid(), "中文任務", "前端修改", "驗收條件") with
    { Ai = new(new("antigravity", "gemini-custom", "high"), new("antigravity", "model-custom", "low")) };

    [Fact] public async Task Routed_planning_and_implementation_send_one_stdin_turn_with_independent_models()
    {
        var process = new Capture("{\"event\":\"init\",\"init\":{}}\n" + Result());
        var registry = new CliRegistry(antigravity: executable);
        var runner = new RoutedCliRunner(process, registry, root); var task = TaskDefinition(); var run = Guid.NewGuid();
        Assert.Equal("規劃內容", await runner.PlanAsync(task, new(root, "base"), run, default));
        Assert.Contains("plan", process.Arguments); Assert.Contains("gemini-custom", process.Arguments); Assert.Contains("high", process.Arguments);
        Assert.DoesNotContain("--prompt", process.Arguments); Assert.DoesNotContain("--dangerously-skip-permissions", process.Arguments);
        Assert.EndsWith("\n", process.Input);
        using var input = JsonDocument.Parse(process.Input!);
        Assert.Equal("user", input.RootElement.GetProperty("event").GetString());
        Assert.Contains("中文任務", input.RootElement.GetProperty("message").GetProperty("content").GetString());
        await runner.ExecuteAsync(task, new(root, "base"), run, TddPhase.Implementation, default);
        Assert.Contains("accept-edits", process.Arguments); Assert.Contains("model-custom", process.Arguments); Assert.Contains("low", process.Arguments);
        Assert.Contains("--sandbox", process.Arguments);
    }

    [Theory]
    [InlineData("ERROR")][InlineData("CANCELED")][InlineData("WAITING")][InlineData("RUNNING")]
    public async Task Zero_exit_with_unsuccessful_status_is_rejected(string status)
    {
        var runner = new AntigravityCliRunner(new Capture(Result(status)), executable, root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.PlanAsync(TaskDefinition(), new(root, "base"), Guid.NewGuid(), default));
    }
    [Theory]
    [InlineData("{\"event\":\"step_update\",\"step_update\":{\"text_delta\":\"plan\"}}", 0)]
    [InlineData("{\"response\":\"Gemini output is incompatible\"}", 0)]
    [InlineData("{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"response\":\"\"}}", 0)]
    [InlineData("{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"response\":\"plan\",\"error\":\"failure\"}}", 0)]
    [InlineData("not json", 0)]
    [InlineData("{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"response\":\"plan\"}}", 1)]
    public async Task Invalid_incomplete_or_failed_process_output_is_rejected(string output, int exit)
    {
        var runner = new AntigravityCliRunner(new Capture(output, exit), executable, root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.PlanAsync(TaskDefinition(), new(root, "base"), Guid.NewGuid(), default));
    }

    [Fact] public async Task Empty_success_with_denied_tools_explains_the_permission_failure()
    {
        var output = """{"event":"result","result":{"status":"SUCCESS","response":"","denied_actions":[{"action":"command","display_name":"RunCommand"}]}}""";
        var runner = new AntigravityCliRunner(new Capture(output), executable, root);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.PlanAsync(TaskDefinition(), new(root, "base"), Guid.NewGuid(), default));
        Assert.Contains("工具權限被拒絕", error.Message);
        Assert.DoesNotContain("invalid, incomplete", error.Message);
    }

    [Fact] public async Task Useful_response_explaining_a_denied_tool_is_still_a_valid_reply()
    {
        var output = """{"event":"result","result":{"status":"SUCCESS","response":"請提供需要討論的檔案內容。","denied_actions":[{"action":"command"}]}}""";
        var runner = new AntigravityCliRunner(new Capture(output), executable, root);
        Assert.Equal("請提供需要討論的檔案內容。", await runner.PlanAsync(TaskDefinition() with { ReadOnlyInstruction = "Discuss the requirements without implementing." }, new(root, "base"), Guid.NewGuid(), default));
    }

    [Fact] public async Task Progress_response_with_success_and_denied_tool_is_not_a_completed_plan()
    {
        var output = """{"event":"result","result":{"status":"SUCCESS","response":"正在分析儲存庫結構，以擬定美化實作計畫。","denied_actions":[{"action":"command"}]}}""";
        var runner = new AntigravityCliRunner(new Capture(output), executable, root);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(()=>runner.PlanAsync(TaskDefinition(),new(root,"base"),Guid.NewGuid(),default));
        Assert.Contains("規劃未完成",error.Message);
        Assert.Contains("工具權限被拒絕",error.Message);
    }

    [Fact] public async Task Progress_only_response_is_rejected_even_without_denied_actions()
    {
        var runner = new AntigravityCliRunner(new Capture(Result(response:"正在分析儲存庫與相關檔案，以擬定計畫。")), executable, root);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(()=>runner.PlanAsync(TaskDefinition(),new(root,"base"),Guid.NewGuid(),default));
        Assert.Contains("尚未完成實作計畫",error.Message);
    }

    [Fact] public async Task Provider_picker_replaces_gemini_but_legacy_tasks_keep_their_original_provider()
    {
        var registry = new CliRegistry(gemini: executable, antigravity: executable);
        Assert.DoesNotContain(registry.List(), p => p.Id == "gemini");
        Assert.Contains(registry.List(), p => p.Id == "antigravity" && p.Installed && p.Models.Length == 0);
        Assert.NotNull(registry.Require("gemini"));
        var wrapper = Path.Combine(root, "antigravity.cmd"); await File.WriteAllTextAsync(wrapper, "");
        Assert.False(new CliRegistry(antigravity: wrapper).List().Single(p => p.Id == "antigravity").Installed);
        Assert.Throws<ArgumentException>(() => AiSelection.Normalize(new("antigravity", null, "ultra")));
    }

    [Fact] public async Task Model_listing_reads_cli_slugs_without_calling_a_model()
    {
        var process = new Capture("Fetching available models...\ngemini-3.1-pro-high\tGemini Pro (High)\nclaude-sonnet-custom     Claude Sonnet\n");
        var diagnostics = new CliDiagnostics(process, new CliRegistry(codex: Path.Combine(root, "missing.exe"), antigravity: executable), root);
        var providers = await diagnostics.ListProvidersAsync();
        var provider = providers.Single(p => p.Id == "antigravity");
        Assert.Equal(new[] { "gemini-3.1-pro-high", "claude-sonnet-custom" }, provider.Models);
        Assert.Equal(new[] { "models" }, process.Arguments);
        Assert.Null(process.Input); Assert.False(provider.ModelsVerified);
        await diagnostics.ListProvidersAsync(); Assert.Equal(1, process.Calls);
    }

    [Fact] public async Task Probe_requires_exact_completed_response()
    {
        var process = new Capture(Result("ERROR", "DEVFLOW_OK"));
        var diagnostics = new CliDiagnostics(process, new CliRegistry(antigravity: executable), root);
        Assert.Equal("Error", (await diagnostics.ProbeAsync(new("antigravity"))).ExecutionState);
        Assert.Contains("stream-json", process.Arguments); Assert.Contains("DEVFLOW_OK", process.Input);
    }
    [Fact] public async Task Version_check_does_not_query_npm_or_enable_npm_update()
    {
        var process = new Capture("agy 1.2.3");
        using var maintenance = new CliMaintenance(process, new CliRegistry(antigravity: executable), new CliMaintenanceGate(root), _ => Task.FromResult(false), root);
        var check = await maintenance.CheckAsync("antigravity");
        Assert.False(check.CanUpdate); Assert.Equal("1.2.3", check.InstalledVersion);
        Assert.Equal(new[] { "--version" }, process.Arguments); Assert.Equal(1, process.Calls);
        await Assert.ThrowsAsync<ConflictException>(() => maintenance.StartAsync("antigravity", check.Id));
    }
}
