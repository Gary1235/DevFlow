using System.Text.Json.Serialization;
using DevFlow.Contracts;
using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Api;
using DevFlow.Runners;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration.GetValue<bool>("DevFlow:DesktopManaged"))
    builder.Services.AddHostedService<DevFlow.Desktop.DesktopLifetime>();
builder.WebHost.UseUrls(builder.Configuration["DevFlow:ApiUrl"] ?? "http://127.0.0.1:5180");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IFolderPicker, WindowsFolderPicker>();
builder.Services.AddSingleton(new CliRegistry(builder.Configuration["DevFlow:CodexExecutable"], builder.Configuration["DevFlow:GeminiExecutable"],
    builder.Configuration["DevFlow:NodeExecutable"],
    builder.Configuration.GetSection("DevFlow:Models:Codex").GetChildren().Select(c => c.Value!).ToArray(),
    builder.Configuration.GetSection("DevFlow:Models:Gemini").Exists() ? builder.Configuration.GetSection("DevFlow:Models:Gemini").GetChildren().Select(c => c.Value!).ToArray() : null,
    builder.Configuration["DevFlow:ClaudeExecutable"],
    builder.Configuration.GetSection("DevFlow:Models:Claude").Exists() ? builder.Configuration.GetSection("DevFlow:Models:Claude").GetChildren().Select(c => c.Value!).ToArray() : null));
var cliControl = Path.GetFullPath(builder.Configuration["DevFlow:CliControlRoot"] ?? Path.Combine(Environment.CurrentDirectory, ".data", "cli-control"));
builder.Services.AddSingleton(new CliMaintenanceGate(cliControl));
builder.Services.AddSingleton(sp => new CliDiagnostics(sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<CliRegistry>(), cliControl));
builder.Services.AddSingleton<ILoginTerminalLauncher, WindowsLoginTerminalLauncher>();
builder.Services.AddSingleton(sp => new CliLoginManager(sp.GetRequiredService<CliRegistry>(), sp.GetRequiredService<ILoginTerminalLauncher>(), cliControl));
builder.Services.AddSingleton<IResultDiffReader, GitResultDiffReader>();
builder.Services.AddSingleton<IGitMergeService, GitMergeService>();
builder.Services.AddSingleton<MergeCoordinator>();
builder.Services.AddSingleton<IRunArtifactReader>(_ => new RunArtifactReader(builder.Configuration["DevFlow:ArtifactsRoot"] ??
    Path.Combine(Environment.CurrentDirectory, ".data", "artifacts")));
builder.Services.AddSingleton(_ => new SqliteTaskStore(
    builder.Configuration["DevFlow:DatabasePath"] ?? Path.Combine(Environment.CurrentDirectory, ".data", "devflow.db")));
builder.Services.AddSingleton<ITaskStore>(sp => sp.GetRequiredService<SqliteTaskStore>());
builder.Services.AddSingleton(sp => new CliMaintenance(sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<CliRegistry>(),
    sp.GetRequiredService<CliMaintenanceGate>(), async ct =>
    {
        var store = sp.GetRequiredService<SqliteTaskStore>();
        return sp.GetRequiredService<CliLoginManager>().HasActiveLogins ||
            (await store.ListTasksAsync(ct)).Any(t => t.Status is DevFlow.Core.TaskStatus.Running or DevFlow.Core.TaskStatus.Planning) ||
            (await store.ListDiscussionsAsync(ct)).Any(d => d.ActiveOperation is not null);
    }, cliControl, builder.Configuration["DevFlow:NodeExecutable"], builder.Configuration["DevFlow:NpmCliPath"], builder.Configuration["DevFlow:NpmGlobalRoot"]));
var collaborationArtifacts = Path.GetFullPath(builder.Configuration["DevFlow:ArtifactsRoot"] ?? Path.Combine(Environment.CurrentDirectory, ".data", "artifacts"));
builder.Services.AddSingleton<IPlanningRunner>(sp => new RoutedCliRunner(sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<CliRegistry>(), collaborationArtifacts));
var collaborationWorktrees = Path.GetFullPath(builder.Configuration["DevFlow:WorktreeRoot"] ?? Path.Combine(Directory.GetParent(Environment.CurrentDirectory)!.FullName, "DevFlow.Worktrees"));
builder.Services.AddSingleton<IWorkspaceManager>(sp => new GitWorkspaceManager(sp.GetRequiredService<IProcessRunner>(),
    collaborationWorktrees));
builder.Services.AddSingleton(sp => new StorageManager(sp.GetRequiredService<SqliteTaskStore>(), sp.GetRequiredService<IProcessRunner>(), collaborationArtifacts, collaborationWorktrees));
var app = builder.Build();
// Desktop sessions have a fresh secret injected by Electron, never exposed to React.
var desktopToken = builder.Configuration["DevFlow:DesktopToken"];
if (!string.IsNullOrEmpty(desktopToken))
    app.Use(async (context, next) =>
    {
        var supplied = context.Request.Headers["X-DevFlow-Desktop"].ToString();
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(supplied), System.Text.Encoding.UTF8.GetBytes(desktopToken)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next(context);
    });
