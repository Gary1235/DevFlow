using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
namespace DevFlow.Tests;

public class ResultDiffTests
{
    private sealed class CaptureProcess(string output = "patch") : IProcessRunner
    {
        public int Calls { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string w, string? input, CancellationToken ct)
        { Calls++; Arguments = a; return Task.FromResult(new ProcessResult(0, output, "")); }
    }
    private static ProjectDefinition Project => new(Guid.NewGuid(), "Demo", ".", "main", ["test"]);
    private static TaskRun Run => new(Guid.NewGuid(), Guid.NewGuid(), "AwaitingReview", ".", new string('a', 40),
        new string('b', 40), null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    [Fact] public async Task Diff_uses_saved_commits_and_disables_external_diff_drivers()
    {
        var process = new CaptureProcess();
        var diff = await new GitResultDiffReader(process).ReadAsync(Project, Run);
        Assert.Equal("patch", diff.Patch);
        Assert.Contains("--no-ext-diff", process.Arguments);
        Assert.Contains("--no-textconv", process.Arguments);
        Assert.Contains(Run.BaseCommit!, process.Arguments);
        Assert.Contains(Run.ResultCommit!, process.Arguments);
    }
    [Fact] public async Task Malformed_commit_is_rejected_before_starting_git()
    {
        var process = new CaptureProcess();
        await Assert.ThrowsAsync<ConflictException>(() => new GitResultDiffReader(process)
            .ReadAsync(Project, Run with { ResultCommit = "--output=other-file" }));
        Assert.Equal(0, process.Calls);
    }
    [Fact] public async Task Truncated_diff_must_not_be_presented_as_a_complete_result()
    {
        var process = new CaptureProcess("patch\n[Output truncated at 4 MiB.]");
        await Assert.ThrowsAsync<ConflictException>(() => new GitResultDiffReader(process).ReadAsync(Project, Run));
    }
}
