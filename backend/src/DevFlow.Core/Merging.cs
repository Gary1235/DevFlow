using System.Collections.Concurrent;
namespace DevFlow.Core;

public enum MergeState { Pending, Succeeded, Failed, Uncertain }
public enum MergeObservation { Applied, NotApplied, Unknown }
public sealed record RepositoryState(string Branch, string HeadCommit, bool Clean);
public sealed record TaskMerge(Guid Id, Guid TaskId, Guid ProjectId, Guid RunId, string ResultCommit,
    string ExpectedTargetCommit, MergeState State, string? MergedCommit, string? Error,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);
public sealed class MergeUncertainException(string message) : Exception(message);
public interface IGitMergeService
{
    Task<RepositoryState> InspectAsync(ProjectDefinition project, CancellationToken ct = default);
    Task<string> MergeAsync(ProjectDefinition project, TaskMerge merge, CancellationToken ct = default);
    Task<MergeObservation> ObserveAsync(ProjectDefinition project, TaskMerge merge, CancellationToken ct = default);
}
public sealed class MergeCoordinator(ITaskStore store, IGitMergeService git, TimeProvider? clock = null)
{
    private readonly ConcurrentDictionary<Guid, byte> active = new();
    public async Task<TaskMerge> MergeAsync(Guid taskId, Guid runId, string resultCommit, string expectedTargetCommit,
        int expectedVersion, CancellationToken ct = default)
    {
        var merge = await store.BeginMergeAsync(taskId, runId, resultCommit, expectedTargetCommit, expectedVersion, ct);
        if (merge.State == MergeState.Succeeded) return merge;
        active.TryAdd(merge.Id, 0);
        try { return await ExecuteReservedAsync(merge); }
        finally { active.TryRemove(merge.Id, out _); }
    }
    private async Task<TaskMerge> ExecuteReservedAsync(TaskMerge merge)
    {
        var project = await store.GetProjectAsync(merge.ProjectId, CancellationToken.None) ?? throw new MissingEntityException("Project not found.");
        // After reservation, a client disconnect must not abandon a Git mutation.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string commit;
        try { commit = await git.MergeAsync(project, merge, timeout.Token); }
        catch (ConflictException ex)
        {
            await store.FinishMergeAsync(merge.Id, MergeState.Failed, null, ex.Message, CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            await store.FinishMergeAsync(merge.Id, MergeState.Uncertain, null, "Merge outcome unknown; reconcile before retrying.", CancellationToken.None);
            throw new MergeUncertainException("Merge outcome unknown; reconcile before retrying.");
        }
        if (!string.Equals(commit, merge.ResultCommit, StringComparison.OrdinalIgnoreCase))
        {
            await store.FinishMergeAsync(merge.Id, MergeState.Uncertain, null, "Git returned an unexpected commit.", CancellationToken.None);
            throw new MergeUncertainException("Git returned an unexpected commit; reconcile before retrying.");
        }
        return await store.FinishMergeAsync(merge.Id, MergeState.Succeeded, commit, null, CancellationToken.None);
    }
    public async Task<TaskMerge> ReconcileAsync(Guid taskId, CancellationToken ct = default)
    {
        if (await store.GetTaskAsync(taskId, ct) is null) throw new MissingEntityException("Task not found.");
        var merge = (await store.ListMergesAsync(taskId, ct)).LastOrDefault() ?? throw new MissingEntityException("Merge not found.");
        if (merge.State is MergeState.Succeeded or MergeState.Failed) return merge;
        if (active.ContainsKey(merge.Id) || (merge.State == MergeState.Pending &&
            (clock ?? TimeProvider.System).GetUtcNow() < merge.StartedAt.AddMinutes(2)))
            throw new ConflictException("Merge may still be running; wait before reconciling a pending attempt.");
        var project = await store.GetProjectAsync(merge.ProjectId, ct) ?? throw new MissingEntityException("Project not found.");
        var observation = await git.ObserveAsync(project, merge, ct);
        if (observation == MergeObservation.Unknown) throw new MergeUncertainException("Repository state is inconclusive; keep the merge blocked for inspection.");
        return await store.FinishMergeAsync(merge.Id, observation == MergeObservation.Applied ? MergeState.Succeeded : MergeState.Failed,
            observation == MergeObservation.Applied ? merge.ResultCommit : null,
            observation == MergeObservation.NotApplied ? "Reconciled: merge was not applied." : null, CancellationToken.None);
    }
}
public static class GitCommit
{
    public static string Require(string? value)
    {
        if (value is not { Length: 40 or 64 } || !value.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A full Git commit ID is required.");
        return value.ToLowerInvariant();
    }
}
