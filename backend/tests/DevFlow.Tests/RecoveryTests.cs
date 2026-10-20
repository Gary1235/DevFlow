using DevFlow.Core;
using DevFlow.Runners;
using System.Text.Json;
using Xunit;

namespace DevFlow.Tests;

public sealed class RecoveryTests
{
    [Fact]
    public void Streamed_tool_progress_handles_fragments_and_does_not_disclose_payloads()
    {
        var progress = new List<string>();
        var parser = new ToolActivityParser(progress.Add);
        parser.Append("{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\",\"command\":\"dotnet ");
        Assert.Empty(progress);
        parser.Append("test --secret-private-argument\"}}\n");
        parser.Append("{\"event\":\"step_update\",\"step_update\":{\"state\":\"ACTIVE\",\"step_type\":\"tool\",\"tool_name\":\"view_file\",\"tool_info\":{\"output\":\"private contents\"}}}\n");
        parser.Append("{\"type\":\"item.updated\",\"item\":{\"type\":\"reasoning\",\"text\":\"private reasoning\"}}\n");
        parser.Append("bad-json\n" + new string('x', 70000) + "\n");
        parser.Append("{\"type\":\"item.completed\",\"item\":{\"type\":\"file_change\",\"changes\":[\"private path\"]}}");
        parser.Finish();
        Assert.Equal(new[] { "正在執行測試", "正在讀取專案檔案", "已完成：修改檔案" }, progress);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(progress));
    }

    [Theory]
    [InlineData("401 unauthorized", "Authentication")]
    [InlineData("model_not_found", "ModelUnavailable")]
    [InlineData("rate_limit exceeded", "Quota")]
    [InlineData("permission denied", "Permission")]
    [InlineData("fetch failed", "Network")]
    [InlineData("unrecognized failure", "CliFailure")]
    public void Classified_cli_failures_offer_safe_messages_and_keep_raw_output_out_of_errors(string diagnostic, string code)
    {
        var error = CliExecutionErrors.Failed("test-cli", "test-phase", new(1, "private stdout", diagnostic+" secret-token"));
        Assert.Contains("["+code+"]", error.Message);
        Assert.DoesNotContain("secret-token", error.Message);
        Assert.DoesNotContain("private stdout", error.Message);
    }

    [Fact]
    public void Retry_preview_distinguishes_retained_steps_original_base_and_rework()
    {
        var backend = new WorkflowStep(Guid.NewGuid(), "後端"); var frontend = new WorkflowStep(Guid.NewGuid(), "前端");
        var task = WorkTask.Create(Guid.NewGuid(), "Feature", "Build", "Pass").SetPlan("approved") with
        {
            Status = DevFlow.Core.TaskStatus.Failed, ExecutionBaseCommit = "original-base",
            Workflow = new(Steps: [backend, frontend]),
            StepResults = [new(backend.Id, backend.Name, backend.Kind, "Completed", Guid.NewGuid(), "backend-result", "後端已完成", null, DateTimeOffset.UtcNow)]
        };
        var resume = RetryStrategy.Preview(task);
        Assert.Equal("backend-result", resume.BaseCommit);
        Assert.Equal(new[] { "後端" }, resume.RetainedSteps);
        Assert.Equal(new[] { "前端" }, resume.RepeatedSteps);
        var restart = RetryStrategy.Preview(task, "Restart");
        Assert.Equal("original-base", restart.BaseCommit);
        Assert.Empty(restart.RetainedSteps);
        Assert.Equal(new[] { "後端", "前端" }, restart.RepeatedSteps);
        Assert.Throws<ArgumentException>(() => RetryStrategy.Preview(task, "Unknown"));
        Assert.Throws<ConflictException>(() => RetryStrategy.Preview(task with { Status = DevFlow.Core.TaskStatus.Running }));
    }
}
