using DevFlow.Infrastructure;
namespace DevFlow.Api;
public sealed record RecoverWorkspaceRequest(Guid PreviewId, bool OldProcessStopped);
public static class RecoveryEndpoints
{
    public static void MapRecoveryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/tasks/{id:guid}/recovery/preview", async (Guid id, HttpContext context, WorkspaceRecoveryService recovery, CancellationToken ct) => {
            AiEndpoints.ExplicitAction(context); return await recovery.PreviewAsync(id, ct);
        });
        app.MapPost("/api/tasks/{id:guid}/recovery", async (Guid id, RecoverWorkspaceRequest input, HttpContext context, WorkspaceRecoveryService recovery, CancellationToken ct) => {
            AiEndpoints.ExplicitAction(context);
            var task = await recovery.RecoverAsync(id, input.PreviewId, input.OldProcessStopped, ct);
            return Results.Created("/api/tasks/" + task.Id, task);
        });
    }
}
