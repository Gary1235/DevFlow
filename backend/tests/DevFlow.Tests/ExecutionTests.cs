using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Tests;

public sealed class ExecutionTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-execution-" + Guid.NewGuid());
    private SqliteTaskStore store = null!;
    private WorkTask task = null!;
    public async Task InitializeAsync()
    {
        store = new(Path.Combine(directory, "tasks.db"));
        await store.InitializeAsync();
        var project = new ProjectDefinition(Guid.NewGuid(), "Demo", directory, "main", ["test"]);
        await store.AddProjectAsync(project);
        task = WorkTask.Create(project.Id, "API", "Build", "Tests").SetPlan("TDD").ApprovePlan();
        await store.AddTaskAsync(task);
    }
    public Task DisposeAsync() { Directory.Delete(directory, true); return Task.CompletedTask; }
    private sealed class FakeAgent : IAgentRunner
    {
        public List<TddPhase> Phases { get; } = [];
        public Task ExecuteAsync(WorkTask t, Workspace w, Guid r, TddPhase p, CancellationToken ct)
        { Phases.Add(p); return Task.CompletedTask; }
    }
    private sealed class FakeWorkspace : IWorkspaceManager
    {
        public bool Snapshotted { get; private set; }
        public Task<Workspace> CreateAsync(ProjectDefinition p, Guid r, CancellationToken ct) => Task.FromResult(new Workspace("worktree", "base"));
        public Task<string> SnapshotAsync(Workspace w, CancellationToken ct) { Snapshotted = true; return Task.FromResult("result"); }
    }
    private sealed class FakeValidator(params ValidationEvidence[] evidence) : IValidationRunner
    {
        private int index;
        public ValidationEvidence Baseline { get; init; } = Green;
        public int BaselineCalls { get; private set; }
        public Task<ValidationEvidence> ValidateAsync(ProjectDefinition p, Workspace w, Guid r, TddPhase phase, CancellationToken ct)
        {
            if (phase == TddPhase.Baseline) { BaselineCalls++; return Task.FromResult(Baseline); }
            return Task.FromResult(evidence[index++]);
        }
    }
    private static ValidationEvidence Red => new(1, 1, 1, [], ["feature-test"]);
    private sealed class BlockingAgent : IAgentRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(WorkTask t, Workspace w, Guid r, TddPhase p, CancellationToken ct)
        { Started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
    }
    [Fact] public async Task Cancelling_running_task_stops_agent_and_retains_workspace()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var agent = new BlockingAgent();
        var execution = new ExecutionCoordinator(store, agent, new FakeWorkspace(), new FakeValidator(Red)).ExecuteNextAsync(cts.Token);
        try
        {
            await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var running = (await store.GetTaskAsync(task.Id))!;
            await store.SaveTaskAsync(running.Cancel(), running.Version);
            await execution.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(Status.Cancelled, (await store.GetTaskAsync(task.Id))!.Status);
            Assert.Equal("worktree", (await store.ListRunsAsync(task.Id)).Single().WorktreePath);
        }
        finally { cts.Cancel(); await execution; }
    }
    private static ValidationEvidence Green => new(0, 1, 0, ["feature-test"], []);
    [Fact] public async Task Successful_tdd_requires_red_green_refactor_before_review()
    {
        var agent = new FakeAgent(); var workspace = new FakeWorkspace();
        var coordinator = new ExecutionCoordinator(store, agent, workspace, new FakeValidator(Red, Green, Green));
        Assert.True(await coordinator.ExecuteNextAsync());
        Assert.Equal(new[] { TddPhase.Red, TddPhase.Green, TddPhase.Refactor }, agent.Phases);
        Assert.True(workspace.Snapshotted);
        Assert.Equal(Status.AwaitingReview, (await store.GetTaskAsync(task.Id))!.Status);
        Assert.Equal("result", (await store.ListRunsAsync(task.Id)).Single().ResultCommit);
        Assert.False(await coordinator.ExecuteNextAsync());
    }
    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 0)]
    public async Task Already_passing_or_build_error_is_not_valid_red(int exit, int total, int failed)
    {
        var agent = new FakeAgent(); var workspace = new FakeWorkspace();
        await new ExecutionCoordinator(store, agent, workspace, new FakeValidator(new ValidationEvidence(exit, total, failed, [], []))).ExecuteNextAsync();
        Assert.Equal(Status.Failed, (await store.GetTaskAsync(task.Id))!.Status);
        Assert.Single(agent.Phases);
        Assert.False(workspace.Snapshotted);
    }
    [Fact] public async Task Deleting_failed_test_cannot_satisfy_green()
    {
        var agent = new FakeAgent();
        await new ExecutionCoordinator(store, agent, new FakeWorkspace(),
            new FakeValidator(Red, new(0, 1, 0, ["unrelated-test"], []))).ExecuteNextAsync();
        Assert.Equal(Status.Failed, (await store.GetTaskAsync(task.Id))!.Status);
        Assert.Equal(2, agent.Phases.Count);
    }
    [Fact] public async Task Failed_refactor_does_not_enter_review()
    {
        await new ExecutionCoordinator(store, new FakeAgent(), new FakeWorkspace(), new FakeValidator(Red, Green, Red)).ExecuteNextAsync();
        Assert.Equal(Status.Failed, (await store.GetTaskAsync(task.Id))!.Status);
    }
    [Fact] public async Task Existing_failing_tests_block_agent_execution()
    {
        var agent = new FakeAgent();
        var validator = new FakeValidator(Red, Green, Green) { Baseline = Red };
        await new ExecutionCoordinator(store, agent, new FakeWorkspace(), validator).ExecuteNextAsync();
        Assert.Equal(1, validator.BaselineCalls);
        Assert.Empty(agent.Phases);
        Assert.Equal(Status.Failed, (await store.GetTaskAsync(task.Id))!.Status);
    }
    [Fact] public async Task Trx_parser_distinguishes_test_failure_from_build_failure()
    {
        var path = Path.Combine(directory, "red.trx");
        await File.WriteAllTextAsync(path, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testId="test-1" outcome="Failed" /></Results>
              <ResultSummary><Counters total="1" failed="1" passed="0" /></ResultSummary>
            </TestRun>
            """);
        var evidence = TrxEvidence.Read(path, 1);
        Assert.Equal(1, evidence.Failed);
        Assert.Contains("test-1", evidence.FailedTests);
        Assert.Equal(0, TrxEvidence.Read(Path.Combine(directory, "missing.trx"), 1).Total);
    }
}
