using DevFlow.Core;
namespace DevFlow.Infrastructure;
public sealed class GitWorkspaceManager(IProcessRunner process, string root) : IWorkspaceManager, IIntegrationWorkspaceManager
{
    public async Task<Workspace> CreateAsync(ProjectDefinition project, Guid runId, CancellationToken ct)
    {
        var fullRoot = Path.GetFullPath(root);
        var repository = Path.GetFullPath(project.RepositoryPath);
        if (fullRoot.Equals(repository, StringComparison.OrdinalIgnoreCase) ||
            fullRoot.StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Worktree root must be outside the source repository.");
        if (project.BaseBranch.StartsWith('-')) throw new ArgumentException("Invalid base branch.");
        Directory.CreateDirectory(fullRoot);
        await Git(repository, ct, "rev-parse", "--show-toplevel");
        var commit = (await Git(repository, ct, "rev-parse", "--verify", project.BaseBranch + "^{commit}")).Trim();
        var path = Path.Combine(fullRoot, runId.ToString("N"));
        await Git(repository, ct, "worktree", "add", "-b", "devflow/" + runId.ToString("N"), path, commit);
        return new(path, commit);
    }
    public async Task<string> SnapshotAsync(Workspace workspace, CancellationToken ct)
    {
        await Git(workspace.Path, ct, "add", "-A");
        await Git(workspace.Path, ct, "-c", "user.name=DevFlow", "-c", "user.email=devflow@localhost",
            "commit", "--allow-empty", "-m", "DevFlow: validated TDD result");
        return (await Git(workspace.Path, ct, "rev-parse", "HEAD")).Trim();
    }
    public async Task<string[]> IntegrateAsync(Workspace workspace, string sourceCommit, CancellationToken ct)
    {
        sourceCommit = GitCommit.Require(sourceCommit);
        var result = await process.RunAsync("git", ["-c", "merge.autoStash=false", "-c", "user.name=DevFlow", "-c", "user.email=devflow@localhost",
            "merge", "--no-commit", "--no-ff", "--no-edit", "--no-autostash", "--no-overwrite-ignore", sourceCommit], workspace.Path, null, ct);
        var unresolved = await Git(workspace.Path, ct, "diff", "--name-only", "--diff-filter=U", "-z");
        var conflicts = unresolved.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (result.ExitCode != 0 && conflicts.Length == 0) throw new InvalidOperationException("隔離副本整合失敗：" + result.StandardError);
        return conflicts;
    }
    private async Task<string> Git(string directory, CancellationToken ct, params string[] args)
    {
        var result = await process.RunAsync("git", args, directory, null, ct);
        if (result.ExitCode != 0) throw new InvalidOperationException("Git failed: " + result.StandardError);
        return result.StandardOutput;
    }
    public async Task AssertUnchangedAsync(Workspace workspace, CancellationToken ct)
    {
        var head = (await Git(workspace.Path, ct, "rev-parse", "HEAD")).Trim();
        var status = await Git(workspace.Path, ct, "status", "--porcelain=v1", "--untracked-files=all");
        if (head != workspace.BaseCommit || !string.IsNullOrWhiteSpace(status))
            throw new ConflictException("Planning changed the worktree or commit.");
    }
    public async Task CleanupReadOnlyAsync(ProjectDefinition project, Workspace workspace, Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            var repository = Path.GetFullPath(project.RepositoryPath);
            if (Directory.Exists(workspace.Path))
                await process.RunAsync("git", ["worktree", "remove", "--", workspace.Path], repository, null, cancellationToken);
            await process.RunAsync("git", ["branch", "-D", "devflow/" + runId.ToString("N")], repository, null, cancellationToken);
        }
        catch
        {
            // Retain worktree if git cleanup fails so StorageManager or human inspection can handle it.
        }
    }
    public async Task RemoveScratchAsync(ProjectDefinition project, Workspace workspace, Guid id, CancellationToken ct)
    {
        var expected = Path.GetFullPath(Path.Combine(root, id.ToString("N")));
        if (!Path.GetFullPath(workspace.Path).Equals(expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            File.GetAttributes(expected).HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("暫存副本路徑無法核對。");
        await Git(project.RepositoryPath, ct, "worktree", "remove", "--force", "--", expected);
        await Git(project.RepositoryPath, ct, "branch", "-D", "devflow/" + id.ToString("N"));
    }
}
