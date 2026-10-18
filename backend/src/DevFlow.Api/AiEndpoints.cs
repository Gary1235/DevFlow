using DevFlow.Core;
using DevFlow.Contracts;
using DevFlow.Runners;


namespace DevFlow.Api;
public sealed record LoginRequest(string Mode = "browser");
public sealed record BrowseFoldersRequest(string? Path = null);
public sealed record CliUpdateRequest(Guid CheckId);
public static class AiEndpoints
{
    public static void MapAiEndpoints(this WebApplication app)
    {
        app.MapPost("/api/ai/{provider}/cli/check", async (string provider, HttpContext context, CliMaintenance maintenance, CancellationToken ct) =>
        { ExplicitAction(context); return Results.Ok(await maintenance.CheckAsync(provider, ct)); });
        app.MapPost("/api/ai/{provider}/cli/update", async (string provider, CliUpdateRequest input, HttpContext context, CliMaintenance maintenance, CancellationToken ct) =>
        {
            ExplicitAction(context);
            var session = await maintenance.StartAsync(provider, input.CheckId, ct);
            return Results.Accepted($"/api/ai/cli/updates/{session.Id}", session);
        });
        app.MapGet("/api/ai/cli/updates/{id:guid}", (Guid id, CliMaintenance maintenance) => maintenance.Get(id));
        app.MapGet("/api/ai/providers", async (CliDiagnostics diagnostics, bool? refresh, CancellationToken ct) => await diagnostics.ListProvidersAsync(ct, refresh == true));
        app.MapGet("/api/ai/{provider}/diagnostics", (string provider, CliDiagnostics diagnostics, CancellationToken ct) => diagnostics.InspectAsync(provider, ct));
        app.MapPost("/api/ai/{provider}/probe", async (string provider, AiSelection input, HttpContext context, CliDiagnostics diagnostics, CancellationToken ct) =>
        {
            ExplicitAction(context);
            var selection = AiSelection.Normalize(input);
            if (selection.Provider != provider) throw new ArgumentException("Provider does not match request.");
            return Results.Ok(await diagnostics.ProbeAsync(selection, ct));
        });
        app.MapPost("/api/ai/{provider}/login", (string provider, LoginRequest input, HttpContext context, CliLoginManager logins) =>
        {
            ExplicitAction(context);
            var session = logins.Start(provider, input.Mode);
            return Results.Accepted($"/api/ai/logins/{session.Id}", session);
        });
        app.MapGet("/api/ai/logins/{id:guid}", (Guid id, CliLoginManager logins) => logins.Get(id));
        app.MapPost("/api/ai/logins/{id:guid}/cancel", (Guid id, HttpContext context, CliLoginManager logins) =>
        { ExplicitAction(context); return Results.Ok(logins.Cancel(id)); });
        app.MapPut("/api/projects/{id:guid}/ai-defaults", (Guid id, SetProjectAiRequest input, ITaskStore store, CancellationToken ct) =>
            store.SetProjectAiAsync(id, input.AiDefaults, input.ExpectedVersion, ct));
        app.MapPost("/api/projects/folders", (BrowseFoldersRequest input, HttpContext context) =>
        {
            ExplicitAction(context);
            return Results.Ok(DevFlow.Infrastructure.LocalFolderBrowser.Browse(input.Path));
        });
        app.MapPost("/api/projects/pick-folder", async (BrowseFoldersRequest input, HttpContext context, DevFlow.Infrastructure.IFolderPicker picker, CancellationToken ct) =>
        {
            ExplicitAction(context);
            return Results.Ok(new { path = await picker.PickAsync(input.Path, ct) });
        });
        var ui = Path.Combine(AppContext.BaseDirectory, "ui");
        var assets = Path.Combine(ui, "assets");
        if (Directory.Exists(assets)) app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(assets),
            RequestPath = "/assets"
        });
        app.MapGet("/", () => File.Exists(Path.Combine(ui, "index.html"))
            ? Results.File(Path.Combine(ui, "index.html"), "text/html; charset=utf-8")
            : Results.Problem("Build frontend with npm run build before building or publishing the API.", statusCode: 503));
    }
    internal static void ExplicitAction(HttpContext context)
    {
        if (context.Request.Headers["X-DevFlow-Action"] != "1") throw new ArgumentException("Explicit local action header is required.");
        if (context.Request.Host.Host.ToLowerInvariant() is not ("localhost" or "127.0.0.1" or "::1" or "[::1]"))
            throw new ArgumentException("CLI actions require a localhost host name.");
        if (context.Connection.RemoteIpAddress is { } address && !System.Net.IPAddress.IsLoopback(address))
            throw new ArgumentException("CLI actions are only available on localhost.");
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase) || uri.Scheme != context.Request.Scheme))
            throw new ArgumentException("CLI actions require a same-origin request.");
    }
}
