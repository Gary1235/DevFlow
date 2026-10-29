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
public sealed record WorkflowStep(Guid Id, string Name, string Kind = "Implementation", AiSelection? Selection = null,
    string? Instructions = null, string[]? AllowedPaths = null, bool WaitForApproval = false, string? AcceptanceCriteria = null,
    string? ValidationProfileId = null);
public sealed record WorkflowTemplate(string Name, WorkflowDefinition Workflow);
public sealed record WorkflowStepResult(Guid StepId, string Name, string Kind, string State, Guid RunId,
    string? ResultCommit, string? Summary, AiSelection? Selection, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt = null,
    bool? RequiresRework = null);
public sealed record WorkflowDefinition(bool PlanningEnabled = true, AiSelection[]? Reviewers = null, int MaxReviewRetries = 0,
    WorkflowStep[]? Steps = null, bool SimpleMode = false)
{
    public static WorkflowDefinition Normalize(WorkflowDefinition? flow)
    {
        flow ??= new();
        if (flow.MaxReviewRetries is < 0 or > 2 || (flow.Reviewers?.Length ?? 0) > 2) throw new ArgumentException("At most two AI reviewers and two correction rounds are supported.");
        if (flow.Steps is { } steps)
        {
            if (steps.Length is < 1 or > 20 || !steps.Any(s => s?.Kind == "Implementation"))
                throw new ArgumentException("流程需有 1 至 20 個步驟，並至少包含一個實作步驟。");
            if (steps.Any(s => s is null || s.Id == Guid.Empty) || steps.Select(s => s.Id).Distinct().Count() != steps.Length)
                throw new ArgumentException("流程步驟 ID 必須唯一且非空。");
            if (flow.MaxReviewRetries != 0 && !flow.SimpleMode) throw new ArgumentException("自訂流程失敗後請手動檢查並重試，不支援自動修正輪次。");
            steps = steps.Select(s =>
            {
                if (s.Kind is not ("Implementation" or "Review" or "Validation" or "Approval"))
                    throw new ArgumentException("不支援的流程步驟類型。");
                return s with { Name = WorkTask.Required(s.Name, 100),
                    ValidationProfileId = string.IsNullOrWhiteSpace(s.ValidationProfileId) ? null : WorkTask.Required(s.ValidationProfileId, 60),
                    AcceptanceCriteria = string.IsNullOrWhiteSpace(s.AcceptanceCriteria) ? null : WorkTask.Required(s.AcceptanceCriteria, 12000),
                    Instructions = string.IsNullOrWhiteSpace(s.Instructions) ? null : WorkTask.Required(s.Instructions, 12000),
                    Selection = s.Kind is "Implementation" or "Review" ? AiSelection.Normalize(s.Selection) : null,
                    AllowedPaths = ExecutionPolicy.Normalize(new(false, s.AllowedPaths), []).AllowedPaths };
            }).ToArray();
            return flow with { Steps = steps, Reviewers = [] };
        }
        return flow with { Reviewers = (flow.Reviewers ?? []).Select(AiSelection.Normalize).ToArray() };
    }
}
public sealed record AiAcceptanceReport(Guid Id, int Attempt, int Stage, AiSelection Selection, string ResultCommit, string Verdict, string Summary, DateTimeOffset CreatedAt);
public sealed record PlanDiscussionContext(int TaskVersion, int Revision, string Text, string? BaseCommit,
    string Requirements, string AcceptanceCriteria, string? TaskRules = null);
public sealed record DiscussionExecutionPlan(string Title, string Description, string AcceptanceCriteria, string[] OpenQuestions);
public sealed record DiscussionImage(Guid Id, string Name, string MimeType, int Bytes);
public sealed record ImageUpload(string Name, string MimeType, string Data);
public sealed record PromptImage(string Name, string MimeType, string Data);
public sealed record DiscussionMessage(Guid Id, string Role, string Content, DateTimeOffset CreatedAt, AiSelection? Selection = null, string? BaseCommit = null, Guid? OperationId = null,
    PlanDiscussionContext? PlanContext = null, string? ProposedPlan = null, DiscussionExecutionPlan? ExecutionPlan = null, Guid? CreatedTaskId = null, DiscussionImage[]? Images = null, AgentDecision? AgentProposal = null, TaskFeedbackContext? FeedbackContext = null);
public sealed record DiscussionDraft(Guid Id, string Title, string Description, string AcceptanceCriteria, string? TaskRules = null,
    string[]? AllowedPaths = null, Guid[]? RuleIds = null, string[]? OpenQuestions = null, Guid? CreatedTaskId = null, int SourceVersion = 0);
public sealed record Discussion(Guid Id, Guid ProjectId, string Title, AiSelection Selection, RuleSnapshot[] Rules,
    DiscussionMessage[] Messages, DiscussionDraft[] Drafts, int Version, DateTimeOffset UpdatedAt,
    Guid? ActiveOperation = null, string? Error = null, bool AutoTitle = false, Guid? LastOperationId = null, Guid? PlanTaskId = null,
    bool AgentMode=false,Guid? ActiveTaskId=null,TaskFeedbackContext? FeedbackContext=null);

public static class TaskInstructions
{
    public static WorkTask WithValidationCommands(WorkTask task, ProjectDefinition project)
    {
        var profiles = (project.ValidationProfiles ?? []).Select(p => new { p.Id, p.Executable, p.Arguments, p.WorkingDirectory }).ToList();
        if (project.DefaultValidationProfileId is null) profiles.Add(new { Id = "legacy-dotnet-test", Executable = "dotnet", Arguments = project.ValidationArguments, WorkingDirectory = "." });
        return task with { TaskRules = task.TaskRules +
            "\nPreapproved validation commands (exact executable, argv and worktree-relative cwd):\n" + JsonSerializer.Serialize(profiles) +
            "\nUse the tool's working-directory argument and invoke one exact command at a time, without cd, shell wrappers, extra flags or chaining. " +
            "These grants do not cover installation/preparation scripts. Prefer native structured file read/search/edit tools over shell scripts. " +
            "For shell searches, specify narrow source/test directories instead of scanning generated dependency trees. " +
            "If a request needs additional authorization, report its exact reason; do not bypass a denial." };
    }
    public static string For(WorkTask task, string stage, string? workspace = null) =>
        (workspace is null ? "" : "Designated task worktree (the only default authorized project directory): " + workspace +
            "\nUse paths relative to this worktree. The original checkout, DevFlow source/install directories and other tasks' worktrees are outside this boundary. " +
            "Do not leave this worktree to inspect DevFlow permission hooks or change tools/commands to bypass a denied operation. " +
            "If a required operation is blocked, report the exact tool, target and reason to the user. Request explicit additional access when necessary; otherwise continue only within the designated worktree.\n") +
        "Applicable rule snapshots:\n" + string.Join("\n\n", (task.Rules ?? []).Where(r => r.Stages.Contains(stage))
            .Select(r => $"[{r.Name} v{r.Version}]\n{r.Content}")) +
        "\nTask-specific rules:\n" + task.TaskRules +
        "\nAllowed edit paths (empty means entire repository):\n" + string.Join("\n", task.Policy?.AllowedPaths ?? []) +
        "\nExecution mode: " + (task.Integration is not null ? "Integrate a previously validated result, then validate and review again" : task.Recovery is { Applied: false } ? "Repair an interrupted implementation checkpoint; preserve previous TDD evidence" : task.Policy?.Tdd == false ? "Standard implementation with validation" : "TDD") +
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
