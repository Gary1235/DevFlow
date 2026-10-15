using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Tests;

public sealed class ReviewTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-review-" + Guid.NewGuid());
    private SqliteTaskStore Store => new(Path.Combine(directory, "tasks.db"));
    public Task InitializeAsync() => Store.InitializeAsync();
    public Task DisposeAsync() { Directory.Delete(directory, true); return Task.CompletedTask; }
    private async Task<(WorkTask Task, TaskRun Run)> Completed(bool success = true)
    {
        var p = new ProjectDefinition(Guid.NewGuid(), "Demo", directory, "main", ["test"]);
        await Store.AddProjectAsync(p);
        var t = WorkTask.Create(p.Id, "API", "Build", "Tests").SetPlan("TDD").ApprovePlan();
        await Store.AddTaskAsync(t);
        var claim = (await Store.ClaimNextAsync())!;
        await Store.CompleteRunAsync(claim.Run.Id, success, directory, "base", success ? "result" : null, success ? null : "test failed");
        return ((await Store.GetTaskAsync(t.Id))!, (await Store.ListRunsAsync(t.Id)).Single());
    }
    [Fact] public async Task Approval_is_bound_to_run_commit_and_is_persisted()
    {
        var (task, run) = await Completed();
        var review = await Store.ReviewAsync(task.Id, run.Id, run.ResultCommit!, ReviewDecision.Approve, "Looks good", task.Version);
        Assert.Equal(Status.Approved, (await Store.GetTaskAsync(task.Id))!.Status);
        Assert.Equal("result", review.ResultCommit);
        Assert.Equal(review, Assert.Single(await Store.ListReviewsAsync(task.Id)));
        Assert.Null(await Store.ClaimNextAsync());
    }
    [Fact] public async Task Rejection_retry_preserves_history_and_revision_commit()
    {
        var (task, run) = await Completed();
        await Store.ReviewAsync(task.Id, run.Id, "result", ReviewDecision.Reject, "Handle missing IDs", task.Version);
        var rejected = (await Store.GetTaskAsync(task.Id))!;
        var queued = await Store.RetryAsync(task.Id, null, rejected.Version);
        Assert.Equal(Status.Queued, queued.Status);
        Assert.Equal("result", queued.RevisionCommit);
        Assert.Equal("Handle missing IDs", queued.RevisionInstructions);
        var next = (await Store.ClaimNextAsync())!;
        Assert.NotEqual(run.Id, next.Run.Id);
        Assert.Equal(2, (await Store.ListRunsAsync(task.Id)).Count);
        Assert.Equal(run, await Store.GetRunAsync(run.Id));
        Assert.Single(await Store.ListReviewsAsync(task.Id));
    }
    [Theory]
    [InlineData("other", false)]
    [InlineData("result", true)]
    public async Task Wrong_commit_or_stale_version_cannot_be_approved(string commit, bool stale)
    {
        var (task, run) = await Completed();
        await Assert.ThrowsAsync<ConflictException>(() => Store.ReviewAsync(task.Id, run.Id, commit,
            ReviewDecision.Approve, null, stale ? task.Version - 1 : task.Version));
        Assert.Equal(Status.AwaitingReview, (await Store.GetTaskAsync(task.Id))!.Status);
        Assert.Empty(await Store.ListReviewsAsync(task.Id));
    }
    [Fact] public async Task Reject_requires_actionable_comments()
    {
        var (task, run) = await Completed();
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ReviewAsync(task.Id, run.Id, "result", ReviewDecision.Reject, " ", task.Version));
        Assert.Empty(await Store.ListReviewsAsync(task.Id));
    }
    [Fact] public async Task Review_cannot_target_another_tasks_run()
    {
        var (task, _) = await Completed();
        var (_, otherRun) = await Completed();
        await Assert.ThrowsAsync<ConflictException>(() => Store.ReviewAsync(task.Id, otherRun.Id, "result", ReviewDecision.Approve, null, task.Version));
    }
    [Fact] public async Task Only_one_concurrent_review_can_win()
    {
        var (task, run) = await Completed();
        async Task<bool> TryReview()
        {
            try { await Store.ReviewAsync(task.Id, run.Id, "result", ReviewDecision.Approve, null, task.Version); return true; }
            catch (ConflictException) { return false; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(TryReview)));
        Assert.Single(results, r => r);
        Assert.Single(await Store.ListReviewsAsync(task.Id));
    }
    [Fact] public async Task Failed_run_can_retry_but_cannot_be_approved()
    {
        var (task, run) = await Completed(false);
        await Assert.ThrowsAsync<ConflictException>(() => Store.ReviewAsync(task.Id, run.Id, "result", ReviewDecision.Approve, null, task.Version));
        var retry = await Store.RetryAsync(task.Id, "Fix failing tests", task.Version);
        Assert.Equal(Status.Queued, retry.Status);
        Assert.Null(retry.RevisionCommit);
        Assert.Single(await Store.ListRunsAsync(task.Id));
    }
    [Fact] public async Task Approved_task_cannot_retry_or_cancel()
    {
        var (task, run) = await Completed();
        await Store.ReviewAsync(task.Id, run.Id, "result", ReviewDecision.Approve, null, task.Version);
        var approved = (await Store.GetTaskAsync(task.Id))!;
        await Assert.ThrowsAsync<ConflictException>(() => Store.RetryAsync(task.Id, "Change it", approved.Version));
        Assert.Throws<ConflictException>(() => approved.Cancel());
    }
    [Fact] public async Task Previous_run_cannot_be_approved_after_a_retry_completes()
    {
        var (task, oldRun) = await Completed();
        await Store.ReviewAsync(task.Id, oldRun.Id, "result", ReviewDecision.Reject, "Fix IDs", task.Version);
        var rejected = (await Store.GetTaskAsync(task.Id))!;
        await Store.RetryAsync(task.Id, null, rejected.Version);
        var claim = (await Store.ClaimNextAsync())!;
        await Store.CompleteRunAsync(claim.Run.Id, true, directory, "result", "new-result", null);
        var latest = (await Store.GetTaskAsync(task.Id))!;
        await Assert.ThrowsAsync<ConflictException>(() => Store.ReviewAsync(task.Id, oldRun.Id, "result",
            ReviewDecision.Approve, null, latest.Version));
        Assert.Single(await Store.ListReviewsAsync(task.Id));
        Assert.Equal(Status.AwaitingReview, (await Store.GetTaskAsync(task.Id))!.Status);
    }
    [Fact] public async Task Repeated_retry_with_old_version_does_not_create_duplicate_work()
    {
        var (task, _) = await Completed(false);
        await Store.RetryAsync(task.Id, "Fix tests", task.Version);
        await Assert.ThrowsAsync<ConflictException>(() => Store.RetryAsync(task.Id, "Fix tests", task.Version));
        Assert.Single(await Store.ListRunsAsync(task.Id));
        Assert.Equal(Status.Queued, (await Store.GetTaskAsync(task.Id))!.Status);
    }
    [Fact] public void Existing_task_json_remains_readable_after_adding_revision_fields()
    {
        var json = $$"""
            {"Id":"{{Guid.NewGuid()}}","ProjectId":"{{Guid.NewGuid()}}","Title":"API","Description":"Build",
             "AcceptanceCriteria":"Tests","Status":4,"Plan":{"Text":"TDD"},"Version":4,"CreatedAt":"2026-10-13T00:00:00Z"}
            """;
        var task = System.Text.Json.JsonSerializer.Deserialize<WorkTask>(json)!;
        Assert.Equal(Status.AwaitingReview, task.Status);
        Assert.Null(task.RevisionCommit);
        Assert.Null(task.RevisionInstructions);
    }
}
