namespace DevFlow.Core;

// Evidence from a completed model call, shared by the API and Worker. No credentials or output.
public sealed record CliVerification(string Provider, string CommandIdentity, DateTimeOffset ObservedAt,
    bool Successful, string? Model, string Source);
public interface ICliVerificationStore
{
    Task<CliVerification?> GetCliVerificationAsync(string provider, CancellationToken ct = default);
    Task SaveCliVerificationAsync(CliVerification verification, CancellationToken ct = default);
}
