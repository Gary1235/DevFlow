using System.Text.Json;
using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
namespace DevFlow.Tests;

public sealed class ProjectedFileSearchPermissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-projected-search-" + Guid.NewGuid());
    private const string Screenshot = "Select-String -Path (Get-ChildItem -Path frontend/src -Recurse -Include *.ts,*.html).FullName -Pattern \"isDark|toggleTheme|toggleDarkMode|darkMode|dark-mode|dark mode|深色|淺色|dark mode|light mode\" -List | Select-Object -First 30 Path, LineNumber, Line";
    public ProjectedFileSearchPermissionTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "frontend", "src", "nested"));
        File.WriteAllText(Path.Combine(root, "frontend", "src", "nested", "app.ts"), "const isDark = true;\n");
    }
    public void Dispose() => Directory.Delete(root, true);
    private PermissionAssessment Risk(string command, bool readOnly = true, string shell = "powershell", PermissionPolicySettings? settings = null,
        string[][]? scopes = null) => PermissionPolicy.Assess(settings ?? PermissionPolicySettings.Defaults, "run_command",
            JsonSerializer.SerializeToElement(new { CommandLine = command, Shell = shell }), root, readOnly, scopes);

    [Theory]
    [InlineData(Screenshot)]
    [InlineData("Select-String -Pattern '(isDark|toggleTheme)' -Path (Get-ChildItem frontend/src -Recurse -File).FullName -List")]
    [InlineData("Select-String -LiteralPath (Get-ChildItem -LiteralPath frontend/src -Recurse).FullName -Pattern 'please delete file'")]
    [InlineData("select-string -path (get-childitem frontend/src -Recurse).fullname -pattern isDark")]
    [InlineData("Select-String -Path (Get-ChildItem -Path frontend/src/*.ts -Recurse).FullName -Pattern isDark")]
    [InlineData("Select-String -Path (Get-ChildItem -Path frontend/src,frontend/src/nested -Recurse).FullName -Pattern isDark")]
    public void Explicit_file_name_projections_are_general_reads_in_readonly_stages(string command)
    {
        var risk = Risk(command, scopes: [["unrelated/"]]);
        Assert.True(risk.AutoAllowed, risk.Reason); Assert.Equal("GeneralRead", risk.Category);
    }

    private const string Standalone = "(Get-ChildItem -Path \"frontend/src\" -Filter \"*shared-mgt*\" -Recurse).FullName";

    [Theory]
    [InlineData(Standalone)]
    [InlineData("  (get-childitem frontend/src -Recurse).fullname  ")]
    [InlineData("(Get-ChildItem -LiteralPath frontend/src -File -Recurse).FullName")]
    [InlineData("(Get-ChildItem -Path frontend/src,frontend/src/nested -Directory -Force).FullName")]
    [InlineData("(Get-ChildItem frontend/src -Recurse).FullName | Select-Object -First 30")]
    public void Standalone_projections_only_list_names_and_are_allowed_in_both_execution_modes(string command)
    {
        foreach (var readOnly in new[] { true, false }) {
            var risk = Risk(command, readOnly, scopes: [["unrelated/"]]);
            Assert.True(risk.AutoAllowed, risk.Reason); Assert.Equal("GeneralRead", risk.Category);
        }
    }

    [Theory]
    [InlineData("(Get-ChildItem ../outside -Recurse).FullName")]
    [InlineData("(Get-ChildItem frontend/src,../outside -Recurse).FullName")]
    [InlineData("(Get-ChildItem frontend/src -FollowSymlink).FullName")]
    [InlineData("(Get-Content frontend/src/app.ts).FullName")]
    [InlineData("(Get-ChildItem (Get-Content source.txt)).FullName")]
    [InlineData("(Get-ChildItem frontend/src; Remove-Item frontend/src).FullName")]
    [InlineData("(Get-ChildItem frontend/src | ForEach-Object { $_ }).FullName")]
    [InlineData("(Get-ChildItem frontend/src).FullName.Length")]
    [InlineData("(Get-ChildItem frontend/src).FullName + (Get-Content ../secret.txt)")]
    [InlineData("(Get-ChildItem frontend/src).FullName,../outside")]
    [InlineData("(Get-ChildItem frontend/src -Name).FullName")]
    [InlineData("(Get-ChildItem frontend/src).ToString()")]
    [InlineData("(Get-ChildItem frontend/src -Filter \"$filter\").FullName")]
    [InlineData("(Get-ChildItem frontend/src).FullName > result.txt")]
    public void Standalone_projection_does_not_authorize_other_expressions_or_external_operations(string command) =>
        Assert.False(Risk(command).AutoAllowed);

    [Fact]
    public void Standalone_projection_preserves_command_restrictions_shell_context_and_legacy_mode()
    {
        Assert.False(Risk(Standalone, shell: "bash").AutoAllowed);
        Assert.False(Risk(Standalone, shell: "unknown").AutoAllowed);
        Assert.False(Risk(Standalone, settings: new(AutomationEnabled: false)).AutoAllowed);
        Assert.False(Risk(Standalone, settings: new(SensitiveCommands: ["Get-ChildItem"])).AutoAllowed);
        if (!OperatingSystem.IsWindows()) return;
        const string alias = "(gci frontend/src -Recurse).FullName";
        Assert.True(Risk(alias).AutoAllowed);
        foreach (var restriction in new[] { "gci", "Get-ChildItem", "Get-ChildItem frontend/src" })
            Assert.False(Risk(alias, settings: new(SensitiveCommands: [restriction])).AutoAllowed);
    }

    [Fact]
    public void Printed_wildcard_names_do_not_require_search_consumer_restrictions()
    {
        File.WriteAllText(Path.Combine(root, "frontend", "src", "[shared-mgt].ts"), "synthetic");
        Directory.CreateDirectory(Path.Combine(root, "frontend", "src", "[directory]"));
        Assert.True(Risk(Standalone).AutoAllowed);
        Assert.True(Risk("(Get-ChildItem -LiteralPath 'frontend/src/[directory]' -Recurse).FullName").AutoAllowed);
    }

    [Fact]
    public void Standalone_projection_output_keeps_write_permissions_and_scope_checks()
    {
        var command = Standalone + " | Out-File result.txt";
        Assert.False(Risk(command).AutoAllowed);
        Assert.True(Risk(command, readOnly: false).AutoAllowed);
        Assert.Equal("Write", Risk(command, readOnly: false).Category);
        Assert.False(Risk(command, readOnly: false, scopes: [["frontend/"]]).AutoAllowed);
        Assert.False(Risk(Standalone + " | Out-File ../outside.txt", readOnly: false).AutoAllowed);
    }

    [Fact]
    public async Task Standalone_screenshot_lists_matching_file_paths_in_actual_PowerShell()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(root, "frontend", "src", "nested", "shared-mgt.scss");
        File.WriteAllText(path, "synthetic");
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await new ProcessRunner().RunAsync(exe, ["-NoProfile", "-NonInteractive", "-Command", Standalone], root, null, timeout.Token);
        Assert.Equal(0, result.ExitCode); Assert.Equal(path, result.StandardOutput.Trim());
        Assert.True(Risk(Standalone).AutoAllowed);
    }

    [Theory]
    [InlineData("Select-String -Path (Get-ChildItem ../outside -Recurse).FullName -Pattern isDark")]
    [InlineData("Select-String -Path (Get-ChildItem frontend/src,../outside -Recurse).FullName -Pattern isDark")]
    [InlineData("Select-String -Path (Get-ChildItem frontend/src -Recurse -FollowSymlink).FullName -Pattern isDark")]
    [InlineData("Select-String -Path (Get-Content frontend/src/file.txt).FullName -Pattern isDark")]
    [InlineData("Select-String -Path (Get-ChildItem (Get-Content source.txt)).FullName -Pattern isDark")]
    [InlineData("Select-String -Path (Get-ChildItem frontend/src; Remove-Item frontend/src).FullName -Pattern isDark")]
    [InlineData("Select-String -Path (Get-ChildItem frontend/src | ForEach-Object { $_ }).FullName -Pattern isDark")]
    [InlineData("Select-String -Path (Get-ChildItem frontend/src).FullName.Length -Pattern isDark")]
    [InlineData("Select-String -Path (Get-ChildItem frontend/src).ToString() -Pattern isDark")]
    [InlineData("Select-String -Pattern (Get-ChildItem frontend/src).FullName -Path frontend/src/a.ts")]
    [InlineData("Select-String -Path (Get-ChildItem frontend/src).FullName,../outside.ts -Pattern isDark")]
    [InlineData("Select-String -Path (Get-ChildItem frontend/src).FullName -Path ../outside.ts -Pattern isDark")]
    [InlineData("Select-String -Path (Get-ChildItem frontend/src).FullName -Pattern $(Get-Content .env)")]
    [InlineData("Select-String -Path (Get-ChildItem frontend/src -Name).FullName -Pattern isDark")]
    public void Other_expressions_scripts_members_and_external_sources_stay_manual(string command) => Assert.False(Risk(command).AutoAllowed);

    [Theory][InlineData("Get-ChildItem")][InlineData("Select-String")][InlineData("Select-Object")]
    public void Explicit_command_restrictions_apply_to_every_embedded_operation(string restricted) =>
        Assert.False(Risk(Screenshot, settings: new(SensitiveCommands: [restricted])).AutoAllowed);

    [Fact]
    public void PowerShell_aliases_keep_both_inner_and_outer_command_restrictions()
    {
        if (!OperatingSystem.IsWindows()) return;
        var command = "sls -Path (gci -Path frontend/src -Recurse).FullName -Pattern isDark";
        Assert.True(Risk(command).AutoAllowed);
        foreach (var restriction in new[] { "Select-String", "sls", "Get-ChildItem", "gci", "Select-String -Path" }) {
            var risk = Risk(command, settings: new(SensitiveCommands: [restriction]));
            Assert.False(risk.AutoAllowed); Assert.Equal("SensitiveCommand", risk.Category);
        }
    }

    [Theory][InlineData("bash")][InlineData("unknown")]
    public void Projection_semantics_are_only_used_for_PowerShell(string shell) => Assert.False(Risk(Screenshot, shell: shell).AutoAllowed);

    [Fact]
    public void Settings_off_keep_the_legacy_manual_grammar()
    {
        Assert.False(Risk(Screenshot, settings: new(AutomationEnabled: false)).AutoAllowed);
        Assert.False(Risk(Screenshot, settings: new(AutomationEnabled: false)).BatchAllowed);
    }

    [Fact]
    public void Projected_paths_preserve_all_file_types_and_sensitive_names()
    {
        File.WriteAllText(Path.Combine(root, "frontend", "src", "secrets.ts"), "isDark");
        File.WriteAllText(Path.Combine(root, "frontend", "src", "data.sqlite"), "synthetic");
        Assert.True(Risk(Screenshot, settings: new(SensitivePaths: ["**/secrets.ts"])).AutoAllowed);
    }

    [Fact]
    public void Returned_wildcard_names_need_literal_consumer_paths()
    {
        File.WriteAllText(Path.Combine(root, "frontend", "src", "[view].ts"), "isDark");
        Assert.False(Risk(Screenshot).AutoAllowed);
        Assert.True(Risk(Screenshot.Replace("Select-String -Path", "Select-String -LiteralPath")).AutoAllowed);
    }

    [Fact]
    public void A_literal_directory_name_is_not_reinterpreted_as_a_wildcard_consumer_path()
    {
        Directory.CreateDirectory(Path.Combine(root, "frontend", "src", "[view]"));
        File.WriteAllText(Path.Combine(root, "frontend", "src", "[view]", "app.ts"), "isDark");
        var command = "Select-String -Path (Get-ChildItem -LiteralPath 'frontend/src/[view]' -Recurse).FullName -Pattern isDark";
        Assert.False(Risk(command).AutoAllowed);
        Assert.True(Risk(command.Replace("Select-String -Path", "Select-String -LiteralPath")).AutoAllowed);
    }

    [Fact]
    public void Search_output_retains_write_and_scope_checks()
    {
        var command = Screenshot + " | Out-File result.txt";
        Assert.True(Risk(command, readOnly: false).AutoAllowed);
        Assert.Equal("Write", Risk(command, readOnly: false).Category);
        Assert.False(Risk(command).AutoAllowed);
        Assert.False(Risk(command, readOnly: false, scopes: [["frontend/"]]).AutoAllowed);
        Assert.False(Risk(Screenshot + " | Out-File ../outside.txt", readOnly: false).AutoAllowed);
    }

    [Fact]
    public async Task Screenshot_expression_and_pipeline_search_the_same_synthetic_file_in_actual_PowerShell()
    {
        if (!OperatingSystem.IsWindows()) return;
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var pipeline = "Get-ChildItem -Path frontend/src -Recurse -File -Include *.ts,*.html | Select-String -Pattern isDark -List | Select-Object -First 30 Path,LineNumber,Line";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var process = new ProcessRunner();
        var expressionResult = await process.RunAsync(exe, ["-NoProfile", "-NonInteractive", "-Command", Screenshot + " | ConvertTo-Json -Compress"], root, null, timeout.Token);
        var pipelineResult = await process.RunAsync(exe, ["-NoProfile", "-NonInteractive", "-Command", pipeline + " | ConvertTo-Json -Compress"], root, null, timeout.Token);
        Assert.Equal(0, expressionResult.ExitCode); Assert.Equal(0, pipelineResult.ExitCode);
        Assert.Equal(pipelineResult.StandardOutput.Trim(), expressionResult.StandardOutput.Trim());
        Assert.Contains("app.ts", expressionResult.StandardOutput);
    }
}
