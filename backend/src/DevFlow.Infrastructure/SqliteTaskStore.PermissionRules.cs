using System.Text.Json;
using DevFlow.Core;
using Microsoft.Data.Sqlite;

namespace DevFlow.Infrastructure;

public sealed partial class SqliteTaskStore
{
    private static List<ToolPermissionRule> ReadPermissionRules(SqliteConnection c, SqliteTransaction tx)
    {
        using var command = Command(c, tx, "SELECT Payload FROM ToolPermissionRules ORDER BY rowid DESC");
        using var reader = command.ExecuteReader();
        var rules = new List<ToolPermissionRule>();
        while (reader.Read()) rules.Add(JsonSerializer.Deserialize<ToolPermissionRule>(reader.GetString(0))!);
        return rules;
    }
    public Task<IReadOnlyList<ToolPermissionRule>> ListPermissionRulesAsync(CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        return Task.FromResult<IReadOnlyList<ToolPermissionRule>>(ReadPermissionRules(c, tx));
    }
    public Task<ToolPermissionRule> SavePermissionRuleAsync(ToolPermissionRule rule, int? expectedVersion, CancellationToken ct = default)
    {
        rule = ToolPermissionMatching.Validate(rule);
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var existing = ReadOne<ToolPermissionRule>(c, tx, "ToolPermissionRules", rule.Id);
        if (existing is not null && expectedVersion != existing.Version || existing is null && expectedVersion is not null)
            throw new ConflictException("工具規則已改變，請重新讀取後再儲存。");
        if (ReadOne<ProjectDefinition>(c, tx, "Projects", rule.ProjectId) is null) throw new MissingEntityException("Project not found.");
        if (rule.TaskId is { } id && ReadOne<WorkTask>(c, tx, "Tasks", id)?.ProjectId != rule.ProjectId) throw new ArgumentException("任務不屬於此專案。");
        if (rule.DiscussionId is { } d && ReadOne<Discussion>(c, tx, "Discussions", d)?.ProjectId != rule.ProjectId) throw new ArgumentException("對話不屬於此專案。");
        rule = rule with { Version = existing is null ? 0 : existing.Version + 1, CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow };
        Execute(c, tx, "INSERT INTO ToolPermissionRules VALUES($id,$payload) ON CONFLICT(Id) DO UPDATE SET Payload=$payload", ("$id", rule.Id.ToString()), ("$payload", JsonSerializer.Serialize(rule)));
        tx.Commit(); return Task.FromResult(rule);
    }
    public Task DeletePermissionRuleAsync(Guid id, int expectedVersion, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var rule = ReadOne<ToolPermissionRule>(c, tx, "ToolPermissionRules", id) ?? throw new MissingEntityException("工具規則不存在。");
        if (rule.Version != expectedVersion) throw new ConflictException("工具規則已改變，請重新讀取。");
        Execute(c, tx, "DELETE FROM ToolPermissionRules WHERE Id=$id", ("$id", id.ToString()));
        tx.Commit(); return Task.CompletedTask;
    }
    public async Task<ToolPermissionRule?> MatchDiscussionRuleAsync(Guid projectId, Guid discussionId, string provider, string tool, JsonElement args, string workspace, CancellationToken ct) =>
        (await ListPermissionRulesAsync(ct)).FirstOrDefault(r => ToolPermissionMatching.Matches(r, projectId, Guid.Empty, tool, args, workspace, provider, discussionId));
}
