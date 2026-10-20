using DevFlow.Core;
namespace DevFlow.Runners;

public sealed class RoutedCliRunner(IProcessRunner process, CliRegistry registry, string artifactsRoot,
    ITaskToolApprovalStore? approvals = null, string? permissionHelper = null) : IAgentRunner, IPlanningRunner
{
    private async Task<T> Invoke<T>(WorkTask task, Workspace workspace, Guid runId, bool planning, Func<IProcessRunner, CliCommand, string, Task<T>> action, CancellationToken ct)
    {
        var provider = AiSelection.Normalize(planning ? task.Ai?.Planning : task.Ai?.Implementation).Provider;
        var command = registry.Require(provider);
        IProcessRunner prefixed = new PrefixedProcess(process, command);
        var operation = CliPermissions.Current ?? (approvals is null ? null : new TaskPermissionPlanningRunner.Operation(approvals, task.Id, runId, ct, provider, planning));
        if (operation is null) return await action(prefixed, command, provider);
        T result;
        if (provider == "antigravity")
        {
            if (permissionHelper is null) throw new InvalidOperationException("Antigravity permission helper is not configured.");
            await using var hook = await NativePermissionHook.CreateAsync(operation, workspace.Path,
                Path.Combine(artifactsRoot, runId.ToString("N"), "permission-decisions.jsonl"), ct, permissionHelper);
            result = await action(prefixed, command, provider);
        }
        else result = await action(new PermissionProcess(prefixed, provider, operation), command, provider);
        if (operation is TaskPermissionPlanningRunner.Operation { Failure: { } failure }) throw new InvalidOperationException(failure);
        return result;
    }
    public async Task ExecuteAsync(WorkTask task, Workspace workspace, Guid runId, TddPhase phase, CancellationToken ct) =>
        await Invoke(task, workspace, runId, false, async (runner, command, provider) =>
        {
            IAgentRunner adapter = provider switch {
                "codex" => new CodexCliRunner(runner, command.Executable, artifactsRoot),
                "antigravity" => new AntigravityCliRunner(runner, command.Executable, artifactsRoot),
                "gemini" => new GeminiCliRunner(runner, command.Executable, artifactsRoot),
                "claude" => new ClaudeCliRunner(runner, command.Executable, artifactsRoot),
                _ => throw new ArgumentException("Unsupported CLI provider.") };
            await adapter.ExecuteAsync(task, workspace, runId, phase, ct); return true;
        }, ct);
    public Task<string> PlanAsync(WorkTask task, Workspace workspace, Guid runId, CancellationToken ct) =>
        Invoke(task, workspace, runId, true, (runner, command, provider) => {
            IPlanningRunner adapter = provider switch {
                "codex" => new CodexPlanningRunner(runner, command.Executable, artifactsRoot),
                "antigravity" => new AntigravityCliRunner(runner, command.Executable, artifactsRoot),
                "gemini" => new GeminiCliRunner(runner, command.Executable, artifactsRoot),
                "claude" => new ClaudeCliRunner(runner, command.Executable, artifactsRoot),
                _ => throw new ArgumentException("Unsupported CLI provider.") };
            return adapter.PlanAsync(task, workspace, runId, ct);
        }, ct);
}
