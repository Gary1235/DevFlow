namespace DevFlow.Core;
public interface IPlanningRunner
{
    Task<string> PlanAsync(WorkTask task, Workspace workspace, Guid runId, CancellationToken ct);
}
public sealed class PlanningCoordinator(ITaskStore store, IPlanningRunner planner, IWorkspaceManager workspaces)
{
    public async Task<bool> ExecuteNextAsync(CancellationToken ct = default)
    {
        var claim = await store.ClaimPlanningAsync(ct);
        if (claim is null) return false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = MonitorAsync(claim.Task.Id, operation);
        try
        {
            await using var activity = new TaskActivityReporter(store, claim.Run.Id);
            await activity.StageAsync("準備規劃工作目錄", operation.Token);
            var project = await store.GetProjectAsync(claim.Task.ProjectId, operation.Token) ?? throw new MissingEntityException("Project not found.");
            var workspace = await workspaces.CreateAsync(project, claim.Run.Id, operation.Token);
            await store.SetWorkspaceAsync(claim.Run.Id, workspace, operation.Token);
            await activity.StageAsync("AI 正在規劃", operation.Token);
            var plan = await planner.PlanAsync(claim.Task, workspace, claim.Run.Id, operation.Token);
            PlanContent.EnsureCompleted(plan);
            await activity.StageAsync("檢查並保存規劃結果", operation.Token);
            await workspaces.AssertUnchangedAsync(workspace, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            await store.CompletePlanningAsync(claim.Run.Id, plan, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await store.CompletePlanningAsync(claim.Run.Id, null,
                ex is OperationCanceledException ? "Planning cancelled, interrupted, or timed out." : ex.Message, CancellationToken.None);
        }
        finally
        {
            operation.Cancel();
            try { await monitor; } catch (OperationCanceledException) { }
        }
        return true;
    }
    private async Task MonitorAsync(Guid taskId, CancellationTokenSource operation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(operation.Token))
            if (await store.GetTaskStatusAsync(taskId, operation.Token) == TaskStatus.Cancelled)
            { operation.Cancel(); return; }
    }
}
