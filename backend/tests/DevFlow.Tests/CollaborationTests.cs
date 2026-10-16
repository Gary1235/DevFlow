using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Tests;

public sealed class CollaborationTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-collaboration-" + Guid.NewGuid());
    private SqliteTaskStore store = null!;
    private ProjectDefinition project = null!;
    public async Task InitializeAsync()
    {
        store = new(Path.Combine(directory, "tasks.db")); await store.InitializeAsync();
        project = new(Guid.NewGuid(), "Demo", directory, "main", ["test"]); await store.AddProjectAsync(project);
    }
    public Task DisposeAsync() { Directory.Delete(directory, true); return Task.CompletedTask; }

    [Fact] public async Task Rule_edits_and_archival_preserve_existing_task_snapshots()
    {
        var rule = await store.SaveRuleAsync(null, "Compatibility", "Keep the API shape.", ["Planning", "Acceptance"], 0, false, default);
        var task = WorkTask.Create(project.Id, "Feature", "Implement", "Compatible") with { Rules = await store.ResolveRulesAsync([rule.Id], default) };
        await store.AddTaskAsync(task);
        await store.SaveRuleAsync(rule.Id, rule.Name, "New instructions", rule.Stages, rule.Version, true, default);
        Assert.Equal("Keep the API shape.", (await store.GetTaskAsync(task.Id))!.Rules!.Single().Content);
        await Assert.ThrowsAsync<ConflictException>(() => store.SaveRuleAsync(rule.Id, rule.Name, "Stale edit", rule.Stages, rule.Version, false, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ResolveRulesAsync([rule.Id], default));
    }

    [Fact] public async Task Failed_discussion_retains_message_and_conversion_is_idempotent()
    {
        var d = new Discussion(Guid.NewGuid(), project.Id, "Idea", new("gemini", "custom-model"), [], [], [], 0, DateTimeOffset.UtcNow);
        await store.AddDiscussionAsync(d, default);
        var operation = await store.BeginDiscussionAsync(d.Id, 0, "Discuss my idea", default);
        await Assert.ThrowsAsync<ConflictException>(() => store.BeginDiscussionAsync(d.Id, 0, "Duplicate", default));
        var failed = await store.FinishDiscussionAsync(d.Id, operation.ActiveOperation!.Value, null, null, null, "CLI failed", default);
        Assert.Single(failed.Messages); Assert.Equal("Discuss my idea", failed.Messages[0].Content);
        var retry = await store.BeginDiscussionAsync(d.Id, failed.Version, null, default);
        var draft = new DiscussionDraft(Guid.NewGuid(), "Feature", "Build", "Tests pass");
        await store.FinishDiscussionAsync(d.Id, retry.ActiveOperation!.Value, "Confirmed", "base", [draft], null, default);
        var task = WorkTask.Create(project.Id, draft.Title, draft.Description, draft.AcceptanceCriteria) with { SourceDiscussionId = d.Id, SourceDraftId = draft.Id };
        var first = await store.AddDiscussionTaskAsync(task, default);
        var second = await store.AddDiscussionTaskAsync(task with { Id = Guid.NewGuid() }, default);
        Assert.Equal(first.Id, second.Id); Assert.Single(await store.ListTasksAsync());
        Assert.Equal(Status.AwaitingPlan, first.Status);
        Assert.Equal(first.Id, (await store.GetDiscussionAsync(d.Id, default))!.Drafts.Single().CreatedTaskId);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:/outside")]
    [InlineData("/absolute")]
    [InlineData("src/**/*.cs")]
    public void Edit_scope_rejects_ambiguous_or_escaping_paths(string path) =>
        Assert.Throws<ArgumentException>(() => ExecutionPolicy.Normalize(new(AllowedPaths: [path]), []));

    [Fact] public void Selected_tdd_rule_cannot_be_silently_disabled()
    {
        var rule = new RuleSnapshot(Guid.NewGuid(), "TDD", "Use TDD", ["Implementation"], 1, true);
        Assert.Throws<ArgumentException>(() => ExecutionPolicy.Normalize(new(false), [rule]));
    }

    [Fact] public async Task Edit_scope_checks_untracked_files_and_directory_boundaries()
    {
        var guard = new GitExecutionPolicyGuard(new ScopeProcess("src/allowed/file.cs\0", "src/allowed-other/new.cs\0"));
        await Assert.ThrowsAsync<ConflictException>(() => guard.CheckAsync(new("repo", "base"), new(AllowedPaths: ["src/allowed/"]), default));
        await new GitExecutionPolicyGuard(new ScopeProcess("src/allowed/file.cs\0", "tests/new.cs\0"))
            .CheckAsync(new("repo", "base"), new(AllowedPaths: ["src/allowed/", "tests/"]), default);
    }

    [Fact] public async Task Ai_corrections_are_bounded_and_final_review_keeps_the_complete_diff_base()
    {
        var task = WorkTask.Create(project.Id, "Feature", "Build", "Tests pass").SetPlan("Approved plan").ApprovePlan() with
        { Policy = new(false), Workflow = new(Reviewers: [new("codex", "model-a"), new("gemini", "model-b")], MaxReviewRetries: 1) };
        await store.AddTaskAsync(task);
        var reviewer = new Reviewer(); var workspace = new TestWorkspace(); var diffs = new TestDiff();
        var coordinator = new ExecutionCoordinator(store, new Agent(), workspace, new Validator(), reviewer, diffs);
        await coordinator.ExecuteNextAsync();
        var correction = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Status.Queued, correction.Status); Assert.Equal(1, correction.AutoReviewAttempt); Assert.Equal("result1", correction.RevisionCommit);
        await coordinator.ExecuteNextAsync();
        var final = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Status.AwaitingReview, final.Status); Assert.False(await coordinator.ExecuteNextAsync());
        var runs = await store.ListRunsAsync(task.Id);
        Assert.Equal(2, runs.Count); Assert.All(runs, r => Assert.Equal("base", r.BaseCommit));
        Assert.All(runs, r => Assert.Equal(2, r.AiReviews!.Length));
        Assert.Equal("result1", workspace.LastSource); Assert.Equal(4, reviewer.Calls);
        Assert.Equal("NeedsChanges", runs[1].AiReviews![1].Verdict);
        await store.ReviewAsync(task.Id, runs[1].Id, "result2", ReviewDecision.Approve, "Inspected the findings", final.Version);
        Assert.Equal(Status.Approved, (await store.GetTaskAsync(task.Id))!.Status);
    }
    private sealed class ScopeProcess(string tracked, string untracked) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string w, string? input, CancellationToken ct) =>
            Task.FromResult(new ProcessResult(0, a[0] == "diff" ? tracked : untracked, ""));
    }
    private sealed class Agent : IAgentRunner
    { public Task ExecuteAsync(WorkTask t, Workspace w, Guid r, TddPhase p, CancellationToken ct) { Assert.Equal(TddPhase.Implementation, p); return Task.CompletedTask; } }
    private sealed class Validator : IValidationRunner
    { public Task<ValidationEvidence> ValidateAsync(ProjectDefinition p, Workspace w, Guid r, TddPhase phase, CancellationToken ct) => Task.FromResult(new ValidationEvidence(0, 1, 0, ["feature-test"], [])); }
    private sealed class TestWorkspace : IWorkspaceManager
    {
        private int snapshots; public string? LastSource;
        public Task<Workspace> CreateAsync(ProjectDefinition p, Guid r, CancellationToken ct) { LastSource = p.BaseBranch; return Task.FromResult(new Workspace("repo", snapshots == 0 ? "base" : "result1")); }
        public Task<string> SnapshotAsync(Workspace w, CancellationToken ct) => Task.FromResult("result" + ++snapshots);
        public Task AssertUnchangedAsync(Workspace w, CancellationToken ct) { Assert.StartsWith("result", w.BaseCommit); return Task.CompletedTask; }
    }
    private sealed class TestDiff : IResultDiffReader
    { public Task<ResultDiff> ReadAsync(ProjectDefinition p, TaskRun r, CancellationToken ct) { Assert.Equal("base", r.BaseCommit); return Task.FromResult(new ResultDiff(r.Id, r.BaseCommit!, r.ResultCommit!, "+implementation")); } }
    private sealed class Reviewer : IPlanningRunner
    {
        public int Calls;
        public Task<string> PlanAsync(WorkTask t, Workspace w, Guid r, CancellationToken ct)
        {
            Calls++; Assert.Contains("Independent validation evidence", t.ReadOnlyInstruction); Assert.Contains("feature-test", t.ReadOnlyInstruction);
            Assert.Equal("Acceptance-" + (Calls % 2 == 1 ? "1" : "2"), t.ArtifactStage);
            return Task.FromResult(Calls % 2 == 1 ? "{\"verdict\":\"Pass\",\"summary\":\"Requirements satisfied\"}" : "{\"verdict\":\"NeedsChanges\",\"summary\":\"Fix the edge case in file.cs\"}");
        }
    }
}
