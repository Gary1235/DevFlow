using DevFlow.Api;
using DevFlow.Core;
using Xunit;
namespace DevFlow.Tests;

public sealed class DiscussionProgressTests
{
    [Fact] public void Preview_reassembles_chunks_and_excludes_reasoning_tools_and_other_protocols()
    {
        using var operation = new DiscussionOperations.Operation(Guid.NewGuid(), new("antigravity"), default);
        operation.Append("{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"tool\",\"text_delta\":\"secret tool output\"}}\n");
        operation.Append("{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"thinking\",\"text_delta\":\"private reasoning\"}}\n");
        operation.Append("{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"agent_response\",\"text_del");
        Assert.Empty(operation.Content);
        operation.Append("ta\":\"中文回覆\"}}\nmalformed\n{\"event\":123}\n");
        Assert.Equal("中文回覆", operation.Content);
        using var other = new DiscussionOperations.Operation(Guid.NewGuid(), new("codex"), default);
        other.Append("{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"agent_response\",\"text_delta\":\"not this provider\"}}\n");
        Assert.Empty(other.Content);
    }

    [Fact] public async Task Output_observers_are_isolated_between_concurrent_async_operations()
    {
        var first = ""; var second = "";
        await Task.WhenAll(Task.Run(async () => { using var scope = ProcessOutput.Observe(s => first += s); await Task.Yield(); ProcessOutput.Observer!("one"); }),
            Task.Run(async () => { using var scope = ProcessOutput.Observe(s => second += s); await Task.Yield(); ProcessOutput.Observer!("two"); }));
        Assert.Equal("one", first); Assert.Equal("two", second); Assert.Null(ProcessOutput.Observer);
    }
}
