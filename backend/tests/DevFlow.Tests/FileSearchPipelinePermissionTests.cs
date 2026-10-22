using System.Text.Json;
using DevFlow.Core;
using Xunit;
namespace DevFlow.Tests;

public sealed class FileSearchPipelinePermissionTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"devflow-file-pipeline-"+Guid.NewGuid());
    private const string Pipeline="Get-ChildItem -Path frontend/src -Recurse -File -Include *.ts,*.html | Select-String -Pattern \"theme|dark|darkMode|toggleTheme|isDark|深色|淺色|深淺\" | Select-Object -First 50 -Property Path, LineNumber, Line";
    public FileSearchPipelinePermissionTests(){Directory.CreateDirectory(Path.Combine(root,"frontend","src","nested"));File.WriteAllText(Path.Combine(root,"frontend","src","nested","app.ts"),"theme");}
    public void Dispose()=>Directory.Delete(root,true);
    private PermissionAssessment Risk(string command,PermissionPolicySettings? settings=null)=>PermissionPolicy.Assess(settings??PermissionPolicySettings.Defaults,"run_command",JsonSerializer.SerializeToElement(new{CommandLine=command}),root);
    [Fact] public void Screenshot_pipeline_is_a_general_read_under_both_policies()
    {
        var general=Risk(Pipeline);Assert.True(general.AutoAllowed,general.Reason);Assert.Equal("GeneralRead",general.Category);
        Assert.True(Risk(Pipeline,new(AutomationEnabled:false)).BatchAllowed);
    }
    [Theory]
    [InlineData("secrets.ts")][InlineData("appsettings.ts")][InlineData("credentials.html")]
    public void Workspace_search_ignores_sensitive_file_names_but_strict_mode_keeps_them(string file)
    {
        File.WriteAllText(Path.Combine(root,"frontend","src","nested",file),"sensitive");
        var risk=Risk(Pipeline);Assert.True(risk.AutoAllowed);Assert.False(risk.BatchAllowed);
        Assert.False(Risk(Pipeline,new(AutomationEnabled:false)).BatchAllowed);
    }
    [Fact] public void File_name_lists_are_ignored_but_custom_command_rules_still_apply()
    {
        File.WriteAllText(Path.Combine(root,"frontend","src","nested","data.db"),"sensitive nonmatching data");
        Assert.True(Risk(Pipeline).AutoAllowed);
        Assert.True(Risk(Pipeline,new(SensitivePaths:["**/app.ts"])).AutoAllowed);
        foreach(var name in new[]{"Get-ChildItem","Select-String","Select-Object"})Assert.False(Risk(Pipeline,new(SensitiveCommands:[name])).AutoAllowed);
    }
    [Theory]
    [InlineData("Get-ChildItem -Path frontend/src -Recurse -File -Include *.ts -FollowSymlink | Select-String -Pattern dark")]
    [InlineData("Get-ChildItem -Path ../ -Recurse -File -Include *.ts | Select-String -Pattern dark")]
    [InlineData("Get-ChildItem -Path frontend/src -Recurse -File -Include *.ts | Select-String -Path ../secret.ts -Pattern dark")]
    [InlineData("Get-ChildItem -Path frontend/src -Recurse -File -Include *.ts | Select-String -Pattern dark | ForEach-Object { Remove-Item $_ }")]
    [InlineData("Get-ChildItem -Path frontend/src -Recurse -File -Include *.ts | Select-String -Pattern $(Get-Content .env)")]
    [InlineData("Get-ChildItem -Path frontend/src -Recurse -File -Include *.ts | Select-String -Pattern dark | Select-Object -Property @{n='value';e={Get-Content .env}}")]
    public void Paths_scripts_unknown_sources_and_output_writes_do_not_inherit_the_read_grant(string command)
    {Assert.False(Risk(command).AutoAllowed);Assert.False(Risk(command,new(AutomationEnabled:false)).BatchAllowed);}
    [Theory]
    [InlineData("Get-ChildItem -Path frontend/src -Recurse -Include *.ts,*.html | Select-String -Pattern \"(isDark|toggleDark|dark-theme|darkMode|themeService|theme)\" | Select-Object -First 30 -Property Path, LineNumber, Line")]
    [InlineData("Get-ChildItem -Path frontend/src -Recurse -File | Select-String -Pattern dark")]
    [InlineData("Get-ChildItem frontend/src -Recurse | Select-String -Pattern dark")]
    [InlineData("Get-ChildItem -Path frontend/src -Recurse -File -Include * | Select-String -Pattern dark")]
    [InlineData("Get-ChildItem -Path frontend/src -Recurse -Name -Include *.ts | Select-String -Pattern dark")]
    [InlineData("Get-ChildItem -Path frontend/src -Recurse -File -Include *.ts | Select-Object -Property FullName | Select-String -Pattern dark")]
    public void Automation_classifies_content_search_without_mandatory_file_or_extension_switches(string command)
    {
        File.WriteAllText(Path.Combine(root,"frontend","src","nested","layout.json"),"dark");
        Assert.True(Risk(command).AutoAllowed);
        Assert.False(Risk(command,new(AutomationEnabled:false)).BatchAllowed);
        File.WriteAllText(Path.Combine(root,"frontend","src","nested","secrets.ts"),"dark");
        Assert.True(Risk(command).AutoAllowed);
    }
    [Fact] public void Outside_execution_context_is_not_granted_even_when_the_target_is_inside()
    {
        var args=JsonSerializer.SerializeToElement(new{CommandLine=Pipeline.Replace("frontend/src",Path.GetFileName(root)+"/frontend/src"),Cwd=Path.GetTempPath()});
        Assert.False(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"run_command",args,root).AutoAllowed);
        Assert.False(PermissionRisk.Assess("run_command",args,root).BatchAllowed);
    }
    [Fact] public void Search_output_is_a_write_subject_to_the_same_scope_and_readonly_rules()
    {
        var command=Pipeline+" | Out-File result.txt";
        Assert.True(Risk(command).AutoAllowed);
        Assert.False(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"run_command",JsonSerializer.SerializeToElement(new{CommandLine=command}),root,readOnly:true).AutoAllowed);
        Assert.False(Risk(Pipeline+" | Out-File ../outside.txt").AutoAllowed);
        Assert.False(Risk(Pipeline+" | Out-File AGENTS.md").AutoAllowed);
    }
    [Fact] public void Password_component_screenshot_search_is_allowed_under_workspace_policy()
    {
        var folder=Path.Combine(root,"frontend","src","elecomponents","input-password");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"input-password.component.ts"),"export class PasswordInput {}");
        File.WriteAllText(Path.Combine(folder,"input-password.component.html"),"<input type='password'>");
        var command="Get-ChildItem -Path frontend/src -Recurse -Include *.ts,*.html | Select-String -Pattern \"dark|theme|isDark|toggleTheme|深色|淺色|darkMode\" | Select-Object -First 50 -Property Path, LineNumber, Line";
        Assert.True(Risk(command).AutoAllowed);
        Assert.True(Risk(command,new(SensitivePaths:["**/input-password/**"])).AutoAllowed);
    }
}
