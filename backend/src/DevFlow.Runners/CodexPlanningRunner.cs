using DevFlow.Core;
using System.Text.Json;
namespace DevFlow.Runners;
public sealed class CodexPlanningRunner(IProcessRunner process, string executable, string artifactsRoot) : IPlanningRunner
{
    public async Task<string> PlanAsync(WorkTask task, Workspace workspace, Guid runId, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetFullPath(artifactsRoot), runId.ToString("N"));
        Directory.CreateDirectory(directory);
        var schema = Path.Combine(directory, "Planning-schema.json");
        var final = Path.Combine(directory, "Planning-result.json");
        await File.WriteAllTextAsync(schema, """
            {"type":"object","properties":{"plan":{"type":"string"}},"required":["plan"],"additionalProperties":false}
            """, ct);
        var prompt = $"""
            Analyze this repository and produce an implementation plan for human approval.
            Read repository instructions and source files. Do not edit files, commit, merge, push, or implement the task.
            Describe API contracts, data changes, affected files, meaningful failing tests, implementation steps,
            validation commands, risks, assumptions and unresolved questions. Use the requested JSON schema.
            Title: {task.Title}
            Description: {task.Description}
            Acceptance criteria: {task.AcceptanceCriteria}
            Previous plan: {task.Plan?.Text}
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, "Planning-prompt.txt"), prompt, ct);
        var selection = AiSelection.Normalize(task.Ai?.Planning);
        await File.WriteAllTextAsync(Path.Combine(directory, "Planning-selection.json"), JsonSerializer.Serialize(selection), ct);
        List<string> arguments = ["exec", "--sandbox", "read-only", "--json", "--ephemeral"];
        if (selection.Model != null) arguments.AddRange(["--model", selection.Model]);
        arguments.AddRange(["--output-schema", schema, "--output-last-message", final, "-C", workspace.Path, "-"]);
        var result = await process.RunAsync(executable, arguments, workspace.Path, prompt, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "Planning-agent.jsonl"), result.StandardOutput, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "Planning-agent.stderr.log"), result.StandardError, ct);
        if (result.ExitCode != 0 || !File.Exists(final) || new FileInfo(final).Length > 1024 * 1024)
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
