using DevFlow.Core;
using DevFlow.Infrastructure;
namespace DevFlow.Api;

public sealed record ProjectExecutionRequest(int ExpectedVersion, ValidationProfile[]? ValidationProfiles,
    string? DefaultValidationProfileId, PreviewService[]? PreviewServices, AiConfiguration? AiDefaults = null);
public static class ExecutionSettingsEndpoints
{
    public static void MapExecutionSettingsEndpoints(this WebApplication app)
    {
        app.MapPut("/api/projects/{id:guid}/execution-settings", (Guid id, ProjectExecutionRequest input, HttpContext context, SqliteTaskStore store, CancellationToken ct) => {
            AiEndpoints.ExplicitAction(context);
            return store.SetExecutionSettingsAsync(id, input.ExpectedVersion, input.ValidationProfiles, input.DefaultValidationProfileId, input.PreviewServices, input.AiDefaults, ct);
        });
    }
}
