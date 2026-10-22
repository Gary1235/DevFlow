using System.Text.Json;
using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
namespace DevFlow.Tests;

public sealed class TrustedValidationPermissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-trusted-validation-" + Guid.NewGuid());
    public TrustedValidationPermissionTests() => Directory.CreateDirectory(Path.Combine(root, "frontend"));
    public void Dispose() => Directory.Delete(root, true);
    private ProjectDefinition Project() => new(Guid.NewGuid(), "Synthetic", root, "main", ["test"], Version: 3,
        ValidationProfiles: [new("api", "API", ".", "dotnet", ["build"]), new("web", "Web", "frontend", "npm", ["run", "build"], PrepareArguments: ["ci"])], DefaultValidationProfileId: "api");
    private PermissionAssessment Risk(string text, string cwd = ".", string shell = "powershell", bool readOnly = false,
        PermissionPolicySettings? settings = null, ProjectDefinition? project = null) => PermissionPolicy.Assess(settings ?? PermissionPolicySettings.Defaults,
            "run_command", JsonSerializer.SerializeToElement(new { CommandLine = text, Cwd = cwd, Shell = shell }), root, readOnly, project: project ?? Project());

    [Theory][InlineData("powershell")][InlineData("bash")][InlineData("unknown")]
    public void Exact_saved_build_is_preapproved_across_shell_adapters(string shell)
    {
        var project = Project(); var risk = Risk("dotnet build", shell: shell, project: project);
        Assert.True(risk.AutoAllowed, risk.Reason); Assert.Equal("Execution", risk.Category);
        Assert.Contains(project.Id.ToString(), risk.Reason); Assert.Contains("api", risk.Reason); Assert.Contains("3", risk.Reason);
        Assert.True(Risk("npm run build", "frontend", shell).AutoAllowed);
        Assert.True(Risk("dotnet 'build'", shell: shell).AutoAllowed);
    }

    [Theory]
    [InlineData("dotnet build --configuration Release")][InlineData("dotnet build Another.sln")]
    [InlineData("dotnet build; node other.js")][InlineData("dotnet build | Out-File out.txt")]
    [InlineData("dotnet build > out.txt")][InlineData("dotnet build && git push")]
    [InlineData("powershell -Command 'dotnet build'")][InlineData("cmd /c dotnet build")]
    [InlineData("node other.js")][InlineData("npm ci")][InlineData("npm install")]
    [InlineData("git push")][InlineData("dotnet restore")]
    public void Saved_profile_is_not_a_prefix_script_installation_or_network_grant(string text) => Assert.False(Risk(text).AutoAllowed);

    [Fact]
    public void Trusted_build_keeps_readonly_scope_and_sensitive_command_restrictions()
    {
        Assert.False(Risk("dotnet build", readOnly: true).AutoAllowed);
        Assert.False(Risk("dotnet build", "frontend").AutoAllowed);
        Assert.False(Risk("npm run build").AutoAllowed);
        Assert.False(Risk("dotnet build", "../outside").AutoAllowed);
        Assert.False(Risk("dotnet build", settings: new(SensitiveCommands: ["dotnet build"])).AutoAllowed);
        Assert.False(Risk("dotnet build", settings: new(AutomationEnabled: false)).AutoAllowed);
        var expanded = JsonSerializer.SerializeToElement(new { CommandLine = "dotnet build", AdditionalPermissions = new { network = true } });
        Assert.False(PermissionPolicy.Assess(PermissionPolicySettings.Defaults, "run_command", expanded, root, project: Project()).AutoAllowed);
    }

    [Fact]
    public void The_legacy_configured_dotnet_test_is_also_preapproved()
    {
        var legacy = new ProjectDefinition(Guid.NewGuid(), "Legacy", root, "main", ["test", "Synthetic.sln"]);
        Assert.True(Risk("dotnet test Synthetic.sln", project: legacy).AutoAllowed);
        Assert.False(Risk("dotnet test Other.sln", project: legacy).AutoAllowed);
    }

    [Fact]
    public void Trusted_working_directories_use_boundaries_instead_of_sensitive_names()
    {
        Directory.CreateDirectory(Path.Combine(root, "private"));
        var project = Project() with { ValidationProfiles = [new("api", "API", "private", "dotnet", ["build"])] };
        Assert.True(Risk("dotnet build", "private", project: project).AutoAllowed);
        Assert.False(Risk("dotnet build", project: project).AutoAllowed);
    }

    [Fact]
    public void An_unavailable_profile_directory_does_not_mask_another_exact_grant()
    {
        var project = Project() with { ValidationProfiles = [new("missing", "Missing", "absent", "dotnet", ["build"]), new("api", "API", ".", "dotnet", ["build"])] };
        Assert.True(Risk("dotnet build", project: project).AutoAllowed);
    }

    [Theory][InlineData("bash")][InlineData("unknown")][InlineData("powershell")]
    public void Shell_expansion_is_never_equated_to_literal_profile_arguments(string shell)
    {
        foreach (var argument in new[] { "*.sln", "$target", "~/app.sln", "a;node", "a|node", "[ab].sln" }) {
            var project = Project() with { ValidationProfiles = [new("api", "API", ".", "dotnet", ["build", argument])] };
            Assert.False(Risk("dotnet build " + argument, shell: shell, project: project).AutoAllowed);
        }
    }

    [Fact]
    public async Task Shared_store_uses_current_project_grants_and_revokes_subsequent_requests_when_removed()
    {
        var store = new SqliteTaskStore(Path.Combine(root, "tasks.db")); await store.InitializeAsync();
        var project = Project() with { Version = 0 }; await store.AddProjectAsync(project);
        var task = WorkTask.Create(project.Id, "Feature", "Implement", "Validate").SetPlan("Implement changes and run tests.").ApprovePlan();
        await store.AddTaskAsync(task); var claim = (await store.ClaimNextAsync())!;
        TaskToolApproval Request() => new(Guid.NewGuid(), task.Id, claim.Run.Id, "run_command",
            JsonSerializer.SerializeToElement(new { CommandLine = "dotnet build", Cwd = root, Shell = "unknown" }), root, DateTimeOffset.UtcNow, null);
        await store.AddToolApprovalAsync(Request());
        Assert.Equal("AllowedByPolicy", (await store.ListToolApprovalsAsync(task.Id)).Single().State);
        await store.SetExecutionSettingsAsync(project.Id, 0, [], null, [], null);
        await new SqliteTaskStore(store.DatabasePath).AddToolApprovalAsync(Request());
        Assert.Equal("Pending", (await store.ListToolApprovalsAsync(task.Id)).Last().State);
        var other = new ProjectDefinition(Guid.NewGuid(), "Other", root, "main", ["test"]); await store.AddProjectAsync(other);
        var otherTask = WorkTask.Create(other.Id, "Other", "Implement", "Validate").SetPlan("Implement changes and run tests.").ApprovePlan();
        await store.AddTaskAsync(otherTask); var otherClaim = (await store.ClaimNextAsync())!;
        await store.AddToolApprovalAsync(Request() with { Id = Guid.NewGuid(), TaskId = otherTask.Id, OperationId = otherClaim.Run.Id });
        Assert.Equal("Pending", (await store.ListToolApprovalsAsync(otherTask.Id)).Single().State);
    }
}
