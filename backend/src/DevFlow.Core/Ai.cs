using System.Text.RegularExpressions;
namespace DevFlow.Core;
public sealed record AiSelection(string Provider = "codex", string? Model = null)
{
    public static AiSelection Normalize(AiSelection? value)
    {
        if (value is null) return new();
        var provider = WorkTask.Required(value.Provider, 40).ToLowerInvariant();
        if (provider is not ("codex" or "gemini" or "claude")) throw new ArgumentException("Unsupported AI CLI provider.");
        var model = string.IsNullOrWhiteSpace(value.Model) ? null : value.Model.Trim();
        if (model != null && (model.Length > 128 || !Regex.IsMatch(model, @"\A[A-Za-z0-9][A-Za-z0-9._:/-]*\z")))
            throw new ArgumentException("Invalid model ID.");
        return new(provider, model);
    }
}
public sealed record AiConfiguration(AiSelection? Planning = null, AiSelection? Implementation = null)
{
    public static AiConfiguration Resolve(AiConfiguration? defaults, AiSelection? planning = null, AiSelection? implementation = null) =>
        new(AiSelection.Normalize(planning ?? defaults?.Planning), AiSelection.Normalize(implementation ?? defaults?.Implementation));
}
