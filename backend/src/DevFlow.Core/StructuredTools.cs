using System.Text.Json;
using System.Text.Json.Serialization;
namespace DevFlow.Core;

public sealed record StructuredToolDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] JsonElement InputSchema)
{
    [JsonPropertyName("type")] public string Type => "function";
}
public interface IStructuredTools
{
    IReadOnlyList<StructuredToolDefinition> Definitions { get; }
    Task<string> ExecuteAsync(string tool, JsonElement arguments, CancellationToken ct);
}
public interface IStructuredToolFactory
{
    IStructuredTools Create(WorkTask task, Workspace workspace, Guid runId, IToolApprovalOperation operation);
}
