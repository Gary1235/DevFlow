using System.Text.Json;
using System.Text.Json.Serialization;
namespace DevFlow.Core;

public sealed record RuleDefinition(Guid Id, string Name, string Content, string[] Stages, int Version = 1, bool Archived = false, bool RequiresTdd = false);
public sealed record RuleSnapshot(Guid Id, string Name, string Content, string[] Stages, int Version, bool RequiresTdd = false)
{
    public static RuleSnapshot From(RuleDefinition rule) => new(rule.Id, rule.Name, rule.Content, rule.Stages, rule.Version, rule.RequiresTdd);
}
public sealed record ExecutionPolicy(bool Tdd = true, string[]? AllowedPaths = null)
{
    public static ExecutionPolicy Normalize(ExecutionPolicy? policy, RuleSnapshot[] rules)
    {
        policy ??= new();
        if (!policy.Tdd && rules.Any(r => r.RequiresTdd)) throw new ArgumentException("Selected rules require TDD mode.");
        if (policy.AllowedPaths?.Any(p => p is null) == true) throw new ArgumentException("Allowed paths cannot contain null values.");
        var paths = (policy.AllowedPaths ?? []).Select(p => p.Trim().Replace('\\', '/')).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length > 100 || paths.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 500 || p.StartsWith('/') ||
            p.Contains(':') || p.Contains('*') || p.Contains('?') || p.Contains("//") || p.Split('/').Any(s => s is ".." or ".") || p.Any(char.IsControl)))
            throw new ArgumentException("Allowed paths must be repository-relative files or directories ending in /, without wildcards or traversal.");
        return policy with { AllowedPaths = paths };
    }
}
public sealed record WorkflowDefinition(bool PlanningEnabled = true, AiSelection[]? Reviewers = null, int MaxReviewRetries = 0)
{
    public static WorkflowDefinition Normalize(WorkflowDefinition? flow)
    {
        flow ??= new();
        if (flow.MaxReviewRetries is < 0 or > 2 || (flow.Reviewers?.Length ?? 0) > 2) throw new ArgumentException("At most two AI reviewers and two correction rounds are supported.");
        return flow with { Reviewers = (flow.Reviewers ?? []).Select(AiSelection.Normalize).ToArray() };
    }
}
public sealed record AiAcceptanceReport(Guid Id, int Attempt, int Stage, AiSelection Selection, string ResultCommit, string Verdict, string Summary, DateTimeOffset CreatedAt);
public sealed record DiscussionMessage(Guid Id, string Role, string Content, DateTimeOffset CreatedAt, AiSelection? Selection = null, string? BaseCommit = null, Guid? OperationId = null);
public sealed record DiscussionDraft(Guid Id, string Title, string Description, string AcceptanceCriteria, string? TaskRules = null,
    string[]? AllowedPaths = null, Guid[]? RuleIds = null, string[]? OpenQuestions = null, Guid? CreatedTaskId = null, int SourceVersion = 0);
public sealed record Discussion(Guid Id, Guid ProjectId, string Title, AiSelection Selection, RuleSnapshot[] Rules,
    DiscussionMessage[] Messages, DiscussionDraft[] Drafts, int Version, DateTimeOffset UpdatedAt,
    Guid? ActiveOperation = null, string? Error = null);

public static class TaskInstructions
{
    public static string For(WorkTask task, string stage) =>
        "Applicable rule snapshots:\n" + string.Join("\n\n", (task.Rules ?? []).Where(r => r.Stages.Contains(stage))
            .Select(r => $"[{r.Name} v{r.Version}]\n{r.Content}")) +
        "\nTask-specific rules:\n" + task.TaskRules +
        "\nAllowed edit paths (empty means entire repository):\n" + string.Join("\n", task.Policy?.AllowedPaths ?? []) +
        "\nExecution mode: " + (task.Policy?.Tdd == false ? "Standard implementation with validation" : "TDD") +
        "\nIf requirements or rules conflict, report the conflict; do not silently ignore a rule.";

    public static T ParseJson<T>(string output)
    {
        var text = output.Trim();
        if (text.StartsWith("```"))
        {
            var start = text.IndexOf('\n'); var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start < 0 || end <= start) throw new ArgumentException("AI returned an invalid JSON result.");
            text = text[(start + 1)..end].Trim();
        }
        try { return JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new ArgumentException("AI returned an invalid structured result. Please retry.", ex); }
    }
}

public interface IExecutionPolicyGuard
{
    Task CheckAsync(Workspace workspace, ExecutionPolicy policy, CancellationToken ct);
}
