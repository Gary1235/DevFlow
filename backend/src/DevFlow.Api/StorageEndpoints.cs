using DevFlow.Infrastructure;

namespace DevFlow.Api;

public sealed record StoragePreviewRequest(int OlderThanDays = 30, bool Artifacts = true, bool Worktrees = true);
public sealed record StorageCleanupRequest(Guid PreviewId, string[] Keys);

public static class StorageEndpoints
{
    public static void MapStorageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/storage", (HttpContext context, StorageManager storage, CancellationToken ct) =>
        { AiEndpoints.ExplicitAction(context); return storage.OverviewAsync(ct); });
        app.MapPost("/api/storage/preview", (StoragePreviewRequest input, HttpContext context, StorageManager storage, CancellationToken ct) =>
        { AiEndpoints.ExplicitAction(context); return storage.PreviewAsync(input.OlderThanDays, input.Artifacts, input.Worktrees, ct); });
        app.MapPost("/api/storage/cleanup", (StorageCleanupRequest input, HttpContext context, StorageManager storage, CancellationToken ct) =>
        { AiEndpoints.ExplicitAction(context); return storage.CleanAsync(input.PreviewId, input.Keys, ct); });
    }
}