await app.Services.GetRequiredService<ITaskStore>().InitializeAsync();
await app.Services.GetRequiredService<SqliteTaskStore>().RecoverDiscussionsAsync();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (Exception ex) when (ex is ArgumentException or ConflictException or MissingEntityException or MergeUncertainException)
    {
        var code = ex is MergeUncertainException ? 503 : ex is ConflictException ? 409 : ex is MissingEntityException ? 404 : 400;
        await Results.Problem(statusCode: code, title: ex.Message).ExecuteAsync(context);
    }
});
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    var usesCli = path.StartsWith("/api/ai/", StringComparison.OrdinalIgnoreCase) && !path.Contains("/cli/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/discussions", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "POST";
    using var lease = usesCli ? app.Services.GetRequiredService<CliMaintenanceGate>().EnterExecution() : null;
    await next(context);
});
app.MapGet("/api/projects", (ITaskStore store, CancellationToken ct) => store.ListProjectsAsync(ct));
app.MapPost("/api/projects", async (CreateProjectRequest input, ITaskStore store, CancellationToken ct) =>
{
    var name = WorkTask.Required(input.Name, 200);
    var path = Path.GetFullPath(WorkTask.Required(input.RepositoryPath, 4096));
    if (!Directory.Exists(path)) throw new ArgumentException("Repository directory does not exist.");
    var branch = WorkTask.Required(input.BaseBranch, 200);
    if (branch.StartsWith('-')) throw new ArgumentException("Invalid base branch.");
    DotnetValidationRunner.ValidateArguments(input.ValidationArguments);
    var project = new ProjectDefinition(Guid.NewGuid(), name, path, branch, input.ValidationArguments, AiConfiguration.Resolve(input.AiDefaults));
    await store.AddProjectAsync(project, ct);
    return Results.Created($"/api/projects/{project.Id}", project);
});
app.MapGet("/api/projects/{id:guid}", async (Guid id, ITaskStore store, CancellationToken ct) =>
    await store.GetProjectAsync(id, ct) is { } p ? Results.Ok(p) : Results.NotFound());
app.MapGet("/api/tasks", (ITaskStore store, CancellationToken ct) => store.ListTasksAsync(ct));
app.MapPost("/api/tasks", async (CreateTaskRequest input, SqliteTaskStore store, CancellationToken ct) =>
{
    var project = await store.GetProjectAsync(input.ProjectId, ct) ?? throw new MissingEntityException("Project not found.");
    var rules = await store.ResolveRulesAsync(input.RuleIds ?? project.DefaultRuleIds ?? [], ct);
    var flow = WorkflowDefinition.Normalize(input.Workflow);
    if (input.SourceDiscussionId.HasValue != input.SourceDraftId.HasValue) throw new ArgumentException("Discussion and draft IDs must be supplied together.");
    if (input.SourceDiscussionId is { } discussionId)
    {
        var discussion = await store.GetDiscussionAsync(discussionId, ct) ?? throw new MissingEntityException("Discussion not found.");
        var draft = discussion.Drafts.SingleOrDefault(d => d.Id == input.SourceDraftId) ?? throw new MissingEntityException("Draft not found.");
        if (draft.OpenQuestions is { Length: > 0 } && !input.AcknowledgeOpenQuestions) throw new ArgumentException("Review the open questions and explicitly acknowledge their handling before creating a task.");
    }
    var task = WorkTask.Create(input.ProjectId, input.Title, input.Description, input.AcceptanceCriteria) with
    { Ai = AiConfiguration.Resolve(project.AiDefaults, input.Planning, input.Implementation), Rules = rules,
      TaskRules = string.IsNullOrWhiteSpace(input.TaskRules) ? null : WorkTask.Required(input.TaskRules, 12000),
      Policy = ExecutionPolicy.Normalize(input.Policy, rules), Workflow = flow,
      SourceDiscussionId = input.SourceDiscussionId, SourceDraftId = input.SourceDraftId };
    if (!flow.PlanningEnabled) task = task.SetPlan("依照已確認的需求與驗收條件直接實作；遵守本次規則及允許編輯範圍。");
    if (input.AutoPlan) task = flow.PlanningEnabled ? task.RequestPlanning() : task.ApprovePlan();
    if (task.SourceDiscussionId.HasValue) task = await store.AddDiscussionTaskAsync(task, ct);
    else await store.AddTaskAsync(task, ct);
    return Results.Created($"/api/tasks/{task.Id}", task);
});
app.MapGet("/api/tasks/{id:guid}", async (Guid id, ITaskStore store, CancellationToken ct) => Results.Ok(await Find(id, store, ct)));
app.MapPost("/api/tasks/{id:guid}/plan", async (Guid id, SetPlanRequest input, ITaskStore store, CancellationToken ct) =>
{
    var task = await Find(id, store, ct);
    if (input.ExpectedVersion.HasValue && input.ExpectedVersion != task.Version)
        throw new ConflictException("Task changed; reload the current plan before saving editor changes.");
    var updated = task.SetPlan(input.Text);
    await store.SaveTaskAsync(updated, task.Version, ct);
    return Results.Ok(updated);
});
app.MapPost("/api/tasks/{id:guid}/approve-plan", async (Guid id, ApprovePlanRequest input, ITaskStore store, CancellationToken ct) =>
{
    var task = await Find(id, store, ct);
    if (task.Version != input.ExpectedVersion || task.Plan?.Revision != input.PlanRevision)
        throw new ConflictException("Task or plan revision changed; review the current plan before approval.");
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
app.MapReviewEndpoints();
app.MapMergeEndpoints();
app.MapPlanningArtifactEndpoints();
app.MapAiEndpoints();
app.MapCollaborationEndpoints();
app.MapStorageEndpoints();
app.Run();
static async Task<WorkTask> Find(Guid id, ITaskStore store, CancellationToken ct) =>
    await store.GetTaskAsync(id, ct) ?? throw new MissingEntityException("Task not found.");
public partial class Program;
