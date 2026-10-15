using DevFlow.Contracts;
using DevFlow.Core;
namespace DevFlow.Api;

public static class MergeEndpoints
{
    public static void MapMergeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{id:guid}/git-state", async (Guid id, ITaskStore store, IGitMergeService git, CancellationToken ct) =>
        {
            var project = await store.GetProjectAsync(id, ct) ?? throw new MissingEntityException("Project not found.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return Results.Ok(await git.InspectAsync(project, timeout.Token));
        });
        app.MapPost("/api/tasks/{id:guid}/merge", async (Guid id, MergeTaskRequest input, MergeCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.MergeAsync(id, input.RunId, input.ResultCommit, input.ExpectedTargetCommit, input.ExpectedVersion, ct)));
        app.MapGet("/api/tasks/{id:guid}/merges", async (Guid id, ITaskStore store, CancellationToken ct) =>
        {
            if (await store.GetTaskAsync(id, ct) is null) throw new MissingEntityException("Task not found.");
            return Results.Ok(await store.ListMergesAsync(id, ct));
        });
        app.MapPost("/api/tasks/{id:guid}/merge/reconcile", async (Guid id, MergeCoordinator coordinator, CancellationToken ct) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return Results.Ok(await coordinator.ReconcileAsync(id, timeout.Token));
        });
    }
}
