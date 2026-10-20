using System.Text.Json;
using DevFlow.Core;
using Xunit;

namespace DevFlow.Tests;

public sealed class CommandPermissionRiskTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"devflow-command-risk-"+Guid.NewGuid());
    public CommandPermissionRiskTests(){Directory.CreateDirectory(Path.Combine(root,"backend"));File.WriteAllText(Path.Combine(root,"backend","source.cs"),"class Example {}");}
    public void Dispose()=>Directory.Delete(root,true);
    private PermissionAssessment Risk(string command)=>PermissionRisk.Assess("run_command",JsonSerializer.SerializeToElement(new{CommandLine=command}),root);
    [Theory]
    [InlineData("Get-ChildItem -Force")]
    [InlineData("Get-ChildItem -Path backend -Recurse -File -Name")]
    [InlineData("Get-ChildItem -LiteralPath backend -Depth 3 -Directory")]
    [InlineData("Get-Content -Path backend/source.cs -Raw")]
    [InlineData("Get-Content backend/source.cs -TotalCount 30")]
    [InlineData("Select-String -Path \"backend/*.cs\" -Pattern \"salDispatchList|Dispatch|NET6NBS\" -CaseSensitive:$false")]
    [InlineData("Select-String -LiteralPath backend/source.cs -Pattern 'class' -List")]
    [InlineData("rg -n -F class backend/source.cs")]
    [InlineData("git status --short")]
    [InlineData("git --no-optional-locks status --porcelain=v1")]
    [InlineData("git diff --no-ext-diff --no-textconv --stat")]
    [InlineData("git diff --no-ext-diff --no-textconv -- backend/source.cs")]
    public void Supported_complete_read_commands_share_the_general_read_grant(string command)
    {
        var risk=Risk(command);Assert.True(risk.BatchAllowed,risk.Reason);Assert.Equal("GeneralRead",risk.Category);
        var task=Guid.NewGuid();var project=Guid.NewGuid();
        var rule=new ToolPermissionRule(Guid.NewGuid(),project,task,"general_read","GeneralRead",".",Provider:"*");
        Assert.True(ToolPermissionMatching.Matches(rule,project,task,"run_command",JsonSerializer.SerializeToElement(new{CommandLine=command}),root,"antigravity"));
    }
    [Theory]
    [InlineData("Select-String -Path \"backend/**\" -Pattern \"salDispatchList|Dispatch|NET6NBS\" -CaseSensitive:$false")]
    [InlineData("Select-String -Path backend/* -Pattern class")]
    [InlineData("Select-String -Path backend/appsettings.json -Pattern ConnectionString")]
    [InlineData("Get-Content .env")]
    [InlineData("Get-Content ../outside.cs")]
    [InlineData("Get-ChildItem ../")]
    [InlineData("git diff --stat")]
    [InlineData("git diff --no-ext-diff --no-textconv")]
    [InlineData("rg -n class backend")]
    public void Recognized_reads_with_sensitive_or_broad_targets_still_require_single_confirmation(string command)
    {var risk=Risk(command);Assert.False(risk.BatchAllowed);Assert.Equal("SensitiveRead",risk.Category);}
    [Theory]
    [InlineData("Get-ChildItem -Force; whoami")]
    [InlineData("Get-ChildItem | ForEach-Object Name")]
    [InlineData("Get-Content backend/source.cs > output.cs")]
    [InlineData("Get-ChildItem $(whoami)")]
    [InlineData("Get-ChildItem -Path \"$env:USERPROFILE\"")]
    [InlineData("Get-ChildItem %USERPROFILE%")]
    [InlineData("Get-ChildItem @args")]
    [InlineData("Get-ChildItem @options")]
    [InlineData("Get-ChildItem -FollowSymlink")]
    [InlineData("Get-Content backend/source.cs -Stream other")]
    [InlineData("Select-String -Path backend/*.cs -Pattern x -UnknownFlag")]
    [InlineData("rg --pre execute-tool class backend/source.cs")]
    [InlineData("git -c core.pager=whoami status")]
    [InlineData("git diff --no-ext-diff --no-textconv --output=result.txt")]
    [InlineData("powershell -Command Get-ChildItem")]
    public void Shell_composition_interpreters_and_unknown_switches_never_batch_allow(string command)=>Assert.False(Risk(command).BatchAllowed);
    [Fact] public void Search_globs_cannot_hide_sensitive_source_names()
    {
        File.WriteAllText(Path.Combine(root,"backend","secrets.cs"),"secret");
        Assert.False(Risk("Select-String -Path backend/*.cs -Pattern class").BatchAllowed);
    }
    [Fact] public void Execution_context_and_extra_permissions_are_checked()
    {
        foreach(var args in new[]{JsonSerializer.SerializeToElement(new{CommandLine="Get-ChildItem -Force",Cwd=Path.GetTempPath()}),JsonSerializer.SerializeToElement(new{CommandLine="Get-ChildItem -Force",AdditionalPermissions=new{network=true}}),JsonSerializer.SerializeToElement(new{CommandLine="Get-ChildItem -Force",Kind="stdin"})})
            Assert.False(PermissionRisk.Assess("run_command",args,root).BatchAllowed);
    }
}
