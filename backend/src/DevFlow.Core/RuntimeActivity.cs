namespace DevFlow.Core;

public sealed record RuntimeUpdate(DateTimeOffset At, string Text);
public sealed record RuntimeSnapshot(DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, string Stage,
    DateTimeOffset LastActivityAt, DateTimeOffset? LastOutputAt, string? Process, bool ProcessRunning,
    long OutputCharacters, RuntimeUpdate[] Recent, string? CurrentWork = null);

public sealed class RuntimeActivity
{
    private readonly object sync = new();
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly Dictionary<Guid, string> processes = [];
    private readonly List<RuntimeUpdate> recent = [];
    private string stage = "準備執行";
    private DateTimeOffset lastActivity = DateTimeOffset.UtcNow;
    private DateTimeOffset? lastOutput;
    private long outputCharacters;
    private string? currentWork;

    public RuntimeSnapshot Snapshot()
    {
        lock (sync) return new(started, DateTimeOffset.UtcNow, stage, lastActivity, lastOutput,
            processes.Values.LastOrDefault(), processes.Count > 0, outputCharacters, recent.ToArray(), currentWork);
    }
    public void Stage(string text)
    {
        lock (sync) { stage = text; currentWork = null; Add(text); }
    }
    public void Observe(ProcessActivity activity)
    {
        lock (sync)
        {
            if (activity.Kind == "Started")
            {
                var name = Path.GetFileName(activity.Executable);
                processes[activity.Id] = name;
                Add("啟動程序：" + name);
            }
            else if (activity.Kind == "Output")
            {
                lastOutput = lastActivity = DateTimeOffset.UtcNow;
                outputCharacters += activity.Characters;
            }
            else if (activity.Kind == "Work" && activity.Summary is { } summary && summary != currentWork)
            {
                currentWork = summary;
                Add(summary);
            }
            else if (activity.Kind == "Finished")
            {
                if (processes.Remove(activity.Id, out var name))
                    Add("程序結束：" + name + (activity.ExitCode is { } exit ? "（exit " + exit + "）" : "（已中止）"));
            }
        }
    }
    private void Add(string text)
    {
        lastActivity = DateTimeOffset.UtcNow;
        recent.Add(new(lastActivity, text));
        if (recent.Count > 12) recent.RemoveAt(0);
    }
}

// The worker and API may be separate processes. Persist only the latest snapshot per run,
// rather than adding heartbeat messages to the task's event history.
public sealed class TaskActivityReporter : IAsyncDisposable
{
    private readonly ITaskStore store;
    private readonly Guid runId;
    private readonly CancellationTokenSource stop = new();
    private readonly Task heartbeat;
    private readonly IDisposable observer;
    public RuntimeActivity Activity { get; } = new();

    public TaskActivityReporter(ITaskStore store, Guid runId)
    {
        this.store = store; this.runId = runId;
        observer = ProcessOutput.ObserveActivity(Activity.Observe);
        heartbeat = HeartbeatAsync();
    }
    public async Task StageAsync(string stage, CancellationToken ct)
    {
        Activity.Stage(stage);
        await store.SetRunActivityAsync(runId, Activity.Snapshot(), ct);
    }
    private async Task HeartbeatAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                // A failed heartbeat leaves its old timestamp visible as stale; never rerun work.
                try { await store.SetRunActivityAsync(runId, Activity.Snapshot(), stop.Token); }
                catch (Exception) when (!stop.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        observer.Dispose();
        stop.Cancel();
        await heartbeat;
        stop.Dispose();
    }
}
