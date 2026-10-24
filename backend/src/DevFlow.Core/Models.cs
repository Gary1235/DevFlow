namespace DevFlow.Core;

public enum TaskStatus { AwaitingPlan, AwaitingApproval, Queued, Running, AwaitingReview, Failed, Cancelled, Approved, Rejected, Merging, Merged, PlanningQueued, Planning, PlanningFailed, AwaitingStepApproval }
public enum RunKind { Implementation, Planning }
public enum ReviewDecision { Approve, Reject }
public sealed record ProjectDefinition(Guid Id, string Name, string RepositoryPath, string BaseBranch, string[] ValidationArguments,
    AiConfiguration? AiDefaults = null, int Version = 0, Guid[]? DefaultRuleIds = null,
    ValidationProfile[]? ValidationProfiles = null, string? DefaultValidationProfileId = null, PreviewService[]? PreviewServices = null);
public sealed record PlanDefinition(string Text, int Revision = 1, string? BaseCommit = null)
{
    public string? Problem => PlanContent.Problem(Text);
}
public sealed record WorkTask(Guid Id, Guid ProjectId, string Title, string Description, string AcceptanceCriteria,
    TaskStatus Status, PlanDefinition? Plan, int Version, DateTimeOffset CreatedAt,
    string? RevisionInstructions = null, string? RevisionCommit = null, AiConfiguration? Ai = null,
    RuleSnapshot[]? Rules = null, string? TaskRules = null, ExecutionPolicy? Policy = null,
    WorkflowDefinition? Workflow = null, Guid? SourceDiscussionId = null, Guid? SourceDraftId = null,
    int AutoReviewAttempt = 0, string? ExecutionBaseCommit = null,
    WorkflowStepResult[]? StepResults = null, Guid? ActiveStepId = null,
    string? ResumeCommit = null, IntegrationSource? Integration = null, RecoverySource? Recovery = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ReadOnlyInstruction { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string ArtifactStage { get; init; } = "Planning";
    public static WorkTask Create(Guid projectId, string title, string description, string criteria)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project ID is required.");
        return new(Guid.NewGuid(), projectId, Required(title, 200), Required(description, 20000),
            Required(criteria, 20000), TaskStatus.AwaitingPlan, null, 0, DateTimeOffset.UtcNow);
    }
    public WorkTask SetPlan(string text)
    {
        if (Status is not (TaskStatus.AwaitingPlan or TaskStatus.AwaitingApproval or TaskStatus.PlanningFailed))
            throw new ConflictException("The plan can only be edited before approval.");
        return this with { Plan = new(Required(text, 40000), (Plan?.Revision ?? 0) + 1, Plan?.BaseCommit), Status = TaskStatus.AwaitingApproval, Version = Version + 1 };
    }
    public WorkTask RequestPlanning()
    {
        if (Status is not (TaskStatus.AwaitingPlan or TaskStatus.AwaitingApproval or TaskStatus.PlanningFailed))
            throw new ConflictException("Planning can only be requested before implementation approval.");
        return this with { Status = TaskStatus.PlanningQueued, Version = Version + 1 };
    }
    public WorkTask ReceivePlan(string text, string? baseCommit = null)
    {
        if (Status != TaskStatus.Planning) throw new ConflictException("Task is not planning.");
        PlanContent.EnsureCompleted(text);
        return this with { Plan = new(Required(text, 40000), (Plan?.Revision ?? 0) + 1, baseCommit),
            Status = TaskStatus.AwaitingApproval, Version = Version + 1 };
    }
    public WorkTask ApprovePlan()
    {
        if (Status != TaskStatus.AwaitingApproval || Plan is null) throw new ConflictException("A plan must exist before approval.");
        PlanContent.EnsureCompleted(Plan.Text);
        return this with { Status = TaskStatus.Queued, Version = Version + 1 };
    }
    public WorkTask Cancel()
    {
        if (Status == TaskStatus.Cancelled) return this;
        if (Status is TaskStatus.AwaitingReview or TaskStatus.Failed or TaskStatus.Approved or TaskStatus.Merging or TaskStatus.Merged)
            throw new ConflictException("Finished or merging tasks cannot be cancelled.");
        return this with { Status = TaskStatus.Cancelled, Version = Version + 1, ActiveStepId = null };
    }
    public WorkTask RestartCancelled()
    {
        if (Status != TaskStatus.Cancelled) throw new ConflictException("只有已取消的任務可以重新開始。");
        var status = Workflow?.PlanningEnabled != false ? TaskStatus.PlanningQueued :
            Plan is not null && PlanContent.Problem(Plan.Text) is null ? TaskStatus.AwaitingApproval : TaskStatus.AwaitingPlan;
        return this with { Status = status, Version = Version + 1, StepResults = [], ActiveStepId = null,
            RevisionCommit = null, RevisionInstructions = null, ExecutionBaseCommit = null, AutoReviewAttempt = 0, ResumeCommit = null, Recovery = null };
    }
    public WorkTask Review(ReviewDecision decision, string? comments, string resultCommit)
    {
        if (!Enum.IsDefined(decision)) throw new ArgumentException("Unknown review decision.");
        if (Status != TaskStatus.AwaitingReview) throw new ConflictException("Only tasks awaiting review can be reviewed.");
        var commit = Required(resultCommit, 128);
        var feedback = decision == ReviewDecision.Reject ? Required(comments, 20000) :
            string.IsNullOrWhiteSpace(comments) ? null : Required(comments, 20000);
        return this with { Status = decision == ReviewDecision.Approve ? TaskStatus.Approved : TaskStatus.Rejected,
            RevisionInstructions = decision == ReviewDecision.Reject ? feedback : RevisionInstructions,
            RevisionCommit = decision == ReviewDecision.Reject ? commit : RevisionCommit, Version = Version + 1 };
    }
    public WorkTask Retry(string? instructions)
    {
        if (Status is not (TaskStatus.Failed or TaskStatus.Rejected) || Plan is null)
            throw new ConflictException("Only failed or rejected tasks with an approved plan can retry.");
        var feedback = string.IsNullOrWhiteSpace(instructions) ? Required(RevisionInstructions, 20000) :
            Required(string.IsNullOrWhiteSpace(RevisionInstructions) ? instructions : RevisionInstructions + "\n" + instructions, 20000);
        return this with { Status = TaskStatus.Queued, RevisionInstructions = feedback, Version = Version + 1 };
    }
    public static string Required(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
            throw new ArgumentException($"A nonblank value of at most {maxLength} characters is required.");
        return value.Trim();
    }
}
public sealed record TaskEvent(long Sequence, Guid TaskId, string Kind, string Message, DateTimeOffset CreatedAt);
public sealed record IntegrationSource(Guid TaskId, Guid RunId, string ResultCommit, string TargetCommit, string[] ValidationProfileIds, AiSelection[]? Reviewers = null);
public sealed record RecoverySource(Guid TaskId, Guid RunId, string CheckpointCommit, Guid StepId, string[] RequiredTests, bool Applied = false);
public sealed record TaskRun(Guid Id, Guid TaskId, string Status, string? WorktreePath, string? BaseCommit,
    string? ResultCommit, string? Error, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, RunKind Kind = RunKind.Implementation,
    AiSelection? Selection = null, AiAcceptanceReport[]? AiReviews = null, WorkflowStepResult[]? Steps = null, RuntimeSnapshot? Activity = null);
public sealed record PlanRevision(Guid Id, Guid TaskId, Guid? RunId, int Revision, string Text, string Source, DateTimeOffset CreatedAt, string? BaseCommit = null);
public sealed record TaskClaim(WorkTask Task, TaskRun Run);
public sealed record TaskReview(Guid Id, Guid TaskId, Guid RunId, string ResultCommit, ReviewDecision Decision,
    string? Comments, DateTimeOffset CreatedAt);
public sealed class ConflictException(string message) : Exception(message);
public sealed class MissingEntityException(string message) : Exception(message);
