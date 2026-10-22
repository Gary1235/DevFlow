using System.Text.Json;
using DevFlow.Core;
using Xunit;

namespace DevFlow.Tests;

public sealed class ToolPermissionMatchingTests
{
    private readonly Guid project=Guid.NewGuid(),task=Guid.NewGuid();
    private readonly string root=Path.Combine(Path.GetTempPath(),"rule-worktree");
    private ToolPermissionRule Batch()=>ToolPermissionMatching.Validate(new(Guid.NewGuid(),project,task,"general_read","GeneralRead",".",Provider:"*"));
    [Theory]
    [InlineData("read_file","Path","src/component.ts",true)]
    [InlineData("read_file","Path","src/component.scss",true)]
    [InlineData("read_file","Path","frontend/src/app/sales/customer-management/components/_shared-mgt.scss",true)]
    [InlineData("view_file","AbsolutePath","src/users/user.service.ts",true)]
    [InlineData("read_file","Path","backend/Customers/CustomerController.cs",true)]
    [InlineData("read_file","Path","src/customer-management/secrets.ts",false)]
    [InlineData("read_file","Path","src/users/credentials.cs",false)]
    [InlineData("read_file","Path","backups/component.scss",false)]
    [InlineData("read_file","Path","exports/users/service.ts",false)]
    [InlineData("read_file","Path","src/private/component.scss",false)]
    [InlineData("read_file","Path","src/customer-management/client.key",false)]
    [InlineData("view_file","AbsolutePath","src/customer-management/client.pfx",false)]
    [InlineData("read_file","Path","src/users/customers.xlsx",false)]
    [InlineData("read_file","Path","src/users/data.json",false)]
    [InlineData("read_file","Path","src/users/data.unknown",false)]
    [InlineData("view_file","AbsolutePath","README.md",true)]
    [InlineData("list_dir","DirectoryPath","src",true)]
    [InlineData("find_by_name","SearchDirectory","src",true)]
    [InlineData("read_file","Path",".env",false)]
    [InlineData("read_file","Path",".env.local",false)]
    [InlineData("read_file","Path","appsettings.Development.json",false)]
    [InlineData("read_file","Path","secrets/config.cs",false)]
    [InlineData("read_file","Path",".ssh/id_rsa",false)]
    [InlineData("read_file","Path","data.db",false)]
    [InlineData("read_file","Path","customers.csv",false)]
    [InlineData("read_file","Path","data.sqlite",false)]
    [InlineData("read_file","Path","../outside.cs",false)]
    [InlineData("grep_search","SearchPath",".",false)]
    [InlineData("write_file","Path","src/component.ts",false)]
    [InlineData("delete_file","Path","src/component.ts",false)]
    public void Only_known_project_reads_can_use_batch_rules(string tool,string pathKey,string path,bool general)
    {
        var args=JsonSerializer.SerializeToElement(new Dictionary<string,string>{{pathKey,Path.Combine(root,path)}});
        var risk=PermissionRisk.Assess(tool,args,root);
        Assert.Equal(general,risk.BatchAllowed); Assert.False(string.IsNullOrEmpty(risk.Reason));
        Assert.Equal(general,ToolPermissionMatching.Matches(Batch(),project,task,tool,args,root,"codex"));
        Assert.Equal(general,ToolPermissionMatching.Options(tool,args,root).Length>0);
    }
    [Theory]
    [InlineData("client.pem","金鑰／憑證")]
    [InlineData("client.KEY","金鑰／憑證")]
    [InlineData("password-vault.kdbx","機敏資料")]
    [InlineData("records.accdb","資料庫／備份")]
    [InlineData("records.db3","資料庫／備份")]
    [InlineData("records.csv","資料／匯出檔")]
    [InlineData("records.parquet","資料／匯出檔")]
    [InlineData("records.jsonl","資料／匯出檔")]
    public void File_type_has_a_specific_reason_and_cannot_use_general_read_rules(string filename,string label)
    {
        var args=JsonSerializer.SerializeToElement(new{Path=Path.Combine(root,"src","customer-management",filename)});
        var risk=PermissionRisk.Assess("read_file",args,root);
        Assert.Equal("SensitiveRead",risk.Category);
        Assert.Contains(label,risk.Label);
        Assert.False(risk.BatchAllowed);
        Assert.False(ToolPermissionMatching.Matches(Batch(),project,task,"read_file",args,root,"antigravity"));
    }
    [Theory]
    [InlineData("read_database","SensitiveRead")]
    [InlineData("native_sql_query","SensitiveRead")]
    [InlineData("write_file","Write")]
    [InlineData("delete_file","Deletion")]
    [InlineData("run_command","Unknown")]
    public void Sensitive_and_unknown_operations_do_not_match_even_legacy_exact_rules(string tool,string category)
    {
        var args=JsonSerializer.SerializeToElement(new{Path=Path.Combine(root,"source.cs"),CommandLine="git diff --stat"});
        Assert.Equal(category,PermissionRisk.Assess(tool,args,root).Category);
        foreach(var kind in new[]{"Exact","CommandPrefix","WorkspacePath"}){
            var rule=new ToolPermissionRule(Guid.NewGuid(),project,task,tool,kind,kind=="Exact"?ToolPermissionMatching.Canonical(args,root,tool):".",Provider:"*");
            Assert.False(ToolPermissionMatching.Matches(rule,project,task,tool,args,root,"claude"));
        }
    }
    [Fact] public void General_reads_are_isolated_by_project_task_provider_and_enabled_state()
    {
        var rule=Batch();var args=JsonSerializer.SerializeToElement(new{Path=Path.Combine(root,"source.cs")});
        Assert.True(ToolPermissionMatching.Matches(rule,project,task,"read_file",args,root,"claude"));
        Assert.False(ToolPermissionMatching.Matches(rule,Guid.NewGuid(),task,"read_file",args,root,"claude"));
        Assert.False(ToolPermissionMatching.Matches(rule,project,Guid.NewGuid(),"read_file",args,root,"claude"));
        Assert.False(ToolPermissionMatching.Matches(rule with{Provider="codex"},project,task,"read_file",args,root,"claude"));
        Assert.False(ToolPermissionMatching.Matches(rule with{Enabled=false},project,task,"read_file",args,root,"claude"));
        Assert.False(ToolPermissionMatching.Matches(rule with{TaskId=null},project,task,"read_file",args,root,"claude"));
        Assert.Throws<ArgumentException>(()=>ToolPermissionMatching.Validate(rule with{TaskId=null}));
        Assert.False(ToolPermissionMatching.Matches(rule with{Tool="read_file",Kind="WorkspacePath"},project,task,"read_file",args,root,"claude"));
    }
    [Theory] [InlineData("codex","Read")] [InlineData("claude","Read")] [InlineData("gemini","read_file")]
    public void Native_file_reads_share_the_category_and_sensitive_files_stay_sensitive(string provider,string tool)
    {
        foreach(var filename in new[]{"source.ts",".env","data.db"}){
            var request=PermissionTools.Normalize(provider,tool,JsonSerializer.SerializeToElement(new{file_path=Path.Combine(root,filename)}),root);
            Assert.Equal(filename=="source.ts",ToolPermissionMatching.Matches(Batch(),project,task,request.Tool,request.Arguments,root,provider));
        }
    }
    [Fact] public void Legacy_rules_stay_antigravity_only_when_provider_is_absent()
    {
        var json=JsonSerializer.Serialize(new {Id=Guid.NewGuid(),ProjectId=project,TaskId=(Guid?)null,Tool="run_command",Kind="CommandPrefix",Pattern="git diff",Enabled=true,Version=0});
        Assert.Equal("antigravity",JsonSerializer.Deserialize<ToolPermissionRule>(json)!.Provider);
    }
    [Theory]
    [InlineData("Remove-Item src/file.cs","Deletion")]
    [InlineData("git reset --hard","Deletion")]
    [InlineData("git push --force","Deletion")]
    [InlineData("sqlite3 data.db 'select * from users'","SensitiveRead")]
    [InlineData("UPDATE accounts SET name='example'","Write")]
    [InlineData("DROP TABLE accounts","Deletion")]
    [InlineData("Get-Content src/source.cs; Remove-Item other.cs","Deletion")]
    public void Database_and_compound_commands_have_clear_sensitive_labels(string command,string category)
    {
        var args=JsonSerializer.SerializeToElement(new{CommandLine=command,Cwd=root});
        var risk=PermissionRisk.Assess("run_command",args,root);Assert.Equal(category,risk.Category);Assert.False(risk.BatchAllowed);
    }
}
