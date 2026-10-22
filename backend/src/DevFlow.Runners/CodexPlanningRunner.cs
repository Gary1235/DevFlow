using DevFlow.Core;
using System.Text.Json;
namespace DevFlow.Runners;
public sealed class CodexPlanningRunner(IProcessRunner process, string executable, string artifactsRoot) : IPlanningRunner
{
    public async Task<string> PlanAsync(WorkTask task, Workspace workspace, Guid runId, CancellationToken ct)
    {
        workspace = workspace with { Path = Path.GetFullPath(workspace.Path) };
        var directory = Path.Combine(Path.GetFullPath(artifactsRoot), runId.ToString("N"));
        Directory.CreateDirectory(directory);
        var prefix = task.ArtifactStage;
        var schema = Path.Combine(directory, prefix + "-schema.json");
        var final = Path.Combine(directory, prefix + "-result.json");
        await File.WriteAllTextAsync(schema, """
            {"type":"object","properties":{"plan":{"type":"string"}},"required":["plan"],"additionalProperties":false}
            """, ct);
        var prompt = $"""
            {task.ReadOnlyInstruction ?? "Analyze this repository and produce an implementation plan for human approval. Describe contracts, affected files, meaningful tests, steps, validation, risks and unresolved questions." + PlanContent.CompletionInstruction}
            Read repository instructions and source files. Do not edit files, commit, merge, push, or implement the task.
            Use the requested JSON schema; put your complete response in the plan string.
            {TaskInstructions.For(task, task.ReadOnlyInstruction is null ? "Planning" : "Discussion", workspace.Path)}
            Title: {task.Title}
            Description: {task.Description}
            Acceptance criteria: {task.AcceptanceCriteria}
            Previous plan: {task.Plan?.Text}
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, prefix + "-prompt.txt"), prompt, ct);
        var selection = AiSelection.Normalize(task.Ai?.Planning);
        await File.WriteAllTextAsync(Path.Combine(directory, prefix + "-selection.json"), JsonSerializer.Serialize(selection), ct);
        List<string> arguments = ["exec", "--sandbox", "read-only", "--json", "--ephemeral"];
        if (selection.Model != null) arguments.AddRange(["--model", selection.Model]);
        CliReasoning.AddArguments(arguments, selection);
        arguments.AddRange(["--output-schema", schema, "--output-last-message", final, "-C", workspace.Path, "-"]);
        var result = await process.RunAsync(executable, arguments, workspace.Path, prompt, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, prefix + "-agent.jsonl"), result.StandardOutput, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, prefix + "-agent.stderr.log"), result.StandardError, ct);
        if (result.ExitCode != 0) throw CliExecutionErrors.Failed("Codex", "Planning", result);
        if (!File.Exists(final) || new FileInfo(final).Length > 1024 * 1024)
            throw new InvalidOperationException("Planning CLI failed or did not produce a bounded final result; see run artifacts.");
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(final, ct));
            return WorkTask.Required(document.RootElement.GetProperty("plan").GetString(), 40000);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException("Planning CLI returned an invalid or empty plan.", ex);
        }
    }
}
