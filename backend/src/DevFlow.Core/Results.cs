namespace DevFlow.Core;
public sealed record ResultDiff(Guid RunId, string BaseCommit, string ResultCommit, string Patch);
public interface IResultDiffReader
{
    Task<ResultDiff> ReadAsync(ProjectDefinition project, TaskRun run, CancellationToken ct = default);
}
