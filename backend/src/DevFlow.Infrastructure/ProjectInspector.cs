using System.Text.Json;
using System.Xml.Linq;
using DevFlow.Core;
namespace DevFlow.Infrastructure;

public sealed record ProjectDiscovery(ValidationProfile[] Validations, PreviewService[] Previews, string[] Warnings);
public sealed record EnvironmentCheck(string Name, bool Success, string Detail);
public sealed record ProjectPreflight(EnvironmentCheck[] Checks, DateTimeOffset CheckedAt)
{
    public bool Ready => Checks.All(c => c.Success);
}
public sealed class ProjectInspector(IProcessRunner process)
{
    public ProjectDiscovery Discover(ProjectDefinition project, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(project.RepositoryPath);
        if (!PermissionPolicy.WorkspaceTarget(root, root).AutoAllowed) throw new ArgumentException("專案根目錄不能是連結。");
        var validations = new List<ValidationProfile>(); var previews = new List<PreviewService>(); var warnings = new List<string>();
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0)); var count = 0;
        while (pending.TryPop(out var next)) {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(next.Path)) {
                if (++count > 5000) { warnings.Add("偵測超過 5000 個項目，請自行指定較深層專案。"); pending.Clear(); break; }
                if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) continue;
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry)) {
                    if (next.Depth < 6 && name is not (".git" or "node_modules" or "bin" or "obj" or ".data" or ".test-work" or "dist" or "vendor" or "target")) pending.Push((entry, next.Depth + 1));
                    continue;
                }
                var relative = Path.GetRelativePath(root, next.Path).Replace('\\', '/'); var id = "detected-" + count;
                if (new FileInfo(entry).Length > 1024 * 1024) continue;
                try {
                    if (name == "package.json") {
                        using var package = JsonDocument.Parse(File.ReadAllText(entry));
                        if (!package.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object) continue;
                        foreach (var script in new[] { "build", "test" }) if (scripts.TryGetProperty(script, out _))
                            validations.Add(new(id + "-" + script, relative + " · npm " + script, relative, "npm", ["run", script]));
                        if (scripts.TryGetProperty("dev", out var dev) && dev.ValueKind == JsonValueKind.String && dev.GetString()!.Contains("vite", StringComparison.Ordinal))
                            previews.Add(new(id, relative + " · Vite", relative, "npm", ["run", "dev", "--", "--host", "127.0.0.1", "--port", "{port}", "--strictPort"]));
                    }
                    else if (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                        validations.Add(new(id, relative + "/" + name, relative, "dotnet", ["test", name], "DotnetTest"));
                    else if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {
                        using var xmlReader = System.Xml.XmlReader.Create(entry, new() { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
                        var xml = XDocument.Load(xmlReader);
                        if (xml.Descendants().Any(e => e.Name.LocalName == "IsTestProject" && e.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                            e.Name.LocalName == "PackageReference" && e.Attribute("Include")?.Value is "Microsoft.NET.Test.Sdk" or "xunit" or "NUnit"))
                            validations.Add(new(id, relative + "/" + name, relative, "dotnet", ["test", name], "DotnetTest"));
                        if (xml.Root?.Attribute("Sdk")?.Value == "Microsoft.NET.Sdk.Web")
                            previews.Add(new(id, relative + "/" + name, relative, "dotnet", ["run", "--project", name, "--no-launch-profile", "--urls", "http://127.0.0.1:{port}"], Environment: new() { ["ASPNETCORE_ENVIRONMENT"] = "Development" }));
                    }
                }
                catch (Exception ex) when (ex is JsonException or System.Xml.XmlException or IOException or UnauthorizedAccessException) { warnings.Add("無法解析：" + Path.GetRelativePath(root, entry)); }
            }
        }
        return new(validations.Take(20).ToArray(), previews.Take(4).ToArray(), warnings.ToArray());
    }
    public async Task<ProjectPreflight> CheckAsync(ProjectDefinition project, ValidationProfile profile, CancellationToken ct)
    {
        ProjectExecutionSettings.Normalize([profile]); var checks = new List<EnvironmentCheck>();
        string directory;
        try { directory = ProjectExecutionSettings.ResolveDirectory(project.RepositoryPath, profile.WorkingDirectory); checks.Add(new("工作目錄", true, directory)); }
        catch (ArgumentException ex) { return new([new("工作目錄", false, ex.Message)], DateTimeOffset.UtcNow); }
        try {
            if (profile.Executable.Equals("npm", StringComparison.OrdinalIgnoreCase)) {
                var file = Path.Combine(directory, "package.json");
                if (!PermissionPolicy.WorkspaceTarget(file, project.RepositoryPath).AutoAllowed) throw new ArgumentException("package.json 經過符號連結。");
                using var package = JsonDocument.Parse(File.ReadAllText(file));
                var script = profile.Arguments.Length > 1 && profile.Arguments[0] == "run" ? profile.Arguments[1] : profile.Arguments.FirstOrDefault();
                checks.Add(new("npm script", script is not null && package.RootElement.TryGetProperty("scripts", out var scripts) && scripts.TryGetProperty(script, out _), "腳本：" + script));
                if (profile.PrepareArguments is ["ci"]) checks.Add(new("lockfile", File.Exists(Path.Combine(directory, "package-lock.json")) || File.Exists(Path.Combine(directory, "npm-shrinkwrap.json")), "npm ci 需要 lockfile。"));
                var npm = LocalExecutable.Resolve("npm", ["--version"]);
                await Version("Node.js", npm.Executable, ["--version"]); await Version("npm", npm.Executable, npm.Arguments);
            }
            else if (profile.Executable.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
                var targets = profile.Arguments.Where(a => a.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (targets.Length == 0) {
                    var solutions = Directory.GetFiles(directory).Count(p => p.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));
                    var projects = Directory.GetFiles(directory, "*.csproj").Length;
                    checks.Add(new(".NET 目標選擇", solutions == 1 || solutions == 0 && projects == 1, "未指定目標時，工作目錄應只有一個方案或一個專案。"));
                }
                foreach (var argument in profile.Arguments.Where(a => a.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))) {
                    var path = Path.GetFullPath(argument, directory);
                    checks.Add(new(".NET 目標", PermissionPolicy.WorkspaceTarget(path, project.RepositoryPath).AutoAllowed && File.Exists(path), argument));
                }
                await Version(".NET SDK（包含 global.json 選版）", "dotnet", ["--version"]);
            }
            else checks.Add(new("執行程式", true, "自訂程式將由獨立驗證測試確認：" + profile.Executable));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception) { checks.Add(new("設定", false, ex.Message)); }
        return new(checks.ToArray(), DateTimeOffset.UtcNow);
        async Task Version(string label, string exe, string[] args) {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try {
                var result = await process.RunAsync(exe, args, directory, null, timeout.Token);
                checks.Add(new(label, result.ExitCode == 0, (result.ExitCode == 0 ? result.StandardOutput : result.StandardError).Trim()[..Math.Min(2000, (result.ExitCode == 0 ? result.StandardOutput : result.StandardError).Trim().Length)]));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { checks.Add(new(label, false, "版本檢查逾時。")); }
        }
    }
}
