using System.Text.Json;
using DevFlow.Core;
using Xunit;
namespace DevFlow.Tests;

public sealed class GitGrepPermissionTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"devflow-git-grep-"+Guid.NewGuid());
    public GitGrepPermissionTests(){Directory.CreateDirectory(Path.Combine(root,"frontend","src"));File.WriteAllText(Path.Combine(root,"frontend","src","app.ts"),"dark currentTheme toggleTheme");}
    public void Dispose()=>Directory.Delete(root,true);
    private PermissionAssessment Risk(string command,PermissionPolicySettings? settings=null)=>PermissionPolicy.Assess(settings??PermissionPolicySettings.Defaults,"run_command",JsonSerializer.SerializeToElement(new{CommandLine=command}),root);
    [Theory]
    [InlineData("git grep -i \"dark\" frontend/")]
    [InlineData("git grep -n \"currentTheme\\|toggleTheme\" frontend/src")]
    [InlineData("git grep -n -C 5 \"toggleDarkMode\" frontend/")]
    [InlineData("git grep -l currentTheme frontend/src")]
    [InlineData("git grep -in -C5 -e dark -- frontend/")]
    [InlineData("git --no-optional-locks grep --fixed-strings -e dark -- frontend/src/app.ts")]
    [InlineData("git grep -i dark frontend/; git status -s")]
    public void Ordinary_worktree_searches_are_automatically_allowed(string command)
    {
        var risk=Risk(command);Assert.True(risk.AutoAllowed,risk.Reason);
        Assert.True(PermissionRisk.Assess("run_command",JsonSerializer.SerializeToElement(new{CommandLine=command}),root).BatchAllowed);
    }
    [Theory]
    [InlineData(".env")][InlineData("secrets.ts")][InlineData("data.db")][InlineData("backup.json")]
    public void Automation_allows_all_workspace_file_names(string name)
    {
        File.WriteAllText(Path.Combine(root,"frontend","src",name),"sensitive");
        Assert.True(Risk("git grep -i dark frontend/").AutoAllowed);
    }
    [Theory]
    [InlineData("app~old.ts")][InlineData("app,old.ts")][InlineData("app~old,copy.ts")]
    public void Literal_git_paths_allow_embedded_tildes_and_commas(string name)
    {
        File.WriteAllText(Path.Combine(root,"frontend","src",name),"dark");
        var command=$"git grep --no-textconv -n dark -- 'frontend/src/{name}'";
        Assert.True(Risk(command).AutoAllowed);
        Assert.False(Risk(command,new(AutomationEnabled:false)).AutoAllowed);
        Assert.False(Risk($"git grep --no-textconv -n dark -- '../{name}'").AutoAllowed);
        Assert.False(Risk(command,new(SensitiveCommands:["git grep"])).AutoAllowed);
    }
    [Theory]
    [InlineData("git grep dark -- ~/app.ts")]
    [InlineData("git grep dark -- frontend/src/[ab].ts")]
    [InlineData("git grep dark -- frontend/src/*.ts")]
    [InlineData("git grep dark -- ':(literal)../outside.ts'")]
    public void Path_expansion_and_git_pathspec_magic_remain_manual(string command) => Assert.False(Risk(command).AutoAllowed);
    [Theory]
    [InlineData("git grep --cached dark frontend/")]
    [InlineData("git grep --no-index dark frontend/")]
    [InlineData("git grep --untracked dark frontend/")]
    [InlineData("git grep --textconv dark frontend/")]
    [InlineData("git grep --recurse-submodules dark frontend/")]
    [InlineData("git grep -O dark frontend/")]
    [InlineData("git grep dark HEAD -- frontend/")]
    [InlineData("git grep dark -- ../outside/")]
    [InlineData("git grep dark -- :(glob)**/*.ts")]
    [InlineData("git -c core.pager=whoami grep dark frontend/")]
    public void Unknown_flags_history_external_paths_and_execution_extensions_still_require_confirmation(string command)=>Assert.False(Risk(command).AutoAllowed);
    [Fact] public void Custom_rules_and_json_targets_follow_the_active_policy()
    {
        File.WriteAllText(Path.Combine(root,"frontend","src","config.json"),"{}");
        Assert.True(Risk("git grep dark -- frontend/").AutoAllowed);
        Assert.True(Risk("git grep dark -- frontend/",new(SensitivePaths:["**/config.json"])).AutoAllowed);
        Assert.False(Risk("git grep dark -- frontend/",new(SensitiveCommands:["git grep"])).AutoAllowed);
        var strict=Risk("git grep dark -- frontend/",new(AutomationEnabled:false));Assert.False(strict.AutoAllowed);Assert.False(strict.BatchAllowed);
    }
}
