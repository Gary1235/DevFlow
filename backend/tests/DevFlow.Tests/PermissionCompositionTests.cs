using System.Text.Json;
using DevFlow.Core;
using Xunit;
namespace DevFlow.Tests;

public sealed class PermissionCompositionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-composition-" + Guid.NewGuid());
    public PermissionCompositionTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "out"));
        foreach (var name in new[] { "a.json", "b.json", "app~old.cs", "[literal].json" })
            File.WriteAllText(Path.Combine(root, "src", name), "synthetic fixture");
    }
    public void Dispose() => Directory.Delete(root, true);
    private PermissionAssessment Command(string command, bool readOnly = false, string[][]? scopes = null) =>
        PermissionPolicy.Assess(PermissionPolicySettings.Defaults, "run_command",
            JsonSerializer.SerializeToElement(new { CommandLine = command, Shell = "powershell" }), root, readOnly, scopes);

    [Theory]
    [InlineData("Remove-Item src/a.json,src/b.json")]
    [InlineData("Remove-Item src/a.json; Remove-Item src/b.json")]
    [InlineData("Remove-Item src/a.json | Remove-Item src/b.json")]
    [InlineData("Remove-Item src/a.json | Select-Object Name; Remove-Item src/b.json")]
    [InlineData("git status -s; Remove-Item src/a.json | Remove-Item src/b.json")]
    public void Multiple_deletions_require_confirmation_across_the_entire_request(string command)
    {
        var risk = Command(command);
        Assert.False(risk.AutoAllowed); Assert.True(risk.FixedBoundary); Assert.Equal("Deletion", risk.Category);
        Assert.True(File.Exists(Path.Combine(root, "src", "a.json")));
        Assert.True(File.Exists(Path.Combine(root, "src", "b.json")));
    }

    [Theory]
    [InlineData("Set-Content src/a.json x; Set-Content src/b.json y", "Write", 0)]
    [InlineData("Set-Content src/a.json x; Get-Content src/b.json", "Write", 0)]
    [InlineData("Get-Content src/a.json; Set-Content src/b.json x", "Write", 0)]
    [InlineData("Set-Content src/a.json x | Select-Object Name", "Write", 0)]
    [InlineData("Remove-Item src/a.json; Get-Content src/b.json", "Deletion", 1)]
    [InlineData("Get-Content src/a.json; Remove-Item src/b.json", "Deletion", 1)]
    [InlineData("Remove-Item src/a.json | Out-File out/log.txt", "Deletion", 1)]
    [InlineData("git status -s; Get-Content src/a.json", "GeneralRead", 0)]
    public void Composition_preserves_effects_and_single_file_deletion(string command, string category, int deleted)
    {
        var risk = Command(command);
        Assert.True(risk.AutoAllowed, risk.Reason); Assert.Equal(category, risk.Category); Assert.Equal(deleted, risk.DeletedFileCount);
    }

    [Theory]
    [InlineData("Get-Content -LiteralPath src/app~old.cs")]
    [InlineData("Set-Content -LiteralPath src/app~old.cs -Value x")]
    [InlineData("Copy-Item -LiteralPath src/a.json -Destination 'out/[literal].json'")]
    [InlineData("Copy-Item -LiteralPath 'src/[literal].json' -Destination out")]
    [InlineData("Move-Item -LiteralPath src/a.json -Destination 'out/app~old.cs'")]
    [InlineData("Rename-Item -LiteralPath src/a.json -NewName '[renamed]~old.json'")]
    public void Literal_worktree_names_do_not_become_path_expansion(string command) => Assert.True(Command(command).AutoAllowed);

    [Theory]
    [InlineData("read_file")][InlineData("write_file")][InlineData("delete_file")]
    public void Native_exact_paths_allow_embedded_tilde(string tool)
    {
        var risk = PermissionPolicy.Assess(PermissionPolicySettings.Defaults, tool,
            JsonSerializer.SerializeToElement(new { Path = Path.Combine(root, "src", "app~old.cs") }), root);
        Assert.True(risk.AutoAllowed, risk.Reason);
        Assert.Equal(tool == "delete_file" ? 1 : 0, risk.DeletedFileCount);
    }

    [Fact]
    public void Native_and_command_transfers_agree_on_literal_destination_names()
    {
        var risk = PermissionPolicy.Assess(PermissionPolicySettings.Defaults, "copy_file",
            JsonSerializer.SerializeToElement(new { SourcePath = "src/a.json", DestinationPath = "out/[literal]~old.json" }), root);
        Assert.True(risk.AutoAllowed);
        Assert.True(Command("Copy-Item -LiteralPath src/a.json -Destination 'out/[literal]~old.json'").AutoAllowed);
    }

    [Fact]
    public void Native_delete_with_multiple_targets_requires_confirmation()
    {
        var risk = PermissionPolicy.Assess(PermissionPolicySettings.Defaults, "delete_file",
            JsonSerializer.SerializeToElement(new { Path = "src/a.json", AbsolutePath = Path.Combine(root, "src", "b.json") }), root);
        Assert.False(risk.AutoAllowed); Assert.True(risk.FixedBoundary); Assert.Equal("Deletion", risk.Category);
    }

    [Theory]
    [InlineData("Copy-Item -LiteralPath src/a.json -Destination '../[literal].json'")]
    [InlineData("Copy-Item -LiteralPath src/a.json -Destination '~/[literal].json'")]
    [InlineData("Copy-Item -LiteralPath src/a.json -Destination 'out/*.json'")]
    [InlineData("Get-Content -LiteralPath ../app~old.cs")]
    [InlineData("Get-Content -LiteralPath ~/app~old.cs")]
    public void Literal_names_do_not_expand_boundaries(string command) => Assert.False(Command(command).AutoAllowed);

    [Theory]
    [InlineData("Set-Content -LiteralPath src/app~old.cs -Value x")]
    [InlineData("Copy-Item -LiteralPath src/a.json -Destination 'out/[literal].json'")]
    [InlineData("git status -s; Set-Content src/a.json x")]
    public void Composition_and_literal_names_keep_readonly_and_edit_scopes(string command)
    {
        Assert.False(Command(command, readOnly: true).AutoAllowed);
        Assert.False(Command(command, scopes: [["unrelated/"]]).AutoAllowed);
    }
}
