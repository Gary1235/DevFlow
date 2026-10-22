using System.Text.Json;
using DevFlow.Core;
using Xunit;
namespace DevFlow.Tests;

public sealed class WorkspaceOperationPolicyTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"devflow-operation-"+Guid.NewGuid());
    public WorkspaceOperationPolicyTests(){Directory.CreateDirectory(Path.Combine(root,"src","nested"));File.WriteAllText(Path.Combine(root,"src","nested","layout.json"),"theme");}
    public void Dispose()=>Directory.Delete(root,true);
    private PermissionAssessment Risk(string command,PermissionPolicySettings? policy=null)=>PermissionPolicy.Assess(policy??PermissionPolicySettings.Defaults,"run_command",JsonSerializer.SerializeToElement(new{CommandLine=command}),root);
    [Theory]
    [InlineData("rg theme src")]
    [InlineData("rg -n -i -e theme -- src")]
    [InlineData("rg theme")]
    [InlineData("Get-Content src/nested/layout.json")]
    [InlineData("Select-String -Pattern theme -LiteralPath src/nested/layout.json")]
    [InlineData("Get-Content src/nested/*.json")]
    [InlineData("Select-String -Pattern theme -Path src/nested/*")]
    [InlineData("git diff --no-ext-diff --no-textconv -- src/nested/layout.json")]
    public void Ordinary_reads_are_classified_by_operation_and_targets_instead_of_extension(string command)
    {
        Assert.True(Risk(command).AutoAllowed);
        Assert.False(Risk(command,new(AutomationEnabled:false)).AutoAllowed);
        Assert.True(Risk(command,new(SensitivePaths:["**/layout.json"])).AutoAllowed);
    }
    [Theory]
    [InlineData(".env")][InlineData("data.db")][InlineData("credentials.ts")][InlineData("records.csv")]
    public void Workspace_contents_are_authorized_regardless_of_sensitive_names(string file)
    {
        File.WriteAllText(Path.Combine(root,"src","nested",file),"unrelated content");
        Assert.True(Risk("rg theme src").AutoAllowed);
        Assert.True(NativeSearch().AutoAllowed);
    }
    [Fact] public void Workspace_authorization_never_grants_a_sibling_project_or_expands_read_only_and_edit_scopes()
    {
        var policy=new PermissionPolicySettings(SensitivePaths:["**"]);
        foreach(var tool in new[]{"read_file","write_file","delete_file"}) {
            Assert.True(PermissionPolicy.Assess(policy,tool,JsonSerializer.SerializeToElement(new{Path="src/.env"}),root).AutoAllowed);
            Assert.False(PermissionPolicy.Assess(policy,tool,JsonSerializer.SerializeToElement(new{Path="../other-task/.env"}),root).AutoAllowed);
        }
        foreach(var tool in new[]{"write_file","delete_file"}) {
            Assert.False(PermissionPolicy.Assess(policy,tool,JsonSerializer.SerializeToElement(new{Path="src/.env"}),root,readOnly:true).AutoAllowed);
            Assert.False(PermissionPolicy.Assess(policy,tool,JsonSerializer.SerializeToElement(new{Path="src/.env"}),root,editScopes:[["frontend/"]]).AutoAllowed);
        }
        var move=JsonSerializer.SerializeToElement(new{SourcePath="src/.env",DestinationPath="../other-task/.env"});
        Assert.False(PermissionPolicy.Assess(policy,"move_file",move,root).AutoAllowed);
        Assert.False(Risk("Get-Content ../other-task/*").AutoAllowed);
    }
    private PermissionAssessment NativeSearch(PermissionPolicySettings? policy=null)=>PermissionPolicy.Assess(policy??PermissionPolicySettings.Defaults,"grep_search",JsonSerializer.SerializeToElement(new{SearchPath="src",Query="theme"}),root);
    [Fact] public void Native_search_uses_the_same_target_policy()
    {
        Assert.True(NativeSearch().AutoAllowed);
        Assert.True(NativeSearch(new(SensitivePaths:["**/layout.json"])).AutoAllowed);
    }
    [Theory]
    [InlineData("rg theme ../")]
    [InlineData("rg theme src --pre node")]
    [InlineData("rg theme src --follow")]
    [InlineData("rg theme src > output.txt")]
    [InlineData("rg theme src; npm run deploy")]
    [InlineData("Get-Content src/nested/layout.json | Invoke-Expression")]
    [InlineData("Get-Content src/nested/layout.json -Stream hidden")]
    public void Outside_targets_unknown_execution_and_output_writes_stay_manual(string command)=>Assert.False(Risk(command).AutoAllowed);
    [Fact] public void Custom_command_restrictions_and_link_boundaries_still_win()
    {
        Assert.False(Risk("rg theme src",new(SensitiveCommands:["rg"])).AutoAllowed);
        var link=Path.Combine(root,"src","linked.json");
        try {File.CreateSymbolicLink(link,Path.Combine(root,"src","nested","layout.json"));}
        catch(Exception ex) when(ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException){return;}
        Assert.False(Risk("rg theme src").AutoAllowed);
        Assert.False(NativeSearch().AutoAllowed);
    }
}
