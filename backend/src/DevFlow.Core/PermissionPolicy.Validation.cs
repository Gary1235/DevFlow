using System.Text.Json;
namespace DevFlow.Core;

public static partial class PermissionPolicy
{
    private static PermissionAssessment TrustedValidation(PermissionAssessment risk, JsonElement args, string workspace,
        ProjectDefinition project, bool readOnly)
    {
        // Never override a custom restriction, expanded native permission request,
        // package install, or read-only stage. A project profile is not a prefix grant.
        if (readOnly || risk.AutoAllowed || risk.Category != "Execution") return risk;
        var text = args.GetProperty("CommandLine").GetString()!;
        var powershell = args.TryGetProperty("Shell", out var shell) && shell.GetString() == "powershell";
        // Only a single literal invocation is comparable to the configured argv.
        // Shell expansion, redirection, wrappers and command composition stay manual.
        if (text.IndexOfAny([';', '|', '&', '<', '>', '`', '$', '%', '@', '!', '^', '#', '*', '?', '[', ']', '~', '(', ')', '{', '}', '\r', '\n']) >= 0 ||
            !powershell && text.Contains('\\')) return risk;
        var words = CommandPermissionRisk.Tokenize(text);
        if (words is not { Count: > 0 }) return risk;
        var cwd = args.TryGetProperty("Cwd", out var directory) ? Path.GetFullPath(directory.GetString()!, workspace) : Path.GetFullPath(workspace);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var profiles = ProjectExecutionSettings.Normalize(project.ValidationProfiles).ToList();
        // The legacy project setting is the validator's configured command when
        // no default profile is selected. Do not inherit installation/preparation
        // commands or preview services as a general execution grant.
        if (project.DefaultValidationProfileId is null && project.ValidationArguments is { Length: > 0 } legacy && legacy[0] == "test" &&
            !legacy.Any(a => string.IsNullOrWhiteSpace(a) || a == "-l" || a.StartsWith("--logger", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--results-directory", StringComparison.OrdinalIgnoreCase)))
            profiles.Add(new("legacy-dotnet-test", "專案 .NET 測試", ".", "dotnet", legacy, "DotnetTest"));
        foreach (var profile in profiles) {
            if (!words[0].Equals(profile.Executable, StringComparison.Ordinal) || !words.Skip(1).SequenceEqual(profile.Arguments, StringComparer.Ordinal)) continue;
            string expected;
            try { expected = ProjectExecutionSettings.ResolveDirectory(workspace, profile.WorkingDirectory); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { continue; }
            if (!Path.TrimEndingDirectorySeparator(cwd).Equals(Path.TrimEndingDirectorySeparator(expected), comparison)) continue;
            return new("Execution", "自動允許：預先核准的驗證指令",
                $"完整執行程式、參數及工作目錄符合專案 {project.Id} 的驗證設定 {profile.Id}（專案版本 {project.Version}）；未擴張為其他指令或永久 CLI 權限。",
                AutoAllowed: true);
        }
        return risk;
    }
}
