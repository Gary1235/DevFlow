using System.Text.Json;
using DevFlow.Core;
namespace DevFlow.Infrastructure;
public sealed record PermissionHistoryPage(TaskToolApproval[] Items, long? NextBefore);
public sealed partial class SqliteTaskStore
{
    public Task<IReadOnlyList<TaskToolApproval>> RecentToolApprovalsAsync(Guid taskId, CancellationToken ct)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        using var cmd = Command(c, tx, """
            SELECT Payload FROM ToolApprovals WHERE rowid IN (
                SELECT rowid FROM ToolApprovals WHERE TaskId=$task AND json_extract(Payload,'$.State')='Pending'
                UNION SELECT rowid FROM (SELECT rowid FROM ToolApprovals WHERE TaskId=$task ORDER BY rowid DESC LIMIT 100)
            ) ORDER BY rowid
            """, ("$task", taskId.ToString()));
        var requests = new List<TaskToolApproval>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) requests.Add(JsonSerializer.Deserialize<TaskToolApproval>(reader.GetString(0))!);
        for (var i = 0; i < requests.Count; i++) {
            var request = requests[i];
            if (request.State == "Pending" && !ApprovalRunActive(c, tx, taskId, request.OperationId)) {
                requests[i] = request with { State = "Expired", EndReason = "執行已結束或取消。" }; SaveApproval(c, tx, requests[i]);
            }
        }
        tx.Commit(); return Task.FromResult<IReadOnlyList<TaskToolApproval>>(requests);
    }
    public Task<PermissionHistoryPage> PermissionHistoryAsync(Guid taskId, Guid runId, long? before, int limit = 100, CancellationToken ct = default)
    {
        if (limit is < 1 or > 200 || before is <= 0) throw new ArgumentException("分頁參數無效。");
        using var c = Open(ct);
        using var cmd = Command(c, null, """
            SELECT rowid,Payload FROM ToolApprovals WHERE TaskId=$task AND RunId=$run AND rowid<$before
            AND json_extract(Payload,'$.State')<>'Pending' ORDER BY rowid DESC LIMIT $limit
            """, ("$task", taskId.ToString()), ("$run", runId.ToString()), ("$before", before ?? long.MaxValue), ("$limit", limit + 1));
        var rows = new List<(long Row, TaskToolApproval Approval)>();
        using var reader = cmd.ExecuteReader(); while (reader.Read()) rows.Add((reader.GetInt64(0), JsonSerializer.Deserialize<TaskToolApproval>(reader.GetString(1))!));
        return Task.FromResult(new PermissionHistoryPage(rows.Take(limit).Select(p => p.Approval).ToArray(), rows.Count > limit ? rows[limit - 1].Row : null));
    }
}
