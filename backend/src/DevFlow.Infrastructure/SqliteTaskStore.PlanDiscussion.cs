using System.Text.Json;
using DevFlow.Core;

namespace DevFlow.Infrastructure;

public sealed partial class SqliteTaskStore
{
    public Task<Discussion> EnsurePlanDiscussionAsync(Guid taskId, CancellationToken ct)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var task = ReadOne<WorkTask>(c, tx, "Tasks", taskId) ?? throw new MissingEntityException("Task not found.");
        var existing = ReadOne<Discussion>(c, tx, "Discussions", taskId);
        if (existing is not null)
        {
            if (existing.PlanTaskId != taskId) throw new ConflictException("Discussion ID is already in use.");
            return Task.FromResult(existing);
        }
        var project = ReadOne<ProjectDefinition>(c, tx, "Projects", task.ProjectId) ?? throw new MissingEntityException("Project not found.");
        var discussion = new Discussion(taskId, task.ProjectId, task.Title,
            AiSelection.Normalize(task.Ai?.Planning ?? project.AiDefaults?.Planning), task.Rules ?? [], [], [], 0,
            DateTimeOffset.UtcNow, PlanTaskId: taskId);
        Execute(c, tx, "INSERT INTO Discussions VALUES($id,$project,$payload)", ("$id", taskId.ToString()),
            ("$project", task.ProjectId.ToString()), ("$payload", JsonSerializer.Serialize(discussion)));
        tx.Commit(); return Task.FromResult(discussion);
    }

    public Task<WorkTask> ApplyPlanProposalAsync(Guid taskId, Guid messageId, int expectedVersion, CancellationToken ct)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var task = ReadOne<WorkTask>(c, tx, "Tasks", taskId) ?? throw new MissingEntityException("Task not found.");
        var discussion = ReadOne<Discussion>(c, tx, "Discussions", taskId) ?? throw new MissingEntityException("Discussion not found.");
        var message = discussion.PlanTaskId == taskId ? discussion.Messages.SingleOrDefault(m => m.Id == messageId) : null;
        if (message is not { Role: "assistant", ProposedPlan: not null, PlanContext: { } context })
            throw new ArgumentException("找不到可套用的計畫草案。");
        if (expectedVersion != task.Version || context.TaskVersion != task.Version || context.Revision != (task.Plan?.Revision ?? 0))
            throw new ConflictException("任務或計畫已更新，請依最新版本重新產生修訂草案。");
        PlanContent.EnsureCompleted(message.ProposedPlan);
        var updated = task.SetPlan(message.ProposedPlan);
        updated = updated with { Plan = updated.Plan! with { BaseCommit = task.Plan?.BaseCommit ?? message.BaseCommit } };
        UpdateTask(c, tx, updated, expectedVersion);
        RecordPlan(c, tx, updated, null, "PlanDiscussion");
        Event(c, tx, taskId, "PlanProposalApplied", $"Plan revision {updated.Plan.Revision}; discussion message {messageId}");
        tx.Commit(); return Task.FromResult(updated);
    }
}
