using System.Text.Json;

namespace DevFlow.Core;

public interface IToolApprovalOperation
{
    Guid Id { get; }
    CancellationToken Token { get; }
    string Provider => "antigravity";
    bool ReadOnly => true;
    string? Failure => null;
    Task<bool> RequestPermissionAsync(string tool, JsonElement arguments, string workspace, CancellationToken ct);
}

public sealed record TaskToolApproval(Guid Id, Guid TaskId, Guid OperationId, string Tool, JsonElement Arguments,
    string Workspace, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string State = "Pending")
{
    public DateTimeOffset ReminderAt => CreatedAt.AddMinutes(2);
    public string? EndReason { get; init; }
    public Guid? MatchedRuleId { get; init; }
    public string Provider { get; init; } = "antigravity";
    public string ApprovalScope { get; init; } = "Once";
    public bool ReadOnly { get; init; }
    public int? PolicyVersion { get; init; }
    public PermissionAssessment? PolicyAssessment { get; init; }
    public PermissionAssessment Risk => PolicyAssessment ?? PermissionRisk.Assess(Tool, Arguments, Workspace);
    public ToolPermissionOption[] RuleOptions => ToolPermissionMatching.Options(Tool, Arguments, Workspace);
}

public interface ITaskToolApprovalStore
{
    Task AddToolApprovalAsync(TaskToolApproval request, CancellationToken ct = default);
    Task<IReadOnlyList<TaskToolApproval>> ListToolApprovalsAsync(Guid taskId, CancellationToken ct = default);
    async Task<TaskToolApproval?> GetToolApprovalAsync(Guid taskId, Guid requestId, CancellationToken ct = default) =>
        (await ListToolApprovalsAsync(taskId, ct)).SingleOrDefault(r => r.Id == requestId);
    Task ReplyToolApprovalAsync(Guid taskId, Guid requestId, Guid operationId, bool allow, CancellationToken ct = default);
    Task ExpireToolApprovalAsync(Guid taskId, Guid requestId, CancellationToken ct = default, string? reason = null);
}

public sealed record PendingPermission(Guid Id, Guid OwnerId, string Kind, string Title, string Tool, string Provider,
    DateTimeOffset CreatedAt, DateTimeOffset ReminderAt, string ReasonCode, string Reason);
