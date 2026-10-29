using System.Text.Json;
using DevFlow.Api;
using DevFlow.Core;
using DevFlow.Runners;
using Xunit;
namespace DevFlow.Tests;
public sealed class ConversationAgentTests
{
    [Theory][InlineData("這是什麼意思？","Discuss")][InlineData("好","Discuss")][InlineData("請問要開始做嗎？","Discuss")][InlineData("開始做","Start")][InlineData("開始實作","Start")][InlineData("繼續","Resume")]
    public void Questions_are_not_execution_approval(string message,string expected)=>Assert.Equal(expected,ConversationAgent.Intent(message,null));
    [Theory][InlineData("好","Start")][InlineData("好，照這份計畫做","Start")][InlineData("好的，請開始吧","Start")][InlineData("可以，做吧","Start")][InlineData("Proceed","Start")][InlineData("好，但先解釋資料庫部分","Discuss")][InlineData("可以改成其他模型嗎？","Discuss")]
    public void Approval_is_contextual_and_questions_do_not_approve(string message,string expected)=>Assert.Equal(expected,ConversationAgent.Intent(message,null,true));
    [Fact] public void Quota_pressure_prefers_soon_reset_but_respects_every_window_and_marks_unknown()
    {
        var now=DateTimeOffset.UtcNow;var codex=new ProviderQuota("codex",[new("short",30,now.AddHours(3))],now,"CLI");
        var agy=new ProviderQuota("antigravity",[new("long",70,now.AddDays(4))],now,"UserReported");
        Assert.True(ConversationAgent.Pressure(codex,now)>ConversationAgent.Pressure(agy,now));
        Assert.False(ConversationAgent.HasBudget(codex with{Windows=[..codex.Windows,new("weekly",5,now.AddDays(4))]},now));
        Assert.Null(ConversationAgent.Pressure(codex with{ObservedAt=now.AddMinutes(-16)},now));Assert.Null(ConversationAgent.Pressure(codex with{Windows=[new("expired",30,now.AddSeconds(-1))]},now));
    }
    [Fact] public void Manual_quotas_are_literal_user_reports_and_codex_usage_uses_remaining_not_used_percent()
    {
        var now=DateTimeOffset.UtcNow;var quotas=ConversationAgent.ParseUserQuotas("codex剩餘30% 3h後重置，agy剩餘70% 4d後重置",now);
        Assert.Equal(2,quotas.Length);Assert.Equal(30,quotas[0].Windows[0].RemainingPercent);Assert.Equal(now.AddHours(3),quotas[0].Windows[0].ResetsAt);Assert.Equal(now.AddDays(4),quotas[1].Windows[0].ResetsAt);
        Assert.Empty(ConversationAgent.ParseUserQuotas("請問 codex 剩餘額度是多少？",now));
        using var json=JsonDocument.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":70,\"resetsAt\":1800000000}}}");
        Assert.Equal(30,CliQuotaReader.Parse(json.RootElement,now)!.Windows[0].RemainingPercent);
    }
    [Fact] public void Agent_stream_displays_only_reply_and_never_control_fields()
    {
        using var operation=new DiscussionOperations.Operation(Guid.NewGuid(),new("claude"),default){StructuredReply=true};
        foreach(var text in new[]{"{\"reply\":\"這是解釋","，可以繼續問。\",\"action\":\"Resume\"}"})operation.Append(JsonSerializer.Serialize(new{@event="devflow_assistant_delta",text})+"\n");
        Assert.Equal("這是解釋，可以繼續問。",operation.Content);
    }
}
