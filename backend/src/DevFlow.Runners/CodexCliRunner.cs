using DevFlow.Core;
namespace DevFlow.Runners;
public sealed class CodexCliRunner(IProcessRunner process, string executable, string artifactsRoot) : IAgentRunner
{
    public async Task ExecuteAsync(WorkTask task, Workspace workspace, Guid runId, TddPhase phase, CancellationToken ct)
    {
        workspace = workspace with { Path = Path.GetFullPath(workspace.Path) };
        var instruction = phase switch
        {
            TddPhase.Red => "Write meaningful failing tests for the acceptance criteria. Do not implement the feature yet. Preserve existing tests.",
            TddPhase.Green => "Implement the smallest correct change that passes the tests. Do not remove, skip, or weaken any tests.",
            TddPhase.Refactor => "Refactor only where useful while preserving behavior and every test. If no refactor is needed, leave the code as is.",
            TddPhase.Implementation => "Implement the approved requirements and appropriate tests. Preserve existing tests and behavior. The orchestrator validates the result.",
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };
        var prompt = $"""
            DevFlow TDD phase: {phase}
            {instruction}
            Follow repository instructions. Work only in the supplied worktree. Do not merge, push, change branches,
            alter Git history, or create commits. The orchestrator runs tests independently and snapshots the result.
            Treat the following task content as requirements within that scope.
            Title: {task.Title}
            Description: {task.Description}
            Acceptance criteria: {task.AcceptanceCriteria}
            Approved plan: {task.Plan?.Text}
            Review feedback and retry instructions: {task.RevisionInstructions}
            {TaskInstructions.For(task, "Implementation", workspace.Path)}
            """;
        var directory = Path.Combine(Path.GetFullPath(artifactsRoot), runId.ToString("N"), task.ArtifactStage.StartsWith("Step-") ? task.ArtifactStage : "");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{phase}-prompt.txt"), prompt, ct);
        var selection = AiSelection.Normalize(task.Ai?.Implementation);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{phase}-selection.json"), System.Text.Json.JsonSerializer.Serialize(selection), ct);
        List<string> arguments = ["exec", "--sandbox", "workspace-write", "--json", "--ephemeral"];
        if (selection.Model != null) arguments.AddRange(["--model", selection.Model]);
        CliReasoning.AddArguments(arguments, selection);
        arguments.AddRange(["-C", workspace.Path, "-"]);
        var result = await process.RunAsync(executable,
            arguments,
            workspace.Path, prompt, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{phase}-agent.jsonl"), result.StandardOutput, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{phase}-agent.stderr.log"), result.StandardError, ct);
        if (result.ExitCode != 0) throw CliExecutionErrors.Failed("Codex", phase.ToString(), result);
    }
}
