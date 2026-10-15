using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
namespace DevFlow.Tests;

public sealed class GitMergeTests : IAsyncLifetime
{
    private readonly string allowedRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("DEVFLOW_GIT_TEST_ROOT") ?? Path.GetTempPath());
    private string directory = null!;
    private string repository = null!;
    private readonly ProcessRunner process = new();
    private string original = null!;
    private string result = null!;
    private ProjectDefinition Project => new(Guid.NewGuid(), "Demo", repository, "main", ["test"]);
    private TaskMerge Merge => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), result, original,
        MergeState.Pending, null, null, DateTimeOffset.UtcNow, null);
    private async Task<string> Git(params string[] args)
    {
        var r = await process.RunAsync("git", args, repository, null, CancellationToken.None);
        Assert.True(r.ExitCode == 0, r.StandardError);
        return r.StandardOutput.Trim();
    }
    public async Task InitializeAsync()
    {
        directory = Path.Combine(allowedRoot, "devflow-gitmerge-" + Guid.NewGuid());
        repository = Path.Combine(directory, "repo");
        Directory.CreateDirectory(repository);
        await Git("init", "-b", "main");
        await Git("config", "user.name", "DevFlow Tests");
        await Git("config", "user.email", "tests@devflow.local");
        await File.WriteAllTextAsync(Path.Combine(repository, "feature.txt"), "before\n");
        await File.WriteAllTextAsync(Path.Combine(repository, ".gitignore"), "*.log\n");
        await Git("add", ".");
        await Git("commit", "-m", "baseline");
        original = await Git("rev-parse", "HEAD");
        await Git("switch", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(repository, "feature.txt"), "after\n");
        await Git("add", ".");
        await Git("commit", "-m", "feature");
        result = await Git("rev-parse", "HEAD");
        await Git("switch", "main");
    }
    public Task DisposeAsync()
    {
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(allowedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(target).StartsWith("devflow-gitmerge-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test cleanup target.");
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(target, true);
        return Task.CompletedTask;
    }
    [Fact] public async Task Approved_result_fast_forwards_target_and_reconciles_as_applied()
    {
        var service = new GitMergeService(process);
        Assert.Equal(new RepositoryState("main", original, true), await service.InspectAsync(Project));
        Assert.Equal(result, await service.MergeAsync(Project, Merge));
        Assert.Equal(result, await Git("rev-parse", "HEAD"));
        Assert.Equal("after\n", (await File.ReadAllTextAsync(Path.Combine(repository, "feature.txt"))).Replace("\r\n", "\n"));
        Assert.Equal(MergeObservation.Applied, await service.ObserveAsync(Project, Merge));
    }
    [Fact] public async Task Dirty_worktree_is_preserved_and_blocks_merge()
    {
        await File.WriteAllTextAsync(Path.Combine(repository, "feature.txt"), "local edit");
        await Assert.ThrowsAsync<ConflictException>(() => new GitMergeService(process).MergeAsync(Project, Merge));
        Assert.Equal(original, await Git("rev-parse", "HEAD"));
        Assert.Equal("local edit", await File.ReadAllTextAsync(Path.Combine(repository, "feature.txt")));
    }
    [Fact] public async Task Wrong_checked_out_branch_blocks_merge()
    {
        await Git("switch", "-c", "other");
        await Assert.ThrowsAsync<ConflictException>(() => new GitMergeService(process).MergeAsync(Project, Merge));
        Assert.Equal(original, await Git("rev-parse", "HEAD"));
    }
    [Fact] public async Task Changed_target_and_divergent_history_are_rejected()
    {
        await File.WriteAllTextAsync(Path.Combine(repository, "other.txt"), "target changed");
        await Git("add", "."); await Git("commit", "-m", "target advances");
        var current = await Git("rev-parse", "HEAD");
        var service = new GitMergeService(process);
        await Assert.ThrowsAsync<ConflictException>(() => service.MergeAsync(Project, Merge));
        await Assert.ThrowsAsync<ConflictException>(() => service.MergeAsync(Project, Merge with { ExpectedTargetCommit = current }));
        Assert.Equal(current, await Git("rev-parse", "HEAD"));
    }
    [Fact] public async Task Ignored_local_file_is_not_overwritten_by_approved_result()
    {
        await Git("switch", "feature");
        await File.WriteAllTextAsync(Path.Combine(repository, "local.log"), "approved content");
        await Git("add", "-f", "local.log"); await Git("commit", "-m", "tracked log for merge protection test");
        var newResult = await Git("rev-parse", "HEAD");
        await Git("switch", "main");
        await File.WriteAllTextAsync(Path.Combine(repository, "local.log"), "local content");
        await Assert.ThrowsAsync<ConflictException>(() => new GitMergeService(process).MergeAsync(Project, Merge with { ResultCommit = newResult }));
        Assert.Equal(original, await Git("rev-parse", "HEAD"));
        Assert.Equal("local content", await File.ReadAllTextAsync(Path.Combine(repository, "local.log")));
    }
    [Fact] public async Task Observation_distinguishes_unapplied_and_inconclusive_state()
    {
        var service = new GitMergeService(process);
        Assert.Equal(MergeObservation.NotApplied, await service.ObserveAsync(Project, Merge));
        await File.WriteAllTextAsync(Path.Combine(repository, "other.txt"), "unrelated");
        await Git("add", "."); await Git("commit", "-m", "unrelated");
        Assert.Equal(MergeObservation.Unknown, await service.ObserveAsync(Project, Merge));
    }
}
