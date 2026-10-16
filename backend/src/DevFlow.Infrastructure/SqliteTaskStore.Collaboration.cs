using System.Text.Json;
using DevFlow.Core;
namespace DevFlow.Infrastructure;

public sealed partial class SqliteTaskStore
{
    public Task<IReadOnlyList<RuleDefinition>> ListRulesAsync(CancellationToken ct = default)
    { using var c = Open(ct); return Task.FromResult(ReadList<RuleDefinition>(c, "SELECT Payload FROM RuleLibrary ORDER BY rowid")); }

    public Task<RuleDefinition> SaveRuleAsync(Guid? id, string name, string content, string[] stages, int expectedVersion, bool archived, CancellationToken ct)
    {
        name = WorkTask.Required(name, 200); content = WorkTask.Required(content, 12000);
        if (stages is not { Length: > 0 } || stages.Any(s => s is not ("Discussion" or "Planning" or "Implementation" or "Acceptance")))
            throw new ArgumentException("Select valid rule stages.");
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var existing = id.HasValue ? ReadOne<RuleDefinition>(c, tx, "RuleLibrary", id.Value) ?? throw new MissingEntityException("Rule not found.") : null;
        if (existing is not null && existing.Version != expectedVersion) throw new ConflictException("Rule changed; reload before editing.");
        var rule = new RuleDefinition(id ?? Guid.NewGuid(), name, content, stages.Distinct().ToArray(), (existing?.Version ?? 0) + 1, archived, existing?.RequiresTdd ?? false);
        Execute(c, tx, "INSERT INTO RuleLibrary VALUES($id,$payload) ON CONFLICT(Id) DO UPDATE SET Payload=$payload", ("$id", rule.Id.ToString()), ("$payload", JsonSerializer.Serialize(rule)));
        tx.Commit(); return Task.FromResult(rule);
    }

    public async Task<RuleSnapshot[]> ResolveRulesAsync(Guid[] ids, CancellationToken ct)
    {
        if (ids.Length > 30) throw new ArgumentException("At most 30 rules can be selected.");
        var rules = await ListRulesAsync(ct);
        var snapshots = ids.Distinct().Select(id => RuleSnapshot.From(rules.SingleOrDefault(r => r.Id == id && !r.Archived)
            ?? throw new ArgumentException("A selected rule is unavailable or archived. Refresh the rule library."))).ToArray();
        if (snapshots.Sum(r => r.Content.Length) > 48000) throw new ArgumentException("Selected rule content exceeds 48,000 characters. Select fewer rules.");
        return snapshots;
    }

    public async Task<ProjectDefinition> SetProjectRulesAsync(Guid id, Guid[] ruleIds, int expectedVersion, CancellationToken ct)
    {
        await ResolveRulesAsync(ruleIds, ct);
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var p = ReadOne<ProjectDefinition>(c, tx, "Projects", id) ?? throw new MissingEntityException("Project not found.");
        if (p.Version != expectedVersion) throw new ConflictException("Project changed; reload before editing.");
        var updated = p with { DefaultRuleIds = ruleIds.Distinct().ToArray(), Version = p.Version + 1 };
        Execute(c, tx, "UPDATE Projects SET Payload=$payload WHERE Id=$id", ("$id", id.ToString()), ("$payload", JsonSerializer.Serialize(updated)));
        tx.Commit(); return updated;
    }

    public Task RecordAiReviewAsync(Guid runId, AiAcceptanceReport report, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var run = ReadOne<TaskRun>(c, tx, "Runs", runId) ?? throw new MissingEntityException("Run not found.");
        if (run.FinishedAt is not null) throw new ConflictException("Run already finished.");
        var updated = run with { AiReviews = [..run.AiReviews ?? [], report] };
        Execute(c, tx, "UPDATE Runs SET Payload=$payload WHERE Id=$id", ("$id", runId.ToString()), ("$payload", JsonSerializer.Serialize(updated)));
        Event(c, tx, run.TaskId, "AiAcceptance", $"Round {report.Attempt}, stage {report.Stage}: {report.Verdict} · {report.Selection.Provider}/{report.Selection.Model ?? "CLI default"}");
        tx.Commit(); return Task.CompletedTask;
    }

    public Task AddDiscussionAsync(Discussion discussion, CancellationToken ct)
    {
        using var c = Open(ct);
        Execute(c, null, "INSERT INTO Discussions VALUES($id,$project,$payload)", ("$id", discussion.Id.ToString()),
            ("$project", discussion.ProjectId.ToString()), ("$payload", JsonSerializer.Serialize(discussion)));
        return Task.CompletedTask;
    }
    public Task<Discussion?> GetDiscussionAsync(Guid id, CancellationToken ct)
    { using var c = Open(ct); return Task.FromResult(ReadOne<Discussion>(c, null, "Discussions", id)); }
    public Task<IReadOnlyList<Discussion>> ListDiscussionsAsync(CancellationToken ct)
    { using var c = Open(ct); return Task.FromResult(ReadList<Discussion>(c, "SELECT Payload FROM Discussions ORDER BY rowid DESC")); }

