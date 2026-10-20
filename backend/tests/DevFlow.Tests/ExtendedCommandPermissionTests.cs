using System.Text.Json;
using DevFlow.Core;
using Xunit;

namespace DevFlow.Tests;

public sealed class ExtendedCommandPermissionTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"devflow-extended-risk-"+Guid.NewGuid());
    public ExtendedCommandPermissionTests(){Directory.CreateDirectory(Path.Combine(root,"src","nested"));File.WriteAllText(Path.Combine(root,"src","nested","source.cs"),"class Example {}");}
    public void Dispose()=>Directory.Delete(root,true);
    private PermissionAssessment Risk(string command)=>PermissionRisk.Assess("run_command",JsonSerializer.SerializeToElement(new{CommandLine=command}),root);
    [Theory]
    [InlineData("Test-Path -LiteralPath src/nested/source.cs -PathType Leaf")]
    [InlineData("Get-Item -Path src/nested/source.cs")]
    [InlineData("Get-FileHash src/nested/source.cs -Algorithm SHA256")]
    [InlineData("Get-Content src/nested/source.cs -Encoding utf8")]
    [InlineData("Select-String -Path src/nested/source.cs -Pattern class -Context 1,2 -Encoding utf8 -NotMatch")]
    [InlineData("Get-ChildItem -Path src -Recurse -File | Where-Object { $_.Extension -eq '.cs' } | Select-Object -ExpandProperty FullName")]
    [InlineData("Get-ChildItem src | Where-Object Name -like '*.cs' | Sort-Object -Property Length -Descending | Select-Object Name,Length -First 20")]
    [InlineData("Get-Content src/nested/source.cs | Measure-Object -Line -Word")]
    [InlineData("Get-ChildItem src | Where-Object { $_.FullName -match 'ship|出貨|delivery' } | Select-Object -ExpandProperty FullName")]
    [InlineData("rg -n --glob '*.cs' class src")]
    [InlineData("rg --glob '*.cs' -e 'DROP TABLE|sqlite|UPDATE accounts SET' src")]
    [InlineData("rg --glob=*.cs -e 'class|interface' src")]
    [InlineData("rg --files src")]
    [InlineData("git branch --list")]
    [InlineData("git branch -a --no-color")]
    [InlineData("git ls-files --stage")]
    [InlineData("git rev-parse --short HEAD")]
    [InlineData("git --version")]
    [InlineData("dotnet --version")]
    [InlineData("node --version")]
    [InlineData("npm --version")]
    [InlineData("rg --version")]
    public void Recognized_read_forms_share_the_same_scoped_rule(string command)
    {
        var risk=Risk(command);Assert.True(risk.BatchAllowed,risk.Reason);
        var project=Guid.NewGuid();var task=Guid.NewGuid();var rule=new ToolPermissionRule(Guid.NewGuid(),project,task,"general_read","GeneralRead",".",Provider:"*");
        Assert.True(ToolPermissionMatching.Matches(rule,project,task,"run_command",JsonSerializer.SerializeToElement(new{CommandLine=command}),root,"claude"));
    }
    [Theory]
    [InlineData("Get-ChildItem src | ForEach-Object { Remove-Item $_.FullName }")]
    [InlineData("Get-ChildItem src | Where-Object { [IO.File]::ReadAllText('.env') }")]
    [InlineData("Get-ChildItem src | Where-Object { $_.Name -eq 'x'; Invoke-Expression 'x' }")]
    [InlineData("Get-ChildItem src | Select-Object @{Name='X';Expression={whoami}}")]
    [InlineData("Get-ChildItem src | Select-Object -ExpandProperty Target")]
    [InlineData("Get-ChildItem src | Sort-Object { Invoke-Expression 'x' }")]
    [InlineData("Get-ChildItem src || Get-Content .env")]
    [InlineData("Get-Content .env | Select-Object -First 1")]
    [InlineData("Get-FileHash .env")]
    [InlineData("Get-Item ../outside.cs")]
    [InlineData("rg --glob '*' class src")]
    [InlineData("rg --glob '*.json' password src")]
    [InlineData("rg --glob '*.cs' --pre command class src")]
    [InlineData("rg --glob '*.cs' --follow class src")]
    [InlineData("git log --oneline")]
    [InlineData("git show HEAD")]
    [InlineData("npm list --depth=0")]
    public void Unsafe_or_sensitive_variants_never_inherit_a_general_read_grant(string command)=>Assert.False(Risk(command).BatchAllowed);
    [Theory]
    [InlineData("dotnet build","Execution")]
    [InlineData("dotnet test","Execution")]
    [InlineData("dotnet list package","Execution")]
    [InlineData("npm run build","Execution")]
    [InlineData("npm test","Execution")]
    [InlineData("npx something","Execution")]
    [InlineData("npm install","Write")]
    [InlineData("dotnet restore","Write")]
    [InlineData("git commit -m example","Write")]
    [InlineData("git log --output=history.txt","Write")]
    [InlineData("git switch main","Write")]
    [InlineData("git push origin main","Write")]
    [InlineData("git branch -D example","Deletion")]
    [InlineData("Set-Content src/new.cs example","Write")]
    [InlineData("npm config set registry https://example.com","Write")]
    [InlineData("npm publish","Network")]
    [InlineData("Get-Content src/nested/source.cs > output.txt","Write")]
    [InlineData("Get-ChildItem src | Out-File output.txt","Write")]
    [InlineData("Move-Item src/old.cs src/new.cs","Write")]
    [InlineData("curl https://example.com","Network")]
    [InlineData("curl -XPOST https://example.com","Network")]
    [InlineData("curl --request=DELETE https://example.com","Deletion")]
    public void Mutations_scripts_and_network_have_specific_sensitive_labels(string command,string category)
    {var risk=Risk(command);Assert.False(risk.BatchAllowed);Assert.Equal(category,risk.Category);Assert.DoesNotContain("未知",risk.Label);}
    [Fact] public void Recursive_search_rechecks_actual_nested_sensitive_paths()
    {
        Directory.CreateDirectory(Path.Combine(root,"src","secrets"));File.WriteAllText(Path.Combine(root,"src","secrets","source.cs"),"secret");
        var risk=Risk("rg --glob '*.cs' class src");Assert.False(risk.BatchAllowed);Assert.Equal("SensitiveRead",risk.Category);
    }
    [Fact] public void Read_pipeline_cannot_bypass_an_outside_working_directory()
    {
        var risk=PermissionRisk.Assess("run_command",JsonSerializer.SerializeToElement(new{CommandLine="Get-ChildItem | Select-Object Name",Cwd=Path.GetTempPath()}),root);
        Assert.False(risk.BatchAllowed);
    }
    [Theory]
    [InlineData("select * from information_schema.columns","資料庫結構")]
    [InlineData("select * from customers","資料庫資料")]
    public void Database_structure_and_data_are_both_single_use_but_clearly_labeled(string query,string label)
    {
        var risk=PermissionRisk.Assess("native_sql_query",JsonSerializer.SerializeToElement(new{query}),root);
        Assert.False(risk.BatchAllowed);Assert.Contains(label,risk.Label);
    }
    [Fact] public void Unsupported_flag_reason_identifies_the_flag_without_exposing_its_value()
    {
        var risk=Risk("rg --secret=hidden-value class src");Assert.False(risk.BatchAllowed);
        Assert.Contains("--secret",risk.Reason);Assert.DoesNotContain("hidden-value",risk.Reason);
    }
}
