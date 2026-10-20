using DevFlow.Infrastructure;
using Xunit;

namespace DevFlow.Tests;

public sealed class WorkerPresenceTests
{
    [Fact] public void Development_paths_are_identical_from_root_api_worker_and_build_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "devflow-paths-" + Guid.NewGuid());
        try
        {
            var api = Path.Combine(root, "backend", "src", "DevFlow.Api");
            var worker = Path.Combine(root, "backend", "src", "DevFlow.Worker");
            Directory.CreateDirectory(api); Directory.CreateDirectory(worker);
            File.WriteAllText(Path.Combine(api, "DevFlow.Api.csproj"), "");
            File.WriteAllText(Path.Combine(worker, "DevFlow.Worker.csproj"), "");
            var expected = RuntimePaths.Resolve(root, root);
            Assert.Equal(Path.Combine(api, ".data", "devflow.db"), expected.Database);
            Assert.Equal(expected, RuntimePaths.Resolve(api, api));
            Assert.Equal(expected, RuntimePaths.Resolve(worker, worker));
            Assert.Equal(expected, RuntimePaths.Resolve(Path.GetTempPath(), Path.Combine(worker, "bin", "Release", "net10.0")));
            var custom = Path.Combine(root, "desktop", "devflow.db");
            var explicitPaths = RuntimePaths.Resolve(worker, worker, custom, Path.Combine(root, "a"), Path.Combine(root, "w"), Path.Combine(root, "c"));
            Assert.Equal(custom, explicitPaths.Database);
            Assert.Equal(Path.Combine(root, "w"), explicitPaths.Worktrees);
            Assert.False(File.Exists(expected.Database)); // Resolving paths never creates or copies data.
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact] public async Task Presence_belongs_to_this_database_and_stale_or_stopped_workers_are_offline()
    {
        var root = Path.Combine(Path.GetTempPath(), "devflow-presence-" + Guid.NewGuid());
        try
        {
            var api = new SqliteTaskStore(Path.Combine(root, "shared.db")); await api.InitializeAsync();
            var worker = new SqliteTaskStore(api.DatabasePath);
            var other = new SqliteTaskStore(Path.Combine(root, "desktop.db")); await other.InitializeAsync();
            Assert.False((await api.GetWorkerPresenceAsync()).Online);
            var pulse = new WorkerPulse(Guid.NewGuid(), 123, "Idle", DateTimeOffset.UtcNow);
            await worker.RecordWorkerPulseAsync(pulse);
            Assert.True((await api.GetWorkerPresenceAsync()).Online);
            Assert.False((await other.GetWorkerPresenceAsync()).Online);
            await worker.RecordWorkerPulseAsync(pulse with { UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(-20) });
            Assert.False((await api.GetWorkerPresenceAsync()).Online);
            await worker.RecordWorkerPulseAsync(pulse with { State = "Stopped", UpdatedAt = DateTimeOffset.UtcNow });
            Assert.False((await api.GetWorkerPresenceAsync()).Online);
        }
        finally { Directory.Delete(root, true); }
    }
}
