using System.Text.Json;
using DevFlow.Core;
using Xunit;
namespace DevFlow.Tests;

public sealed class PermissionAuditRegressionTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"devflow-permission-audit-"+Guid.NewGuid());
    public PermissionAuditRegressionTests(){Directory.CreateDirectory(Path.Combine(root,"src"));Directory.CreateDirectory(Path.Combine(root,"out"));File.WriteAllText(Path.Combine(root,"src","a.json"),"{}");}
    public void Dispose()=>Directory.Delete(root,true);
    private PermissionAssessment Risk(string command,PermissionPolicySettings? settings=null)=>PermissionPolicy.Assess(settings??PermissionPolicySettings.Defaults,"run_command",JsonSerializer.SerializeToElement(new{CommandLine=command}),root);

    [Theory]
    [InlineData(".gemini/settings.json")][InlineData(".gemini/hooks/approval.json")]
    [InlineData("src/.gemini/settings.json")]
    [InlineData(".mcp.json")][InlineData("AGENTS.override.md")][InlineData("CLAUDE.local.md")]
    [InlineData("src/AGENTS.override.md")][InlineData("src/CLAUDE.local.md")]
    public void Gemini_agent_settings_allow_reads_but_require_confirmation_for_mutations(string path)
    {
        foreach(var tool in new[]{"write_file","delete_file"}) {
            var risk=PermissionPolicy.Assess(PermissionPolicySettings.Defaults,tool,JsonSerializer.SerializeToElement(new{Path=path}),root);
            Assert.False(risk.AutoAllowed);Assert.True(risk.FixedBoundary);Assert.Equal("Boundary",risk.Category);
        }
        Assert.True(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"read_file",JsonSerializer.SerializeToElement(new{Path=path}),root).AutoAllowed);
        Assert.False(Risk($"Set-Content -LiteralPath {path} -Value synthetic").AutoAllowed);
        Assert.False(Risk($"Remove-Item -LiteralPath {path}").AutoAllowed);
        Assert.False(Risk($"Copy-Item -LiteralPath src/a.json -Destination {path}").AutoAllowed);
        var changes=JsonSerializer.SerializeToElement(new{changes=new[]{new{path,kind=new{type="update"}}}});
        Assert.False(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"write_file",changes,root).AutoAllowed);
        Assert.True(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"write_file",JsonSerializer.SerializeToElement(new{Path="src/gemini-settings.json"}),root).AutoAllowed);
    }
    [Theory]
    [InlineData("Get-Content.exe src/a.json")]
    [InlineData("Get-Content\\evil.exe src/a.json")]
    [InlineData("Get-Content/evil src/a.json")]
    [InlineData("Set-Content.exe src/a.json value")]
    [InlineData("Set-Content.cmd src/a.json value")]
    [InlineData("Get-ChildItem.exe src")]
    [InlineData("gc.exe src/a.json")]
    [InlineData("sc.exe src/a.json value")]
    [InlineData("Get-Content src/a.json | Select-String.exe -Pattern theme")]
    public void Executable_and_module_prefixes_never_inherit_builtin_file_permissions(string command)=>Assert.False(Risk(command).AutoAllowed);

    [Theory]
    [InlineData("Get-Content -Delimiter ',' -Path src/a.json")]
    [InlineData("Get-Content -LiteralPath src/a.json -AsByteStream")]
    [InlineData("Get-Content -Path src/*.json -Include *.json -Exclude other.json -Raw")]
    [InlineData("Select-String -Pattern theme -Path src/a.json -Culture en-US")]
    public void Read_presentation_and_filters_do_not_require_extra_authorization(string command)
    {
        Assert.True(Risk(command).AutoAllowed);
        Assert.False(Risk(command.Replace("src/a.json","../outside.json").Replace("src/*.json","../*.json")).AutoAllowed);
    }
    [Theory]
    [InlineData("Set-Content","sc src/a.json x")]
    [InlineData("sc","Set-Content src/a.json x")]
    [InlineData("Set-Content src/a.json","sc 'src/a.json' x")]
    [InlineData("Get-Content","gc src/a.json")]
    [InlineData("Get-ChildItem","dir src")]
    [InlineData("Select-String","Get-Content src/a.json | sls -Pattern theme")]
    public void Aliases_obey_explicit_command_restrictions_in_both_directions(string restriction,string command)
    {
        if(!OperatingSystem.IsWindows())return;
        Assert.True(Risk(command).AutoAllowed);
        var risk=Risk(command,new(SensitiveCommands:[restriction]));
        Assert.False(risk.AutoAllowed);Assert.Equal("SensitiveCommand",risk.Category);
    }

    [Theory][InlineData("codex")][InlineData("claude")][InlineData("gemini")]
    public void Native_bulk_deletion_requires_authorization_while_single_file_deletion_is_allowed(string provider)
    {
        object Change(string path)=>new{path,kind=new{type="delete"}};
        var payload=JsonSerializer.SerializeToElement(new{changes=new[]{Change("src/a.json"),Change("src/b.json")}});
        var normalized=PermissionTools.Normalize(provider,"fileChange",payload,root);
        var risk=PermissionPolicy.Assess(PermissionPolicySettings.Defaults,normalized.Tool,normalized.Arguments,root);
        Assert.False(risk.AutoAllowed);Assert.Equal("Deletion",risk.Category);Assert.True(risk.FixedBoundary);
        var single=JsonSerializer.SerializeToElement(new{changes=new[]{Change("src/a.json")}});
        var singleRisk=PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"write_file",single,root);
        Assert.True(singleRisk.AutoAllowed);Assert.Equal("Deletion",singleRisk.Category);
        Assert.False(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"write_file",single,root,readOnly:true).AutoAllowed);
    }
    [Theory][InlineData("copy_file")][InlineData("move_file")][InlineData("rename_file")]
    public void Native_transfers_check_the_actual_destination_file_and_keep_readonly_and_edit_scopes(string tool)
    {
        var args=JsonSerializer.SerializeToElement(new{SourcePath="src/a.json",DestinationPath="out"});
        PermissionAssessment Check(bool readOnly=false,string[][]? scopes=null)=>PermissionPolicy.Assess(PermissionPolicySettings.Defaults,tool,args,root,readOnly,scopes);
        Assert.True(Check().AutoAllowed);
        Assert.False(Check(readOnly:true).AutoAllowed);
        Assert.False(Check(scopes:[["out/b.json"]]).AutoAllowed);
        Assert.Equal(tool=="copy_file",Check(scopes:[["out/"]]).AutoAllowed);
        var outside=JsonSerializer.SerializeToElement(new{SourcePath="src/a.json",DestinationPath="../other-task"});
        Assert.False(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,tool,outside,root).AutoAllowed);
        var protectedTarget=JsonSerializer.SerializeToElement(new{SourcePath="src/a.json",DestinationPath="AGENTS.md"});
        Assert.False(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,tool,protectedTarget,root).AutoAllowed);
    }
}
