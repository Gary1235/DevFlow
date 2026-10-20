using System.Text.Json;

namespace DevFlow.Core;

public interface IToolApprovalOperation
{
    Guid Id { get; }
    CancellationToken Token { get; }
    string Provider => "antigravity";
    bool ReadOnly => true;
    Task<bool> RequestPermissionAsync(string tool, JsonElement arguments, string workspace, CancellationToken ct);
}

public sealed record TaskToolApproval(Guid Id, Guid TaskId, Guid OperationId, string Tool, JsonElement Arguments,
    string Workspace, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string State = "Pending")
{
    public Guid? MatchedRuleId { get; init; }
    public string Provider { get; init; } = "antigravity";
    public string ApprovalScope { get; init; } = "Once";
    public PermissionAssessment Risk => PermissionRisk.Assess(Tool, Arguments, Workspace);
    public ToolPermissionOption[] RuleOptions => ToolPermissionMatching.Options(Tool, Arguments, Workspace);
}

public interface ITaskToolApprovalStore
{
    Task AddToolApprovalAsync(TaskToolApproval request, CancellationToken ct = default);
    Task<IReadOnlyList<TaskToolApproval>> ListToolApprovalsAsync(Guid taskId, CancellationToken ct = default);
    Task ReplyToolApprovalAsync(Guid taskId, Guid requestId, Guid operationId, bool allow, CancellationToken ct = default);
    Task ExpireToolApprovalAsync(Guid taskId, Guid requestId, CancellationToken ct = default);
}
