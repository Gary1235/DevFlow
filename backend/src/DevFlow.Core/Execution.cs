namespace DevFlow.Core;

public enum TddPhase { Baseline, Red, Green, Refactor }
public sealed record Workspace(string Path, string BaseCommit);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
public sealed record ValidationEvidence(int ExitCode, int Total, int Failed, string[] PassedTests, string[] FailedTests);
public interface IAgentRunner
{
    Task ExecuteAsync(WorkTask task, Workspace workspace, Guid runId, TddPhase phase, CancellationToken ct);
}
public interface IWorkspaceManager
{
    Task<Workspace> CreateAsync(ProjectDefinition project, Guid runId, CancellationToken ct);
    Task<string> SnapshotAsync(Workspace workspace, CancellationToken ct);
    Task AssertUnchangedAsync(Workspace workspace, CancellationToken ct);
}
public interface IValidationRunner
{
    Task<ValidationEvidence> ValidateAsync(ProjectDefinition project, Workspace workspace, Guid runId, TddPhase phase, CancellationToken ct);
}
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        string? standardInput, CancellationToken ct);
}
public sealed class ExecutionCoordinator(ITaskStore store, IAgentRunner agent, IWorkspaceManager workspaces, IValidationRunner validation)
{
    public async Task<bool> ExecuteNextAsync(CancellationToken ct = default)
    {
        var claim = await store.ClaimNextAsync(ct);
        if (claim is null) return false;
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = execution.Token;
        var monitor = MonitorCancellationAsync(claim.Task.Id, execution);
        Workspace? workspace = null;
        string? resultCommit = null;
        try
        {
            var project = await store.GetProjectAsync(claim.Task.ProjectId, ct) ?? throw new MissingEntityException("Project not found.");
            var baseCommit = claim.Task.RevisionCommit ?? claim.Task.Plan?.BaseCommit;
            var source = baseCommit is { } revision ? project with { BaseBranch = revision } : project;
            workspace = await workspaces.CreateAsync(source, claim.Run.Id, ct);
            await store.SetWorkspaceAsync(claim.Run.Id, workspace, ct);
            await store.AppendEventAsync(claim.Task.Id, "PhaseStarted", TddPhase.Baseline.ToString(), ct);
            var baseline = await validation.ValidateAsync(project, workspace, claim.Run.Id, TddPhase.Baseline, ct);
            if (baseline.ExitCode != 0 || baseline.Failed != 0)
                throw new InvalidOperationException("Baseline validation failed; fix existing failures before starting TDD.");
            await store.AppendEventAsync(claim.Task.Id, "PhaseVerified", TddPhase.Baseline.ToString(), ct);
            string[] redTests = [];
            foreach (var phase in new[] { TddPhase.Red, TddPhase.Green, TddPhase.Refactor })
            {
                ct.ThrowIfCancellationRequested();
                if ((await store.GetTaskAsync(claim.Task.Id, ct))!.Status == TaskStatus.Cancelled)
                    throw new OperationCanceledException("Task cancelled.");
                await store.AppendEventAsync(claim.Task.Id, "PhaseStarted", phase.ToString(), ct);
                await agent.ExecuteAsync(claim.Task, workspace, claim.Run.Id, phase, ct);
                var evidence = await validation.ValidateAsync(project, workspace, claim.Run.Id, phase, ct);
                if (phase == TddPhase.Red)
                {
                    if (evidence.ExitCode == 0 || evidence.Total == 0 || evidence.Failed == 0 || evidence.FailedTests.Length == 0)
                        throw new InvalidOperationException("Red phase requires actual failing tests in a TRX report; build errors do not qualify.");
                    redTests = evidence.FailedTests;
                }
                else if (evidence.ExitCode != 0 || evidence.Total == 0 || evidence.Failed != 0 ||
                    baseline.PassedTests.Except(evidence.PassedTests, StringComparer.Ordinal).Any() ||
                    redTests.Except(evidence.PassedTests, StringComparer.Ordinal).Any())
                    throw new InvalidOperationException($"{phase} must pass, including every test that failed during Red.");
                await store.AppendEventAsync(claim.Task.Id, "PhaseVerified", phase.ToString(), ct);
            }
            ct.ThrowIfCancellationRequested();
            if ((await store.GetTaskAsync(claim.Task.Id, ct))!.Status == TaskStatus.Cancelled)
                throw new OperationCanceledException("Task cancelled.");
            resultCommit = await workspaces.SnapshotAsync(workspace, ct);
            await store.CompleteRunAsync(claim.Run.Id, true, workspace.Path, workspace.BaseCommit, resultCommit, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await store.CompleteRunAsync(claim.Run.Id, false, workspace?.Path, workspace?.BaseCommit,
                resultCommit, ex is OperationCanceledException ? "Execution cancelled, interrupted, or timed out." : ex.Message, CancellationToken.None);
        }
        finally
        {
            execution.Cancel();
            try { await monitor; } catch (OperationCanceledException) { }
        }
        return true;
    }
    private async Task MonitorCancellationAsync(Guid taskId, CancellationTokenSource execution)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        while (await timer.WaitForNextTickAsync(execution.Token))
            if ((await store.GetTaskAsync(taskId, execution.Token))!.Status == TaskStatus.Cancelled)
            { execution.Cancel(); return; }
    }
}
