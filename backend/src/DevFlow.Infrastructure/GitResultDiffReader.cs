using DevFlow.Core;
namespace DevFlow.Infrastructure;
public sealed class GitResultDiffReader(IProcessRunner process) : IResultDiffReader
{
    public async Task<ResultDiff> ReadAsync(ProjectDefinition project, TaskRun run, CancellationToken ct = default)
    {
        if (run.FinishedAt is null || !IsCommit(run.BaseCommit) || !IsCommit(run.ResultCommit))
            throw new ConflictException("Run has no complete immutable result.");
        var result = await process.RunAsync("git", ["diff", "--no-ext-diff", "--no-textconv", "--binary",
            run.BaseCommit!, run.ResultCommit!, "--"], project.RepositoryPath, null, ct);
        if (result.ExitCode != 0) throw new ConflictException("Stored result commits are unavailable in the project repository.");
        if (result.StandardOutput.EndsWith(ProcessRunner.TruncationMarker, StringComparison.Ordinal))
            throw new ConflictException("Diff exceeds the display limit; inspect the retained worktree before reviewing.");
        return new(run.Id, run.BaseCommit!, run.ResultCommit!, result.StandardOutput);
    }
    private static bool IsCommit(string? value) => value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit);
}
