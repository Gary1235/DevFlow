using DevFlow.Infrastructure;
namespace DevFlow.Api;

public sealed record StartPreviewRequest(Guid RunId, string ResultCommit, int ExpectedVersion, int ProjectVersion);
public static class PreviewEndpoints
{
    public static void MapPreviewEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tasks/{id:guid}/preview", (Guid id, HttpContext context, TaskPreviewManager previews) => { AiEndpoints.ExplicitAction(context); return previews.Get(id); });
        app.MapPost("/api/tasks/{id:guid}/preview/start", async (Guid id, StartPreviewRequest input, HttpContext context, TaskPreviewManager previews, CancellationToken ct) => {
            AiEndpoints.ExplicitAction(context);
            return await previews.StartAsync(id, input.RunId, input.ResultCommit, input.ExpectedVersion, input.ProjectVersion, ct);
        });
        app.MapPost("/api/tasks/{id:guid}/preview/stop", async (Guid id, HttpContext context, TaskPreviewManager previews, CancellationToken ct) => {
            AiEndpoints.ExplicitAction(context); return await previews.StopAsync(id, ct);
        });
    }
}

public sealed class PreviewLifetime(TaskPreviewManager previews) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => previews.DisposeAsync().AsTask();
}
