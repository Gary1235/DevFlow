using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Runners;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
var database = Path.GetFullPath(builder.Configuration["DevFlow:DatabasePath"] ?? Path.Combine(Environment.CurrentDirectory, ".data", "devflow.db"));
var artifacts = Path.GetFullPath(builder.Configuration["DevFlow:ArtifactsRoot"] ?? Path.Combine(Environment.CurrentDirectory, ".data", "artifacts"));
var worktrees = Path.GetFullPath(builder.Configuration["DevFlow:WorktreeRoot"] ??
    Path.Combine(Directory.GetParent(Environment.CurrentDirectory)!.FullName, "DevFlow.Worktrees"));
var registry = new CliRegistry(builder.Configuration["DevFlow:CodexExecutable"], builder.Configuration["DevFlow:GeminiExecutable"], builder.Configuration["DevFlow:NodeExecutable"],
    claude: builder.Configuration["DevFlow:ClaudeExecutable"]);
if (!registry.List().Any(p => p.Installed))
    throw new InvalidOperationException("Install or configure at least one native AI CLI before starting the Worker.");
var timeoutSeconds = builder.Configuration.GetValue("DevFlow:ExecutionTimeoutSeconds", 1800);
if (timeoutSeconds <= 0) throw new ArgumentException("Execution timeout must be positive.");
Directory.CreateDirectory(Path.GetDirectoryName(database)!);
// One local worker per database. Own the lock before recovering interrupted runs.
using var workerLock = new FileStream(database + ".worker.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var store = new SqliteTaskStore(database);
await store.InitializeAsync();
await store.RecoverInterruptedAsync();
builder.Services.AddSingleton<ITaskStore>(store);
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IWorkspaceManager>(sp => new GitWorkspaceManager(sp.GetRequiredService<IProcessRunner>(), worktrees));
builder.Services.AddSingleton(registry);
builder.Services.AddSingleton(sp => new RoutedCliRunner(sp.GetRequiredService<IProcessRunner>(), registry, artifacts));
builder.Services.AddSingleton<IAgentRunner>(sp => sp.GetRequiredService<RoutedCliRunner>());
builder.Services.AddSingleton<IValidationRunner>(sp => new DotnetValidationRunner(sp.GetRequiredService<IProcessRunner>(), artifacts));
builder.Services.AddSingleton<ExecutionCoordinator>();
builder.Services.AddSingleton<IPlanningRunner>(sp => sp.GetRequiredService<RoutedCliRunner>());
builder.Services.AddSingleton<PlanningCoordinator>();
builder.Services.AddSingleton(new WorkerSettings(timeoutSeconds));
builder.Services.AddHostedService<TaskWorker>();
await builder.Build().RunAsync();

internal sealed record WorkerSettings(int TimeoutSeconds);
internal sealed class TaskWorker(ExecutionCoordinator coordinator, PlanningCoordinator planning, WorkerSettings settings, ILogger<TaskWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DevFlow Worker started.");
        bool preferPlanning = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            try
            {
                var worked = preferPlanning
                    ? await planning.ExecuteNextAsync(timeout.Token) || await coordinator.ExecuteNextAsync(timeout.Token)
                    : await coordinator.ExecuteNextAsync(timeout.Token) || await planning.ExecuteNextAsync(timeout.Token);
                preferPlanning = !preferPlanning;
                if (!worked)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker iteration failed.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
