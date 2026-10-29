using System.Text.RegularExpressions;

namespace DevFlow.Core;

public sealed record QuotaWindow(string Name,double RemainingPercent,DateTimeOffset? ResetsAt);
public sealed record ProviderQuota(string Provider,QuotaWindow[] Windows,DateTimeOffset ObservedAt,string Source);
public sealed record AgentCandidate(AiSelection Selection,ProviderQuota? Quota=null,int ActiveTasks=0);
public sealed record AgentAssignment(string Name,string Instructions,AiSelection Selection,string Reason,bool Pinned=false,bool PinnedModel=false,string Kind="Implementation");
public sealed record AgentDelegation(AgentCandidate[] Candidates,AgentAssignment[] Assignments);
public sealed record AgentDecision(string Reply,string Action="Discuss",string? Title=null,string? Description=null,
    string? AcceptanceCriteria=null,string? Plan=null,AgentAssignment[]? Assignments=null,string[]? OpenQuestions=null,string? ResumeInstructions=null,
    Guid? TargetTaskId=null,int? TargetTaskVersion=null,string? TaskReason=null);
public sealed record TaskFeedbackContext(Guid TaskId,Guid RunId,string ResultCommit);
public sealed record TaskImageReference(Guid DiscussionId,DiscussionImage Image);
public interface ITaskImageLoader { Task<WorkTask> LoadAsync(WorkTask task,CancellationToken ct); }
public interface IAutoDelegationStore
{
    Task<WorkTask> ChangeDelegatedModelAsync(Guid taskId,Guid stepId,AiSelection selection,string reason,CancellationToken ct);
}
public static class ConversationAgent
{
    public static string Intent(string? message,string? requested,bool pendingProposal=false)
    {
        if(requested is not null and not ("Discuss" or "Start" or "Resume"))throw new ArgumentException("Invalid conversation action.");
        if(requested is "Start" or "Resume")return requested;
        var text=(message??"").Trim().TrimEnd('。','！','!');
        if(pendingProposal&&Regex.IsMatch(text,@"\A(?:(?:好|好的|可以|沒問題|OK|同意|核准)(?:[，,、 ]*(?:請)?(?:開始(?:吧|做|實作|執行)|做吧|照(?:這樣|這份計畫|這個計畫)做))?|開始吧|做吧|照(?:這樣|這份計畫|這個計畫|目前計畫)做(?:吧)?|照計畫執行|proceed|go ahead)\z",RegexOptions.IgnoreCase))return "Start";
        if(Regex.IsMatch(text,@"\A(?:開始(?:做|實作|執行)|請開始(?:做|實作|執行)|照(?:這個|這份|目前)(?:計畫|方案)(?:開始做|實作|執行)|start implementation)\z",RegexOptions.IgnoreCase))return "Start";
        if(Regex.IsMatch(text,@"\A(?:繼續|繼續執行|依討論繼續|照這樣繼續|resume)\z",RegexOptions.IgnoreCase))return "Resume";
        return "Discuss";
    }
    public static bool HasBudget(ProviderQuota? quota,DateTimeOffset now,double reserve=10)=>
        quota is null||quota.ObservedAt<now.AddMinutes(-15)||quota.Windows.All(w=>w.ResetsAt<=now||w.RemainingPercent>reserve);
    public static double? Pressure(ProviderQuota? quota,DateTimeOffset now,double reserve=10)
    {
        if(quota is null||quota.ObservedAt<now.AddMinutes(-15)||quota.Windows.Length==0||quota.Windows.Any(w=>w.ResetsAt is null||w.ResetsAt<=now))return null;
        return quota.Windows.Min(w=>Math.Max(0,w.RemainingPercent-reserve)/Math.Max(0.5,(w.ResetsAt!.Value-now).TotalHours));
    }
    public static AiSelection ValidateSelection(AiSelection selection,AgentCandidate[] candidates)
    {
        selection=AiSelection.Normalize(selection);
        if(!candidates.Any(c=>c.Selection.Provider==selection.Provider&&c.Selection.Model==selection.Model))throw new ArgumentException("主 AI 分配了不在可用清單內的模型，請在對話重新分配。");
        var quota=candidates.First(c=>c.Selection.Provider==selection.Provider).Quota;
        if(!HasBudget(quota,DateTimeOffset.UtcNow))throw new ArgumentException("指定工具已接近額度預留，請補充額度或在對話選擇其他工具。");
        return selection;
    }
    public static AgentAssignment[] ValidateAssignments(AgentAssignment[]? assignments,AgentCandidate[] candidates,bool requireImplementation=true)
    {
        if(assignments is not {Length:>0 and <=6})throw new ArgumentException("主 AI 必須安排 1 至 6 項可執行工作。");
        if(assignments.Any(a=>a.Kind is not ("Implementation" or "Review"))||requireImplementation&&assignments[0].Kind!="Implementation")throw new ArgumentException("分工需先有實作，可接續 AI 驗收。");
        return assignments.Select(a=>a with{Name=WorkTask.Required(a.Name,100),Instructions=WorkTask.Required(a.Instructions,12000),Reason=WorkTask.Required(a.Reason,2000),Selection=ValidateSelection(a.Selection,candidates)}).ToArray();
    }
    public static bool CanRevise(WorkTask task)=>task.Status is TaskStatus.AwaitingReview or TaskStatus.Approved or TaskStatus.Rejected or TaskStatus.Failed or TaskStatus.AwaitingStepApproval;
    public static WorkflowStep? ImplementationStep(WorkTask task){
        var steps=task.Workflow?.Steps??[];var index=Array.FindIndex(steps,s=>s.Id==task.ActiveStepId);
        return index<0?null:steps.Take(index+1).LastOrDefault(s=>s.Kind=="Implementation");
    }
    public static ProviderQuota[] ParseUserQuotas(string message,DateTimeOffset now)
    {
        var parts=Regex.Matches(message,@"(?i)(?<![a-z0-9_])(codex|agy|antigravity|claude|gemini)(?![a-z0-9_])([^\r\n]*?)(?=(?<![a-z0-9_])(?:codex|agy|antigravity|claude|gemini)(?![a-z0-9_])|$)");
        var result=new List<ProviderQuota>();
        foreach(Match part in parts){
            var text=part.Groups[2].Value;
            // Only literal user-reported remaining values, never an AI-generated estimate.
            var remaining=Regex.Match(text,@"(?:剩餘|剩余|剩下|剩|remaining)\s*[:：]?\s*(\d+(?:\.\d+)?)\s*%",RegexOptions.IgnoreCase);
            var time=Regex.Match(text,@"(\d+(?:\.\d+)?)\s*(小時|小时|分鐘|分钟|天|h(?:ours?)?|d(?:ays?)?|m(?:inutes?)?)\s*(?:後|后)?\s*(?:重置|reset)",RegexOptions.IgnoreCase);
            if(!remaining.Success||!time.Success)continue;
            var value=double.Parse(remaining.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
            var amount=double.Parse(time.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);if(value>100||amount<=0||amount>10000)continue;
            var unit=time.Groups[2].Value.ToLowerInvariant();var hours=unit is "天"||unit.StartsWith('d')?amount*24:unit.Contains('分')||unit.StartsWith('m')?amount/60:amount;
            var provider=part.Groups[1].Value.ToLowerInvariant();if(provider=="agy")provider="antigravity";
            result.Add(new(provider,[new("使用者回報",value,now.AddHours(hours))],now,"UserReported"));
        }
        return result.ToArray();
    }
}
