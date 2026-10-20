using System.Text.RegularExpressions;
namespace DevFlow.Core;
public sealed record AiSelection(string Provider = "codex", string? Model = null, string? ReasoningEffort = null)
{
    public static AiSelection Normalize(AiSelection? value)
    {
        if (value is null) return new();
        var provider = WorkTask.Required(value.Provider, 40).ToLowerInvariant();
        if (provider is not ("codex" or "gemini" or "antigravity" or "claude")) throw new ArgumentException("Unsupported AI CLI provider.");
        var model = string.IsNullOrWhiteSpace(value.Model) ? null : value.Model.Trim();
        if (model != null && (model.Length > 128 || !Regex.IsMatch(model, @"\A[A-Za-z0-9][A-Za-z0-9._:/-]*\z")))
            throw new ArgumentException("Invalid model ID.");
        var effort = string.IsNullOrWhiteSpace(value.ReasoningEffort) ? null : value.ReasoningEffort.Trim().ToLowerInvariant();
        if (effort is not null && !SupportsEffortValue(provider, effort)) throw new ArgumentException("Invalid or unsupported reasoning effort for this CLI provider.");
        return new(provider, model, effort);
    }
    public static bool SupportsEffortValue(string provider, string effort) => provider switch
    {
        "codex" => effort is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra",
        "claude" => effort is "low" or "medium" or "high" or "xhigh" or "max",
        "antigravity" => effort is "low" or "medium" or "high",
        _ => false
    };
}
public sealed record AiConfiguration(AiSelection? Planning = null, AiSelection? Implementation = null)
{
    public static AiConfiguration Resolve(AiConfiguration? defaults, AiSelection? planning = null, AiSelection? implementation = null) =>
        new(AiSelection.Normalize(planning ?? defaults?.Planning), AiSelection.Normalize(implementation ?? defaults?.Implementation));
}
