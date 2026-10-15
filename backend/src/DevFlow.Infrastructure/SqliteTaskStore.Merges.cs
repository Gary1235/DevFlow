using DevFlow.Core;
using System.Text.Json;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Infrastructure;
public sealed partial class SqliteTaskStore
{
    public Task<TaskMerge> BeginMergeAsync(Guid taskId, Guid runId, string resultCommit, string expectedTargetCommit,
        int expectedVersion, CancellationToken ct = default)
    {
        resultCommit = GitCommit.Require(resultCommit);
        expectedTargetCommit = GitCommit.Require(expectedTargetCommit);
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var task = ReadOne<WorkTask>(c, tx, "Tasks", taskId) ?? throw new MissingEntityException("Task not found.");
        using var previous = Command(c, tx, "SELECT Payload FROM Merges WHERE TaskId=$id ORDER BY rowid DESC LIMIT 1", ("$id", taskId.ToString()));
        if (task.Status == Status.Merged && previous.ExecuteScalar() is string json)
        {
            var completed = JsonSerializer.Deserialize<TaskMerge>(json)!;
            if (completed.State == MergeState.Succeeded && completed.RunId == runId && completed.ResultCommit == resultCommit)
                return Task.FromResult(completed);
        }
        if (task.Status != Status.Approved || task.Version != expectedVersion)
            throw new ConflictException("Only the current approved task version can be merged.");
        var run = ReadOne<TaskRun>(c, tx, "Runs", runId) ?? throw new MissingEntityException("Run not found.");
        using var latest = Command(c, tx, "SELECT Id FROM Runs WHERE TaskId=$id ORDER BY rowid DESC LIMIT 1", ("$id", taskId.ToString()));
        using var approval = Command(c, tx, "SELECT Payload FROM Reviews WHERE RunId=$id", ("$id", runId.ToString()));
        var review = approval.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<TaskReview>(payload) : null;
        if (run.TaskId != taskId || (string?)latest.ExecuteScalar() != runId.ToString() ||
            run.FinishedAt is null || run.Status != Status.AwaitingReview.ToString() ||
            !string.Equals(run.ResultCommit, resultCommit, StringComparison.OrdinalIgnoreCase) ||
            review?.Decision != ReviewDecision.Approve || !string.Equals(review.ResultCommit, resultCommit, StringComparison.OrdinalIgnoreCase))
            throw new ConflictException("Merge must target the latest approved run and commit.");
        var project = ReadOne<ProjectDefinition>(c, tx, "Projects", task.ProjectId)!;
        var repositoryKey = Path.GetFullPath(project.RepositoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) repositoryKey = repositoryKey.ToUpperInvariant();
        using var active = Command(c, tx, "SELECT COUNT(*) FROM Merges WHERE RepositoryKey=$key AND State IN ('Pending','Uncertain')", ("$key", repositoryKey));
        if ((long)active.ExecuteScalar()! != 0) throw new ConflictException("Repository already has an active or unresolved merge.");
        var merge = new TaskMerge(Guid.NewGuid(), taskId, project.Id, runId, resultCommit, expectedTargetCommit,
            MergeState.Pending, null, null, DateTimeOffset.UtcNow, null);
        Execute(c, tx, "INSERT INTO Merges VALUES($id,$task,$key,$state,$payload)", ("$id", merge.Id.ToString()),
            ("$task", taskId.ToString()), ("$key", repositoryKey), ("$state", merge.State.ToString()), ("$payload", JsonSerializer.Serialize(merge)));
        UpdateTask(c, tx, task with { Status = Status.Merging, Version = task.Version + 1 }, task.Version);
        Event(c, tx, taskId, "MergeStarted", merge.Id.ToString());
        tx.Commit();
        return Task.FromResult(merge);
    }
    public Task<TaskMerge> FinishMergeAsync(Guid mergeId, MergeState state, string? mergedCommit, string? error,
        CancellationToken ct = default)
    {
        if (state is not (MergeState.Succeeded or MergeState.Failed or MergeState.Uncertain)) throw new ArgumentException("Invalid merge completion state.");
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var merge = ReadOne<TaskMerge>(c, tx, "Merges", mergeId) ?? throw new MissingEntityException("Merge not found.");
        if (merge.State is MergeState.Succeeded or MergeState.Failed)
        {
            if (merge.State == state && merge.MergedCommit == mergedCommit) return Task.FromResult(merge);
            throw new ConflictException("Merge attempt is already finalized.");
        }
        if (state == MergeState.Succeeded && !string.Equals(mergedCommit, merge.ResultCommit, StringComparison.OrdinalIgnoreCase))
            throw new ConflictException("Merged commit must match the approved result.");
        var task = ReadOne<WorkTask>(c, tx, "Tasks", merge.TaskId)!;
        if (task.Status != Status.Merging) throw new ConflictException("Task is not merging.");
        var finished = merge with { State = state, MergedCommit = state == MergeState.Succeeded ? merge.ResultCommit : null,
            Error = error, FinishedAt = state == MergeState.Uncertain ? null : DateTimeOffset.UtcNow };
        Execute(c, tx, "UPDATE Merges SET State=$state,Payload=$payload WHERE Id=$id", ("$state", state.ToString()),
            ("$payload", JsonSerializer.Serialize(finished)), ("$id", mergeId.ToString()));
        var status = state == MergeState.Succeeded ? Status.Merged : state == MergeState.Failed ? Status.Approved : Status.Merging;
        UpdateTask(c, tx, task with { Status = status, Version = task.Version + 1 }, task.Version);
        Event(c, tx, task.Id, "MergeUpdated", state.ToString());
        tx.Commit();
        return Task.FromResult(finished);
    }
    public Task<IReadOnlyList<TaskMerge>> ListMergesAsync(Guid taskId, CancellationToken ct = default)
    { using var c = Open(ct); return Task.FromResult(ReadList<TaskMerge>(c, "SELECT Payload FROM Merges WHERE TaskId=$id ORDER BY rowid", ("$id", taskId.ToString()))); }
}
