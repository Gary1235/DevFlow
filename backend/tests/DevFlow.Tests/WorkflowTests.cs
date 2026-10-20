using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Tests;

public sealed class WorkflowTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-workflow-" + Guid.NewGuid());
    private SqliteTaskStore store = null!;
    private ProjectDefinition project = null!;
    private readonly WorkspaceFixture workspace = new();
    private readonly AgentFixture agent = new();
    public async Task InitializeAsync()
    {
        store = new(Path.Combine(root, "tasks.db")); await store.InitializeAsync();
        project = new(Guid.NewGuid(), "Demo", root, "main", ["test"]); await store.AddProjectAsync(project);
    }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }
    private static WorkflowStep Step(string name, string kind = "Implementation", string provider = "codex", bool pause = false) =>
        new(Guid.NewGuid(), name, kind, new(provider, name + "-model"), "Only " + name, WaitForApproval: pause);
    private async Task<WorkTask> Add(params WorkflowStep[] steps)
    {
        var task = WorkTask.Create(project.Id, "Feature", "Build", "Pass tests").SetPlan("Plan").ApprovePlan() with
        { Policy = new(false), Workflow = WorkflowDefinition.Normalize(new(Steps: steps)) };
        await store.AddTaskAsync(task); return task;
    }
    private ExecutionCoordinator Coordinator(IPlanningRunner? reviewer = null, IValidationRunner? validator = null, IExecutionPolicyGuard? guard = null) =>
        new(store, agent, workspace, validator ?? new ValidatorFixture(), reviewer, new DiffFixture(), guard);

    [Fact] public async Task Custom_order_and_models_share_previous_commits_and_preserve_original_diff_base()
    {
        var task = await Add(Step("backend", provider: "claude"), Step("frontend", provider: "gemini"), Step("tests", "Validation"));
        await Coordinator().ExecuteNextAsync();
        Assert.Equal(new[] { "claude", "gemini" }, agent.Calls.Select(c => c.Task.Ai!.Implementation!.Provider));
        Assert.Equal(new[] { "base", "commit1" }, agent.Calls.Select(c => c.Workspace.BaseCommit));
        Assert.Contains("backend", agent.Calls[1].Task.TaskRules);
        var current = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Status.AwaitingReview, current.Status); Assert.Equal(3, current.StepResults!.Length);
        var run = Assert.Single(await store.ListRunsAsync(task.Id));
        Assert.Equal("base", run.BaseCommit); Assert.Equal("commit2", run.ResultCommit); Assert.Equal(3, run.Steps!.Length);
    }
    [Fact] public async Task Confirmation_and_worker_restart_resume_without_repeating_completed_steps()
    {
        var first = Step("backend", pause: true); var second = Step("frontend", provider: "gemini");
        var task = await Add(first, second);
        await Coordinator().ExecuteNextAsync();
        var paused = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Status.AwaitingStepApproval, paused.Status); Assert.Single(agent.Calls);
        await store.RecoverInterruptedAsync();
        Assert.False(await Coordinator().ExecuteNextAsync());
        var extra = Step("extra");
        var flow = paused.Workflow! with { Steps = [paused.Workflow!.Steps![0], extra, paused.Workflow.Steps[1]] };
        await Assert.ThrowsAsync<ConflictException>(() => store.SetWorkflowAsync(task.Id, flow with { Steps = [extra, first, second] }, paused.Version));
        var updated = await store.SetWorkflowAsync(task.Id, flow, paused.Version);
        await Assert.ThrowsAsync<ConflictException>(() => store.ContinueWorkflowAsync(task.Id, paused.Version));
        await store.ContinueWorkflowAsync(task.Id, updated.Version);
        await Coordinator().ExecuteNextAsync();
        Assert.Equal("commit1", workspace.Sources.Last());
        Assert.Equal(new[] { "Feature · backend", "Feature · extra", "Feature · frontend" }, agent.Calls.Select(c => c.Task.Title));
        var runs = await store.ListRunsAsync(task.Id);
        Assert.Equal(2, runs.Count); Assert.All(runs, r => Assert.Equal("base", r.BaseCommit));
        Assert.Equal("commit3", runs.Last().ResultCommit);
    }
    [Fact] public async Task Failed_step_retry_uses_last_completed_commit_and_does_not_repeat_backend()
    {
        var task = await Add(Step("backend"), Step("frontend", provider: "gemini"));
        agent.FailName = "frontend";
        await Coordinator().ExecuteNextAsync();
        var failed = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Status.Failed, failed.Status); Assert.Single(failed.StepResults!);
        var run = Assert.Single(await store.ListRunsAsync(task.Id));
        Assert.Equal("Failed", run.Steps!.Last().State); Assert.Equal("commit1", run.ResultCommit);
        agent.FailName = null;
        await store.RetryAsync(task.Id, "Retry frontend", failed.Version);
        await Coordinator().ExecuteNextAsync();
        Assert.Equal("commit1", workspace.Sources.Last());
        Assert.Equal(1, agent.Calls.Count(c => c.Task.Title.EndsWith("backend")));
        Assert.Equal(Status.AwaitingReview, (await store.GetTaskAsync(task.Id))!.Status);
    }
    [Fact] public async Task Full_restart_repeats_completed_steps_from_original_base_and_keeps_prior_runs()
    {
        var task = await Add(Step("backend"), Step("frontend"));
        agent.FailName = "frontend";
        await Coordinator().ExecuteNextAsync();
        var failed = (await store.GetTaskAsync(task.Id))!;
        Assert.Single(failed.StepResults!);
        var preview = RetryStrategy.Preview(failed, "Restart");
        var retried = await store.RetryAsync(task.Id, "Redo all steps", failed.Version, scope: "Restart");
        Assert.Empty(retried.StepResults!);
        Assert.Equal(preview.BaseCommit, retried.RevisionCommit);
        agent.FailName = null;
        await Coordinator().ExecuteNextAsync();
        Assert.Equal("base", workspace.Sources.Last());
        Assert.Equal(2, agent.Calls.Count(c => c.Task.Title.EndsWith("backend")));
        var runs = await store.ListRunsAsync(task.Id);
        Assert.Equal(2, runs.Count);
        Assert.Equal("Failed", runs[0].Status);
        Assert.Equal(Status.AwaitingReview, (await store.GetTaskAsync(task.Id))!.Status);
    }
    [Fact] public async Task Confirming_last_step_keeps_the_original_run_and_its_validation_for_final_review()
    {
        var task = await Add(Step("backend", pause: true));
        await Coordinator().ExecuteNextAsync();
        var paused = (await store.GetTaskAsync(task.Id))!;
        var continued = await store.ContinueWorkflowAsync(task.Id, paused.Version);
        Assert.Equal(Status.AwaitingReview, continued.Status);
        Assert.False(await Coordinator().ExecuteNextAsync());
        var run = Assert.Single(await store.ListRunsAsync(task.Id));
        Assert.Equal("AwaitingReview", run.Status); Assert.Single(run.Steps!);
        await store.ReviewAsync(task.Id, run.Id, run.ResultCommit!, ReviewDecision.Approve, "Inspected", continued.Version);
        Assert.Equal(Status.Approved, (await store.GetTaskAsync(task.Id))!.Status);
    }
    [Fact] public async Task Ai_review_failure_blocks_later_work_and_records_the_exact_reviewed_commit()
    {
        var task = await Add(Step("backend"), Step("review", "Review", "claude"), Step("frontend"));
        await Coordinator(new ReviewerFixture()).ExecuteNextAsync();
        Assert.Single(agent.Calls);
        var run = Assert.Single(await store.ListRunsAsync(task.Id));
        Assert.Equal("commit1", Assert.Single(run.AiReviews!).ResultCommit);
        Assert.Equal(Status.Failed, (await store.GetTaskAsync(task.Id))!.Status);
        Assert.Contains("missing contract", run.Error);
    }
    [Fact] public async Task Active_step_is_locked_and_pending_step_edits_are_seen_by_executor()
    {
        var task = await Add(Step("backend"), Step("frontend"));
        agent.OnExecute = async () =>
        {
            if (agent.Calls.Count != 1) return;
            var running = (await store.GetTaskAsync(task.Id))!;
            Assert.Equal(running.Workflow!.Steps![0].Id, running.ActiveStepId);
            var modified = running.Workflow with { Steps = [running.Workflow.Steps[0] with { Name = "wrong" }, running.Workflow.Steps[1]] };
            await Assert.ThrowsAsync<ConflictException>(() => store.SetWorkflowAsync(task.Id, modified, running.Version));
            await store.SetWorkflowAsync(task.Id, running.Workflow with { Steps = [running.Workflow.Steps[0], running.Workflow.Steps[1] with { Name = "changed" }] }, running.Version);
        };
        await Coordinator().ExecuteNextAsync();
        Assert.Equal("Feature · changed", agent.Calls.Last().Task.Title);
    }
    [Fact] public async Task Per_step_scope_is_checked_relative_to_previous_step_and_global_scope_is_still_enforced()
    {
        var task = await Add(Step("backend") with { AllowedPaths = ["backend/"] }, Step("frontend") with { AllowedPaths = ["frontend/"] });
        var queued = (await store.GetTaskAsync(task.Id))!;
        await store.SaveTaskAsync(queued with { Policy = new(false, ["backend/", "frontend/"]), Version = queued.Version + 1 }, queued.Version);
        var guard = new GuardFixture(); await Coordinator(guard: guard).ExecuteNextAsync();
        Assert.Equal(new[] { "base", "base", "commit1", "commit1" }, guard.Calls.Select(c => c.BaseCommit));
        Assert.Equal(Status.AwaitingReview, (await store.GetTaskAsync(task.Id))!.Status);
    }
    [Fact] public async Task Templates_persist_across_store_instances_and_normalization_rejects_invalid_workflows()
    {
        var step = Step("backend");
        await store.SaveWorkflowTemplateAsync(new("API first", new(Steps: [step])));
        var reopened = new SqliteTaskStore(store.DatabasePath);
        Assert.Equal(step.Id, Assert.Single(await reopened.ListWorkflowTemplatesAsync()).Workflow.Steps![0].Id);
        Assert.Throws<ArgumentException>(() => WorkflowDefinition.Normalize(new(Steps: [step, step])));
        Assert.Throws<ArgumentException>(() => WorkflowDefinition.Normalize(new(Steps: [step with { Kind = "Unknown" }])));
        Assert.Throws<ArgumentException>(() => WorkflowDefinition.Normalize(new(Steps: [step with { Kind = "Approval" }])));
        Assert.Throws<ArgumentException>(() => WorkflowDefinition.Normalize(new(Steps: [step with { AllowedPaths = ["../escape"] }])));
    }
    [Fact] public async Task Adding_pending_work_during_completion_queues_continuation_instead_of_skipping_it()
    {
        var task = await Add(Step("backend"));
        var claim = (await store.ClaimNextAsync())!;
        await store.SetWorkspaceAsync(claim.Run.Id, new("repo", "base"));
        var running = await store.BeginWorkflowStepAsync(claim.Run.Id);
        var first = running.Workflow!.Steps![0];
        await store.RecordWorkflowStepAsync(claim.Run.Id, new(first.Id, first.Name, first.Kind, "Completed", claim.Run.Id,
            "commit1", "done", first.Selection, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var after = (await store.GetTaskAsync(task.Id))!;
        await store.SetWorkflowAsync(task.Id, after.Workflow! with { Steps = [first, Step("frontend")] }, after.Version);
        await store.CompleteRunAsync(claim.Run.Id, true, "repo", "base", "commit1", null);
        Assert.Equal(Status.Queued, (await store.GetTaskAsync(task.Id))!.Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Custom_steps_keep_tdd_red_and_green_test_requirements(bool validRed)
    {
        var task = await Add(Step("backend"));
        await store.SaveTaskAsync(task with { Policy = new(true), Version = task.Version + 1 }, task.Version);
        await Coordinator(validator: new TddValidatorFixture(validRed)).ExecuteNextAsync();
        var current = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(validRed ? Status.AwaitingReview : Status.Failed, current.Status);
        Assert.Equal(validRed ? 3 : 1, agent.Calls.Count);
    }
    [Fact] public async Task Planner_supervision_auto_replies_within_rules_and_pauses_for_user_when_exceeding_rules()
    {
        var baseTask = await Add(Step("backend", provider: "claude"), Step("frontend", provider: "gemini"));
        await store.SaveTaskAsync(baseTask with { Ai = new(new("codex", "planner-model"), new("claude", "impl-model")), Version = baseTask.Version + 1 }, baseTask.Version);
        var plannerResponses = new Queue<string>([
            "{\"verdict\":\"ResolvedWithinRules\",\"summary\":\"請依既有規則補上 ProblemDetails 回應格式\"}",
            "{\"verdict\":\"NeedsUserConfirmation\",\"summary\":\"訂單金額超過上限時要阻擋還是需要主管簽核？規則庫未定義\"}",
            "{\"verdict\":\"Pass\",\"summary\":\"已依使用者確認完成前端實作\"}"
        ]);
        var coordinator = Coordinator(new QueuedReviewerFixture(plannerResponses));
        await coordinator.ExecuteNextAsync();
        var pausedTask = (await store.GetTaskAsync(baseTask.Id))!;
        Assert.Equal(Status.AwaitingStepApproval, pausedTask.Status);
        Assert.Equal(3, agent.Calls.Count);
        Assert.Contains("請依既有規則補上 ProblemDetails 回應格式", agent.Calls[1].Task.TaskRules);
        Assert.StartsWith("⚠️ 超出規則，需你確認：", pausedTask.StepResults!.Last().Summary);

        await store.ContinueWorkflowAsync(baseTask.Id, pausedTask.Version, instructions: "超過上限直接阻擋並顯示提示訊息");
        await coordinator.ExecuteNextAsync();
        var completedTask = (await store.GetTaskAsync(baseTask.Id))!;
        Assert.Equal(Status.AwaitingReview, completedTask.Status);
        Assert.Equal(4, agent.Calls.Count);
        Assert.Contains("超過上限直接阻擋並顯示提示訊息", agent.Calls.Last().Task.TaskRules);
    }
    private sealed class AgentFixture : IAgentRunner
    {
        public List<(WorkTask Task, Workspace Workspace)> Calls = [];
        public string? FailName; public Func<Task>? OnExecute;
        public async Task ExecuteAsync(WorkTask task, Workspace w, Guid run, TddPhase phase, CancellationToken ct)
        { Calls.Add((task, w)); if (OnExecute != null) await OnExecute(); if (FailName != null && task.Title.EndsWith(FailName)) throw new InvalidOperationException("fixture failed"); }
    }
    private sealed class WorkspaceFixture : IWorkspaceManager
    {
        public List<string> Sources = []; private int snapshots;
        public Task<Workspace> CreateAsync(ProjectDefinition p, Guid run, CancellationToken ct)
        { Sources.Add(p.BaseBranch); return Task.FromResult(new Workspace("worktree", p.BaseBranch == "main" ? "base" : p.BaseBranch)); }
        public Task<string> SnapshotAsync(Workspace w, CancellationToken ct) => Task.FromResult("commit" + ++snapshots);
        public Task AssertUnchangedAsync(Workspace w, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class ValidatorFixture : IValidationRunner
    { public Task<ValidationEvidence> ValidateAsync(ProjectDefinition p, Workspace w, Guid run, TddPhase phase, CancellationToken ct) => Task.FromResult(new ValidationEvidence(0, 1, 0, ["test"], [])); }
    private sealed class TddValidatorFixture(bool validRed) : IValidationRunner
    { public Task<ValidationEvidence> ValidateAsync(ProjectDefinition p, Workspace w, Guid run, TddPhase phase, CancellationToken ct) =>
        Task.FromResult(phase == TddPhase.Red && validRed ? new ValidationEvidence(1, 1, 1, [], ["feature"]) : new ValidationEvidence(0, 2, 0, ["test", "feature"], [])); }
    private sealed class DiffFixture : IResultDiffReader
    { public Task<ResultDiff> ReadAsync(ProjectDefinition p, TaskRun run, CancellationToken ct) => Task.FromResult(new ResultDiff(run.Id, run.BaseCommit!, run.ResultCommit!, "+code")); }
    private sealed class ReviewerFixture : IPlanningRunner
    { public Task<string> PlanAsync(WorkTask t, Workspace w, Guid run, CancellationToken ct) => Task.FromResult("{\"verdict\":\"NeedsChanges\",\"summary\":\"missing contract\"}"); }
    private sealed class QueuedReviewerFixture(Queue<string> responses) : IPlanningRunner
    { public Task<string> PlanAsync(WorkTask t, Workspace w, Guid run, CancellationToken ct) => Task.FromResult(responses.Dequeue()); }
    private sealed class GuardFixture : IExecutionPolicyGuard
    { public List<Workspace> Calls = []; public Task CheckAsync(Workspace w, ExecutionPolicy p, CancellationToken ct) { Calls.Add(w); return Task.CompletedTask; } }
}
