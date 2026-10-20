using DevFlow.Core;
using DevFlow.Infrastructure;
using System.Text.Json;
using Xunit;

namespace DevFlow.Tests;

public sealed class RuntimeActivityTests
{
    [Fact]
    public async Task Active_worker_persists_heartbeat_for_separate_api_reader_and_completion_wins()
    {
        var root = Path.Combine(Path.GetTempPath(), "devflow-activity-" + Guid.NewGuid());
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var database = Path.Combine(root, "tasks.db");
            var store = new SqliteTaskStore(database);
            await store.InitializeAsync();
            var project = new ProjectDefinition(Guid.NewGuid(), "Test", root, "main", ["test"]);
            await store.AddProjectAsync(project);
            var task = WorkTask.Create(project.Id, "Test", "Build", "Pass").SetPlan("Implement").ApprovePlan()
                with { Policy = new(false, []) };
            await store.AddTaskAsync(task);
            var agent = new WaitingAgent();
            var work = new ExecutionCoordinator(store, agent, new WorkspaceStub(), new ValidationStub()).ExecuteNextAsync(cancel.Token);
            try
            {
                await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var reader = new SqliteTaskStore(database);
                var initial = Assert.Single(await reader.ListRunsAsync(task.Id)).Activity!;
                Assert.Contains("AI 實作", initial.Stage);
                RuntimeSnapshot? pulse = null;
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                while (await timer.WaitForNextTickAsync(cancel.Token))
                {
                    pulse = Assert.Single(await reader.ListRunsAsync(task.Id)).Activity;
                    if (pulse?.ProcessRunning == true) break;
                }
                Assert.True(pulse!.UpdatedAt > initial.UpdatedAt);
                Assert.Equal("fake-cli", pulse.Process);
                Assert.Equal(16, pulse.OutputCharacters);
                // Heartbeats update a snapshot, not the event history or task version.
                Assert.DoesNotContain(await reader.GetEventsAsync(task.Id), e => e.Kind == "Heartbeat");
                cancel.Cancel();
                await work;
                var finished = Assert.Single(await reader.ListRunsAsync(task.Id));
                Assert.NotNull(finished.FinishedAt);
                await reader.SetRunActivityAsync(finished.Id, pulse with { Stage = "late heartbeat" });
                Assert.NotEqual("late heartbeat", (await reader.GetRunAsync(finished.Id))!.Activity!.Stage);
            }
            finally { cancel.Cancel(); await work; }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Process_activity_includes_stderr_but_never_exposes_its_text_or_command_arguments()
    {
        var activity = new RuntimeActivity();
        using var observer = ProcessOutput.ObserveActivity(activity.Observe);
        var executable = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var args = OperatingSystem.IsWindows() ? new[] { "/d", "/c", "echo private-test-data 1>&2" } : new[] { "-c", "echo private-test-data >&2" };
        var result = await new ProcessRunner().RunAsync(executable, args, Path.GetTempPath(), null, default);
        var snapshot = activity.Snapshot();
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("private-test-data", result.StandardError);
        Assert.True(snapshot.OutputCharacters >= 17);
        Assert.NotNull(snapshot.LastOutputAt);
        Assert.False(snapshot.ProcessRunning);
        Assert.Contains(snapshot.Recent, e => e.Text.Contains("啟動程序"));
        Assert.Contains(snapshot.Recent, e => e.Text.Contains("exit 0"));
        Assert.DoesNotContain("private-test-data", JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public async Task Activity_observers_are_isolated_and_nested_scopes_restore_previous_observer()
    {
        var first = new RuntimeActivity(); var second = new RuntimeActivity();
        await Task.WhenAll(Task.Run(async () =>
        {
            using var observer = ProcessOutput.ObserveActivity(first.Observe);
            await Task.Yield();
            ProcessOutput.ActivityObserver!(new(Guid.NewGuid(), "first-cli", "Started"));
            using (ProcessOutput.ObserveActivity(_ => { })) ProcessOutput.ActivityObserver!(new(Guid.NewGuid(), "hidden", "Started"));
            ProcessOutput.ActivityObserver!(new(Guid.NewGuid(), "first-again", "Started"));
        }), Task.Run(async () =>
        {
            using var observer = ProcessOutput.ObserveActivity(second.Observe);
            await Task.Yield();
            ProcessOutput.ActivityObserver!(new(Guid.NewGuid(), "second-cli", "Started"));
        }));
        Assert.Equal("first-again", first.Snapshot().Process);
        Assert.Equal("second-cli", second.Snapshot().Process);
        Assert.Null(ProcessOutput.ActivityObserver);
    }

    private sealed class WaitingAgent : IAgentRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(WorkTask task, Workspace workspace, Guid runId, TddPhase phase, CancellationToken ct)
        {
            var id = Guid.NewGuid();
            ProcessOutput.ActivityObserver!(new(id, "fake-cli", "Started"));
            ProcessOutput.ActivityObserver!(new(id, "fake-cli", "Output", 16));
            Started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { ProcessOutput.ActivityObserver!(new(id, "fake-cli", "Finished")); }
        }
    }
    private sealed class WorkspaceStub : IWorkspaceManager
    {
        public Task<Workspace> CreateAsync(ProjectDefinition p, Guid id, CancellationToken ct) => Task.FromResult(new Workspace("unused", "base"));
        public Task AssertUnchangedAsync(Workspace w, CancellationToken ct) => Task.CompletedTask;
        public Task<string> SnapshotAsync(Workspace w, CancellationToken ct) => Task.FromResult("result");
    }
    private sealed class ValidationStub : IValidationRunner
    {
        public Task<ValidationEvidence> ValidateAsync(ProjectDefinition p, Workspace w, Guid id, TddPhase phase, CancellationToken ct) =>
            Task.FromResult(new ValidationEvidence(0, 1, 0, ["pass"], []));
    }
}
