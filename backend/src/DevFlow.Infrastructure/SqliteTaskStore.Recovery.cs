using System.Text.Json;
using DevFlow.Core;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Infrastructure;
public sealed partial class SqliteTaskStore
{
    public Task<WorkTask> CreateRecoveryTaskAsync(Guid taskId, int expectedVersion, Guid runId, string checkpoint, string head, string[] required, CancellationToken ct)
    {
        checkpoint = GitCommit.Require(checkpoint); head = GitCommit.Require(head);
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var source = ReadOne<WorkTask>(c, tx, "Tasks", taskId) ?? throw new MissingEntityException("Task not found.");
        var run = ReadOne<TaskRun>(c, tx, "Runs", runId);
        if (source.Version != expectedVersion || source.Status is not (Status.Failed or Status.Cancelled) || source.Plan is null || run?.TaskId != taskId || run.FinishedAt is null)
            throw new ConflictException("來源任務或執行狀態已變更。");
        var steps = source.Workflow?.Steps ?? [new(Guid.NewGuid(), "恢復實作", Selection: source.Ai?.Implementation)];
        var completed = source.StepResults ?? [];
        var step = steps.ElementAtOrDefault(completed.Length);
        if (step is null || step.Kind != "Implementation") throw new ConflictException("僅能從尚未完成的實作步驟恢復副本。");
        var cleanBase = completed.LastOrDefault()?.ResultCommit ?? run.BaseCommit ?? source.Plan.BaseCommit ?? head;
        var title = "恢復失敗副本 · " + source.Title; title = title[..Math.Min(200, title.Length)];
        var task = WorkTask.Create(source.ProjectId, title, source.Description, source.AcceptanceCriteria) with {
            Plan = source.Plan, Status = Status.Queued, Ai = source.Ai, Policy = source.Policy, Rules = source.Rules, TaskRules = source.TaskRules,
            Workflow = source.Workflow is { Steps: not null } ? source.Workflow : new(false, source.Workflow?.Reviewers, source.Workflow?.MaxReviewRetries ?? 0, steps),
            StepResults = completed, ExecutionBaseCommit = source.ExecutionBaseCommit ?? run.BaseCommit, ResumeCommit = cleanBase,
            Recovery = new(source.Id, run.Id, checkpoint, step.Id, required)
        };
        Execute(c, tx, "INSERT INTO Tasks VALUES($id,$project,$status,$version,$created,$payload)", ("$id", task.Id.ToString()), ("$project", task.ProjectId.ToString()),
            ("$status", (int)task.Status), ("$version", task.Version), ("$created", task.CreatedAt.ToString("O")), ("$payload", JsonSerializer.Serialize(task)));
        Event(c, tx, task.Id, "WorkspaceRecoveryQueued", "來源任務 " + source.Id + "；未驗證 checkpoint " + checkpoint + "。必須重新修正與驗證。");
        tx.Commit(); return Task.FromResult(task);
    }
}
