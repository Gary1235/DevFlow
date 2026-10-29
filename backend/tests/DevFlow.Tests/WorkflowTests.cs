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

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Simple_workflow_uses_the_supervisor_for_build_repairs_and_pauses_only_when_needed(bool needsUser)
    {
        var task=WorkTask.Create(project.Id,"Simple task","Implement","Build passes").SetPlan("Fix source and build").ApprovePlan() with{
            Policy=new(false,[]),Ai=new(new("codex","manager"),new("antigravity","implementer")),
            Workflow=WorkflowDefinition.Normalize(new(Steps:[Step("Implementation",provider:"antigravity")],SimpleMode:true))};
        await store.AddTaskAsync(task);
        var supervisor=new QueuedReviewerFixture(new Queue<string>(needsUser?
            ["{\"verdict\":\"NeedsUserConfirmation\",\"summary\":\"需求未定義，需要你選擇。\"}"]:
            ["{\"verdict\":\"ResolvedWithinRules\",\"summary\":\"修正編譯錯誤，保留需求與測試。\"}","{\"verdict\":\"Pass\",\"summary\":\"符合計畫。\"}"]));
        var validator=new SimpleValidationFixture();
        var coordinator=new ExecutionCoordinator(store,agent,workspace,validator,supervisor,new DiffFixture());
        await coordinator.ExecuteNextAsync();
        var updated=(await store.GetTaskAsync(task.Id))!;
        Assert.Equal(needsUser?Status.AwaitingStepApproval:Status.AwaitingReview,updated.Status);
        Assert.Equal(needsUser?1:2,agent.Calls.Count);
        if(!needsUser)Assert.Contains("主管",agent.Calls.Last().Task.TaskRules);
        else Assert.True(updated.StepResults!.Last().RequiresRework);
    }

    [Fact] public async Task All_saved_project_profiles_are_validated_without_task_level_configuration()
    {
        var validation=new ProfileValidationFixture();
        var profiles=new[]{new ValidationProfile("front","Frontend","frontend","npm",["run","build"]),new("back","Backend","backend","dotnet",["build"])};
        var result=await new AllProjectValidationRunner(validation).ValidateAsync(project with{ValidationProfiles=profiles},new("worktree","base"),Guid.NewGuid(),TddPhase.Implementation,default);
        Assert.Equal(new[]{"front","back"},validation.Calls);Assert.Equal(1,result.ExitCode);Assert.Contains("Backend",result.Error);
    }

    private sealed class ProfileValidationFixture:IValidationRunner {
        public List<string?> Calls=[];
        public Task<ValidationEvidence> ValidateAsync(ProjectDefinition p,Workspace w,Guid run,TddPhase phase,CancellationToken ct){
            Calls.Add(p.DefaultValidationProfileId);return Task.FromResult(new ValidationEvidence(p.DefaultValidationProfileId=="back"?1:0,0,0,[],[],"Command","compiler error","CommandFailed"));
        }
    }
    [Fact] public async Task Nested_supervision_retries_keep_distinct_validation_artifacts()
    {
        var inner=new AttemptTrackingValidation();var runner=new AllProjectValidationRunner(inner);
        var workspace=new Workspace("worktree","base");var run=Guid.NewGuid();
        await runner.ValidateAsync(project,workspace,run,TddPhase.Implementation,default);
        var guided=Assert.IsType<AllProjectValidationRunner>(runner.ForAttempt(10));
        await guided.ValidateAsync(project,workspace,run,TddPhase.Implementation,default);
        await guided.ForAttempt(1).ValidateAsync(project,workspace,run,TddPhase.Implementation,default);
        Assert.Equal(new[]{1,2,3},inner.Attempts);
    }
    private sealed class AttemptTrackingValidation:IValidationRunner,IValidationAttemptRunner {
        public List<int> Attempts=[];
        public IValidationRunner ForAttempt(int value){Attempts.Add(value);return this;}
        public Task<ValidationEvidence> ValidateAsync(ProjectDefinition p,Workspace w,Guid run,TddPhase phase,CancellationToken ct)=>Task.FromResult(new ValidationEvidence(0,0,0,[],[],"Command"));
    }
    [Fact] public async Task Simple_workflow_stops_repeated_identical_validation_errors_and_preserves_a_resumable_checkpoint()
    {
        var task=WorkTask.Create(project.Id,"Simple task","Implement","Build passes").SetPlan("Fix source and build").ApprovePlan() with{
            Policy=new(false,[]),Ai=new(new("codex","manager"),new("antigravity","implementer")),
            Workflow=WorkflowDefinition.Normalize(new(Steps:[Step("Implementation",provider:"antigravity")],SimpleMode:true))};
        await store.AddTaskAsync(task);
        var answers=new Queue<string>(Enumerable.Repeat("{\"verdict\":\"ResolvedWithinRules\",\"summary\":\"修正編譯錯誤\"}",2));
        await new ExecutionCoordinator(store,agent,workspace,new SimpleValidationFixture(true),new QueuedReviewerFixture(answers),new DiffFixture()).ExecuteNextAsync();
        var paused=(await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Status.AwaitingStepApproval,paused.Status);Assert.Equal(3,agent.Calls.Count);
        var checkpoint=Assert.Single(paused.StepResults!);Assert.True(checkpoint.RequiresRework);Assert.Contains("連續三次",checkpoint.Summary);
        var continued=await store.ContinueWorkflowAsync(task.Id,paused.Version,instructions:"重新檢查編譯錯誤來源");
        Assert.Equal(Status.Queued,continued.Status);Assert.Empty(continued.StepResults!);Assert.Equal(checkpoint.ResultCommit,continued.ResumeCommit);
    }

    [Fact] public async Task Simple_optional_review_routes_changes_through_the_supervisor_then_revalidates_and_reviews_again()
    {
        var task=WorkTask.Create(project.Id,"Simple task","Implement","Build passes").SetPlan("Fix source and build").ApprovePlan() with{
            Policy=new(false,[]),Ai=new(new("codex","manager"),new("antigravity","implementer")),
            Workflow=WorkflowDefinition.Normalize(new(Steps:[Step("Implementation",provider:"antigravity"),Step("Review","Review","claude")],SimpleMode:true))};
        await store.AddTaskAsync(task);
        var answers=new Queue<string>(["{\"verdict\":\"Pass\",\"summary\":\"主管確認初版\"}","{\"verdict\":\"NeedsChanges\",\"summary\":\"補齊空狀態\"}",
            "{\"verdict\":\"ResolvedWithinRules\",\"summary\":\"依原計畫補齊空狀態\"}","{\"verdict\":\"Pass\",\"summary\":\"驗收通過\"}"]);
        await Coordinator(new QueuedReviewerFixture(answers)).ExecuteNextAsync();
        Assert.Equal(Status.AwaitingReview,(await store.GetTaskAsync(task.Id))!.Status);Assert.Equal(2,agent.Calls.Count);
        Assert.Equal("manager",agent.Calls.Last().Task.Ai!.Planning!.Model);Assert.Contains("主管指示",agent.Calls.Last().Task.TaskRules);
        Assert.Equal(2,Assert.Single(await store.ListRunsAsync(task.Id)).AiReviews!.Length);Assert.Empty(answers);
    }

    private sealed class SimpleValidationFixture(bool alwaysFail=false):IValidationRunner {
        private int calls;
        public Task<ValidationEvidence> ValidateAsync(ProjectDefinition p,Workspace w,Guid run,TddPhase phase,CancellationToken ct){
            calls++;var fail=calls==2||alwaysFail&&calls>1;return Task.FromResult(new ValidationEvidence(fail?1:0,0,0,[],[],"Command",fail?"compiler error":null,fail?"CommandFailed":null));
        }
    }
    [Fact] public async Task Delegated_review_corrections_upgrade_the_implementation_model_and_preserve_the_reviewer()
    {
        var implementation=Step("Implementation",provider:"antigravity");var review=Step("Review","Review","claude");
        var task=WorkTask.Create(project.Id,"Review repair","Implement","Build passes").SetPlan("Implement and independently review").ApprovePlan() with{
            Policy=new(false,[]),Ai=new(new("codex","manager"),implementation.Selection),
            Workflow=WorkflowDefinition.Normalize(new(Steps:[implementation,review],SimpleMode:true)),
            Delegation=new([new(implementation.Selection!),new(review.Selection!),new(new("codex","strong"))],
                [new("Implementation","Implement",implementation.Selection!,"Implement"),new("Review","Review",review.Selection!,"Review",Kind:"Review")])};
        await store.AddTaskAsync(task);
        var answers=new Queue<string>(["{\"verdict\":\"Pass\",\"summary\":\"主管確認\"}","{\"verdict\":\"NeedsChanges\",\"summary\":\"需要修正\"}",
            "{\"verdict\":\"ResolvedWithinRules\",\"summary\":\"升級實作模型修正\",\"implementation\":{\"provider\":\"codex\",\"model\":\"strong\"}}","{\"verdict\":\"Pass\",\"summary\":\"驗收通過\"}"]);
        await Coordinator(new QueuedReviewerFixture(answers)).ExecuteNextAsync();
        var result=(await store.GetTaskAsync(task.Id))!;Assert.Equal(Status.AwaitingReview,result.Status);
        Assert.Equal("strong",agent.Calls.Last().Task.Ai!.Implementation!.Model);Assert.Equal("claude",result.Workflow!.Steps![1].Selection!.Provider);Assert.Equal("strong",result.Workflow.Steps[0].Selection!.Model);
    }
    [Fact] public async Task Worker_images_reach_both_implementation_and_independent_review_without_persisting_binary_data()
    {
        var task=await Add(Step("Implementation"),Step("Review","Review","claude"));
        var loader=new ImageLoaderFixture();var reviewer=new ImageReviewerFixture();
        await new ExecutionCoordinator(store,agent,workspace,new ValidatorFixture(),reviewer,new DiffFixture(),images:loader).ExecuteNextAsync();
        Assert.Equal(loader.Image,Assert.Single(Assert.Single(agent.Calls).Task.InputImages!));
        Assert.Equal(loader.Image,Assert.Single(reviewer.Task!.InputImages!));Assert.Null((await store.GetTaskAsync(task.Id))!.InputImages);
    }
    private sealed class ImageLoaderFixture:ITaskImageLoader{
        public PromptImage Image=new("reference.png","image/png","test-image-payload");
        public Task<WorkTask> LoadAsync(WorkTask task,CancellationToken ct)=>Task.FromResult(task with{InputImages=[Image]});
    }
    private sealed class ImageReviewerFixture:IPlanningRunner{
        public WorkTask? Task;
        public Task<string> PlanAsync(WorkTask task,Workspace workspace,Guid runId,CancellationToken ct){Task=task;return System.Threading.Tasks.Task.FromResult("{\"verdict\":\"Pass\",\"summary\":\"符合圖片需求\"}");}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Integration_review_failure_is_never_reported_as_passed_and_simple_flow_repairs_it(bool repair)
    {
        var target=new string('d',40);
        var task=WorkTask.Create(project.Id,"Integration","Preserve both sides","Build passes").SetPlan("Integrate and review").ApprovePlan() with{
            Policy=new(false,[]),Ai=new(new("codex","manager"),new("codex","implementer")),Workflow=new(false,SimpleMode:repair),
            Plan=new("Integrate and review",1,target),Integration=new(Guid.NewGuid(),Guid.NewGuid(),new string('c',40),target,["legacy-dotnet-test"],[new("claude","reviewer")])};
        await store.AddTaskAsync(task);
        var answers=new Queue<string>(repair?["{\"verdict\":\"NeedsChanges\",\"summary\":\"補齊整合處理\"}","{\"verdict\":\"ResolvedWithinRules\",\"summary\":\"依原需求修正整合\"}","{\"verdict\":\"Pass\",\"summary\":\"完整通過\"}"]:["{\"verdict\":\"NeedsChanges\",\"summary\":\"補齊整合處理\"}"]);
        await Coordinator(new QueuedReviewerFixture(answers)).ExecuteNextAsync();
        var updated=(await store.GetTaskAsync(task.Id))!;Assert.Equal(repair?Status.AwaitingReview:Status.Failed,updated.Status);Assert.Equal(repair?2:1,agent.Calls.Count);
        Assert.Equal(repair?"Pass":"NeedsChanges",(await store.ListRunsAsync(task.Id)).Single().AiReviews!.Last().Verdict);Assert.Empty(answers);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Main_ai_can_upgrade_automatic_work_but_cannot_override_an_explicit_provider(bool pinned)
    {
        var assignment=new AgentAssignment("Implementation","修正畫面",new("antigravity","fast"),"依能力分配",pinned);
        var task=WorkTask.Create(project.Id,"Auto task","Implement","Build passes").SetPlan("Fix source and build").ApprovePlan() with{
            Policy=new(false,[]),Ai=new(new("codex","manager"),assignment.Selection),
            Workflow=WorkflowDefinition.Normalize(new(Steps:[new(Guid.NewGuid(),"Implementation",Selection:assignment.Selection)],SimpleMode:true)),
            Delegation=new([new(new("antigravity","fast")),new(new("codex","strong"))],[assignment])};
        await store.AddTaskAsync(task);
        var responses=new Queue<string>(["{\"verdict\":\"ResolvedWithinRules\",\"summary\":\"需要較強模型處理這個錯誤\",\"implementation\":{\"provider\":\"codex\",\"model\":\"strong\"}}","{\"verdict\":\"Pass\",\"summary\":\"完成\"}"]);
        await Coordinator(new QueuedReviewerFixture(responses),new SimpleValidationFixture()).ExecuteNextAsync();
        var result=(await store.GetTaskAsync(task.Id))!;Assert.Equal(pinned?Status.AwaitingStepApproval:Status.AwaitingReview,result.Status);
        Assert.Equal(pinned?1:2,agent.Calls.Count);
        if(!pinned){Assert.Equal("strong",agent.Calls.Last().Task.Ai!.Implementation!.Model);Assert.Equal("manager",result.Ai!.Planning!.Model);}
    }
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
            await store.SetWorkflowAsync(task.Id, running.Workflow with { Steps = [running.Workflow.Steps[0], running.Workflow.Steps[1] with { Name = "changed", Selection = new("claude", "new-model", "high") }] }, running.Version);
        };
        await Coordinator().ExecuteNextAsync();
        Assert.Equal("Feature · changed", agent.Calls.Last().Task.Title);
        Assert.Equal(new AiSelection("claude", "new-model", "high"), agent.Calls.Last().Task.Ai!.Implementation);
        var history = Assert.Single(await store.ListRunsAsync(task.Id));
        Assert.Equal("backend-model", history.Steps![0].Selection!.Model);
        Assert.Equal("new-model", history.Steps[1].Selection!.Model);
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
            "{\"verdict\":\"Pass\",\"summary\":\"後端修正已通過檢查\"}",
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
        Assert.Equal("commit3", workspace.Sources.Last());
        Assert.Equal(Status.AwaitingReview, completedTask.Status);
        Assert.Equal(4, agent.Calls.Count);
        Assert.Contains("超過上限直接阻擋並顯示提示訊息", agent.Calls.Last().Task.TaskRules);
    }
    [Fact] public async Task Each_step_resolves_its_own_validation_profile_and_builds_can_pass_without_fabricated_tests()
    {
        var profiles = new[] { new ValidationProfile("api", "API", ".", "dotnet", ["test"], "DotnetTest"), new ValidationProfile("web", "Build", ".", "node", ["build.js"]) };
        await store.SetExecutionSettingsAsync(project.Id, project.Version, profiles, "api", [], null);
        var task = await Add(Step("frontend") with { ValidationProfileId = "web" }, Step("build", "Validation") with { ValidationProfileId = "web" });
        var validator = new ProfileValidator();
        await Coordinator(validator: validator).ExecuteNextAsync();
        Assert.Equal(Status.AwaitingReview, (await store.GetTaskAsync(task.Id))!.Status);
        Assert.All(validator.Used, id => Assert.Equal("web", id));
        Assert.Equal(3, validator.Used.Count);
    }
    [Fact] public async Task Tdd_cannot_be_satisfied_by_a_build_only_command()
    {
        await store.SetExecutionSettingsAsync(project.Id, project.Version, [new("web", "Build", ".", "node", ["build.js"])], "web", [], null);
        var task = await Add(Step("frontend"));
        await store.SaveTaskAsync(task with { Policy = new(true), Version = task.Version + 1 }, task.Version);
        await Coordinator(validator: new ProfileValidator()).ExecuteNextAsync();
        Assert.Equal(Status.Failed, (await store.GetTaskAsync(task.Id))!.Status);
        Assert.Empty(agent.Calls);
        Assert.Contains("TDD", Assert.Single(await store.ListRunsAsync(task.Id)).Error);
    }
    private sealed class ProfileValidator : IValidationRunner
    {
        public List<string?> Used = [];
        public Task<ValidationEvidence> ValidateAsync(ProjectDefinition p, Workspace w, Guid id, TddPhase phase, CancellationToken ct) {
            Used.Add(p.DefaultValidationProfileId);
            return Task.FromResult(new ValidationEvidence(0, 0, 0, [], [], "Command"));
        }
    }
    [Fact] public async Task Unknown_supervision_verdict_cannot_complete_a_step()
    {
        var task = await Add(Step("backend"));
        await store.SaveTaskAsync(task with { Ai = new(new("codex"), new("codex")), Version = task.Version + 1 }, task.Version);
        await Coordinator(new QueuedReviewerFixture(new(["{\"verdict\":\"Typo\",\"summary\":\"looks fine\"}"]))).ExecuteNextAsync();
        Assert.Equal(Status.Failed, (await store.GetTaskAsync(task.Id))!.Status);
        Assert.Empty((await store.GetTaskAsync(task.Id))!.StepResults ?? []);
    }
    [Fact] public async Task New_approval_summary_emoji_does_not_override_the_explicit_checkpoint_state()
    {
        var task = await Add(Step("backend"), Step("confirm", "Approval") with { Instructions = "⚠️ Check the result" }, Step("frontend"));
        await Coordinator().ExecuteNextAsync();
        var paused = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Status.AwaitingStepApproval, paused.Status);
        var resumed = await store.ContinueWorkflowAsync(task.Id, paused.Version);
        Assert.Equal(2, resumed.StepResults!.Length);
        Assert.Null(resumed.ResumeCommit);
    }
    [Fact] public async Task Custom_review_can_correct_and_review_again_within_the_configured_budget()
    {
        var task = await Add(Step("backend"), Step("review", "Review"));
        await store.SaveTaskAsync(task with { Workflow = task.Workflow! with { MaxReviewRetries = 1 }, Version = task.Version + 1 }, task.Version);
        await Coordinator(new QueuedReviewerFixture(new([
            "{\"verdict\":\"NeedsChanges\",\"summary\":\"fix edge case\"}",
            "{\"verdict\":\"Pass\",\"summary\":\"fixed\"}"]))).ExecuteNextAsync();
        Assert.Equal(Status.AwaitingReview, (await store.GetTaskAsync(task.Id))!.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal(2, Assert.Single(await store.ListRunsAsync(task.Id)).AiReviews!.Length);
    }
    [Fact] public async Task Oversized_supervision_diff_pauses_instead_of_passing()
    {
        var task = await Add(Step("backend"));
        await store.SaveTaskAsync(task with { Ai = new(new("codex"), new("codex")), Version = task.Version + 1 }, task.Version);
        await new ExecutionCoordinator(store, agent, workspace, new ValidatorFixture(),
            new QueuedReviewerFixture(new()), new DiffFixture(new string('x', 120001))).ExecuteNextAsync();
        var current = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Status.AwaitingStepApproval, current.Status);
        Assert.True(Assert.Single(current.StepResults!).RequiresRework);
    }
    [Fact] public async Task Failed_build_is_repaired_in_the_same_worktree_then_revalidated()
    {
        var task = await Add(Step("backend"));
        var validator = new SequenceValidator([
            new(0, 0, 0, [], [], "Command"),
            new(1, 0, 0, [], [], "Command", "error CS1002: missing semicolon", "CommandFailed"),
            new(0, 0, 0, [], [], "Command")]);
        await Coordinator(validator: validator).ExecuteNextAsync();
        Assert.Equal(Status.AwaitingReview, (await store.GetTaskAsync(task.Id))!.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal(agent.Calls[0].Workspace, agent.Calls[1].Workspace);
        Assert.Contains("CS1002", agent.Calls[1].Task.TaskRules);
    }
    [Fact] public async Task Guided_correction_cannot_drop_baseline_tests()
    {
        var task = await Add(Step("backend"));
        await store.SaveTaskAsync(task with { Ai = new(new("codex"), new("codex")), Version = task.Version + 1 }, task.Version);
        var baseline = new ValidationEvidence(0, 1, 0, ["existing-test"], []);
        var validator = new SequenceValidator([baseline, baseline, new(0, 1, 0, ["replacement-test"], [])]);
        await Coordinator(new QueuedReviewerFixture(new([
            "{\"verdict\":\"ResolvedWithinRules\",\"summary\":\"fix edge case\"}"])), validator).ExecuteNextAsync();
        Assert.Equal(Status.Failed, (await store.GetTaskAsync(task.Id))!.Status);
        Assert.Empty((await store.GetTaskAsync(task.Id))!.StepResults ?? []);
    }
    [Fact] public async Task Repeated_build_error_stops_bounded_repairs()
    {
        var task = await Add(Step("backend"));
        var failure = new ValidationEvidence(1, 0, 0, [], [], "Command", "error repeated", "CommandFailed");
        await Coordinator(validator: new SequenceValidator([new(0, 0, 0, [], [], "Command"), failure, failure])).ExecuteNextAsync();
        Assert.Equal(Status.Failed, (await store.GetTaskAsync(task.Id))!.Status);
        Assert.Equal(2, agent.Calls.Count);
    }
    [Fact] public async Task Environment_failure_does_not_trigger_agent_repairs()
    {
        var task = await Add(Step("backend"));
        await Coordinator(validator: new SequenceValidator([new(0, 0, 0, [], [], "Command"),
            new(1, 0, 0, [], [], "Command", "dependency preparation failed", "Environment")])).ExecuteNextAsync();
        Assert.Equal(Status.Failed, (await store.GetTaskAsync(task.Id))!.Status);
        Assert.Single(agent.Calls);
    }
    private sealed class SequenceValidator(ValidationEvidence[] results) : IValidationRunner
    {
        private int index;
        public Task<ValidationEvidence> ValidateAsync(ProjectDefinition p, Workspace w, Guid id, TddPhase phase, CancellationToken ct) => Task.FromResult(results[index++]);
    }
    private sealed class AgentFixture : IAgentRunner
    {
        public List<(WorkTask Task, Workspace Workspace)> Calls = [];
        public string? FailName; public Func<Task>? OnExecute;
        public async Task ExecuteAsync(WorkTask task, Workspace w, Guid run, TddPhase phase, CancellationToken ct)
        { Calls.Add((task, w)); if (OnExecute != null) await OnExecute(); if (FailName != null && task.Title.EndsWith(FailName)) throw new InvalidOperationException("fixture failed"); }
    }
    private sealed class WorkspaceFixture : IWorkspaceManager,IIntegrationWorkspaceManager
    {
        public List<string> Sources = []; private int snapshots;
        public Task<Workspace> CreateAsync(ProjectDefinition p, Guid run, CancellationToken ct)
        { Sources.Add(p.BaseBranch); return Task.FromResult(new Workspace("worktree", p.BaseBranch == "main" ? "base" : p.BaseBranch)); }
        public Task<string> SnapshotAsync(Workspace w, CancellationToken ct) => Task.FromResult("commit" + ++snapshots);
        public Task AssertUnchangedAsync(Workspace w, CancellationToken ct) => Task.CompletedTask;
        public Task<string[]> IntegrateAsync(Workspace w,string sourceCommit,CancellationToken ct)=>Task.FromResult(Array.Empty<string>());
    }
    private sealed class ValidatorFixture : IValidationRunner
    { public Task<ValidationEvidence> ValidateAsync(ProjectDefinition p, Workspace w, Guid run, TddPhase phase, CancellationToken ct) => Task.FromResult(new ValidationEvidence(0, 1, 0, ["test"], [])); }
    private sealed class TddValidatorFixture(bool validRed) : IValidationRunner
    { public Task<ValidationEvidence> ValidateAsync(ProjectDefinition p, Workspace w, Guid run, TddPhase phase, CancellationToken ct) =>
        Task.FromResult(phase == TddPhase.Red && validRed ? new ValidationEvidence(1, 1, 1, [], ["feature"]) : new ValidationEvidence(0, 2, 0, ["test", "feature"], [])); }
    private sealed class DiffFixture(string patch = "+code") : IResultDiffReader
    { public Task<ResultDiff> ReadAsync(ProjectDefinition p, TaskRun run, CancellationToken ct) => Task.FromResult(new ResultDiff(run.Id, run.BaseCommit!, run.ResultCommit!, patch)); }
    private sealed class ReviewerFixture : IPlanningRunner
    { public Task<string> PlanAsync(WorkTask t, Workspace w, Guid run, CancellationToken ct) => Task.FromResult("{\"verdict\":\"NeedsChanges\",\"summary\":\"missing contract\"}"); }
    private sealed class QueuedReviewerFixture(Queue<string> responses) : IPlanningRunner
    { public Task<string> PlanAsync(WorkTask t, Workspace w, Guid run, CancellationToken ct) => Task.FromResult(responses.Dequeue()); }
    private sealed class GuardFixture : IExecutionPolicyGuard
    { public List<Workspace> Calls = []; public Task CheckAsync(Workspace w, ExecutionPolicy p, CancellationToken ct) { Calls.Add(w); return Task.CompletedTask; } }
}
