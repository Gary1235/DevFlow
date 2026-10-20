using System.Text.Json;
using DevFlow.Core;

namespace DevFlow.Infrastructure;

public sealed partial class SqliteTaskStore
{
    public Task<WorkTask> RestartCancelledAsync(Guid taskId, int expectedVersion, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var task = ReadOne<WorkTask>(c, tx, "Tasks", taskId) ?? throw new MissingEntityException("Task not found.");
        if (task.Version != expectedVersion) throw new ConflictException("任務已變更，請重新讀取後再重新開始。");
        var restarted = task.RestartCancelled();
        using var command = Command(c, tx, "SELECT Payload FROM Runs WHERE TaskId=$task", ("$task", taskId.ToString()));
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (JsonSerializer.Deserialize<TaskRun>(reader.GetString(0))!.FinishedAt is null)
                    throw new ConflictException("舊執行仍在停止中，請等它結束後再重新開始。");
        UpdateTask(c, tx, restarted, expectedVersion);
        Event(c, tx, taskId, "TaskRestarted", restarted.Status.ToString());
        tx.Commit(); return Task.FromResult(restarted);
    }
}
