using System.Text.Json.Serialization;
using DevFlow.Contracts;
using DevFlow.Core;
using DevFlow.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["DevFlow:ApiUrl"] ?? "http://127.0.0.1:5180");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<ITaskStore>(_ => new SqliteTaskStore(
    builder.Configuration["DevFlow:DatabasePath"] ?? Path.Combine(Environment.CurrentDirectory, ".data", "devflow.db")));
var app = builder.Build();
await app.Services.GetRequiredService<ITaskStore>().InitializeAsync();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (Exception ex) when (ex is ArgumentException or ConflictException or MissingEntityException)
    {
        var code = ex is ConflictException ? 409 : ex is MissingEntityException ? 404 : 400;
        await Results.Problem(statusCode: code, title: ex.Message).ExecuteAsync(context);
    }
});
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/projects", (ITaskStore store, CancellationToken ct) => store.ListProjectsAsync(ct));
app.MapPost("/api/projects", async (CreateProjectRequest input, ITaskStore store, CancellationToken ct) =>
{
    var name = WorkTask.Required(input.Name, 200);
    var path = Path.GetFullPath(WorkTask.Required(input.RepositoryPath, 4096));
    if (!Directory.Exists(path)) throw new ArgumentException("Repository directory does not exist.");
    var branch = WorkTask.Required(input.BaseBranch, 200);
    if (branch.StartsWith('-')) throw new ArgumentException("Invalid base branch.");
    DotnetValidationRunner.ValidateArguments(input.ValidationArguments);
    var project = new ProjectDefinition(Guid.NewGuid(), name, path, branch, input.ValidationArguments);
    await store.AddProjectAsync(project, ct);
    return Results.Created($"/api/projects/{project.Id}", project);
});
app.MapGet("/api/projects/{id:guid}", async (Guid id, ITaskStore store, CancellationToken ct) =>
    await store.GetProjectAsync(id, ct) is { } p ? Results.Ok(p) : Results.NotFound());
app.MapGet("/api/tasks", (ITaskStore store, CancellationToken ct) => store.ListTasksAsync(ct));
app.MapPost("/api/tasks", async (CreateTaskRequest input, ITaskStore store, CancellationToken ct) =>
{
    var task = WorkTask.Create(input.ProjectId, input.Title, input.Description, input.AcceptanceCriteria);
    await store.AddTaskAsync(task, ct);
    return Results.Created($"/api/tasks/{task.Id}", task);
});
app.MapGet("/api/tasks/{id:guid}", async (Guid id, ITaskStore store, CancellationToken ct) => Results.Ok(await Find(id, store, ct)));
app.MapPost("/api/tasks/{id:guid}/plan", async (Guid id, SetPlanRequest input, ITaskStore store, CancellationToken ct) =>
{
    var task = await Find(id, store, ct);
    var updated = task.SetPlan(input.Text);
    await store.SaveTaskAsync(updated, task.Version, ct);
    return Results.Ok(updated);
});
app.MapPost("/api/tasks/{id:guid}/approve-plan", async (Guid id, ITaskStore store, CancellationToken ct) =>
{
    var task = await Find(id, store, ct);
    var updated = task.ApprovePlan();
    await store.SaveTaskAsync(updated, task.Version, ct);
    return Results.Ok(updated);
});
app.MapPost("/api/tasks/{id:guid}/cancel", async (Guid id, ITaskStore store, CancellationToken ct) =>
{
    var task = await Find(id, store, ct);
    var updated = task.Cancel();
    await store.SaveTaskAsync(updated, task.Version, ct);
    return Results.Ok(updated);
});
app.MapGet("/api/tasks/{id:guid}/runs", async (Guid id, ITaskStore store, CancellationToken ct) =>
{ await Find(id, store, ct); return Results.Ok(await store.ListRunsAsync(id, ct)); });
app.MapGet("/api/tasks/{id:guid}/events", async (Guid id, long? after, ITaskStore store, CancellationToken ct) =>
{
    if (after < 0) throw new ArgumentException("Event cursor cannot be negative.");
    await Find(id, store, ct);
    return Results.Ok(await store.GetEventsAsync(id, after ?? 0, ct));
});
app.Run();
static async Task<WorkTask> Find(Guid id, ITaskStore store, CancellationToken ct) =>
    await store.GetTaskAsync(id, ct) ?? throw new MissingEntityException("Task not found.");
public partial class Program;
