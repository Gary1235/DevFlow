using System.Text.Json;
namespace DevFlow.Infrastructure;
public sealed partial class SqliteTaskStore
{
    public void SaveValidationProbe(ValidationProbe probe)
    {
        using var c = Open(default);
        Execute(c, null, """
            INSERT INTO ValidationProbes VALUES($id,$project,$state,$created,$payload)
            ON CONFLICT(Id) DO UPDATE SET State=excluded.State,Payload=excluded.Payload
            """, ("$id", probe.Id.ToString()), ("$project", probe.ProjectId.ToString()), ("$state", probe.State),
            ("$created", probe.CreatedAt.ToString("O")), ("$payload", JsonSerializer.Serialize(probe)));
    }
    public ValidationProbe? GetValidationProbe(Guid id)
    {
        using var c = Open(default);
        return ReadOne<ValidationProbe>(c, null, "ValidationProbes", id);
    }
    public IReadOnlyList<ValidationProbe> ListValidationProbes(Guid projectId)
    {
        using var c = Open(default);
        return ReadList<ValidationProbe>(c, "SELECT Payload FROM ValidationProbes WHERE ProjectId=$id ORDER BY CreatedAt DESC,Id DESC LIMIT 20", ("$id", projectId.ToString()));
    }
    // Caller owns the probe process lock. Lost ownership never authorizes cleanup.
    public void InterruptValidationProbes()
    {
        using var c = Open(default); using var tx = c.BeginTransaction();
        using var cmd = Command(c, tx, "SELECT Payload FROM ValidationProbes WHERE State IN ('Running','Stopping')");
        var probes = new List<ValidationProbe>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) probes.Add(JsonSerializer.Deserialize<ValidationProbe>(reader.GetString(0))!);
        foreach (var p in probes) {
            var interrupted = p with { State = "Interrupted", FinishedAt = DateTimeOffset.UtcNow, RetainedWorkspace = p.WorkspacePath,
                Error = "API 上次未正常完成設定測試；未自動重跑或清除副本。請先確認舊驗證程序已停止，再檢查保留內容。" };
            Execute(c, tx, "UPDATE ValidationProbes SET State=$state,Payload=$payload WHERE Id=$id",
                ("$state", interrupted.State), ("$payload", JsonSerializer.Serialize(interrupted)), ("$id", p.Id.ToString()));
        }
        tx.Commit();
    }
}
