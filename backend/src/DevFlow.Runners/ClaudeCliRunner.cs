using DevFlow.Core;
using System.Text.Json;
namespace DevFlow.Runners;
public sealed class ClaudeCliRunner(IProcessRunner process, string executable, string artifactsRoot) : IAgentRunner, IPlanningRunner
{
    public async Task ExecuteAsync(WorkTask task, Workspace workspace, Guid runId, TddPhase phase, CancellationToken ct)
    {
        var instruction = phase switch
        {
            TddPhase.Red => "Write meaningful failing behavior tests only. Do not implement the feature. Preserve existing tests.",
            TddPhase.Green => "Implement the smallest correct change that passes tests. Do not remove, skip or weaken tests.",
            TddPhase.Refactor => "Refactor only where useful, preserving behavior and every test.",
            TddPhase.Implementation => "Implement the approved requirements and appropriate tests. Preserve existing tests and behavior.",
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };
        await Invoke(task, workspace, runId, phase.ToString(), false,
            instruction + "\nApproved plan: " + task.Plan?.Text + "\nReview feedback: " + task.RevisionInstructions, ct);
    }
    public async Task<string> PlanAsync(WorkTask task, Workspace workspace, Guid runId, CancellationToken ct)
    {
        var plan = await Invoke(task, workspace, runId, task.ArtifactStage, true,
            (task.ReadOnlyInstruction ?? "Read repository instructions and source, including AGENTS.md and CLAUDE.md. Return an implementation plan for human approval: affected files, contracts, meaningful failing tests, steps, validation commands, risks, assumptions and questions.") + " Do not edit files. Previous plan: " + task.Plan?.Text, ct);
        plan = WorkTask.Required(plan, 40000);
        await File.WriteAllTextAsync(Path.Combine(Path.GetFullPath(artifactsRoot), runId.ToString("N"), task.ArtifactStage + "-result.json"), JsonSerializer.Serialize(new { plan }), ct);
        return plan;
    }
    internal static List<string> Arguments(AiSelection selection, bool planning, bool probe = false)
    {
        // Restricted mode confines file tools to the worktree and prevents user/project hooks and command tools.
        List<string> args = ["--print", "--output-format", "json", "--restricted", "--no-session-persistence",
            "--permission-mode", planning ? "plan" : "acceptEdits", "--tools", probe ? "" : planning ? "Read,Glob,Grep" : "Read,Glob,Grep,Edit,Write",
            "--disallowedTools", "mcp__*", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}"];
        if (selection.Model != null) args.AddRange(["--model", selection.Model]);
        CliReasoning.AddArguments(args, selection);
        return args;
    }
    internal static string ParseResult(string output)
    {
        try
        {
            using var json = JsonDocument.Parse(output); var root = json.RootElement;
            if (root.GetProperty("type").GetString() != "result" || root.GetProperty("subtype").GetString() != "success" ||
                root.GetProperty("is_error").GetBoolean()) throw new InvalidOperationException("Claude Code reported an unsuccessful result; see run artifacts.");
            return WorkTask.Required(root.GetProperty("result").GetString(), 40000);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException)
        { throw new InvalidOperationException("Claude Code returned invalid or empty output; see run artifacts.", ex); }
    }
    private async Task<string> Invoke(WorkTask task, Workspace workspace, Guid runId, string phase, bool planning, string instruction, CancellationToken ct)
    {
        var selection = AiSelection.Normalize(planning ? task.Ai?.Planning : task.Ai?.Implementation);
        var prompt = $"""
            DevFlow phase: {phase}
            {instruction}
            Follow repository instructions. Work only in this worktree. Do not commit, merge, push, change branches or Git history.
            The orchestrator runs all validation commands independently. You have file tools only; no shell tools are provided.
            Title: {task.Title}
            Description: {task.Description}
            Acceptance criteria: {task.AcceptanceCriteria}
            {TaskInstructions.For(task, planning ? task.ReadOnlyInstruction is null ? "Planning" : "Discussion" : "Implementation")}
            """;
        var directory = Path.Combine(Path.GetFullPath(artifactsRoot), runId.ToString("N")); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, phase + "-prompt.txt"), prompt, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, phase + "-selection.json"), JsonSerializer.Serialize(selection), ct);
        var result = await process.RunAsync(executable, Arguments(selection, planning), workspace.Path, prompt, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, phase + "-agent.json"), result.StandardOutput, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, phase + "-agent.stderr.log"), result.StandardError, ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Claude Code {phase} exited with code {result.ExitCode}; check installation, login, account eligibility or model access in the CLI. See run artifacts.");
        return ParseResult(result.StandardOutput);
    }
}
