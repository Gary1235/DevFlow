using DevFlow.Core;
using DevFlow.Infrastructure;

namespace DevFlow.Api;

public sealed record PermissionRuleInput(Guid ProjectId, Guid? TaskId, string Tool, string Kind, string Pattern, bool Enabled = true, int? ExpectedVersion = null, string? Provider = null, Guid? DiscussionId = null);
public sealed record PermissionPolicyInput(int ExpectedVersion, bool AutomationEnabled, string[]? SensitivePaths,
    string[]? SensitiveCommands, string[]? SensitiveDestinations, string[]? ApprovedDownloadHosts);
public static class ToolPermissionEndpoints
{
    public static void MapToolPermissionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tool-permission-policy", (SqliteTaskStore store, CancellationToken ct) => store.GetPermissionPolicyAsync(ct));
        app.MapPut("/api/tool-permission-policy", (PermissionPolicyInput input, HttpContext context, SqliteTaskStore store, CancellationToken ct) => {
            AiEndpoints.ExplicitAction(context);
            return store.SavePermissionPolicyAsync(new(AutomationEnabled: input.AutomationEnabled, SensitivePaths: input.SensitivePaths,
                SensitiveCommands: input.SensitiveCommands, SensitiveDestinations: input.SensitiveDestinations, ApprovedDownloadHosts: input.ApprovedDownloadHosts), input.ExpectedVersion, ct);
        });
        app.MapGet("/api/tool-permission-rules", (SqliteTaskStore store, CancellationToken ct) => store.ListPermissionRulesAsync(ct));
        app.MapPost("/api/tool-permission-rules", async (PermissionRuleInput input, HttpContext context, SqliteTaskStore store, CancellationToken ct) =>
        {
            AiEndpoints.ExplicitAction(context);
            RequireBatchRule(input);
            var rule = await store.SavePermissionRuleAsync(new(Guid.NewGuid(), input.ProjectId, input.TaskId, input.Tool, input.Kind, input.Pattern, input.Enabled, Provider: input.Provider ?? "*", DiscussionId: input.DiscussionId), null, ct);
            return Results.Created("/api/tool-permission-rules/" + rule.Id, rule);
        });
        app.MapPut("/api/tool-permission-rules/{id:guid}", async (Guid id, PermissionRuleInput input, HttpContext context, SqliteTaskStore store, CancellationToken ct) =>
        {
            AiEndpoints.ExplicitAction(context);
            if (input.ExpectedVersion is null) throw new ArgumentException("請提供目前規則版本。");
            var existing = (await store.ListPermissionRulesAsync(ct)).SingleOrDefault(r=>r.Id==id) ?? throw new MissingEntityException("工具規則不存在。");
            if (input.Enabled) RequireBatchRule(input);
            return Results.Ok(await store.SavePermissionRuleAsync(new(id, input.ProjectId, input.TaskId, input.Tool, input.Kind, input.Pattern, input.Enabled, Provider: input.Provider ?? existing.Provider, DiscussionId: input.DiscussionId), input.ExpectedVersion, ct));
        });
        app.MapDelete("/api/tool-permission-rules/{id:guid}", async (Guid id, int expectedVersion, HttpContext context, SqliteTaskStore store, CancellationToken ct) =>
        {
            AiEndpoints.ExplicitAction(context);
            await store.DeletePermissionRuleAsync(id, expectedVersion, ct); return Results.Ok(new { deleted = true });
        });
    }
    private static void RequireBatchRule(PermissionRuleInput input)
    {
        if (input.Tool != "general_read" || input.Kind != "GeneralRead" || input.Pattern != "." || input.TaskId is null && input.DiscussionId is null)
            throw new ArgumentException("僅能建立本次任務或對話的一般讀取規則；敏感操作不能批次或永久允許。");
    }
}
