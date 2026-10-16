using DevFlow.Core;
namespace DevFlow.Infrastructure;

public sealed class GitExecutionPolicyGuard(IProcessRunner process) : IExecutionPolicyGuard
{
    public async Task CheckAsync(Workspace workspace, ExecutionPolicy policy, CancellationToken ct)
    {
        if (policy.AllowedPaths is not { Length: > 0 } allowed) return;
        var tracked = await process.RunAsync("git", ["diff", "--no-ext-diff", "--no-renames", "--name-only", "-z", workspace.BaseCommit, "--"], workspace.Path, null, ct);
        var untracked = await process.RunAsync("git", ["ls-files", "--others", "--exclude-standard", "-z"], workspace.Path, null, ct);
        if (tracked.ExitCode != 0 || untracked.ExitCode != 0 || tracked.StandardOutput.Contains(ProcessRunner.TruncationMarker) || untracked.StandardOutput.Contains(ProcessRunner.TruncationMarker))
            throw new ConflictException("Cannot verify the allowed edit scope.");
        var changed = (tracked.StandardOutput + untracked.StandardOutput).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var violations = changed.Where(p => !allowed.Any(a => a.EndsWith('/') ? p.StartsWith(a, StringComparison.Ordinal) : p.Equals(a, StringComparison.Ordinal))).ToArray();
        if (violations.Length > 0) throw new ConflictException("Changes exceed the allowed edit paths: " + string.Join(", ", violations.Take(20)));
    }
}
