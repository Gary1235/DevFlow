using System.Text.Json;
namespace DevFlow.Core;
public static partial class PermissionPolicy
{
    private static PermissionAssessment SavedValidation(PermissionPolicySettings settings, JsonElement args, string workspace,
        ProjectDefinition? project, bool readOnly)
    {
        if (readOnly || project is null || args.EnumerateObject().Any(p => p.Name is not ("ProfileId" or "ProjectVersion")) ||
            !args.TryGetProperty("ProfileId", out var id) || id.ValueKind != JsonValueKind.String ||
            !args.TryGetProperty("ProjectVersion", out var version) || !version.TryGetInt32(out var v) || v != project.Version)
            return Ask("Execution", "需確認：驗證設定不可用", "只允許實作階段使用目前專案版本的已儲存驗證設定。", true);
        var profile = ProjectExecutionSettings.FindValidation(project, id.GetString()!);
        if (profile is null) return Unknown();
        var risk = WorkspaceTarget(ProjectExecutionSettings.ResolveDirectory(workspace, profile.WorkingDirectory), workspace);
        if (!risk.AutoAllowed) return risk;
        // Check restrictions using the actual saved argv, never model-supplied command text.
        var text = profile.Executable + " " + string.Join(" ", profile.Arguments);
        var canonical = Path.GetFileNameWithoutExtension(profile.Executable) + " " + string.Join(" ", profile.Arguments);
        if ((settings.SensitiveCommands ?? []).Any(p => new[] { text, canonical }.Any(t => Match(p, t) || t.Equals(p, StringComparison.OrdinalIgnoreCase) || t.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))))
            return Ask("SensitiveCommand", "需確認：敏感指令", "驗證設定命中使用者的敏感指令名單。");
        var execution = CommandPermissionRisk.LiteralExecution(profile.Executable, profile.Arguments);
        if (execution.Category != "Execution" && !execution.BatchAllowed) return execution with { FixedBoundary = true };
        if (profile.Executable.Equals("npm", StringComparison.OrdinalIgnoreCase) && profile.Arguments.FirstOrDefault() is "ci" or "install" or "i" or "add" or "update" ||
            profile.Executable.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && profile.Arguments.FirstOrDefault() is "restore" or "tool")
            return Ask("Execution", "需確認：依賴準備", "依賴準備與套件安裝不屬於自動驗證能力。", true);
        return new("Execution", "自動允許：結構化驗證設定", $"專案 {project.Id} 版本 {project.Version} 的驗證設定 {profile.Id}；由主程序使用已儲存的 executable、argv、cwd 執行。", AutoAllowed: true);
    }
}
