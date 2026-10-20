using DevFlow.Core;
using System.Text.Json;
using TaskStatus = DevFlow.Core.TaskStatus;
namespace DevFlow.Infrastructure;

public sealed partial class SqliteTaskStore
{
    public Task<TaskRun?> GetRunAsync(Guid runId, CancellationToken ct = default)
    { using var c = Open(ct); return Task.FromResult(ReadOne<TaskRun>(c, null, "Runs", runId)); }
    public Task<TaskReview> ReviewAsync(Guid taskId, Guid runId, string resultCommit, ReviewDecision decision,
        string? comments, int expectedVersion, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var task = ReadOne<WorkTask>(c, tx, "Tasks", taskId) ?? throw new MissingEntityException("Task not found.");
        if (task.Version != expectedVersion) throw new ConflictException("Task changed; reload before reviewing.");
        var run = ReadOne<TaskRun>(c, tx, "Runs", runId) ?? throw new MissingEntityException("Run not found.");
        using var latest = Command(c, tx, "SELECT Id FROM Runs WHERE TaskId=$id ORDER BY rowid DESC LIMIT 1", ("$id", taskId.ToString()));
        if (run.TaskId != taskId || (string?)latest.ExecuteScalar() != runId.ToString() ||
            run.FinishedAt is null || run.Status != TaskStatus.AwaitingReview.ToString() ||
            string.IsNullOrWhiteSpace(run.ResultCommit) || !string.Equals(run.ResultCommit, resultCommit, StringComparison.OrdinalIgnoreCase))
            throw new ConflictException("Review must target the latest validated run and its exact result commit.");
        var updated = task.Review(decision, comments, run.ResultCommit);
        var review = new TaskReview(Guid.NewGuid(), taskId, runId, run.ResultCommit, decision,
            string.IsNullOrWhiteSpace(comments) ? null : WorkTask.Required(comments, 20000), DateTimeOffset.UtcNow);
        Execute(c, tx, "INSERT INTO Reviews VALUES($id,$task,$run,$payload)", ("$id", review.Id.ToString()),
            ("$task", taskId.ToString()), ("$run", runId.ToString()), ("$payload", JsonSerializer.Serialize(review)));
        UpdateTask(c, tx, updated, expectedVersion);
        Event(c, tx, taskId, "TaskReviewed", decision.ToString());
        tx.Commit();
        return Task.FromResult(review);
    }
    public Task<IReadOnlyList<TaskReview>> ListReviewsAsync(Guid taskId, CancellationToken ct = default)
    { using var c = Open(ct); return Task.FromResult(ReadList<TaskReview>(c, "SELECT Payload FROM Reviews WHERE TaskId=$id ORDER BY rowid", ("$id", taskId.ToString()))); }
    public Task<WorkTask> RetryAsync(Guid taskId, string? instructions, int expectedVersion, CancellationToken ct = default, string scope = "Resume")
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var task = ReadOne<WorkTask>(c, tx, "Tasks", taskId) ?? throw new MissingEntityException("Task not found.");
        if (task.Version != expectedVersion) throw new ConflictException("Task changed; reload before retrying.");
        var preview = RetryStrategy.Preview(task, scope);
        var updated = task.Retry(instructions);
        if (preview.BaseCommit is not null) updated = updated with { RevisionCommit = preview.BaseCommit };
        if (scope == "Restart")
            updated = updated with { StepResults = [], ActiveStepId = null, RevisionCommit = preview.BaseCommit, AutoReviewAttempt = 0 };
        else if (task.Workflow?.Steps is { } steps && (task.StepResults?.Length ?? 0) == steps.Length)
            updated = updated with { StepResults = [], ActiveStepId = null };
        UpdateTask(c, tx, updated, expectedVersion);
        Event(c, tx, taskId, "TaskRetried", preview.Summary + "\n" + updated.RevisionInstructions!);
        tx.Commit();
        return Task.FromResult(updated);
    }
}
