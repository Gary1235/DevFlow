namespace DevFlow.Core;

public interface ITaskStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task AddProjectAsync(ProjectDefinition project, CancellationToken ct = default);
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
}
