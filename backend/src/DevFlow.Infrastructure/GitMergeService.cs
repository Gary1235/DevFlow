using DevFlow.Core;
namespace DevFlow.Infrastructure;
public sealed class GitMergeService(IProcessRunner process) : IGitMergeService
{
    public async Task<RepositoryState> InspectAsync(ProjectDefinition project, CancellationToken ct = default)
    {
        var actualRoot = Path.GetFullPath((await Git(project, ct, "rev-parse", "--show-toplevel")).Trim());
        var configuredRoot = Path.GetFullPath(project.RepositoryPath).TrimEnd(Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(actualRoot.TrimEnd(Path.DirectorySeparatorChar), configuredRoot, comparison))
            throw new ConflictException("Project must point to the repository root.");
        var gitDirectory = (await Git(project, ct, "rev-parse", "--absolute-git-dir")).Trim();
        if (new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" }.Any(name => File.Exists(Path.Combine(gitDirectory, name))) ||
            new[] { "rebase-merge", "rebase-apply" }.Any(name => Directory.Exists(Path.Combine(gitDirectory, name))))
            throw new ConflictException("Repository already has an unfinished Git operation.");
        var branch = (await Git(project, ct, "symbolic-ref", "--short", "HEAD")).Trim();
        var head = GitCommit.Require((await Git(project, ct, "rev-parse", "--verify", "HEAD")).Trim());
        var status = await Git(project, ct, "status", "--porcelain=v1", "--untracked-files=all");
        return new(branch, head, string.IsNullOrWhiteSpace(status));
    }
    public async Task<string> MergeAsync(ProjectDefinition project, TaskMerge merge, CancellationToken ct = default)
    {
        var approved = GitCommit.Require(merge.ResultCommit);
        var expected = GitCommit.Require(merge.ExpectedTargetCommit);
        var before = await InspectAsync(project, ct);
        if (before.Branch != project.BaseBranch) throw new ConflictException("Check out the configured target branch before merging.");
        if (!before.Clean) throw new ConflictException("Commit or remove local changes before merging.");
        if (before.HeadCommit != expected) throw new ConflictException("Target branch changed; refresh its commit before merging.");
        var ancestry = await process.RunAsync("git", ["merge-base", "--is-ancestor", expected, approved], project.RepositoryPath, null, ct);
        if (ancestry.ExitCode != 0) throw new ConflictException("Approved result cannot fast-forward the target branch.");
        if (expected == approved) return approved;
        ProcessResult result;
        RepositoryState after;
        try
        {
            result = await process.RunAsync("git", ["-c", "merge.autoStash=false", "merge", "--ff-only", "--no-autostash",
                "--no-overwrite-ignore", "--no-edit", approved], project.RepositoryPath, null, ct);
            after = await InspectAsync(project, ct);
        }
        catch (Exception)
        {
            throw new MergeUncertainException("Git merge was interrupted or its final state could not be read.");
        }
        if (after.Branch == before.Branch && after.HeadCommit == approved && after.Clean) return approved;
        if (result.ExitCode != 0 && after.Branch == before.Branch && after.HeadCommit == expected && after.Clean)
            throw new ConflictException("Git refused the merge; local files and target commit were preserved.");
        throw new MergeUncertainException("Git state changed unexpectedly; reconcile before retrying.");
    }
    public async Task<MergeObservation> ObserveAsync(ProjectDefinition project, TaskMerge merge, CancellationToken ct = default)
    {
        RepositoryState current;
        try { current = await InspectAsync(project, ct); }
        catch (ConflictException) { return MergeObservation.Unknown; }
        if (current.Branch != project.BaseBranch) return MergeObservation.Unknown;
        var result = await process.RunAsync("git", ["merge-base", "--is-ancestor", GitCommit.Require(merge.ResultCommit), current.HeadCommit],
            project.RepositoryPath, null, ct);
        if (result.ExitCode == 0) return MergeObservation.Applied;
        if (result.ExitCode == 1 && current.HeadCommit == merge.ExpectedTargetCommit) return MergeObservation.NotApplied;
        return MergeObservation.Unknown;
    }
    private async Task<string> Git(ProjectDefinition project, CancellationToken ct, params string[] args)
    {
        var result = await process.RunAsync("git", args, project.RepositoryPath, null, ct);
        if (result.ExitCode != 0) throw new ConflictException("Repository state is unavailable or HEAD is detached.");
        return result.StandardOutput;
    }
}
