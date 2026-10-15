using DevFlow.Contracts;
using DevFlow.Core;
namespace DevFlow.Api;

public static class PlanningArtifactEndpoints
{
    public static void MapPlanningArtifactEndpoints(this WebApplication app)
    {
        app.MapPost("/api/tasks/{id:guid}/generate-plan", async (Guid id, GeneratePlanRequest input, ITaskStore store, CancellationToken ct) =>
        {
            var task = await store.QueuePlanningAsync(id, input.ExpectedVersion, ct);
            return Results.Accepted($"/api/tasks/{id}", task);
        });
        app.MapGet("/api/tasks/{id:guid}/plans", async (Guid id, ITaskStore store, CancellationToken ct) =>
        {
            if (await store.GetTaskAsync(id, ct) is null) throw new MissingEntityException("Task not found.");
            return Results.Ok(await store.ListPlansAsync(id, ct));
        });
        app.MapGet("/api/runs/{id:guid}/artifacts", async (Guid id, ITaskStore store, IRunArtifactReader reader, CancellationToken ct) =>
        {
            await FindRun(id, store, ct);
            return Results.Ok(await reader.ListAsync(id, ct));
        });
        app.MapGet("/api/runs/{id:guid}/artifacts/{**path}", async (Guid id, string path, int? offset, int? limit,
            ITaskStore store, IRunArtifactReader reader, CancellationToken ct) =>
        {
            await FindRun(id, store, ct);
            return Results.Ok(await reader.ReadAsync(id, path, offset ?? 0, limit ?? 65536, ct));
        });
        app.MapGet("/api/runs/{id:guid}/validations", async (Guid id, ITaskStore store, IRunArtifactReader reader, CancellationToken ct) =>
        {
            await FindRun(id, store, ct);
            return Results.Ok(await reader.ValidationsAsync(id, ct));
        });
    }
    private static async Task FindRun(Guid id, ITaskStore store, CancellationToken ct)
    {
        if (await store.GetRunAsync(id, ct) is null) throw new MissingEntityException("Run not found.");
    }
}
