using System.Text.Json;
using DevFlow.Core;
using DevFlow.Infrastructure;

namespace DevFlow.Api;

public sealed record ToolApproval(Guid Id, Guid OperationId, string Tool, JsonElement Arguments, string Workspace,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string State = "Pending")
{
    public DateTimeOffset ReminderAt => CreatedAt.AddMinutes(2);
    public string? EndReason { get; init; }
    public string Provider { get; init; } = "antigravity";
    public string ApprovalScope { get; init; } = "Once";
    public Guid? MatchedRuleId { get; init; }
    public bool RulesAvailable { get; init; }
    public int? PolicyVersion { get; init; }
    public PermissionAssessment? PolicyAssessment { get; init; }
    public PermissionAssessment Risk => PolicyAssessment ?? PermissionRisk.Assess(Tool, Arguments, Workspace);
    public ToolPermissionOption[] RuleOptions => ToolPermissionMatching.Options(Tool, Arguments, Workspace);
}
public sealed record ToolApprovalReply(Guid OperationId, bool Allow, ToolPermissionChoice? Rule = null);

public sealed partial class DiscussionOperations
{
    public PendingPermission[] PendingPermissions() => running.ToArray().SelectMany(pair => pair.Value.Permissions
        .Where(p => p.State == "Pending").Select(p => new PendingPermission(p.Id, pair.Key, "discussion", "需求討論", p.Tool,
            p.Provider, p.CreatedAt, p.ReminderAt, p.Risk.ReasonCode, p.Risk.Reason))).ToArray();

    public void ReplyPermission(Guid discussionId, Guid requestId, ToolApprovalReply reply)
    {
        if (!running.TryGetValue(discussionId, out var operation) || operation.Id != reply.OperationId)
            throw new ConflictException("這次回覆已結束，請重新整理對話。");
        operation.ReplyPermission(requestId, reply.Allow, reply.Rule);
    }

    public sealed partial class Operation
    {
        private sealed record PendingApproval(ToolApproval Request, TaskCompletionSource<bool> Completion);
        private readonly Dictionary<Guid, PendingApproval> permissions = [];
        private SqliteTaskStore? ruleStore;
        private Guid ruleProject, ruleDiscussion;
        public string? Failure { get; private set; }
        public void ConfigurePermissions(SqliteTaskStore store, Guid projectId, Guid discussionId)
        { ruleStore = store; ruleProject = projectId; ruleDiscussion = discussionId; }
        public ToolApproval[] Permissions { get { lock (sync) return permissions.Values.Select(p => p.Request).ToArray(); } }

        public async Task<bool> RequestPermissionAsync(string tool, JsonElement arguments, string workspace, CancellationToken ct)
        {
            tool = WorkTask.Required(tool, 100);
            if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 20000)
                throw new ArgumentException("工具參數格式無效或過長。");
            var policy = ruleStore is null ? null : await ruleStore.GetPermissionPolicyAsync(ct);
            var risk = policy is null ? PermissionRisk.Assess(tool, arguments, workspace) : PermissionPolicy.Assess(policy, tool, arguments, workspace, ReadOnly);
            var matched = ruleStore is null || policy!.AutomationEnabled ? null : await ruleStore.MatchDiscussionRuleAsync(ruleProject, ruleDiscussion, Provider, tool, arguments, workspace, ct);
            var request = new ToolApproval(Guid.NewGuid(), Id, tool, arguments.Clone(), workspace, DateTimeOffset.UtcNow, null)
            { Provider = Provider, RulesAvailable = ruleStore is not null, State = risk.AutoAllowed ? "AllowedByPolicy" : matched is null ? "Pending" : "AllowedByRule", MatchedRuleId = matched?.Id,
                PolicyVersion = policy?.Version, PolicyAssessment = risk,
                ApprovalScope = risk.AutoAllowed ? "Policy" : matched is null ? "Once" : matched.DiscussionId is null ? "Project" : "Discussion" };
            var pending = new PendingApproval(request, new(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (sync)
            {
                if (disposed || Stopped || Token.IsCancellationRequested) return false;
                if (!risk.AutoAllowed && matched is null && permissions.Values.Count(p => p.Request.State == "Pending") >= 50) throw new ConflictException("同時等待的工具確認已達上限；請先處理待確認項目。");
                permissions.Add(request.Id, pending);
            }
            if (risk.AutoAllowed || matched is not null) return true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, Token);
            try { return await pending.Completion.Task.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) {
                if (!Token.IsCancellationRequested) Failure = "[CliInterrupted] CLI 工具確認通道已中斷，未取得核准。請查看執行紀錄。";
                return false;
            }
            finally
            {
                lock (sync)
                {
                    var item = permissions[request.Id];
                    if (item.Request.State == "Pending")
                        permissions[request.Id] = item with { Request = item.Request with { State = "Expired", EndReason = Failure ?? "執行已結束或取消；此確認不再有效。" } };
                }
            }
        }

        public void ReplyPermission(Guid id, bool allow, ToolPermissionChoice? choice = null)
        {
            lock (sync)
            {
                if (disposed || Stopped || Token.IsCancellationRequested || !permissions.TryGetValue(id, out var item) || item.Request.State != "Pending")
                    throw new ConflictException("此確認已失效或已回覆，請重新整理對話。");
                choice ??= new();
                if (choice.Scope is not ("Once" or "Discussion") || !allow && choice.Scope != "Once") throw new ArgumentException("只允許單次或本次對話的一般讀取授權。");
                ToolPermissionRule? rule = null;
                if (choice.Scope != "Once")
                {
                    if (ruleStore is null) throw new ConflictException("尚未啟用對話權限規則。");
                    var r = item.Request;
                    if (!r.Risk.BatchAllowed || choice.Kind != "GeneralRead") throw new ArgumentException("敏感或未知操作只能單次確認。");
                    rule = ToolPermissionMatching.Validate(new(Guid.NewGuid(), ruleProject, null, "general_read", "GeneralRead",
                        ".", Provider: choice.Provider, DiscussionId: ruleDiscussion));
                    if (!ToolPermissionMatching.Matches(rule, ruleProject, Guid.Empty, r.Tool, r.Arguments, r.Workspace, Provider, ruleDiscussion)) throw new ArgumentException("規則未涵蓋本次操作。");
                    // SQLite methods commit synchronously. Keep validation, persistence and the one-use reply
                    // under the same operation lock so expiry/stop cannot turn a stale click into a grant.
                    rule = ruleStore.SavePermissionRuleAsync(rule, null, Token).GetAwaiter().GetResult();
                }
                permissions[id] = item with { Request = item.Request with { State = allow ? "Allowed" : "Denied", ApprovalScope = choice.Scope, MatchedRuleId = rule?.Id } };
                item.Completion.TrySetResult(allow);
            }
        }
    }
}
