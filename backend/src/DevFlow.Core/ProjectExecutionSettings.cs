namespace DevFlow.Core;

public sealed record ValidationProfile(string Id, string Name, string WorkingDirectory, string Executable,
    string[] Arguments, string Kind = "Command", int TimeoutSeconds = 600, string[]? PrepareArguments = null);
public sealed record PreviewService(string Id, string Name, string WorkingDirectory, string Executable,
    string[] Arguments, int Port = 0, Dictionary<string, string>? Environment = null, string[]? PrepareArguments = null);

public static class ProjectExecutionSettings
{
    public static ValidationProfile? FindValidation(ProjectDefinition project, string id) =>
        (project.ValidationProfiles ?? []).FirstOrDefault(p => p.Id == id) ??
        (id == "legacy-dotnet-test" && project.DefaultValidationProfileId is null
            ? new("legacy-dotnet-test", "專案 .NET 測試", ".", "dotnet", project.ValidationArguments, "DotnetTest") : null);
    public static ValidationProfile[] Normalize(ValidationProfile[]? profiles)
    {
        if ((profiles?.Length ?? 0) > 20) throw new ArgumentException("最多 20 組驗證設定。");
        var result = (profiles ?? []).Select(p => {
            if (p is null || p.Kind is not ("Command" or "DotnetTest") || p.TimeoutSeconds is < 1 or > 3600)
                throw new ArgumentException("驗證類型或逾時設定無效。");
            CheckCommand(p.WorkingDirectory, p.Executable, p.Arguments);
            if (p.PrepareArguments is { Length: > 0 }) CheckCommand(p.WorkingDirectory, p.Executable, p.PrepareArguments);
            if (p.Kind == "DotnetTest" && (p.Executable != "dotnet" || p.Arguments.FirstOrDefault() != "test" ||
                p.Arguments.Any(a => a == "-l" || a.StartsWith("--logger") || a.StartsWith("--results-directory"))))
                throw new ArgumentException("DotnetTest 必須使用 dotnet test，報告參數由系統管理。");
            return p with { Id = Id(p.Id), Name = WorkTask.Required(p.Name, 100) };
        }).ToArray();
        if (result.Select(p => p.Id).Distinct().Count() != result.Length) throw new ArgumentException("驗證 ID 不可重複。");
        return result;
    }
    public static PreviewService[] Normalize(PreviewService[]? services)
    {
        if ((services?.Length ?? 0) > 4) throw new ArgumentException("最多 4 個預覽服務。");
        var result = (services ?? []).Select(s => {
            if (s is null || s.Port is < 0 or > 65535) throw new ArgumentException("預覽連接埠無效。");
            CheckCommand(s.WorkingDirectory, s.Executable, s.Arguments);
            if (s.PrepareArguments is { Length: > 0 }) CheckCommand(s.WorkingDirectory, s.Executable, s.PrepareArguments);
            if (s.Environment is { } env && (env.Count > 40 || env.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Contains('=') || p.Key.Any(char.IsControl) || p.Value is null || p.Value.Length > 8192)))
                throw new ArgumentException("預覽環境變數無效。");
            if (!s.Arguments.Concat(s.Environment?.Values ?? Enumerable.Empty<string>()).Any(v => v.Contains("{port}")))
                throw new ArgumentException("預覽指令或環境變數須使用 {port} 指定服務連接埠。");
            return s with { Id = Id(s.Id), Name = WorkTask.Required(s.Name, 100) };
        }).ToArray();
        if (result.Select(p => p.Id).Distinct().Count() != result.Length) throw new ArgumentException("預覽服務 ID 不可重複。");
        return result;
    }
    private static string Id(string value) => System.Text.RegularExpressions.Regex.IsMatch(value ?? "", @"\A[a-zA-Z0-9_-]{1,60}\z")
        ? value! : throw new ArgumentException("設定 ID 僅能包含英文、數字、底線或連字號。");
    public static void CheckCommand(string directory, string executable, string[] arguments)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > 500 || Path.IsPathRooted(directory) || directory.Contains(':') || directory.Split('/', '\\').Any(p => p == "..") || directory.Any(char.IsControl))
            throw new ArgumentException("工作目錄必須是專案內的相對路徑（根目錄填 .）。");
        WorkTask.Required(executable, 4096);
        if (executable.Any(char.IsControl) || arguments is null || arguments.Length > 100 || arguments.Any(a => a is null || a.Length > 8192 || a.Contains('\0')))
            throw new ArgumentException("指令或參數無效。");
    }
    public static string ResolveDirectory(string root, string relative)
    {
        CheckCommand(relative, "check", []);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(relative, fullRoot);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full != fullRoot && !full.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)) throw new ArgumentException("工作目錄超出任務副本。");
        if (!Directory.Exists(full)) throw new ArgumentException("工作目錄不存在：" + relative);
        for (var current = full; current is not null; current = Path.GetDirectoryName(current)) {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("工作目錄不可經過符號連結。");
            if (current.Equals(fullRoot, comparison)) break;
        }
        return full;
    }
}
