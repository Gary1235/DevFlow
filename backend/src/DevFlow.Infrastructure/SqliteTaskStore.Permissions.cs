using System.Text.Json;
using DevFlow.Core;
using Microsoft.Data.Sqlite;
using Status = DevFlow.Core.TaskStatus;

namespace DevFlow.Infrastructure;

public sealed partial class SqliteTaskStore
{
    public Task<TaskToolApproval?> GetToolApprovalAsync(Guid taskId, Guid requestId, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var request = ReadOne<TaskToolApproval>(c, tx, "ToolApprovals", requestId);
        if (request?.TaskId != taskId) return Task.FromResult<TaskToolApproval?>(null);
        if (request.State == "Pending" && !ApprovalRunActive(c, tx, taskId, request.OperationId)) {
            request = request with { State = "Expired", EndReason = "執行已結束或取消；此確認不再有效。" };
            SaveApproval(c, tx, request);
        }
        tx.Commit(); return Task.FromResult<TaskToolApproval?>(request);
    }

    public Task<IReadOnlyList<PendingPermission>> ListPendingPermissionsAsync(CancellationToken ct = default)
    {
        using var c = Open(ct);
        using var cmd = Command(c, null, """
            SELECT a.Payload,t.Payload FROM ToolApprovals a
            JOIN Tasks t ON t.Id=a.TaskId JOIN Runs r ON r.Id=a.RunId
            WHERE json_extract(a.Payload,'$.State')='Pending'
            AND t.Status IN ($planning,$running) AND json_extract(r.Payload,'$.FinishedAt') IS NULL
            ORDER BY a.rowid
            """, ("$planning", (int)Status.Planning), ("$running", (int)Status.Running));
        using var reader = cmd.ExecuteReader();
        var pending = new List<PendingPermission>();
        while (reader.Read()) {
            var r = JsonSerializer.Deserialize<TaskToolApproval>(reader.GetString(0))!;
            var task = JsonSerializer.Deserialize<WorkTask>(reader.GetString(1))!;
            pending.Add(new(r.Id, task.Id, "task", task.Title, r.Tool, r.Provider, r.CreatedAt, r.ReminderAt, r.Risk.ReasonCode, r.Risk.Reason));
        }
        return Task.FromResult<IReadOnlyList<PendingPermission>>(pending);
    }

    private static bool ApprovalRunActive(SqliteConnection c, SqliteTransaction tx, Guid taskId, Guid runId)
    {
        var task = ReadOne<WorkTask>(c, tx, "Tasks", taskId);
        var run = ReadOne<TaskRun>(c, tx, "Runs", runId);
        return run?.TaskId == taskId && run.FinishedAt is null &&
            (task?.Status == Status.Planning && run.Kind == RunKind.Planning || task?.Status == Status.Running && run.Kind == RunKind.Implementation);
    }

    public Task AddToolApprovalAsync(TaskToolApproval request, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        if (!ApprovalRunActive(c, tx, request.TaskId, request.OperationId)) throw new ConflictException("此規劃已結束。");
        WorkTask.Required(request.Tool, 100);
        if (request.Arguments.ValueKind != JsonValueKind.Object || request.Arguments.GetRawText().Length > 20000 ||
            request.State != "Pending")
            throw new ArgumentException("工具確認格式無效。");
        var task = ReadOne<WorkTask>(c, tx, "Tasks", request.TaskId)!;
        var policy = ReadPolicy(c, tx);
        var run = ReadOne<TaskRun>(c, tx, "Runs", request.OperationId)!;
        var step = task.Workflow?.Steps?.FirstOrDefault(s => s.Id == task.ActiveStepId);
        var project = ReadOne<ProjectDefinition>(c, tx, "Projects", task.ProjectId);
        var assessment = PermissionPolicy.Assess(policy, request.Tool, request.Arguments, request.Workspace, request.ReadOnly || run.Kind == RunKind.Planning,
            [task.Policy?.AllowedPaths ?? [], step?.AllowedPaths ?? []], project);
        request = request with { PolicyAssessment = assessment, PolicyVersion = policy.Version };
        var rule = policy.AutomationEnabled ? null : ReadPermissionRules(c, tx).FirstOrDefault(r => ToolPermissionMatching.Matches(r, task.ProjectId, task.Id, request.Tool, request.Arguments, request.Workspace, request.Provider));
        if (assessment.AutoAllowed) request = request with { State = "AllowedByPolicy", ApprovalScope = "Policy" };
        else if (rule is not null) request = request with { State = "AllowedByRule", MatchedRuleId = rule.Id, ApprovalScope = rule.TaskId is null ? "Project" : "Task" };
        else
        {
            using var count = Command(c, tx, "SELECT COUNT(*) FROM ToolApprovals WHERE RunId=$run AND json_extract(Payload,'$.State')='Pending'", ("$run", request.OperationId.ToString()));
            if (Convert.ToInt64(count.ExecuteScalar()) >= 50) throw new ConflictException("同時等待的工具確認已達上限；請先處理待確認項目。");
        }
        Execute(c, tx, "INSERT INTO ToolApprovals VALUES($id,$task,$run,$payload)", ("$id", request.Id.ToString()),
            ("$task", request.TaskId.ToString()), ("$run", request.OperationId.ToString()), ("$payload", JsonSerializer.Serialize(request)));
        Event(c, tx, request.TaskId, assessment.AutoAllowed ? "ToolApprovalPolicyApplied" : rule is null ? "ToolApprovalRequested" : "ToolApprovalRuleApplied", request.Tool + " · " + assessment.Label);
        tx.Commit(); return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TaskToolApproval>> ListToolApprovalsAsync(Guid taskId, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        using var cmd = Command(c, tx, "SELECT Payload FROM ToolApprovals WHERE TaskId=$task ORDER BY rowid", ("$task", taskId.ToString()));
        var requests = new List<TaskToolApproval>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) requests.Add(JsonSerializer.Deserialize<TaskToolApproval>(reader.GetString(0))!);
        for (var i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            if (r.State == "Pending" && !ApprovalRunActive(c, tx, r.TaskId, r.OperationId))
            {
                requests[i] = r with { State = "Expired", EndReason = "執行已結束或取消；此確認不再有效。" };
                SaveApproval(c, tx, requests[i]);
            }
        }
        tx.Commit(); return Task.FromResult<IReadOnlyList<TaskToolApproval>>(requests);
    }

