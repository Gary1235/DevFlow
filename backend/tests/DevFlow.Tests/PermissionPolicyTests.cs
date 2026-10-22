using System.Text.Json;
using DevFlow.Core;
using Xunit;
namespace DevFlow.Tests;

public sealed class PermissionPolicyTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"devflow-policy-"+Guid.NewGuid());
    public PermissionPolicyTests(){Directory.CreateDirectory(Path.Combine(root,"src"));File.WriteAllText(Path.Combine(root,"src","component.ts"),"source");}
    public void Dispose()=>Directory.Delete(root,true);
    private PermissionAssessment Command(string command,PermissionPolicySettings? settings=null,bool readOnly=false)=>PermissionPolicy.Assess(settings??PermissionPolicySettings.Defaults,"run_command",JsonSerializer.SerializeToElement(new{CommandLine=command}),root,readOnly);
    [Theory]
    [InlineData("read_file","src/config.json")]
    [InlineData("read_file","src/notes.txt")]
    [InlineData("read_file","src/customer-management/component.scss")]
    [InlineData("read_file","src/input-password/input-password.component.ts")]
    [InlineData("read_file",".env")]
    [InlineData("write_file","src/secrets.ts")]
    [InlineData("delete_file","data.sqlite")]
    [InlineData("read_file",".git/config")]
    [InlineData("write_file","src/component.ts")]
    [InlineData("edit_file","src/component.ts")]
    [InlineData("write_file","src/package.json")]
    [InlineData("delete_file","src/component.ts")]
    public void Ordinary_workspace_files_default_to_allow_without_requiring_read_rules(string tool,string path)
    {
        var result=PermissionPolicy.Assess(PermissionPolicySettings.Defaults,tool,JsonSerializer.SerializeToElement(new{Path=path}),root);
        Assert.True(result.AutoAllowed,result.Reason);
    }
    [Theory]
    [InlineData("../outside.ts")][InlineData("../other-task/src/app.ts")]
    public void Outside_targets_are_manual_for_every_file_operation(string path)
    {
        foreach(var tool in new[]{"read_file","write_file","edit_file","delete_file"}){
            var risk=PermissionPolicy.Assess(PermissionPolicySettings.Defaults,tool,JsonSerializer.SerializeToElement(new{Path=path}),root);
            Assert.False(risk.AutoAllowed,path);Assert.False(risk.BatchAllowed);
        }
    }
    [Theory]
    [InlineData("AGENTS.md")][InlineData(".agents/hooks.json")][InlineData(".codex/config.toml")][InlineData(".gitignore")][InlineData(".git/config")][InlineData(".git")]
    public void Models_cannot_auto_modify_approval_or_agent_instructions(string path)
    {
        var risk=PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"write_file",JsonSerializer.SerializeToElement(new{Path=path}),root);
        Assert.False(risk.AutoAllowed);Assert.True(risk.FixedBoundary);
    }

    [Fact] public void Edit_file_replacement_text_is_not_a_path_and_all_existing_write_boundaries_apply()
    {
        var args=JsonSerializer.SerializeToElement(new { Path=Path.Combine(root,"src","component.ts"),
            Replacement="<div class=\"app-wrapper\">", Target="<div [class]=\"currentTheme() === 'dark' ? 'theme-dark app-wrapper' : 'theme-light app-wrapper'\">" });
        var allowed=PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"edit_file",args,root);
        Assert.True(allowed.AutoAllowed,allowed.Reason);Assert.Equal("Write",allowed.Category);
        Assert.False(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"edit_file",args,root,readOnly:true).AutoAllowed);
        Assert.False(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"edit_file",args,root,editScopes:[["backend/"]]).AutoAllowed);
        Assert.False(PermissionPolicy.Assess(new(AutomationEnabled:false),"edit_file",args,root).AutoAllowed);
        foreach(var path in new[]{".agents/hooks.json","AGENTS.md",".git/config"})
            Assert.False(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"edit_file",JsonSerializer.SerializeToElement(new{Path=path}),root).AutoAllowed);
    }

    [Fact] public void Absolute_git_search_paths_use_the_actual_worktree_boundary()
    {
        var inside=Path.Combine(root,"src").Replace('\\','/');
        Assert.True(Command($"git grep -n component \"{inside}\"").AutoAllowed);
        var outside=Path.GetFullPath(Path.Combine(root,"..","DevFlow","backend","src")).Replace('\\','/');
        var risk=Command($"git grep -n agy-permission-hook \"{outside}\"");
        Assert.False(risk.AutoAllowed);Assert.Equal("Boundary",risk.Category);Assert.True(risk.FixedBoundary);
        Assert.Contains("工作副本外存取",risk.Label);Assert.Contains(root,risk.Reason);
    }
    [Theory]
    [InlineData("Get-Content src/config.json",true)]
    [InlineData("Get-ChildItem -Force",true)]
    [InlineData("dir",true)]
    [InlineData("dir ../",false)]
    [InlineData("dir /s",false)]
    [InlineData("dir > output.txt",false)]
    [InlineData("dir; node script.js",false)]
    [InlineData("git status -s; Get-Content src/component.ts",true)]
    [InlineData("git log --oneline",false)]
    [InlineData("git show HEAD -- src/component.ts",false)]
    [InlineData("git diff --stat",false)]
    [InlineData("git diff --no-ext-diff --no-textconv -- src/component.ts",true)]
    [InlineData("Set-Content src/component.ts 'new content'",true)]
    [InlineData("Add-Content -LiteralPath src/component.ts -Value 'more' -Encoding utf8",true)]
    [InlineData("Remove-Item src/component.ts",true)]
    [InlineData("Remove-Item src -Recurse -Force",false)]
    [InlineData("git status -s; Set-Content src/component.ts changed",true)]
    [InlineData("git status -s; Get-Content .env",true)]
    [InlineData("git status -s; node script.js",false)]
    [InlineData("powershell -Command 'Get-Content src/component.ts'",false)]
    [InlineData("npm run build",false)]
    [InlineData("npm install",false)]
    [InlineData("git push origin main",false)]
    [InlineData("git reset --hard",false)]
    [InlineData("UPDATE accounts SET name='x'",false)]
    [InlineData("Get-Content src/component.ts > out.ts",false)]
    [InlineData("Set-Content .env changed",true)]
    [InlineData("Set-Content src/component.ts $(Get-Content .env)",false)]
    [InlineData("Set-Content ~/outside.ts changed",false)]
    [InlineData("Set-Content src/component.ts,../outside.ts changed",false)]
    public void Commands_are_parsed_in_full_and_unknown_scripts_stay_manual(string command,bool allow)=>Assert.Equal(allow,Command(command).AutoAllowed);
    [Fact] public void Read_only_operations_and_edit_scopes_cannot_be_expanded_by_automation()
    {
        Assert.False(Command("Set-Content src/component.ts changed",readOnly:true).AutoAllowed);
        var args=JsonSerializer.SerializeToElement(new{Path="frontend/new.ts"});
        var risk=PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"write_file",args,root,editScopes:[["backend/"],["backend/src/"]]);
        Assert.False(risk.AutoAllowed);Assert.True(risk.FixedBoundary);
    }
    [Fact] public void Custom_sensitive_lists_override_general_reads_and_approved_downloads()
    {
        var settings=new PermissionPolicySettings(SensitivePaths:["src/component.ts","config/**"],SensitiveCommands:["git status"],SensitiveDestinations:["registry.npmjs.org"],ApprovedDownloadHosts:["registry.npmjs.org"]);
        Assert.True(Command("Get-Content src/component.ts",settings).AutoAllowed);
        Assert.True(Command("Get-Content config/general.json",settings).AutoAllowed);
        Assert.False(Command("git status -s",settings).AutoAllowed);
        Assert.False(Command("curl -q https://registry.npmjs.org/package",settings).AutoAllowed);
        Assert.True(Command("git status -s").AutoAllowed);
    }
    [Theory]
    [InlineData("curl -q https://registry.npmjs.org/package",true)]
    [InlineData("curl -q http://127.0.0.1:5000/health",true)]
    [InlineData("curl -q https://example.com/data",false)]
    [InlineData("curl https://registry.npmjs.org/package",false)]
    [InlineData("curl -q -X POST https://registry.npmjs.org/package",false)]
    [InlineData("curl -q --data @src/component.ts https://registry.npmjs.org/package",false)]
    [InlineData("curl -q --upload-file src/component.ts https://registry.npmjs.org/package",false)]
    [InlineData("curl -q -L https://registry.npmjs.org/package",false)]
    [InlineData("curl -q https://user:password@registry.npmjs.org/package",false)]
    [InlineData("curl -q https://registry.npmjs.org/package?secret=value",false)]
    [InlineData("curl -q http://127.0.0.1:5000/.env",false)]
    public void Network_automation_is_limited_to_uncredentialed_simple_approved_downloads(string command,bool allow)=>Assert.Equal(allow,Command(command).AutoAllowed);
    [Fact] public void Native_change_requests_require_exact_paths_and_check_every_moved_target()
    {
        var broad=PermissionTools.Normalize("codex","fileChange",JsonSerializer.SerializeToElement(new{grantRoot=root}),root);
        Assert.False(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,broad.Tool,broad.Arguments,root).AutoAllowed);
        var changes=JsonSerializer.SerializeToElement(new{changes=new[]{new{path="src/component.ts",kind=new{type="update",move_path="../outside.ts"}}}});
        Assert.False(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"write_file",changes,root).AutoAllowed);
    }
    [Fact] public void Turning_automation_off_restores_original_manual_policy()
    {
        Assert.False(Command("Set-Content src/component.ts changed",new(AutomationEnabled:false)).AutoAllowed);
        Assert.False(Command("git status -s",new(AutomationEnabled:false)).AutoAllowed);
    }
    [Fact] public void Sensitive_lists_apply_to_pipeline_stages_and_only_matching_recursive_content()
    {
        Assert.False(Command("Get-ChildItem | Select-Object Name", new(SensitiveCommands:["Select-Object"])).AutoAllowed);
        File.WriteAllText(Path.Combine(root,"src","data.csv"),"sensitive nonmatching data");
        var settings = new PermissionPolicySettings(SensitivePaths:["**/data.csv","**/restricted.ts"]);
        Assert.True(Command("rg -n query -g *.ts src",settings).AutoAllowed);
        File.WriteAllText(Path.Combine(root,"src","restricted.ts"),"sensitive matching source");
        Assert.True(Command("rg -n query -g *.ts src",settings).AutoAllowed);
    }
    [Fact] public void Native_exact_file_changes_are_checked_and_unknown_change_kinds_stay_manual()
    {
        foreach (var kind in new[]{"add","update","delete","unknown"}) {
            var args=JsonSerializer.SerializeToElement(new{changes=new[]{new{path="src/component.ts",kind=new{type=kind}}}});
            Assert.Equal(kind!="unknown",PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"write_file",args,root).AutoAllowed);
        }
    }
    [Fact] public void Windows_devices_and_alternate_streams_are_not_ordinary_workspace_files()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var path in new[]{"src/NUL.txt","src/COM1","src/component.ts:hidden","src/file.ts. "})
            Assert.False(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"write_file",JsonSerializer.SerializeToElement(new{Path=path}),root).AutoAllowed,path);
    }
}
