using System.Text.Json;
using DevFlow.Core;
using Xunit;

namespace DevFlow.Tests;

public sealed class SequencePermissionRiskTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-sequence-risk-" + Guid.NewGuid());
    public SequencePermissionRiskTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private PermissionAssessment Risk(string command) => PermissionRisk.Assess("run_command", JsonSerializer.SerializeToElement(new { CommandLine = command }), root);

    [Theory]
    [InlineData("git status --short; git show --stat 7d6a3822 9207b272", "SensitiveRead")]
    [InlineData("git status -s; git log -n 5 --oneline", "SensitiveRead")]
    [InlineData("git show --stat HEAD; git status --short", "SensitiveRead")]
    [InlineData("git status -s; Get-Content .env", "SensitiveRead")]
    [InlineData("git status -s; Set-Content output.cs changed", "Write")]
    [InlineData("git status -s; git show --output=history.txt HEAD", "Write")]
    [InlineData("git status -s; dotnet test", "Execution")]
    [InlineData("git status -s; curl https://example.com", "Network")]
    [InlineData("git status -s; Remove-Item output.cs", "Deletion")]
    [InlineData("git status -s; DROP TABLE accounts", "Deletion")]
    [InlineData("git status -s; UPDATE accounts SET name='example'", "Write")]
    [InlineData("git status -s; sqlite3 data.db 'select * from users'", "SensitiveRead")]
    [InlineData("git show HEAD; Remove-Item output.cs", "Deletion")]
    [InlineData("Remove-Item output.cs; git show HEAD", "Deletion")]
    [InlineData("git status -s; whoami", "Unknown")]
    [InlineData("whoami; git status -s", "Unknown")]
    [InlineData("git log --oneline; unknown-command", "Unknown")]
    public void Every_segment_is_assessed_and_non_general_sequences_cannot_use_rules(string command, string category)
    {
        var risk = Risk(command);
        Assert.Equal(category, risk.Category);
        Assert.False(risk.BatchAllowed);
        var args = JsonSerializer.SerializeToElement(new { CommandLine = command });
        var project = Guid.NewGuid(); var task = Guid.NewGuid();
        var rule = new ToolPermissionRule(Guid.NewGuid(), project, task, "general_read", "GeneralRead", ".", Provider: "*");
        Assert.False(ToolPermissionMatching.Matches(rule, project, task, "run_command", args, root, "antigravity"));
    }

    [Theory]
    [InlineData("git status -s; git branch --list")]
    [InlineData("git status -s; Get-Content src/customer-management/component.scss")]
    [InlineData("git status -s; Get-Content src/source.cs | Measure-Object -Line")]
    [InlineData("Select-String -Path source.cs -Pattern 'a;b'; git status -s")]
    [InlineData("Select-String -Path source.cs -Pattern \"a;b\"; git status -s")]
    public void A_sequence_matches_general_read_rules_only_when_every_segment_does(string command)
    {
        Assert.True(Risk(command).BatchAllowed);
        var args = JsonSerializer.SerializeToElement(new { CommandLine = command });
        var project = Guid.NewGuid(); var task = Guid.NewGuid();
        var rule = new ToolPermissionRule(Guid.NewGuid(), project, task, "general_read", "GeneralRead", ".", Provider: "*");
        Assert.True(ToolPermissionMatching.Matches(rule, project, task, "run_command", args, root, "codex"));
    }

    [Fact]
    public void Sensitive_reason_identifies_the_segment_without_echoing_arguments()
    {
        var risk = Risk("git status --short; git show --stat 7d6a3822 9207b272");
        Assert.Contains("提交歷史", risk.Label);
        Assert.Contains("第 2 段", risk.Reason);
        Assert.DoesNotContain("7d6a3822", risk.Reason);
    }

    [Theory]
    [InlineData("git status -s;")]
    [InlineData("; git status -s")]
    [InlineData("git status -s;; git branch --list")]
    [InlineData("git status -s; git status 'unterminated")]
    [InlineData("git status -s; $(git status -s)")]
    [InlineData("git status -s; & git status -s")]
    [InlineData("git status -s; git status -s #comment")]
    [InlineData("git status -s; git status -s\nwhoami")]
    [InlineData("git status -s; git status `; whoami")]
    [InlineData("git status -s; Get-ChildItem | ForEach-Object { whoami }")]
    [InlineData("Set-Location outside; Get-Content source.cs")]
    [InlineData("git status -s; git status --unknown")]
    public void Scripts_malformed_sequences_and_unknown_options_are_not_batch_allowed(string command) => Assert.False(Risk(command).BatchAllowed);

    [Fact]
    public void Sequence_limits_context_and_additional_permissions_remain_enforced()
    {
        Assert.False(Risk(string.Join("; ", Enumerable.Repeat("git status -s", 17))).BatchAllowed);
        foreach (var args in new[] {
            JsonSerializer.SerializeToElement(new { CommandLine = "git status -s; git branch --list", Cwd = Path.GetTempPath() }),
            JsonSerializer.SerializeToElement(new { CommandLine = "git status -s; git branch --list", AdditionalPermissions = new { network = true } }),
        }) Assert.False(PermissionRisk.Assess("run_command", args, root).BatchAllowed);
    }
}
