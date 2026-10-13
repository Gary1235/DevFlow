using DevFlow.Core;
namespace DevFlow.Runners;
public sealed class CodexCliRunner(IProcessRunner process, string executable, string artifactsRoot) : IAgentRunner
{
    public async Task ExecuteAsync(WorkTask task, Workspace workspace, Guid runId, TddPhase phase, CancellationToken ct)
    {
        var instruction = phase switch
        {
            TddPhase.Red => "Write meaningful failing tests for the acceptance criteria. Do not implement the feature yet. Preserve existing tests.",
            TddPhase.Green => "Implement the smallest correct change that passes the tests. Do not remove, skip, or weaken any tests.",
            TddPhase.Refactor => "Refactor only where useful while preserving behavior and every test. If no refactor is needed, leave the code as is.",
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
            """;
        var directory = Path.Combine(Path.GetFullPath(artifactsRoot), runId.ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{phase}-prompt.txt"), prompt, ct);
        var result = await process.RunAsync(executable,
            ["exec", "--sandbox", "workspace-write", "--json", "--ephemeral", "-C", workspace.Path, "-"],
            workspace.Path, prompt, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{phase}-agent.jsonl"), result.StandardOutput, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{phase}-agent.stderr.log"), result.StandardError, ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Codex {phase} exited with code {result.ExitCode}; see run artifacts.");
    }
}
