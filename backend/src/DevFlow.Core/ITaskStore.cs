namespace DevFlow.Core;

public interface ITaskStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task AddProjectAsync(ProjectDefinition project, CancellationToken ct = default);
    Task<ProjectDefinition> SetProjectAiAsync(Guid id, AiConfiguration defaults, int expectedVersion, CancellationToken ct = default);
    Task<ProjectDefinition?> GetProjectAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectDefinition>> ListProjectsAsync(CancellationToken ct = default);
    Task AddTaskAsync(WorkTask task, CancellationToken ct = default);
    Task<WorkTask?> GetTaskAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<WorkTask>> ListTasksAsync(CancellationToken ct = default);
    Task SaveTaskAsync(WorkTask task, int expectedVersion, CancellationToken ct = default);
    Task<TaskClaim?> ClaimNextAsync(CancellationToken ct = default);
    Task CompleteRunAsync(Guid runId, bool success, string? worktree, string? baseCommit, string? resultCommit, string? error, CancellationToken ct = default);
    Task<IReadOnlyList<TaskRun>> ListRunsAsync(Guid taskId, CancellationToken ct = default);
    Task<IReadOnlyList<TaskEvent>> GetEventsAsync(Guid taskId, long after = 0, CancellationToken ct = default);
    Task AppendEventAsync(Guid taskId, string kind, string message, CancellationToken ct = default);
    Task SetWorkspaceAsync(Guid runId, Workspace workspace, CancellationToken ct = default);
    Task<TaskRun?> GetRunAsync(Guid runId, CancellationToken ct = default);
    Task<TaskReview> ReviewAsync(Guid taskId, Guid runId, string resultCommit, ReviewDecision decision,
        string? comments, int expectedVersion, CancellationToken ct = default);
    Task<IReadOnlyList<TaskReview>> ListReviewsAsync(Guid taskId, CancellationToken ct = default);
    Task<WorkTask> RetryAsync(Guid taskId, string? instructions, int expectedVersion, CancellationToken ct = default);
    Task<TaskMerge> BeginMergeAsync(Guid taskId, Guid runId, string resultCommit, string expectedTargetCommit,
        int expectedVersion, CancellationToken ct = default);
    Task<TaskMerge> FinishMergeAsync(Guid mergeId, MergeState state, string? mergedCommit, string? error, CancellationToken ct = default);
    Task<IReadOnlyList<TaskMerge>> ListMergesAsync(Guid taskId, CancellationToken ct = default);
    Task<WorkTask> QueuePlanningAsync(Guid taskId, int expectedVersion, CancellationToken ct = default);
    Task<TaskClaim?> ClaimPlanningAsync(CancellationToken ct = default);
    Task CompletePlanningAsync(Guid runId, string? plan, string? error, CancellationToken ct = default);
    Task<IReadOnlyList<PlanRevision>> ListPlansAsync(Guid taskId, CancellationToken ct = default);
}
