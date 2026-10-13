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
var executable = builder.Configuration["DevFlow:CodexExecutable"];
if (string.IsNullOrWhiteSpace(executable))
    throw new InvalidOperationException("Set DevFlow__CodexExecutable to a native Codex CLI executable before starting the Worker.");
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
builder.Services.AddSingleton<IAgentRunner>(sp => new CodexCliRunner(sp.GetRequiredService<IProcessRunner>(), executable, artifacts));
builder.Services.AddSingleton<IValidationRunner>(sp => new DotnetValidationRunner(sp.GetRequiredService<IProcessRunner>(), artifacts));
builder.Services.AddSingleton<ExecutionCoordinator>();
builder.Services.AddSingleton(new WorkerSettings(timeoutSeconds));
builder.Services.AddHostedService<TaskWorker>();
await builder.Build().RunAsync();

internal sealed record WorkerSettings(int TimeoutSeconds);
internal sealed class TaskWorker(ExecutionCoordinator coordinator, WorkerSettings settings, ILogger<TaskWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DevFlow Worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            try
            {
                if (!await coordinator.ExecuteNextAsync(timeout.Token))
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
