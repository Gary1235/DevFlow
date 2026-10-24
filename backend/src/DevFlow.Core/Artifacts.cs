namespace DevFlow.Core;
public sealed record ArtifactInfo(string Path, long Bytes);
public sealed record ArtifactPage(string Path, string Text, int Offset, int NextOffset, bool HasMore);
public sealed record ValidationSummary(TddPhase Phase, int ExitCode, int Total, int Failed, string EvidencePath, Guid? StepId = null,
    string Kind = "DotnetTest", string? Error = null, int Attempt = 0, string Origin = "Worker");
public interface IRunArtifactReader
{
    Task<IReadOnlyList<ArtifactInfo>> ListAsync(Guid runId, CancellationToken ct = default);
    Task<ArtifactPage> ReadAsync(Guid runId, string path, int offset = 0, int limit = 65536, CancellationToken ct = default);
    Task<IReadOnlyList<ValidationSummary>> ValidationsAsync(Guid runId, CancellationToken ct = default);
}
