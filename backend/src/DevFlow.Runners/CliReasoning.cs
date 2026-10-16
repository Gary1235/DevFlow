using DevFlow.Core;

namespace DevFlow.Runners;

internal static class CliReasoning
{
    public static void AddArguments(List<string> arguments, AiSelection selection)
    {
        selection = AiSelection.Normalize(selection);
        if (selection.ReasoningEffort is not { } effort) return;
        if (selection.Provider == "codex") arguments.AddRange(["--config", $"model_reasoning_effort=\"{effort}\""]);
        else if (selection.Provider == "claude") arguments.AddRange(["--effort", effort]);
    }
}
