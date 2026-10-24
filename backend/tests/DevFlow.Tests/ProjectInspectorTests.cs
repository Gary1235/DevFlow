using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
namespace DevFlow.Tests;
public sealed class ProjectInspectorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-inspector-" + Guid.NewGuid());
    public ProjectInspectorTests() { Directory.CreateDirectory(root); }
    public void Dispose() {
        if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("devflow-inspector-", StringComparison.Ordinal)) throw new InvalidOperationException();
        Directory.Delete(root, true);
    }
    private ProjectDefinition Project => new(Guid.NewGuid(), "Project", root, "main", ["test"]);
    [Fact] public async Task Discovery_recognizes_real_project_names_and_excludes_generated_packages()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Real.slnx"), "<Solution/>");
        Directory.CreateDirectory(Path.Combine(root, "web")); await File.WriteAllTextAsync(Path.Combine(root, "web/package.json"), "{\"scripts\":{\"build\":\"vite build\",\"dev\":\"vite\"}}");
        Directory.CreateDirectory(Path.Combine(root, "node_modules/fake")); await File.WriteAllTextAsync(Path.Combine(root, "node_modules/fake/package.json"), "{\"scripts\":{\"test\":\"fake\"}}");
        var result = new ProjectInspector(new VersionProcess()).Discover(Project);
        Assert.Contains(result.Validations, p => p.Arguments.Contains("Real.slnx"));
        Assert.Contains(result.Validations, p => p.WorkingDirectory == "web" && p.Arguments.SequenceEqual(["run", "build"]));
        Assert.DoesNotContain(result.Validations, p => p.WorkingDirectory.Contains("node_modules", StringComparison.Ordinal));
        Assert.Single(result.Previews);
    }
    [Fact] public async Task Preflight_detects_missing_script_lockfile_and_target_without_running_the_validation()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "package.json"), "{\"scripts\":{\"build\":\"vite build\"}}");
        var process = new VersionProcess(); var inspector = new ProjectInspector(process);
        var npm = await inspector.CheckAsync(Project, new("web", "Web", ".", "npm", ["run", "missing"], PrepareArguments: ["ci"]), default);
        Assert.False(npm.Ready); Assert.Contains(npm.Checks, c => c.Name == "npm script" && !c.Success); Assert.Contains(npm.Checks, c => c.Name == "lockfile" && !c.Success);
        var dotnet = await inspector.CheckAsync(Project, new("api", "API", ".", "dotnet", ["test", "Missing.slnx"], "DotnetTest"), default);
        Assert.False(dotnet.Ready); Assert.All(process.Arguments, a => Assert.Equal("--version", a.Last()));
    }
    private sealed class VersionProcess : IProcessRunner {
        public List<string[]> Arguments = [];
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string cwd, string? input, CancellationToken ct) { Arguments.Add(a.ToArray()); return Task.FromResult(new ProcessResult(0, "24.15.0", "")); }
    }
}
