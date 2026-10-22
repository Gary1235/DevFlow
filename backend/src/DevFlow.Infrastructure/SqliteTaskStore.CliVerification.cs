using System.Text.Json;
using DevFlow.Core;
namespace DevFlow.Infrastructure;

public sealed partial class SqliteTaskStore : ICliVerificationStore
{
    public Task<CliVerification?> GetCliVerificationAsync(string provider, CancellationToken ct = default)
    {
        using var c = Open(ct);
        using var cmd = Command(c, null, "SELECT Payload FROM CliVerifications WHERE Provider=$provider", ("$provider", provider));
        return Task.FromResult(cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CliVerification>(json) : null);
    }
    public Task SaveCliVerificationAsync(CliVerification verification, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        using var read = Command(c, tx, "SELECT Payload FROM CliVerifications WHERE Provider=$provider", ("$provider", verification.Provider));
        var old = read.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CliVerification>(json) : null;
        if (old is null || old.ObservedAt <= verification.ObservedAt)
            Execute(c, tx, "INSERT INTO CliVerifications VALUES($provider,$payload) ON CONFLICT(Provider) DO UPDATE SET Payload=$payload",
                ("$provider", verification.Provider), ("$payload", JsonSerializer.Serialize(verification)));
        tx.Commit(); return Task.CompletedTask;
    }
}
