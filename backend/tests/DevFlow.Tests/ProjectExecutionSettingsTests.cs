using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
namespace DevFlow.Tests;

public sealed class ProjectExecutionSettingsTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-profile-" + Guid.NewGuid());
    public Task InitializeAsync() { Directory.CreateDirectory(Path.Combine(root, "frontend")); Directory.CreateDirectory(Path.Combine(root, "backend")); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }
    private sealed class Capture(int code = 0, string error = "") : IProcessRunner
    {
        public List<(string Program, string[] Arguments, string Directory)> Calls = [];
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string w, string? input, CancellationToken ct) {
            Calls.Add((e, a.ToArray(), w)); return Task.FromResult(new ProcessResult(code, "build output", error));
        }
    }
    private ProjectDefinition Project(params ValidationProfile[] profiles) => new(Guid.NewGuid(), "Demo", root, "main", ["test"], ValidationProfiles: profiles, DefaultValidationProfileId: profiles[0].Id);
    [Fact] public async Task Command_builds_use_selected_directory_and_preserve_exit_code_without_inventing_test_counts()
    {
        var process = new Capture(); var id = Guid.NewGuid();
        var profile = new ValidationProfile("web", "Web build", "frontend", "node", ["build.js"]);
        var evidence = await new DotnetValidationRunner(process, Path.Combine(root, "reports")).ValidateAsync(Project(profile), new(root, "base"), id, TddPhase.Implementation, default);
        Assert.Equal("Command", evidence.Kind); Assert.Equal(0, evidence.ExitCode); Assert.Equal(0, evidence.Total);
        Assert.Equal(Path.Combine(root, "frontend"), process.Calls[0].Directory);
        Assert.Equal(new[] { "build.js" }, process.Calls[0].Arguments);
        var saved = Assert.Single(await new RunArtifactReader(Path.Combine(root, "reports")).ValidationsAsync(id));
        Assert.Equal("Command", saved.Kind); Assert.Equal(0, saved.Total);
    }
    [Fact] public async Task Failed_commands_save_actionable_errors_and_logs()
    {
        var process = new Capture(1, "MSBUILD: project not found"); var id = Guid.NewGuid();
        var evidence = await new DotnetValidationRunner(process, root).ValidateAsync(Project(new ValidationProfile("web", "Web", ".", "node", ["build.js"])), new(root, "base"), id, TddPhase.Baseline, default);
        Assert.Equal(1, evidence.ExitCode); Assert.Contains("project not found", evidence.Error);
        Assert.Contains("project not found", await File.ReadAllTextAsync(Path.Combine(root, id.ToString("N"), "Baseline", "stderr.log")));
    }
    [Fact] public async Task Optional_preparation_runs_in_the_same_directory_and_stops_validation_on_failure()
    {
        var profile = new ValidationProfile("web", "Build", "frontend", "node", ["build.js"], PrepareArguments: ["prepare.js"]);
        var success = new Capture();
        await new DotnetValidationRunner(success, root).ValidateAsync(Project(profile), new(root, "base"), Guid.NewGuid(), TddPhase.Baseline, default);
        Assert.Equal(new[] { "prepare.js", "build.js" }, success.Calls.Select(c => c.Arguments[0]));
        Assert.All(success.Calls, c => Assert.Equal(Path.Combine(root, "frontend"), c.Directory));
        var failure = new Capture(1, "install failed");
        var evidence = await new DotnetValidationRunner(failure, root).ValidateAsync(Project(profile), new(root, "base"), Guid.NewGuid(), TddPhase.Baseline, default);
        Assert.Single(failure.Calls); Assert.Contains("依賴準備失敗", evidence.Error);
    }
    [Fact] public async Task Dotnet_profiles_set_the_directory_and_collect_reports()
    {
        var process = new Capture();
        await new DotnetValidationRunner(process, root).ValidateAsync(Project(new ValidationProfile("api", "API", "backend", "dotnet", ["test", "CHR.sln"], "DotnetTest")), new(root, "base"), Guid.NewGuid(), TddPhase.Baseline, default);
        Assert.Equal(Path.Combine(root, "backend"), process.Calls[0].Directory);
        Assert.Contains("CHR.sln", process.Calls[0].Arguments); Assert.Contains("trx", process.Calls[0].Arguments);
    }
    [Theory] [InlineData("../outside")] [InlineData("C:/outside")] [InlineData("/outside")]
    public void Working_directories_cannot_escape_the_task(string directory) => Assert.Throws<ArgumentException>(() => ProjectExecutionSettings.Normalize(new[] {new ValidationProfile("web", "Web", directory, "node", ["build.js"])}));
    [Fact] public async Task Invalid_default_or_stale_project_version_cannot_partially_save_settings()
    {
        var store = new SqliteTaskStore(Path.Combine(root, "store.db")); await store.InitializeAsync();
        var project = new ProjectDefinition(Guid.NewGuid(), "Demo", root, "main", ["test"]); await store.AddProjectAsync(project);
        var profiles = new[] {new ValidationProfile("web", "Build", "frontend", "node", ["build.js"])};
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetExecutionSettingsAsync(project.Id, 0, profiles, "missing", [], null));
        var saved = await store.SetExecutionSettingsAsync(project.Id, 0, profiles, "web", [], null);
        Assert.Equal(1, saved.Version);
        await Assert.ThrowsAsync<ConflictException>(() => store.SetExecutionSettingsAsync(project.Id, 0, [], null, [], null));
        Assert.Equal("web", (await store.GetProjectAsync(project.Id))!.DefaultValidationProfileId);
    }
    [Fact] public void Preview_configuration_requires_port_binding_and_unique_ids()
    {
        Assert.Throws<ArgumentException>(() => ProjectExecutionSettings.Normalize(new[] { new PreviewService("web", "Web", ".", "node", ["server.js"]) }));
        var service = new PreviewService("web", "Web", ".", "node", ["server.js", "{port}"]);
        Assert.Throws<ArgumentException>(() => ProjectExecutionSettings.Normalize(new[] { service, service }));
    }
}
