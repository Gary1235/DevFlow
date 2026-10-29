using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Runners;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

if (args is ["--devflow-mcp", var mcpPipe])
{
    await StructuredMcpBridge.RunClientAsync(mcpPipe);
    return;
}
if (args is ["--agy-permission-hook", var pipeName])
{
    await NativePermissionHook.RunClientAsync(pipeName);
    return;
}
var builder = Host.CreateApplicationBuilder(args);
var paths = RuntimePaths.Resolve(Environment.CurrentDirectory, AppContext.BaseDirectory,
    builder.Configuration["DevFlow:DatabasePath"], builder.Configuration["DevFlow:ArtifactsRoot"], builder.Configuration["DevFlow:WorktreeRoot"], builder.Configuration["DevFlow:CliControlRoot"]);
if (builder.Configuration.GetValue<bool>("DevFlow:DesktopManaged") || builder.Configuration.GetValue<bool>("DevFlow:LauncherManaged"))
    builder.Services.AddHostedService<DevFlow.Desktop.DesktopLifetime>();
var database = paths.Database;
var artifacts = paths.Artifacts;
var worktrees = paths.Worktrees;
var registry = new CliRegistry(builder.Configuration["DevFlow:CodexExecutable"], builder.Configuration["DevFlow:GeminiExecutable"], builder.Configuration["DevFlow:NodeExecutable"],
    claude: builder.Configuration["DevFlow:ClaudeExecutable"],
    antigravity: builder.Configuration["DevFlow:AntigravityExecutable"]);
if (!builder.Configuration.GetValue<bool>("DevFlow:DesktopManaged") && !registry.List(includeLegacy: true).Any(p => p.Installed))
    throw new InvalidOperationException("Install or configure at least one native AI CLI before starting the Worker.");
var timeoutSeconds = builder.Configuration.GetValue("DevFlow:ExecutionTimeoutSeconds", 0);
if (timeoutSeconds < 0) throw new ArgumentException("Execution timeout must be zero (unlimited) or positive.");
Directory.CreateDirectory(Path.GetDirectoryName(database)!);
// One local worker per database. Own the lock before recovering interrupted runs.
using var workerLock = new FileStream(database + ".worker.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var store = new SqliteTaskStore(database);
await store.InitializeAsync();
await store.RecoverInterruptedAsync();
builder.Services.AddSingleton<ITaskStore>(store);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<WorkerState>();
builder.Services.AddHostedService<WorkerHeartbeat>();
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IWorkspaceManager>(sp => new GitWorkspaceManager(sp.GetRequiredService<IProcessRunner>(), worktrees));
builder.Services.AddSingleton(registry);
builder.Services.AddSingleton(new CliMaintenanceGate(paths.CliControl));
builder.Services.AddSingleton(sp => new RoutedCliRunner(sp.GetRequiredService<IProcessRunner>(), registry, artifacts, store, typeof(TaskWorker).Assembly.Location, store,
    builder.Configuration.GetValue("DevFlow:StructuredToolsEnabled", true) ? new StructuredToolFactory(store, sp.GetRequiredService<IProcessRunner>(), artifacts) : null));
builder.Services.AddSingleton<IAgentRunner>(sp => sp.GetRequiredService<RoutedCliRunner>());
builder.Services.AddSingleton<IValidationRunner>(sp => new DotnetValidationRunner(sp.GetRequiredService<IProcessRunner>(), artifacts));
builder.Services.AddSingleton<ExecutionCoordinator>();
builder.Services.AddSingleton(new DiscussionImageStore(artifacts));
builder.Services.AddSingleton<ITaskImageLoader,TaskImageLoader>();
builder.Services.AddSingleton<IPlanningRunner>(sp => sp.GetRequiredService<RoutedCliRunner>());
builder.Services.AddSingleton<IResultDiffReader, GitResultDiffReader>();
builder.Services.AddSingleton<IExecutionPolicyGuard, GitExecutionPolicyGuard>();
builder.Services.AddSingleton<PlanningCoordinator>();
var concurrency = builder.Configuration.GetValue("DevFlow:MaxConcurrentTasks", 2);
if (concurrency is < 1 or > 4) throw new ArgumentException("MaxConcurrentTasks must be between 1 and 4.");
builder.Services.AddSingleton(new WorkerSettings(timeoutSeconds, concurrency));
builder.Services.AddHostedService<TaskWorker>();
await builder.Build().RunAsync();

internal sealed record WorkerSettings(int TimeoutSeconds, int Concurrency);
internal sealed class TaskWorker(ExecutionCoordinator coordinator, PlanningCoordinator planning, WorkerSettings settings, CliMaintenanceGate gate, CliRegistry registry, WorkerState state, ILogger<TaskWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DevFlow Worker started with {Concurrency} slots.", settings.Concurrency);
        await Task.WhenAll(Enumerable.Range(0, settings.Concurrency).Select(slot => Task.Run(() => RunSlotAsync(slot, stoppingToken), stoppingToken)));
    }
    private async Task RunSlotAsync(int slot, CancellationToken stoppingToken)
    {
        bool preferPlanning = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            if (settings.TimeoutSeconds > 0) timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            try
            {
                bool worked;
                using (gate.EnterExecution())
                {
                    state.Slots[slot] = "Working";
                    registry.Refresh();
                    worked = preferPlanning
                        ? await planning.ExecuteNextAsync(timeout.Token) || await coordinator.ExecuteNextAsync(timeout.Token)
                        : await coordinator.ExecuteNextAsync(timeout.Token) || await planning.ExecuteNextAsync(timeout.Token);
                }
                preferPlanning = !preferPlanning;
                if (!worked)
                {
                    state.Slots[slot] = "Idle";
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (ConflictException) { state.Slots[slot] = "Waiting"; await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker iteration failed.");
                state.Slots[slot] = "Error";
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}

internal sealed class WorkerState
{
    public Guid InstanceId { get; } = Guid.NewGuid();
    public System.Collections.Concurrent.ConcurrentDictionary<int, string> Slots { get; } = new();
    public string State => Slots.Values.Contains("Working") ? "Working" : Slots.Values.Contains("Error") ? "Error" : Slots.Values.Contains("Waiting") ? "Waiting" : "Idle";
}
internal sealed class WorkerHeartbeat(SqliteTaskStore store, WorkerState state, ILogger<WorkerHeartbeat> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            do
            {
                try {
                    var pending = await store.ListPendingPermissionsAsync(stoppingToken);
                    await store.RecordWorkerPulseAsync(new(state.InstanceId, Environment.ProcessId, pending.Count > 0 ? "WaitingPermission" : state.State, DateTimeOffset.UtcNow), stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(ex, "Cannot report Worker presence."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            try { await store.RecordWorkerPulseAsync(new(state.InstanceId, Environment.ProcessId, "Stopped", DateTimeOffset.UtcNow)); }
            catch (Exception ex) { logger.LogWarning(ex, "Cannot report Worker shutdown."); }
        }
    }
}
