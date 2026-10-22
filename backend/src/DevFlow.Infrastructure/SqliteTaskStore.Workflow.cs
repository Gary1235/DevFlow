using System.Text.Json;
using DevFlow.Core;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Infrastructure;

public sealed partial class SqliteTaskStore
{
    public Task<WorkTask> BeginWorkflowStepAsync(Guid runId, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var run = ReadOne<TaskRun>(c, tx, "Runs", runId) ?? throw new MissingEntityException("Run not found.");
        var task = ReadOne<WorkTask>(c, tx, "Tasks", run.TaskId)!;
        if (task.Status != Status.Running || run.FinishedAt is not null) throw new ConflictException("任務已停止。");
        var step = task.Workflow?.Steps?.ElementAtOrDefault(task.StepResults?.Length ?? 0);
        if (step is null) return Task.FromResult(task);
        var result = new WorkflowStepResult(step.Id, step.Name, step.Kind, "Running", runId,
            task.StepResults?.LastOrDefault()?.ResultCommit ?? run.BaseCommit, null, step.Selection, DateTimeOffset.UtcNow);
        var updated = task with { ActiveStepId = step.Id, Version = task.Version + 1 };
        var updatedRun = run with { Selection = step.Selection ?? run.Selection,
            Steps = [.. (run.Steps ?? []).Where(s => s.StepId != step.Id), result] };
        Execute(c, tx, "UPDATE Runs SET Payload=$payload WHERE Id=$id", ("$payload", JsonSerializer.Serialize(updatedRun)), ("$id", runId.ToString()));
        UpdateTask(c, tx, updated, task.Version);
        Event(c, tx, task.Id, "WorkflowStepRunning", step.Name);
        tx.Commit(); return Task.FromResult(updated);
    }
    public Task<IReadOnlyList<WorkflowTemplate>> ListWorkflowTemplatesAsync(CancellationToken ct = default)
    {
        using var c = Open(ct);
        return Task.FromResult(ReadList<WorkflowTemplate>(c, "SELECT Payload FROM WorkflowTemplates ORDER BY Name"));
    }
    public Task<WorkflowTemplate> SaveWorkflowTemplateAsync(WorkflowTemplate template, CancellationToken ct = default)
    {
        template = template with { Name = WorkTask.Required(template.Name, 60), Workflow = WorkflowDefinition.Normalize(template.Workflow) };
        if (template.Workflow.Steps is null) throw new ArgumentException("範本必須包含自訂流程步驟。");
        using var c = Open(ct); using var tx = c.BeginTransaction();
        Execute(c, tx, "INSERT INTO WorkflowTemplates(Name,Payload) VALUES($name,$payload) ON CONFLICT(Name) DO UPDATE SET Payload=excluded.Payload",
            ("$name", template.Name), ("$payload", JsonSerializer.Serialize(template)));
        tx.Commit(); return Task.FromResult(template);
    }
    public Task RecordWorkflowStepAsync(Guid runId, WorkflowStepResult result, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var run = ReadOne<TaskRun>(c, tx, "Runs", runId) ?? throw new MissingEntityException("Run not found.");
        var task = ReadOne<WorkTask>(c, tx, "Tasks", run.TaskId)!;
        if (task.Status != Status.Running || run.FinishedAt is not null) throw new ConflictException("任務已停止。");
        var completed = task.StepResults ?? [];
        var step = task.Workflow?.Steps?.ElementAtOrDefault(completed.Length);
        if (step?.Id != result.StepId || result.RunId != runId) throw new ConflictException("流程步驟已改變。");
        if (result.State is not ("Running" or "Completed" or "Failed")) throw new ArgumentException("Invalid step state.");
        var updated = task with { Version = task.Version + 1, ActiveStepId = result.State == "Running" ? step.Id : null,
            StepResults = result.State == "Completed" ? [.. completed, result] : completed };
        var updatedRun = run with { Steps = [.. (run.Steps ?? []).Where(s => s.StepId != result.StepId), result],
            ResultCommit = result.State == "Completed" ? result.ResultCommit : run.ResultCommit };
        Execute(c, tx, "UPDATE Runs SET Payload=$payload WHERE Id=$id", ("$payload", JsonSerializer.Serialize(updatedRun)), ("$id", runId.ToString()));
        UpdateTask(c, tx, updated, task.Version);
        Event(c, tx, task.Id, "WorkflowStep" + result.State, step.Name + (result.Summary is { } summary ? ": " + summary : ""));
        tx.Commit(); return Task.CompletedTask;
    }
    public Task PauseWorkflowAsync(Guid runId, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var run = ReadOne<TaskRun>(c, tx, "Runs", runId) ?? throw new MissingEntityException("Run not found.");
        var task = ReadOne<WorkTask>(c, tx, "Tasks", run.TaskId)!;
        if (task.Status != Status.Running || run.FinishedAt is not null || task.StepResults is not { Length: > 0 })
            throw new ConflictException("任務不在可暫停的流程步驟。");
        var updated = task with { Status = Status.AwaitingStepApproval, Version = task.Version + 1, ActiveStepId = null };
        var paused = run with { Status = Status.AwaitingStepApproval.ToString(), FinishedAt = DateTimeOffset.UtcNow };
        Execute(c, tx, "UPDATE Runs SET Payload=$payload WHERE Id=$id", ("$payload", JsonSerializer.Serialize(paused)), ("$id", runId.ToString()));
        UpdateTask(c, tx, updated, task.Version);
        Event(c, tx, task.Id, "WorkflowPaused", "步驟已完成，等待人工確認後接續。");
        tx.Commit(); return Task.CompletedTask;
    }
    public Task<WorkTask> ContinueWorkflowAsync(Guid taskId, int expectedVersion, CancellationToken ct = default, string? instructions = null)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var task = ReadOne<WorkTask>(c, tx, "Tasks", taskId) ?? throw new MissingEntityException("Task not found.");
        if (task.Version != expectedVersion || task.Status != Status.AwaitingStepApproval)
            throw new ConflictException("任務狀態已改變，請重新讀取。");
        var completedResults = task.StepResults ?? [];
        var lastResult = completedResults.LastOrDefault();
        var requiresStepReRun = lastResult?.Summary?.StartsWith("⚠️", StringComparison.Ordinal) == true;
        var effectiveResults = requiresStepReRun ? completedResults[..^1] : completedResults;
        var finished = effectiveResults.Length == task.Workflow?.Steps?.Length;
        var updatedTaskRules = task.TaskRules;
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            var cleanedInstruction = WorkTask.Required(instructions, 12000);
            var stepLabel = lastResult?.Name ?? "目前步驟";
            updatedTaskRules = string.IsNullOrWhiteSpace(updatedTaskRules)
                ? $"【人工確認回覆（{stepLabel}）】：{cleanedInstruction}"
                : $"{updatedTaskRules}\n【人工確認回覆（{stepLabel}）】：{cleanedInstruction}";
        }
        if (finished)
        {
            using var latest = Command(c, tx, "SELECT Payload FROM Runs WHERE TaskId=$id ORDER BY rowid DESC LIMIT 1", ("$id", taskId.ToString()));
            var run = JsonSerializer.Deserialize<TaskRun>((string?)latest.ExecuteScalar() ?? throw new ConflictException("找不到暫停紀錄。"))!;
            if (run.Status != Status.AwaitingStepApproval.ToString() || run.ResultCommit is null) throw new ConflictException("成果紀錄無法核對。");
            Execute(c, tx, "UPDATE Runs SET Payload=$payload WHERE Id=$id",
                ("$payload", JsonSerializer.Serialize(run with { Status = Status.AwaitingReview.ToString() })), ("$id", run.Id.ToString()));
        }
        var updated = task with
        {
            Status = finished ? Status.AwaitingReview : Status.Queued,
            StepResults = effectiveResults,
            TaskRules = updatedTaskRules,
            RevisionCommit = requiresStepReRun ? lastResult?.ResultCommit ?? task.RevisionCommit : task.RevisionCommit,
            Version = task.Version + 1
        };
        UpdateTask(c, tx, updated, task.Version);
        Event(c, tx, task.Id, "WorkflowContinued", string.IsNullOrWhiteSpace(instructions) ? "已確認完成步驟，接續執行。" : "已回覆確認問題，接續執行。");
        tx.Commit(); return Task.FromResult(updated);
    }
    public Task<WorkTask> SetWorkflowAsync(Guid taskId, WorkflowDefinition workflow, int expectedVersion, CancellationToken ct = default, AiSelection? planningSelection = null)
    {
        workflow = WorkflowDefinition.Normalize(workflow);
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var task = ReadOne<WorkTask>(c, tx, "Tasks", taskId) ?? throw new MissingEntityException("Task not found.");
        if (task.Version != expectedVersion || task.Status is not (Status.AwaitingPlan or Status.AwaitingApproval or Status.PlanningFailed or
            Status.AwaitingStepApproval or Status.Running or Status.Failed or Status.Cancelled or Status.Queued or Status.PlanningQueued or Status.Planning)) throw new ConflictException("目前無法編輯流程，請重新讀取。");
        if (task.Workflow?.Steps is not { } oldSteps || workflow.Steps is not { } nextSteps)
            throw new ConflictException("既有任務保留原流程；請建立新任務使用自訂步驟。");
        if (workflow.PlanningEnabled != task.Workflow.PlanningEnabled) throw new ConflictException("建立後不能改變規劃模式。");
        var locked = (task.StepResults?.Length ?? 0) + (task.ActiveStepId.HasValue ? 1 : 0);
        if (nextSteps.Length < locked || oldSteps.Take(locked).Zip(nextSteps).Any(pair =>
                JsonSerializer.Serialize(pair.First) != JsonSerializer.Serialize(pair.Second)))
            throw new ConflictException("已完成或正在執行的步驟不能修改、刪除或移動。");
        var ai = task.Ai;
        if (planningSelection is not null)
        {
            if (!workflow.PlanningEnabled || task.Status is not (Status.AwaitingPlan or Status.AwaitingApproval or Status.PlanningFailed or Status.PlanningQueued or Status.Cancelled))
                throw new ConflictException("目前無法更換規劃模型，請等待執行結束。");
            ai = (ai ?? new AiConfiguration()) with { Planning = AiSelection.Normalize(planningSelection) };
        }
        var updated = task with { Workflow = workflow, Ai = ai, Version = task.Version + 1 };
        UpdateTask(c, tx, updated, task.Version);
        Event(c, tx, task.Id, "WorkflowUpdated", "尚未開始的流程步驟已更新。");
        tx.Commit(); return Task.FromResult(updated);
    }
}
