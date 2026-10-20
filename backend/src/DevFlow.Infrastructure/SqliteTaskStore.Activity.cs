using DevFlow.Core;
using System.Text.Json;

namespace DevFlow.Infrastructure;

public sealed partial class SqliteTaskStore
{
    public Task SetRunActivityAsync(Guid runId, RuntimeSnapshot activity, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var run = ReadOne<TaskRun>(c, tx, "Runs", runId) ?? throw new MissingEntityException("Run not found.");
        // Completion wins over an in-flight heartbeat. Never resurrect a finished run.
        if (run.FinishedAt is null && (run.Activity is null || activity.UpdatedAt >= run.Activity.UpdatedAt))
            Execute(c, tx, "UPDATE Runs SET Payload=$payload WHERE Id=$id",
                ("$payload", JsonSerializer.Serialize(run with { Activity = activity })), ("$id", runId.ToString()));
        tx.Commit();
        return Task.CompletedTask;
    }
}