    public Task<Discussion> BeginDiscussionAsync(Guid id, int expectedVersion, string? message, CancellationToken ct)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var d = ReadOne<Discussion>(c, tx, "Discussions", id) ?? throw new MissingEntityException("Discussion not found.");
        if (d.Version != expectedVersion || d.ActiveOperation is not null) throw new ConflictException("Discussion changed or an AI reply is still running. Refresh before retrying.");
        if (d.Messages.Sum(m => m.Content.Length) > 120000) throw new ArgumentException("This discussion is too long. Start a new discussion with a summary.");
        var messages = message is null ? d.Messages : [..d.Messages, new DiscussionMessage(Guid.NewGuid(), "user", WorkTask.Required(message, 20000), DateTimeOffset.UtcNow)];
        var updated = d with { Messages = messages, ActiveOperation = Guid.NewGuid(), Error = null, Version = d.Version + 1, UpdatedAt = DateTimeOffset.UtcNow };
        UpdateDiscussion(c, tx, updated); tx.Commit(); return Task.FromResult(updated);
    }

    public Task<Discussion> FinishDiscussionAsync(Guid id, Guid operation, string? reply, string? baseCommit, DiscussionDraft[]? drafts, string? error, CancellationToken ct)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var d = ReadOne<Discussion>(c, tx, "Discussions", id) ?? throw new MissingEntityException("Discussion not found.");
        if (d.ActiveOperation != operation) throw new ConflictException("Discussion operation is no longer active.");
        var messages = reply is null ? d.Messages : [..d.Messages, new DiscussionMessage(Guid.NewGuid(), "assistant", reply, DateTimeOffset.UtcNow, d.Selection, baseCommit, operation)];
        var updated = d with { Messages = messages, Drafts = drafts is null ? d.Drafts : [..d.Drafts, ..drafts], Error = error,
            ActiveOperation = null, Version = d.Version + 1, UpdatedAt = DateTimeOffset.UtcNow };
        UpdateDiscussion(c, tx, updated); tx.Commit(); return Task.FromResult(updated);
    }

    public async Task RecoverDiscussionsAsync(CancellationToken ct = default)
    {
        foreach (var d in await ListDiscussionsAsync(ct))
            if (d.ActiveOperation is { } operation)
                await FinishDiscussionAsync(d.Id, operation, null, null, null, "API restarted during the AI operation. Your messages are saved; retry explicitly.", ct);
    }

    public Task<WorkTask> AddDiscussionTaskAsync(WorkTask task, CancellationToken ct)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var d = ReadOne<Discussion>(c, tx, "Discussions", task.SourceDiscussionId!.Value) ?? throw new MissingEntityException("Discussion not found.");
        var draft = d.Drafts.SingleOrDefault(v => v.Id == task.SourceDraftId) ?? throw new MissingEntityException("Discussion draft not found.");
        if (task.ProjectId != d.ProjectId) throw new ConflictException("Discussion project does not match.");
        if (draft.CreatedTaskId is { } created) return Task.FromResult(ReadOne<WorkTask>(c, tx, "Tasks", created)!);
        if (d.ActiveOperation is not null) throw new ConflictException("Discussion is busy.");
        Execute(c, tx, "INSERT INTO Tasks VALUES($id,$project,$status,$version,$created,$payload)",
            ("$id", task.Id.ToString()), ("$project", task.ProjectId.ToString()), ("$status", (int)task.Status),
            ("$version", task.Version), ("$created", task.CreatedAt.ToString("O")), ("$payload", JsonSerializer.Serialize(task)));
        Event(c, tx, task.Id, "TaskCreated", task.Status.ToString());
        if (task.Plan is not null) RecordPlan(c, tx, task, null, "Discussion");
        UpdateDiscussion(c, tx, d with { Drafts = d.Drafts.Select(v => v.Id == draft.Id ? v with { CreatedTaskId = task.Id } : v).ToArray(),
            Version = d.Version + 1, UpdatedAt = DateTimeOffset.UtcNow });
        tx.Commit(); return Task.FromResult(task);
    }

    private static void UpdateDiscussion(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, Discussion d) =>
        Execute(c, tx, "UPDATE Discussions SET Payload=$payload WHERE Id=$id", ("$id", d.Id.ToString()), ("$payload", JsonSerializer.Serialize(d)));
}
