namespace DevFlow.Core;

public enum TaskStatus { AwaitingPlan, AwaitingApproval, Queued, Running, AwaitingReview, Failed, Cancelled }
public sealed record ProjectDefinition(Guid Id, string Name, string RepositoryPath, string BaseBranch, string[] ValidationArguments);
public sealed record PlanDefinition(string Text);
public sealed record WorkTask(Guid Id, Guid ProjectId, string Title, string Description, string AcceptanceCriteria,
    TaskStatus Status, PlanDefinition? Plan, int Version, DateTimeOffset CreatedAt)
{
    public static WorkTask Create(Guid projectId, string title, string description, string criteria)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project ID is required.");
        return new(Guid.NewGuid(), projectId, Required(title, 200), Required(description, 20000),
            Required(criteria, 20000), TaskStatus.AwaitingPlan, null, 0, DateTimeOffset.UtcNow);
    }
    public WorkTask SetPlan(string text)
    {
        if (Status is not (TaskStatus.AwaitingPlan or TaskStatus.AwaitingApproval))
            throw new ConflictException("The plan can only be edited before approval.");
        return this with { Plan = new(Required(text, 40000)), Status = TaskStatus.AwaitingApproval, Version = Version + 1 };
    }
    public WorkTask ApprovePlan()
    {
        if (Status != TaskStatus.AwaitingApproval || Plan is null) throw new ConflictException("A plan must exist before approval.");
        return this with { Status = TaskStatus.Queued, Version = Version + 1 };
    }
    public WorkTask Cancel()
    {
        if (Status == TaskStatus.Cancelled) return this;
        if (Status is TaskStatus.AwaitingReview or TaskStatus.Failed) throw new ConflictException("Finished tasks cannot be cancelled.");
        return this with { Status = TaskStatus.Cancelled, Version = Version + 1 };
    }
    public static string Required(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
            throw new ArgumentException($"A nonblank value of at most {maxLength} characters is required.");
        return value.Trim();
    }
}
public sealed record TaskEvent(long Sequence, Guid TaskId, string Kind, string Message, DateTimeOffset CreatedAt);
public sealed record TaskRun(Guid Id, Guid TaskId, string Status, string? WorktreePath, string? BaseCommit,
    string? ResultCommit, string? Error, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);
public sealed record TaskClaim(WorkTask Task, TaskRun Run);
public sealed class ConflictException(string message) : Exception(message);
public sealed class MissingEntityException(string message) : Exception(message);
