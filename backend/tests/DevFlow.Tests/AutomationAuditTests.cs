using System.Text.Json;
using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Tests;

public sealed class AutomationAuditTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-audit-" + Guid.NewGuid());
    private SqliteTaskStore store = null!;
    public async Task InitializeAsync() { store = new(Path.Combine(root, "tasks.db")); await store.InitializeAsync(); }
    public Task DisposeAsync() {
        var target = Path.GetFullPath(root);
        if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("devflow-audit-", StringComparison.Ordinal)) throw new InvalidOperationException();
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(target, true); return Task.CompletedTask;
    }
    private async Task<WorkTask> Seed(string repository, bool planning = false) {
        var project = new ProjectDefinition(Guid.NewGuid(), "Project", repository, "main", ["test"]);
        await store.AddProjectAsync(project);
        var task = WorkTask.Create(project.Id, "Task", "Build", "Pass");
        task = planning ? task.RequestPlanning() : task.SetPlan("Plan").ApprovePlan();
        await store.AddTaskAsync(task); return task;
    }
    [Fact] public async Task Queue_skips_busy_repository_across_project_ids_and_planning()
    {
        var first = await Seed(root);
        await store.ClaimNextAsync();
        await Seed(root + Path.DirectorySeparatorChar); // Same checkout under another project.
        await Seed(root, planning: true);
        var other = await Seed(Path.Combine(root, "independent"));
        Assert.Equal(other.Id, (await store.ClaimNextAsync())!.Task.Id);
        Assert.Null(await store.ClaimPlanningAsync());
        Assert.Null(await store.ClaimNextAsync());
        Assert.Equal(Status.Running, (await store.GetTaskAsync(first.Id))!.Status);
    }
    [Fact] public async Task Pending_feed_and_single_request_lookup_follow_reply_and_run_lifetime()
    {
        var task = await Seed(root);
        var claim = (await store.ClaimNextAsync())!;
        var request = new TaskToolApproval(Guid.NewGuid(), task.Id, claim.Run.Id, "unknown_tool",
            JsonSerializer.SerializeToElement(new { privateArgument = "do-not-expose" }), root, DateTimeOffset.UtcNow, null);
        await store.AddToolApprovalAsync(request);
        var pending = Assert.Single(await store.ListPendingPermissionsAsync());
        Assert.Equal(task.Id, pending.OwnerId);
        Assert.DoesNotContain("do-not-expose", JsonSerializer.Serialize(pending));
        Assert.NotNull(await store.GetToolApprovalAsync(task.Id, request.Id));
        Assert.Null(await store.GetToolApprovalAsync(Guid.NewGuid(), request.Id));
        await store.ReplyToolApprovalAsync(task.Id, request.Id, claim.Run.Id, true);
        Assert.Empty(await store.ListPendingPermissionsAsync());
        Assert.Equal("Allowed", (await store.GetToolApprovalAsync(task.Id, request.Id))!.State);
    }
    [Fact] public async Task Previously_answered_requests_do_not_exhaust_the_pending_request_budget()
    {
        var task = await Seed(root); var claim = (await store.ClaimNextAsync())!;
        for (var i = 0; i < 55; i++) {
            var request = new TaskToolApproval(Guid.NewGuid(), task.Id, claim.Run.Id, "unknown_tool", JsonSerializer.SerializeToElement(new { value = i }), root, DateTimeOffset.UtcNow, null);
            await store.AddToolApprovalAsync(request);
            await store.ReplyToolApprovalAsync(task.Id, request.Id, claim.Run.Id, true);
        }
        Assert.Empty(await store.ListPendingPermissionsAsync());
    }
    [Fact] public void Error_summary_includes_late_compiler_error()
    {
        var log = string.Join('\n', Enumerable.Repeat("restore progress", 30)) + "\nfile.cs(12,3): error CS1002: ; expected\nbuild failed";
        Assert.Contains("CS1002", DotnetValidationRunner.Summarize(log));
    }
    [Fact] public async Task Second_step_resume_uses_its_own_real_git_commit()
    {
        var repo = Path.Combine(root, "repo"); Directory.CreateDirectory(repo);
        var process = new ProcessRunner();
        async Task Git(params string[] args) {
            var result = await process.RunAsync("git", args, repo, null, default);
            Assert.True(result.ExitCode == 0, result.StandardError);
        }
        await Git("init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "seed.txt"), "seed");
        await Git("add", "."); await Git("-c", "user.name=DevFlow", "-c", "user.email=devflow@localhost", "commit", "-m", "seed");
        var project = new ProjectDefinition(Guid.NewGuid(), "Real Git", repo, "main", ["test"]); await store.AddProjectAsync(project);
        var task = WorkTask.Create(project.Id, "Feature", "Build", "Pass").SetPlan("Plan").ApprovePlan() with {
            Policy = new(false), Ai = new(new("codex"), new("codex")),
            Workflow = new(Steps: [new(Guid.NewGuid(), "A", Selection: new("codex")), new(Guid.NewGuid(), "B", Selection: new("codex"))])
        };
        await store.AddTaskAsync(task);
        var agent = new RealFileAgent();
        var coordinator = new ExecutionCoordinator(store, agent, new GitWorkspaceManager(process, Path.Combine(root, "worktrees")),
            new PassingValidator(), new PauseReviewer(), new GitResultDiffReader(process));
        await coordinator.ExecuteNextAsync();
        var paused = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Status.AwaitingStepApproval, paused.Status);
        await store.ContinueWorkflowAsync(task.Id, paused.Version, instructions: "Complete B");
        await coordinator.ExecuteNextAsync();
        Assert.True(agent.RetainedB);
        Assert.Equal(Status.AwaitingReview, (await store.GetTaskAsync(task.Id))!.Status);
    }
    private sealed class PassingValidator : IValidationRunner {
        public Task<ValidationEvidence> ValidateAsync(ProjectDefinition p, Workspace w, Guid id, TddPhase phase, CancellationToken ct) => Task.FromResult(new ValidationEvidence(0, 1, 0, ["baseline"], []));
    }
    private sealed class RealFileAgent : IAgentRunner {
        private int bCalls; public bool RetainedB;
        public async Task ExecuteAsync(WorkTask t, Workspace w, Guid id, TddPhase phase, CancellationToken ct) {
            var isB = t.Title.EndsWith("B", StringComparison.Ordinal);
            var file = Path.Combine(w.Path, isB ? "b.txt" : "a.txt");
            if (isB && ++bCalls == 2) RetainedB = File.Exists(file) && await File.ReadAllTextAsync(file, ct) == "B-1";
            await File.WriteAllTextAsync(file, isB ? "B-" + bCalls : "A", ct);
        }
    }
    private sealed class PauseReviewer : IPlanningRunner {
        private int calls;
        public Task<string> PlanAsync(WorkTask t, Workspace w, Guid id, CancellationToken ct) => Task.FromResult(++calls == 2
            ? "{\"verdict\":\"NeedsUserConfirmation\",\"summary\":\"Confirm B\"}"
            : "{\"verdict\":\"Pass\",\"summary\":\"Complete\"}");
    }
}
