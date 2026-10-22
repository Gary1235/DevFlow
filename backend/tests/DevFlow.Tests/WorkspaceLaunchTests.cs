using System.Text.Json;
using DevFlow.Core;
using DevFlow.Runners;
using Xunit;

namespace DevFlow.Tests;

public sealed class WorkspaceLaunchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow workspace " + Guid.NewGuid());
    public WorkspaceLaunchTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    private sealed class Capture(string provider) : IProcessRunner
    {
        public string? Cwd, Prompt;
        public string[] Arguments = [];
        public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, string cwd, string? input, CancellationToken ct)
        {
            Cwd = cwd; Arguments = args.ToArray(); Prompt = input;
            if (provider == "antigravity") {
                using var json = JsonDocument.Parse(input!);
                Prompt = json.RootElement.GetProperty("message").GetProperty("content").GetString();
            }
            var outputIndex = Array.IndexOf(Arguments, "--output-last-message");
            if (outputIndex >= 0) await File.WriteAllTextAsync(Arguments[outputIndex + 1], "{\"plan\":\"修改樣式並驗證建置。\"}", ct);
            return new(0, provider switch {
                "antigravity" => "{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"response\":\"修改樣式並驗證建置。\"}}",
                "claude" => "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"修改樣式並驗證建置。\"}",
                "gemini" => "{\"response\":\"修改樣式並驗證建置。\"}",
                _ => "{\"type\":\"turn.completed\"}"
            }, "");
        }
    }

    [Theory]
    [InlineData("codex", true)][InlineData("codex", false)]
    [InlineData("antigravity", true)][InlineData("antigravity", false)]
    [InlineData("claude", true)][InlineData("claude", false)]
    [InlineData("gemini", true)][InlineData("gemini", false)]
    public async Task Every_provider_uses_the_same_absolute_path_for_process_workspace_and_prompt(string provider, bool planning)
    {
        var capture = new Capture(provider);
        var workspace = new Workspace(Path.GetRelativePath(Environment.CurrentDirectory, root), "base");
        var task = WorkTask.Create(Guid.NewGuid(), "Task", "Description", "Acceptance") with {
            Ai = new(new(provider), new(provider))
        };
        var run = Guid.NewGuid();
        if (provider == "codex") {
            if (planning) await new CodexPlanningRunner(capture, "fake", root).PlanAsync(task, workspace, run, default);
            else await new CodexCliRunner(capture, "fake", root).ExecuteAsync(task, workspace, run, TddPhase.Implementation, default);
        } else {
            IPlanningRunner planner = provider switch {
                "antigravity" => new AntigravityCliRunner(capture, "fake", root),
                "claude" => new ClaudeCliRunner(capture, "fake", root),
                _ => new GeminiCliRunner(capture, "fake", root)
            };
            if (planning) await planner.PlanAsync(task, workspace, run, default);
            else await ((IAgentRunner)planner).ExecuteAsync(task, workspace, run, TddPhase.Implementation, default);
        }
        Assert.Equal(root, capture.Cwd);
        Assert.Contains("Designated task worktree (the only default authorized project directory): " + root, capture.Prompt);
        Assert.Contains("Use paths relative to this worktree", capture.Prompt);
        if (provider == "codex") Assert.Equal(root, capture.Arguments[Array.IndexOf(capture.Arguments, "-C") + 1]);
        Assert.DoesNotContain("--add-dir", capture.Arguments);
    }
}