    public Task ReplyToolApprovalAsync(Guid taskId, Guid requestId, Guid operationId, bool allow, CancellationToken ct = default)
        => ReplyToolApprovalWithRuleAsync(taskId, requestId, operationId, allow, null, ct);

    public Task ReplyToolApprovalWithRuleAsync(Guid taskId, Guid requestId, Guid operationId, bool allow, ToolPermissionChoice? choice, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var r = ReadOne<TaskToolApproval>(c, tx, "ToolApprovals", requestId);
        if (r is null || r.TaskId != taskId || r.OperationId != operationId || r.State != "Pending" ||
            !ApprovalRunActive(c, tx, taskId, operationId))
            throw new ConflictException("此確認已失效或已回覆，請重新整理任務。");
        choice ??= new();
        if (choice.Scope is not ("Once" or "Task") || !allow && choice.Scope != "Once") throw new ArgumentException("只允許單次或本次任務的一般讀取授權。");
        Guid? ruleId = null;
        if (choice.Scope != "Once")
        {
            var task = ReadOne<WorkTask>(c, tx, "Tasks", taskId)!;
            if (!r.Risk.BatchAllowed || choice.Kind != "GeneralRead") throw new ArgumentException("敏感或未知操作只能單次確認。");
            var rule = ToolPermissionMatching.Validate(new(Guid.NewGuid(), task.ProjectId, taskId, "general_read", "GeneralRead", ".", CreatedAt: DateTimeOffset.UtcNow, Provider: choice.Provider));
            if (!ToolPermissionMatching.Matches(rule, task.ProjectId, taskId, r.Tool, r.Arguments, r.Workspace, r.Provider)) throw new ArgumentException("設定的規則未涵蓋本次工具操作，請檢查比對範圍。");
            var existing = ReadPermissionRules(c, tx).FirstOrDefault(x => x.Enabled && x.ProjectId == rule.ProjectId && x.TaskId == rule.TaskId && x.DiscussionId is null && x.Provider == rule.Provider && x.Tool == rule.Tool && x.Kind == rule.Kind && x.Pattern == rule.Pattern);
            if (existing is not null) rule = existing;
            else Execute(c, tx, "INSERT INTO ToolPermissionRules VALUES($id,$payload)", ("$id", rule.Id.ToString()), ("$payload", JsonSerializer.Serialize(rule)));
            ruleId = rule.Id;
        }
        SaveApproval(c, tx, r with { State = allow ? "Allowed" : "Denied", MatchedRuleId = ruleId, ApprovalScope = choice.Scope });
        Event(c, tx, taskId, "ToolApprovalAnswered", allow ? "Allowed" : "Denied");
        tx.Commit(); return Task.CompletedTask;
    }

    public Task ExpireToolApprovalAsync(Guid taskId, Guid requestId, CancellationToken ct = default, string? reason = null)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var r = ReadOne<TaskToolApproval>(c, tx, "ToolApprovals", requestId);
        if (r?.TaskId == taskId && r.State == "Pending") SaveApproval(c, tx, r with { State = "Expired", EndReason = reason ?? "執行已結束或取消；此確認不再有效。" });
        tx.Commit(); return Task.CompletedTask;
    }

    private static void SaveApproval(SqliteConnection c, SqliteTransaction tx, TaskToolApproval request) =>
        Execute(c, tx, "UPDATE ToolApprovals SET Payload=$payload WHERE Id=$id", ("$payload", JsonSerializer.Serialize(request)), ("$id", request.Id.ToString()));
}
