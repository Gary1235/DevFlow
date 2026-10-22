using System.Text.Json;
using DevFlow.Core;
using Microsoft.Data.Sqlite;
namespace DevFlow.Infrastructure;

public sealed partial class SqliteTaskStore
{
    private static PermissionPolicySettings ReadPolicy(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = Command(c, tx, "SELECT Payload FROM PermissionPolicies WHERE Id='global'");
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<PermissionPolicySettings>(json)! : PermissionPolicySettings.Defaults;
    }
    public Task<PermissionPolicySettings> GetPermissionPolicyAsync(CancellationToken ct = default)
    { using var c = Open(ct); return Task.FromResult(ReadPolicy(c, null)); }
    public Task<PermissionPolicySettings> SavePermissionPolicyAsync(PermissionPolicySettings settings, int expectedVersion, CancellationToken ct = default)
    {
        settings = PermissionPolicySettings.Normalize(settings);
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var old = ReadPolicy(c, tx);
        if (old.Version != expectedVersion) throw new ConflictException("權限政策已改變，請重新讀取後再保存。");
        var updated = settings with { Version = old.Version + 1 };
        Execute(c, tx, "INSERT INTO PermissionPolicies VALUES('global',$payload) ON CONFLICT(Id) DO UPDATE SET Payload=$payload", ("$payload", JsonSerializer.Serialize(updated)));
        tx.Commit(); return Task.FromResult(updated);
    }
}
