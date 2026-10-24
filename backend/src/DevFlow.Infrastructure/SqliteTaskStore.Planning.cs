using DevFlow.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Infrastructure;
public sealed partial class SqliteTaskStore
{
    public Task<WorkTask> QueuePlanningAsync(Guid taskId, int expectedVersion, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var task = ReadOne<WorkTask>(c, tx, "Tasks", taskId) ?? throw new MissingEntityException("Task not found.");
        if (task.Version != expectedVersion) throw new ConflictException("Task changed; reload before requesting planning.");
        var queued = task.RequestPlanning();
        UpdateTask(c, tx, queued, expectedVersion);
        Event(c, tx, taskId, "PlanningQueued", queued.Status.ToString());
        tx.Commit(); return Task.FromResult(queued);
    }
    public Task<TaskClaim?> ClaimPlanningAsync(CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var task = NextForRepository(c, tx, Status.PlanningQueued);
        if (task is null) return Task.FromResult<TaskClaim?>(null);
        var running = task with { Status = Status.Planning, Version = task.Version + 1 };
        UpdateTask(c, tx, running, task.Version);
        var run = new TaskRun(Guid.NewGuid(), task.Id, "Planning", null, null, null, null, DateTimeOffset.UtcNow, null, RunKind.Planning,
            AiSelection.Normalize(task.Ai?.Planning));
        Execute(c, tx, "INSERT INTO Runs VALUES($id,$task,$payload)", ("$id", run.Id.ToString()), ("$task", task.Id.ToString()), ("$payload", JsonSerializer.Serialize(run)));
        Event(c, tx, task.Id, "PlanningStarted", run.Id.ToString());
        tx.Commit(); return Task.FromResult<TaskClaim?>(new(running, run));
    }
    public Task CompletePlanningAsync(Guid runId, string? plan, string? error, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var run = ReadOne<TaskRun>(c, tx, "Runs", runId) ?? throw new MissingEntityException("Run not found.");
        if (run.Kind != RunKind.Planning || run.FinishedAt != null) throw new ConflictException("Planning run is not active.");
        var task = ReadOne<WorkTask>(c, tx, "Tasks", run.TaskId)!;
        var updated = task;
        if (task.Status != Status.Cancelled)
        {
            if (task.Status != Status.Planning) throw new ConflictException("Task is not planning.");
            updated = plan != null ? task.ReceivePlan(plan, run.BaseCommit) : task with { Status = Status.PlanningFailed, Version = task.Version + 1 };
            UpdateTask(c, tx, updated, task.Version);
            if (plan != null) RecordPlan(c, tx, updated, runId, "Codex");
        }
        var finished = run with { Status = updated.Status.ToString(), Error = error, FinishedAt = DateTimeOffset.UtcNow };
        Execute(c, tx, "UPDATE Runs SET Payload=$payload WHERE Id=$id", ("$payload", JsonSerializer.Serialize(finished)), ("$id", runId.ToString()));
        Event(c, tx, task.Id, "PlanningFinished", updated.Status.ToString());
        tx.Commit(); return Task.CompletedTask;
    }
    public Task<IReadOnlyList<PlanRevision>> ListPlansAsync(Guid taskId, CancellationToken ct = default)
    { using var c = Open(ct); return Task.FromResult(ReadList<PlanRevision>(c, "SELECT Payload FROM Plans WHERE TaskId=$id ORDER BY Revision", ("$id", taskId.ToString()))); }
    private static void RecordPlan(SqliteConnection c, SqliteTransaction? tx, WorkTask task, Guid? runId, string source, bool ignoreExisting = false)
    {
        var plan = new PlanRevision(Guid.NewGuid(), task.Id, runId, task.Plan!.Revision, task.Plan.Text, source, DateTimeOffset.UtcNow, task.Plan.BaseCommit);
        Execute(c, tx, (ignoreExisting ? "INSERT OR IGNORE" : "INSERT") + " INTO Plans VALUES($id,$task,$revision,$payload)",
            ("$id", plan.Id.ToString()), ("$task", task.Id.ToString()), ("$revision", plan.Revision), ("$payload", JsonSerializer.Serialize(plan)));
    }
}
