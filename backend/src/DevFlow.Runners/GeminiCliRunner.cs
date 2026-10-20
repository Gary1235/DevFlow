using DevFlow.Core;
using System.Text.Json;
namespace DevFlow.Runners;
public sealed class GeminiCliRunner(IProcessRunner process, string executable, string artifactsRoot) : IAgentRunner, IPlanningRunner
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
        plan = WorkTask.Required(plan, 40000);
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
        List<string> arguments = ["--sandbox", "--approval-mode", planning ? "plan" : "auto_edit", "--output-format", "json", "--prompt", "Follow the complete DevFlow instructions supplied on stdin."];
        if (selection.Model != null) arguments.AddRange(["--model", selection.Model]);
        var result = await process.RunAsync(executable, arguments, workspace.Path, prompt, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, phase + "-agent.json"), result.StandardOutput, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, phase + "-agent.stderr.log"), result.StandardError, ct);
        if (result.ExitCode != 0) throw CliExecutionErrors.Failed("Gemini", phase, result);
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException("Gemini reported an error; see run artifacts.");
            return WorkTask.Required(json.RootElement.GetProperty("response").GetString(), 40000);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException)
        { throw new InvalidOperationException("Gemini returned invalid or empty output; see run artifacts.", ex); }
    }
}
