using System.Text.Json;
using DevFlow.Core;
using Xunit;

namespace DevFlow.Tests;

public sealed class WorkspaceFileOperationTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"devflow-semantic-files-"+Guid.NewGuid());
    public WorkspaceFileOperationTests() {
        foreach(var folder in new[]{"frontend/src/app/Quality","frontend/src/app/sales/crm","src/folder with spaces","out"})Directory.CreateDirectory(Path.Combine(root,folder));
        foreach(var file in new[]{"frontend/src/app/Quality/app.ts","frontend/src/app/sales/crm/app.html","src/a.json","src/b.json","src/a,b.json","src/[literal].json","src/folder with spaces/app.json"})File.WriteAllText(Path.Combine(root,file),"theme");
    }
    public void Dispose()=>Directory.Delete(root,true);
    private PermissionAssessment Risk(string command,bool readOnly=false,string[][]? scopes=null,PermissionPolicySettings? settings=null)=>PermissionPolicy.Assess(settings??PermissionPolicySettings.Defaults,"run_command",JsonSerializer.SerializeToElement(new{CommandLine=command}),root,readOnly,scopes);
    [Theory]
    [InlineData("Get-ChildItem -Path frontend/src/app/Quality,frontend/src/app/sales/crm -Recurse -Include *.ts,*.html | Select-String -Pattern \"toggleTheme|currentTheme|darkMode|theme-toggle\"")]
    [InlineData("Get-ChildItem -Path 'frontend/src/app/Quality', 'frontend/src/app/sales/crm' -Include '*.ts', '*.html' -Recurse | Select-String -Pattern 'theme','dark' | Select-Object -First 50 -Property Path, LineNumber, Line")]
    [InlineData("Get-Content -Path src/a.json, src/b.json -Raw")]
    [InlineData("Get-Content -LiteralPath 'src/a,b.json','src/[literal].json'")]
    [InlineData("Select-String -Path 'src/a.json', 'src/folder with spaces/app.json' -Pattern theme")]
    [InlineData("Get-Item src/a.json,src/b.json")]
    [InlineData("Test-Path -LiteralPath src/a.json,src/b.json -PathType Leaf")]
    [InlineData("Get-FileHash src/a.json,src/b.json -Algorithm SHA256")]
    [InlineData("Get-Content src/*/*.json")]
    [InlineData("Get-ChildItem frontend/src/app/*/* -Recurse | Select-String -Pattern theme -CaseSensitive:$false")]
    [InlineData("rg -n -g *.ts -g *.html -e theme -e dark frontend/src/app/Quality frontend/src/app/sales/crm")]
    [InlineData("rg -n --glob=**/* -A 2 --column theme frontend/src")]
    public void Equivalent_file_operations_are_allowed_by_all_targets_instead_of_exact_command_shape(string command)
    {var risk=Risk(command);Assert.True(risk.AutoAllowed,risk.Reason);}

    [Theory]
    [InlineData("Get-Content src/a.json,../outside.json")]
    [InlineData("Get-ChildItem -Path frontend/src/app/Quality,../other-task -Recurse | Select-String -Pattern theme")]
    [InlineData("Get-ChildItem src | Select-String -Path ../outside -Pattern theme")]
    [InlineData("Get-ChildItem src | Invoke-Expression")]
    [InlineData("Get-Content src/a.json\nRemove-Item src/b.json")]
    [InlineData("Get-Content -Path $(Get-Content src/a.json)")]
    [InlineData("Get-ChildItem src -FollowSymlink")]
    [InlineData("rg --pre node theme src")]
    [InlineData("Get-Content src/*/../../outside.json")]
    public void Mixed_outside_targets_dynamic_execution_and_link_expansion_require_authorization(string command)=>Assert.False(Risk(command).AutoAllowed);

    [Theory]
    [InlineData("Set-Content -Path src/a.json,src/b.json -Value 'new content'")]
    [InlineData("Set-Content -LiteralPath 'src/a,b.json' -Value ''")]
    [InlineData("Add-Content src/a.json,src/b.json 'more'")]
    [InlineData("Copy-Item src/a.json,src/b.json -Destination out")]
    [InlineData("Move-Item src/a.json,src/b.json out")]
    [InlineData("Rename-Item src/a.json renamed.json")]
    [InlineData("Get-Content src/a.json | Out-File -FilePath out/result.json")]
    [InlineData("Get-Content src/a.json | Set-Content -Path out/a.json,out/b.json")]
    public void File_mutations_check_all_sources_and_destinations_and_respect_readonly(string command)
    {var risk=Risk(command);Assert.True(risk.AutoAllowed,risk.Reason);Assert.False(Risk(command,readOnly:true).AutoAllowed);}

    [Theory]
    [InlineData("Set-Content src/a.json,../outside.json 'new'")]
    [InlineData("Copy-Item src/a.json,../other-task/a.json out")]
    [InlineData("Move-Item src/a.json ../outside.json")]
    [InlineData("Copy-Item src/a.json AGENTS.md")]
    [InlineData("Remove-Item src/a.json,src/b.json")]
    [InlineData("Remove-Item src -Recurse")]
    public void Out_of_scope_mutations_bulk_deletion_and_authority_changes_stay_manual(string command)=>Assert.False(Risk(command).AutoAllowed);

    [Fact] public void Edit_scopes_and_custom_command_restrictions_apply_after_parsing_every_variant()
    {
        Assert.False(Risk("Set-Content src/a.json,src/b.json x",scopes:[["src/a.json"]]).AutoAllowed);
        Assert.False(Risk("Get-Content 'src/a.json'",settings:new(SensitiveCommands:["Get-Content src/a.json"])).AutoAllowed);
        Assert.False(Risk("Get-ChildItem src | Select-String -Pattern theme",settings:new(SensitiveCommands:["Select-String"])).AutoAllowed);
    }
    [Fact] public void Windows_file_aliases_use_the_same_operation_boundaries()
    {
        if(!OperatingSystem.IsWindows())return;
        Assert.True(Risk("dir src,frontend -Recurse").AutoAllowed);
        Assert.True(Risk("gc src/a.json,src/b.json").AutoAllowed);
        Assert.True(Risk("copy src/a.json out").AutoAllowed);
        Assert.False(Risk("copy src/a.json ../outside.json").AutoAllowed);
        Assert.False(Risk("del src/a.json,src/b.json").AutoAllowed);
    }
}
