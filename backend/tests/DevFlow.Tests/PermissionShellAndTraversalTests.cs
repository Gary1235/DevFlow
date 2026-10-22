using System.Text.Json;
using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
namespace DevFlow.Tests;

public sealed class PermissionShellAndTraversalTests : IDisposable
{
    private readonly string temporary=Path.Combine(Path.GetTempPath(),"devflow-shell-boundary-"+Guid.NewGuid());
    private string Root=>Path.Combine(temporary,"worktree");
    public PermissionShellAndTraversalTests(){Directory.CreateDirectory(Path.Combine(Root,"src","nested"));File.WriteAllText(Path.Combine(Root,"src","[literal].json"),"{}");}
    public void Dispose()=>Directory.Delete(temporary,true);
    private PermissionAssessment Risk(string command,string shell="powershell",bool readOnly=false)=>PermissionPolicy.Assess(PermissionPolicySettings.Defaults,"run_command",JsonSerializer.SerializeToElement(new{CommandLine=command,Shell=shell}),Root,readOnly);

    [Fact] public async Task Recursive_filename_glob_checks_deep_directory_junctions_even_without_top_level_matches()
    {
        var outside=Path.Combine(temporary,"outside");Directory.CreateDirectory(outside);File.WriteAllText(Path.Combine(outside,"external.ts"),"synthetic");
        var link=Path.Combine(Root,"src","nested","junction");
        if(OperatingSystem.IsWindows()) {
            var result=await new ProcessRunner().RunAsync("cmd.exe",["/d","/c","mklink","/J",link,outside],Root,null,default);
            Assert.True(result.ExitCode==0,result.StandardError); // Do not silently pass without creating the fixture.
        } else Directory.CreateSymbolicLink(link,outside);
        try {
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
            foreach(var pattern in new[]{"src/*.ts","src/*/*.ts","src/*/*/*.ts"}) {
                var risk=Risk($"Get-ChildItem -Path {pattern} -Recurse | Select-String -Pattern synthetic");
                Assert.False(risk.AutoAllowed);Assert.True(risk.FixedBoundary);
            }
            var native=PermissionTools.Normalize("claude","Glob",JsonSerializer.SerializeToElement(new{pattern="src/**/*.ts"}),Root);
            var nativeRisk=PermissionPolicy.Assess(PermissionPolicySettings.Defaults,native.Tool,native.Arguments,Root);
            Assert.False(nativeRisk.AutoAllowed);Assert.True(nativeRisk.FixedBoundary);
            foreach(var pathParameter in new[]{"-Path","-LiteralPath"}) {
                var projection=Risk($"Select-String {pathParameter} (Get-ChildItem src -Recurse).FullName -Pattern synthetic");
                Assert.False(projection.AutoAllowed);Assert.True(projection.FixedBoundary);
                var names=Risk($"(Get-ChildItem {pathParameter} src -Filter '*.ts' -Recurse).FullName");
                Assert.False(names.AutoAllowed);Assert.True(names.FixedBoundary);
            }
        } finally { Directory.Delete(link); } // Unlink explicitly; never recurse through the junction during cleanup.
    }
    [Fact] public void Recursive_glob_without_links_remains_an_ordinary_read()
    {
        File.WriteAllText(Path.Combine(Root,"src","nested","app.ts"),"theme");
        Assert.True(Risk("Get-ChildItem src/*.ts -Recurse | Select-String -Pattern theme").AutoAllowed);
    }
    [Theory][InlineData("sc stop Spooler")][InlineData("gc src/file.json")][InlineData("Get-Content src/file.json")]
    public void Bash_and_unknown_shells_do_not_inherit_PowerShell_builtin_or_alias_permissions(string command)
    {
        var normalized=PermissionTools.Normalize("claude","Bash",JsonSerializer.SerializeToElement(new{command,Shell="powershell"}),Root);
        Assert.Equal("bash",normalized.Arguments.GetProperty("Shell").GetString());
        Assert.False(PermissionPolicy.Assess(PermissionPolicySettings.Defaults,normalized.Tool,normalized.Arguments,Root).AutoAllowed);
        Assert.False(Risk(command,"unknown").AutoAllowed);
        Assert.False(Risk(command,"bash").AutoAllowed);
    }
    [Theory][InlineData("bash")][InlineData("unknown")][InlineData("powershell")]
    public void Shell_independent_git_queries_keep_worktree_automation(string shell)=>Assert.True(Risk("git status --short",shell).AutoAllowed);
    [Fact] public void Actual_PowerShell_aliases_and_read_presentation_options_remain_allowed()
    {
        Assert.True(Risk("Set-Content -LiteralPath 'src/[literal].json' -Value x").AutoAllowed);
        Assert.False(Risk("Set-Content -LiteralPath 'src/[literal].json' -Value x",readOnly:true).AutoAllowed);
        Assert.False(Risk("Set-Content -Path 'src/[literal].json' -Value x").AutoAllowed);
        Assert.True(Risk("Get-Content -LiteralPath 'src/[literal].json' -Encoding Byte").AutoAllowed);
        Assert.True(Risk("Get-Content -LiteralPath 'src/[literal].json' -Encoding 950").AutoAllowed);
        Assert.True(Risk("Select-String -LiteralPath 'src/[literal].json' -Pattern 'please delete file' -Culture en-US",readOnly:true).AutoAllowed);
    }
}
