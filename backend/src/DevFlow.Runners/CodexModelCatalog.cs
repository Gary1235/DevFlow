using System.Text.Json;
using DevFlow.Core;

namespace DevFlow.Runners;

public static class CodexModelCatalog
{
    public static CliModelOption[] Read(JsonElement result, string? configuredModel = null)
    {
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var models = new List<CliModelOption>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string? Text(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var id = Text("model") ?? Text("id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            try { id = AiSelection.Normalize(new("codex", id)).Model!; } catch (ArgumentException) { continue; }
            if (item.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True &&
                !string.Equals(id, configuredModel, StringComparison.Ordinal)) continue;
            var efforts = new List<string>();
            if (item.TryGetProperty("supportedReasoningEfforts", out var options) && options.ValueKind == JsonValueKind.Array)
                foreach (var option in options.EnumerateArray())
                    if (option.ValueKind == JsonValueKind.Object && option.TryGetProperty("reasoningEffort", out var effort) && effort.ValueKind == JsonValueKind.String &&
                        effort.GetString() is { } level && AiSelection.SupportsEffortValue("codex", level)) efforts.Add(level);
            var defaultEffort = Text("defaultReasoningEffort");
            if (defaultEffort is not null && !efforts.Contains(defaultEffort)) defaultEffort = null;
            var display = Text("displayName");
            if (string.IsNullOrWhiteSpace(display) || display.Length > 128 || display.Any(char.IsControl)) display = id;
            models.Add(new(id, display, efforts.Distinct(StringComparer.Ordinal).ToArray(), defaultEffort,
                item.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True));
        }
        return models.DistinctBy(m => m.Id).ToArray();
    }
}
