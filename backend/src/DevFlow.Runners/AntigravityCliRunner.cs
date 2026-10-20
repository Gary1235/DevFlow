using DevFlow.Core;
using System.Text.Json;
namespace DevFlow.Runners;

public sealed class AntigravityCliRunner(IProcessRunner process, string executable, string artifactsRoot) : IAgentRunner, IPlanningRunner
{
    public async Task ExecuteAsync(WorkTask task, Workspace workspace, Guid runId, TddPhase phase, CancellationToken ct)
    {
        var instruction = phase switch
        {
            TddPhase.Red => "Add meaningful failing behavior tests only. Do not change implementation. Preserve existing tests.",
            TddPhase.Green => "Implement the smallest correct change. Do not remove, skip or weaken tests.",
            TddPhase.Refactor => "Refactor where useful, preserving behavior and every test.",
            TddPhase.Implementation => "Implement the approved requirements and appropriate tests. Preserve existing tests and behavior.",
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };
        await Invoke(task, workspace, runId, (task.ArtifactStage.StartsWith("Step-") ? task.ArtifactStage + "-" : "") + phase, false,
            instruction + "\nApproved plan: " + task.Plan?.Text + "\nReview feedback: " + task.RevisionInstructions, ct);
    }

    public async Task<string> PlanAsync(WorkTask task, Workspace workspace, Guid runId, CancellationToken ct)
    {
        var plan = await Invoke(task, workspace, runId, task.ArtifactStage, true,
            (task.ReadOnlyInstruction ?? "Analyze repository instructions and source. Return an implementation plan for human approval with affected files, contracts, meaningful failing tests, steps, validation commands, assumptions and unresolved questions." + PlanContent.CompletionInstruction) + " Do not modify files. Previous plan: " + task.Plan?.Text, ct);
        await File.WriteAllTextAsync(Path.Combine(Path.GetFullPath(artifactsRoot), runId.ToString("N"), task.ArtifactStage + "-result.json"), JsonSerializer.Serialize(new { plan }), ct);
        return plan;
    }

    private async Task<string> Invoke(WorkTask task, Workspace workspace, Guid runId, string phase, bool planning, string instruction, CancellationToken ct)
    {
        var selection = AiSelection.Normalize(planning ? task.Ai?.Planning : task.Ai?.Implementation);
        var prompt = $"""
            DevFlow phase: {phase}
            {instruction}
            Work only in the supplied worktree. Do not commit, merge, push, change branches or Git history.
            The orchestrator independently validates tests and snapshots results.
            Title: {task.Title}
            Description: {task.Description}
            Acceptance criteria: {task.AcceptanceCriteria}
            {TaskInstructions.For(task, planning ? task.ReadOnlyInstruction is null ? "Planning" : "Discussion" : "Implementation")}
            """;
        var directory = Path.Combine(Path.GetFullPath(artifactsRoot), runId.ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, phase + "-prompt.txt"), prompt, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, phase + "-selection.json"), JsonSerializer.Serialize(selection), ct);
        var result = await process.RunAsync(executable, Arguments(selection, planning), workspace.Path, Input(prompt), ct);
        await File.WriteAllTextAsync(Path.Combine(directory, phase + "-agent.json"), result.StandardOutput, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, phase + "-agent.stderr.log"), result.StandardError, ct);
        if (result.ExitCode != 0) throw CliExecutionErrors.Failed("Antigravity", phase, result);
        return ParseResult(result.StandardOutput, planning && task.ReadOnlyInstruction is null);
    }

    internal static List<string> Arguments(AiSelection selection, bool planning)
    {
        List<string> args = ["--input-format", "stream-json", "--output-format", "stream-json",
            "--sandbox", "--mode", planning ? "plan" : "accept-edits", "--print-timeout", "15m"];
        if (selection.Model is not null) args.AddRange(["--model", selection.Model]);
        CliReasoning.AddArguments(args, selection);
        return args;
    }

    internal static string Input(string prompt) => JsonSerializer.Serialize(new { @event = "user", message = new { content = prompt } }) + "\n";

    // A zero exit code or partial response alone does not indicate a completed turn.
    internal static string ParseResult(string output, bool requireCompletePlan = false)
    {
        var deniedWithoutReply = false;
        var deniedPlan = false;
        string? planProblem = null;
        try
        {
            string? response = null;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var json = JsonDocument.Parse(line);
                var item = json.RootElement;
                var kind = item.GetProperty("event").GetString();
                if (response is not null) throw new InvalidOperationException("Antigravity emitted events after its terminal result.");
                if (kind == "error") throw new InvalidOperationException("Antigravity reported an error; see run artifacts.");
                if (kind != "result") continue;
                var result = item.GetProperty("result");
                if (result.GetProperty("status").GetString() != "SUCCESS" ||
                    result.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    throw new InvalidOperationException("Antigravity did not complete successfully; see run artifacts.");
                var answer = result.GetProperty("response").GetString();
                var hasDenied = result.TryGetProperty("denied_actions", out var denied) && denied.ValueKind == JsonValueKind.Array && denied.GetArrayLength() > 0;
                deniedWithoutReply = string.IsNullOrWhiteSpace(answer) && hasDenied;
                deniedPlan = requireCompletePlan && hasDenied;
                if (deniedPlan) throw new InvalidOperationException("Required planning tools were denied.");
                response = WorkTask.Required(answer, 40000);
                if (requireCompletePlan && (planProblem = PlanContent.Problem(response)) is not null)
                    throw new InvalidOperationException(planProblem);
            }
            return response ?? throw new InvalidOperationException("Antigravity returned no terminal result; see run artifacts.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(deniedPlan
                ? "Antigravity 規劃未完成：工具權限被拒絕，無法完成專案檢查。此次回覆不能作為可核准的計畫；請先檢查 CLI 權限與執行紀錄，再重新規劃。"
                : planProblem is not null ? "Antigravity 規劃未完成：" + planProblem
                : deniedWithoutReply
                ? "Antigravity 未產生回覆：所需工具權限被拒絕，無人值守模式無法互動確認。請先在 agy 權限設定允許本次必要的指令或工具，再重試；詳見執行紀錄。"
                : "Antigravity returned invalid, incomplete or unsuccessful output; see run artifacts.", ex);
        }
    }
}
