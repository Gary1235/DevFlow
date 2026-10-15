using DevFlow.Core;
namespace DevFlow.Runners;
public sealed class RoutedCliRunner(IProcessRunner process, CliRegistry registry, string artifactsRoot) : IAgentRunner, IPlanningRunner
{
    public Task ExecuteAsync(WorkTask task, Workspace workspace, Guid runId, TddPhase phase, CancellationToken ct)
    {
        var selection = AiSelection.Normalize(task.Ai?.Implementation);
        var command = registry.Require(selection.Provider);
        var prefixed = new PrefixedProcess(process, command);
        return selection.Provider switch
        {
            "codex" => new CodexCliRunner(prefixed, command.Executable, artifactsRoot).ExecuteAsync(task, workspace, runId, phase, ct),
            "gemini" => new GeminiCliRunner(prefixed, command.Executable, artifactsRoot).ExecuteAsync(task, workspace, runId, phase, ct),
            "claude" => new ClaudeCliRunner(prefixed, command.Executable, artifactsRoot).ExecuteAsync(task, workspace, runId, phase, ct),
            _ => throw new ArgumentException("Unsupported CLI provider.")
        };
    }
    public Task<string> PlanAsync(WorkTask task, Workspace workspace, Guid runId, CancellationToken ct)
    {
        var selection = AiSelection.Normalize(task.Ai?.Planning);
        var command = registry.Require(selection.Provider);
        var prefixed = new PrefixedProcess(process, command);
        return selection.Provider switch
        {
            "codex" => new CodexPlanningRunner(prefixed, command.Executable, artifactsRoot).PlanAsync(task, workspace, runId, ct),
            "gemini" => new GeminiCliRunner(prefixed, command.Executable, artifactsRoot).PlanAsync(task, workspace, runId, ct),
            "claude" => new ClaudeCliRunner(prefixed, command.Executable, artifactsRoot).PlanAsync(task, workspace, runId, ct),
            _ => throw new ArgumentException("Unsupported CLI provider.")
        };
    }
}
