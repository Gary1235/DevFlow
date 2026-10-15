using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Runners;
using System.Text.Json;
using Status = DevFlow.Core.TaskStatus;

// This opt-in tool spends model usage and changes only its new synthetic repository.
if (args.Length != 2 || args[0] != "--run-synthetic-fixture")
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/DevFlow.LiveCheck -- --run-synthetic-fixture <native-codex-executable>");
    return 2;
}
var backend = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
if (!File.Exists(Path.Combine(backend, "DevFlow.slnx"))) throw new InvalidOperationException("Backend root not found.");
var root = Path.Combine(backend, ".test-work", "live-" + Guid.NewGuid().ToString("N"));
var repository = Path.Combine(root, "repo");
var artifacts = Path.Combine(root, "artifacts");
Directory.CreateDirectory(repository);
Console.WriteLine($"Evidence directory: {root}");
var process = new ProcessRunner();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
var ct = timeout.Token;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; timeout.Cancel(); };
async Task<ProcessResult> Command(string exe, string[] arguments, string cwd)
{
    var result = await process.RunAsync(exe, arguments, cwd, null, ct);
    if (result.ExitCode != 0) throw new InvalidOperationException($"{exe} failed: {result.StandardOutput}\n{result.StandardError}");
    return result;
}
await File.WriteAllTextAsync(Path.Combine(repository, "Greeting.csproj"), """
    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsTestProject>true</IsTestProject><UseSharedCompilation>false</UseSharedCompilation></PropertyGroup>
      <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1"/><PackageReference Include="xunit" Version="2.9.3"/><PackageReference Include="xunit.runner.visualstudio" Version="3.1.5"/></ItemGroup>
    </Project>
    """, ct);
await File.WriteAllTextAsync(Path.Combine(repository, "GreetingService.cs"), """
    namespace Greeting;
    public static class GreetingService
    {
        public static string Greet(string name) => $"Hello, {name}!";
    }
    """, ct);
await File.WriteAllTextAsync(Path.Combine(repository, "GreetingTests.cs"), """
    using Xunit;
    namespace Greeting;
    public class GreetingTests
    {
        [Fact] public void GreetsName() => Assert.Equal("Hello, Ada!", GreetingService.Greet("Ada"));
    }
    """, ct);
await File.WriteAllTextAsync(Path.Combine(repository, ".gitignore"), "bin/\nobj/\nTestResults/\n", ct);
// An explicitly supplied local package cache makes this fixture independent of NuGet connectivity.
var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
if (!string.IsNullOrEmpty(cache))
{
    var config = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("configuration",
        new System.Xml.Linq.XElement("packageSources", new System.Xml.Linq.XElement("clear"),
            new System.Xml.Linq.XElement("add", new System.Xml.Linq.XAttribute("key", "fixture-cache"),
                new System.Xml.Linq.XAttribute("value", Path.GetFullPath(cache))))));
    config.Save(Path.Combine(repository, "NuGet.Config"));
}
await Command("dotnet", ["test", "Greeting.csproj", "-m:1", "/nr:false"], repository);
await Command("git", ["init", "-b", "main"], repository);
await Command("git", ["add", "."], repository);
await Command("git", ["-c", "user.name=DevFlow", "-c", "user.email=devflow@localhost", "commit", "-m", "Synthetic baseline"], repository);
var store = new SqliteTaskStore(Path.Combine(root, "devflow.db"));
await store.InitializeAsync(ct);
var project = new ProjectDefinition(Guid.NewGuid(), "Synthetic live CLI fixture", repository, "main", ["test", "Greeting.csproj", "-m:1", "/nr:false"]);
await store.AddProjectAsync(project, ct);
var task = WorkTask.Create(project.Id, "Trim greeting names",
    "Update GreetingService.Greet to trim leading and trailing whitespace. Preserve all other behavior and the greeting format.",
    "Ada stays Hello, Ada!; spaces around Ada produce Hello, Ada!; tabs and newlines around Ada are trimmed. Existing tests stay passing. Red adds behavior tests only, without implementation changes.").RequestPlanning();
await store.AddTaskAsync(task, ct);
var workspaces = new GitWorkspaceManager(process, Path.Combine(root, "worktrees"));
var planning = new PlanningCoordinator(store, new CodexPlanningRunner(process, args[1], artifacts), workspaces);
Console.WriteLine("Planning with real Codex CLI...");
await planning.ExecuteNextAsync(ct);
task = (await store.GetTaskAsync(task.Id, ct))!;
async Task<int> Finish(bool success)
{
    var runs = await store.ListRunsAsync(task.Id, CancellationToken.None);
    await File.WriteAllTextAsync(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(new
    {
        success, task, runs, events = await store.GetEventsAsync(task.Id, ct: CancellationToken.None)
    }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
    Console.WriteLine($"Status: {task.Status}; summary: {Path.Combine(root, "summary.json")}");
    foreach (var run in runs.Where(r => r.Error != null)) Console.Error.WriteLine(run.Error);
    return success ? 0 : 1;
}
if (task.Status != Status.AwaitingApproval) return await Finish(false);
Console.WriteLine(task.Plan!.Text);
Console.WriteLine("Approving synthetic fixture plan; production tasks still require human approval.");
await store.SaveTaskAsync(task.ApprovePlan(), task.Version, ct);
var execution = new ExecutionCoordinator(store, new CodexCliRunner(process, args[1], artifacts), workspaces,
    new DotnetValidationRunner(process, artifacts));
Console.WriteLine("Running Baseline -> Red -> Green -> Refactor...");
await execution.ExecuteNextAsync(ct);
task = (await store.GetTaskAsync(task.Id, ct))!;
if (task.Status != Status.AwaitingReview) return await Finish(false);
var implementation = (await store.ListRunsAsync(task.Id, ct)).Last();
var diff = await Command("git", ["diff", "--no-ext-diff", "--no-textconv", implementation.BaseCommit!, implementation.ResultCommit!], repository);
await File.WriteAllTextAsync(Path.Combine(root, "result.diff"), diff.StandardOutput, ct);
Console.WriteLine(diff.StandardOutput);
Console.WriteLine("Fixture awaits human result review. No merge is performed automatically.");
return await Finish(true);
