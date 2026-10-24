using DevFlow.Core;
using DevFlow.Infrastructure;
namespace DevFlow.Api;

public sealed record ProjectExecutionRequest(int ExpectedVersion, ValidationProfile[]? ValidationProfiles,
    string? DefaultValidationProfileId, PreviewService[]? PreviewServices, AiConfiguration? AiDefaults = null);
public sealed record ProjectProbeRequest(int ExpectedVersion, ValidationProfile Profile);
public sealed class ValidationProbeLifetime(ValidationProbeService probes) : IHostedService
{
    public Task StartAsync(CancellationToken ct) { probes.RecoverInterrupted(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) => probes.WaitForStopAsync(ct);
}
public static class ExecutionSettingsEndpoints
{
    public static void MapExecutionSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{id:guid}/execution-discovery", async (Guid id, SqliteTaskStore store, ProjectInspector inspector, CancellationToken ct) =>
            inspector.Discover(await store.GetProjectAsync(id, ct) ?? throw new MissingEntityException("Project not found."), ct));
        app.MapPost("/api/projects/{id:guid}/execution-preflight", async (Guid id, ProjectProbeRequest input, HttpContext context, SqliteTaskStore store, ProjectInspector inspector, CancellationToken ct) => {
            AiEndpoints.ExplicitAction(context);
            var project = await store.GetProjectAsync(id, ct) ?? throw new MissingEntityException("Project not found.");
            if (project.Version != input.ExpectedVersion) throw new ConflictException("專案版本已改變，請重新開啟設定。");
            return await inspector.CheckAsync(project, input.Profile, ct);
        });
        app.MapPost("/api/projects/{id:guid}/execution-probes", async (Guid id, ProjectProbeRequest input, HttpContext context, SqliteTaskStore store, ValidationProbeService probes, CancellationToken ct) => {
            AiEndpoints.ExplicitAction(context);
            var project = await store.GetProjectAsync(id, ct) ?? throw new MissingEntityException("Project not found.");
            if (project.Version != input.ExpectedVersion) throw new ConflictException("專案版本已改變，請重新開啟設定。");
            var probe = probes.Start(project, input.Profile);
            return Results.Accepted("/api/execution-probes/" + probe.Id, probe);
        });
        app.MapGet("/api/execution-probes/{id:guid}", (Guid id, ValidationProbeService probes) => probes.Get(id));
        app.MapGet("/api/projects/{id:guid}/execution-probes", async (Guid id, SqliteTaskStore store, ValidationProbeService probes, CancellationToken ct) => {
            if (await store.GetProjectAsync(id, ct) is null) throw new MissingEntityException("Project not found.");
            return probes.List(id);
        });
        app.MapPost("/api/execution-probes/{id:guid}/cancel", (Guid id, HttpContext context, ValidationProbeService probes) => {
            AiEndpoints.ExplicitAction(context); return probes.Cancel(id);
        });
        app.MapPut("/api/projects/{id:guid}/execution-settings", (Guid id, ProjectExecutionRequest input, HttpContext context, SqliteTaskStore store, CancellationToken ct) => {
            AiEndpoints.ExplicitAction(context);
            return store.SetExecutionSettingsAsync(id, input.ExpectedVersion, input.ValidationProfiles, input.DefaultValidationProfileId, input.PreviewServices, input.AiDefaults, ct);
        });
    }
}
