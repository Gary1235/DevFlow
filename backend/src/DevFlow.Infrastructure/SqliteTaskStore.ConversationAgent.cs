using System.Text.Json;
using DevFlow.Core;
using Status=DevFlow.Core.TaskStatus;
namespace DevFlow.Infrastructure;

public sealed partial class SqliteTaskStore
{
    public Task<ProviderQuota[]> ReadAgentQuotasAsync(CancellationToken ct){using var c=Open(ct);return Task.FromResult(ReadList<ProviderQuota>(c,"SELECT Payload FROM AgentQuotas").ToArray());}
    public Task SaveAgentQuotaAsync(ProviderQuota quota,CancellationToken ct){using var c=Open(ct);Execute(c,null,"INSERT INTO AgentQuotas VALUES($provider,$payload) ON CONFLICT(Provider) DO UPDATE SET Payload=excluded.Payload",("$provider",quota.Provider),("$payload",JsonSerializer.Serialize(quota)));return Task.CompletedTask;}
    public Task<Discussion> SetDiscussionAgentAsync(Guid id,int expectedVersion,AiSelection? selection,CancellationToken ct,Guid? linkedTask=null)
    {
        using var c=Open(ct);using var tx=c.BeginTransaction();
        var d=ReadOne<Discussion>(c,tx,"Discussions",id)??throw new MissingEntityException("Discussion not found.");
        if(d.Version!=expectedVersion)throw new ConflictException("對話已更新，請重試。");
        var chosen=AiSelection.Normalize(selection??d.Selection);
        WorkTask? task=(linkedTask??d.ActiveTaskId) is {} taskId?ReadOne<WorkTask>(c,tx,"Tasks",taskId):null;
        if(task is not null&&task.ProjectId!=d.ProjectId)throw new ConflictException("任務與對話專案不符。");
        if(task is null){using var find=Command(c,tx,"SELECT Payload FROM Tasks WHERE json_extract(Payload,'$.SourceDiscussionId')=$id ORDER BY rowid DESC LIMIT 1",("$id",id.ToString()));if(find.ExecuteScalar() is string payload)task=JsonSerializer.Deserialize<WorkTask>(payload);}
        if(task is not null&&task.Status is not (Status.Merged or Status.Cancelled)&&task.Ai?.Planning!=chosen){
            UpdateTask(c,tx,task with{Ai=new(chosen,task.Ai?.Implementation),Version=task.Version+1},task.Version);
            Event(c,tx,task.Id,"PrimaryChanged","主 AI 已切換；既有實作繼續，後續協調使用新模型。");
        }
        var updated=d with{AgentMode=true,Selection=chosen,ActiveTaskId=task?.Id,Version=d.Version+1,UpdatedAt=DateTimeOffset.UtcNow};
        UpdateDiscussion(c,tx,updated);tx.Commit();return Task.FromResult(updated);
    }
    public async Task<Discussion> EnsureTaskConversationAsync(Guid taskId,CancellationToken ct,bool bindTask=true)
    {
        var task=await GetTaskAsync(taskId,ct)??throw new MissingEntityException("Task not found.");
        if(task.SourceDiscussionId is {} source&&await GetDiscussionAsync(source,ct) is {} d&&d.PlanTaskId is null){
            if(!bindTask&&d.ActiveTaskId is not null&&d.ActiveTaskId!=task.Id)return d;
            if(d.ActiveTaskId is null||d.ActiveTaskId==task.Id)return await SetDiscussionAgentAsync(source,d.Version,null,ct,taskId);
        }
        using(var find=Open(ct)){
            var existing=ReadList<Discussion>(find,"SELECT Payload FROM Discussions WHERE json_extract(Payload,'$.ActiveTaskId')=$task ORDER BY rowid DESC",("$task",taskId.ToString())).FirstOrDefault(d=>d.AgentMode&&d.PlanTaskId is null);
            if(existing is not null)return await SetDiscussionAgentAsync(existing.Id,existing.Version,null,ct,taskId);
        }
        using var c=Open(ct);using var tx=c.BeginTransaction();task=ReadOne<WorkTask>(c,tx,"Tasks",taskId)!;
        var created=new Discussion(Guid.NewGuid(),task.ProjectId,task.Title,AiSelection.Normalize(task.Ai?.Planning),task.Rules??[],[],[],0,DateTimeOffset.UtcNow,AgentMode:true,ActiveTaskId:taskId);
        Execute(c,tx,"INSERT INTO Discussions VALUES($id,$project,$payload)",("$id",created.Id.ToString()),("$project",created.ProjectId.ToString()),("$payload",JsonSerializer.Serialize(created)));
        if(task.SourceDiscussionId is null)UpdateTask(c,tx,task with{SourceDiscussionId=created.Id,Version=task.Version+1},task.Version);
        tx.Commit();return created;
    }
    public async Task<Discussion> OpenFeedbackAsync(Guid taskId,Guid runId,string commit,int expectedVersion,CancellationToken ct)
    {
        // Opening feedback never rejects or restarts work. Bind the exact version the user tested.
        var original=await GetTaskAsync(taskId,ct)??throw new MissingEntityException("Task not found.");
        if(original.Version!=expectedVersion)throw new ConflictException("任務已更新，請重新讀取驗收成果。");
        var run=await GetRunAsync(runId,ct)??throw new MissingEntityException("Run not found.");
        if(run.TaskId!=taskId||run.ResultCommit!=commit||run.FinishedAt is null)throw new ConflictException("驗收版本與任務不符。");
        var d=await EnsureTaskConversationAsync(taskId,ct);
        using var c=Open(ct);using var tx=c.BeginTransaction();
        d=ReadOne<Discussion>(c,tx,"Discussions",d.Id)!;
        if(d.ActiveOperation is not null)throw new ConflictException("主 AI 正在回覆，請稍後再開啟驗收回報。");
        var updated=d with{FeedbackContext=new(taskId,runId,commit),Version=d.Version+1,UpdatedAt=DateTimeOffset.UtcNow};
        UpdateDiscussion(c,tx,updated);tx.Commit();return updated;
    }
    public Task<Discussion> CompleteAgentTurnAsync(Guid id,Guid operation,AiSelection usedSelection,string baseCommit,AgentDecision decision,string intent,
        AgentCandidate[] candidates,int? taskVersion,CancellationToken ct)
    {
        using var c=Open(ct);using var tx=c.BeginTransaction();
        var d=ReadOne<Discussion>(c,tx,"Discussions",id)??throw new MissingEntityException("Discussion not found.");
        if(d.ActiveOperation!=operation)throw new ConflictException("對話操作已結束。");
        var reply=WorkTask.Required(decision.Reply,20000);
        var active=d.ActiveTaskId is {} taskId?ReadOne<WorkTask>(c,tx,"Tasks",taskId):null;
        Guid? createdTask=null;
        AgentDecision? proposal=null;
        if(decision.Action=="Start"){
            if(intent!="Start")throw new ArgumentException("尚未收到開始實作的明確指示。");
            var displayed=d.Messages.Length>=2?d.Messages[^2]:null;
            if(displayed?.AgentProposal is {} confirmed){decision=confirmed with{Action="Start"};baseCommit=displayed.BaseCommit??throw new ConflictException("計畫缺少基底，請重新規劃。");}
            else {decision=decision with{Action="Propose"};reply="已整理預計實作計畫，請檢視計畫表後按 Proceed 或直接回覆同意。";}
        }
        if(decision.Action=="Propose"){
            var continuing=active is not null&&active.Status is not (Status.Merged or Status.Cancelled);
            if(continuing&&(!ConversationAgent.CanRevise(active!)||active!.Version!=taskVersion))throw new ConflictException("目前任務仍在執行或狀態已更新，請待執行結束後再確認修改。");
            if(decision.OpenQuestions is {Length:>0})throw new ArgumentException("請先釐清問題，再提出待確認計畫。");
            var plan=WorkTask.Required(decision.Plan,40000);PlanContent.EnsureCompleted(plan);
            proposal=decision with{Title=WorkTask.Required(decision.Title,200),Description=WorkTask.Required(decision.Description,20000),AcceptanceCriteria=WorkTask.Required(decision.AcceptanceCriteria,20000),Plan=plan,Assignments=ConversationAgent.ValidateAssignments(decision.Assignments,candidates),TargetTaskId=continuing?active!.Id:null,TargetTaskVersion=continuing?active!.Version:null};
        }
        if(decision.Action=="Start"){
            if(intent!="Start")throw new ArgumentException("尚未收到開始實作的明確指示。");
            var continuing=decision.TargetTaskId is not null;
            if(continuing&&(active is null||active.Id!=decision.TargetTaskId||active.Version!=decision.TargetTaskVersion||!ConversationAgent.CanRevise(active)))throw new ConflictException("計畫對應的任務版本已更新，請重新討論後確認。");
            if(!continuing&&active is not null&&active.Status is not (Status.Merged or Status.Cancelled))throw new ConflictException("目前仍有未完成任務，請接續或另開對話。");
            if(decision.OpenQuestions is {Length:>0})throw new ArgumentException("主 AI 仍有未決問題，請先在對話釐清。");
            var assignments=ConversationAgent.ValidateAssignments(decision.Assignments,candidates);
            var plan=WorkTask.Required(decision.Plan,40000);PlanContent.EnsureCompleted(plan);
            var steps=assignments.Select(a=>new WorkflowStep(Guid.NewGuid(),a.Name,Kind:a.Kind,Selection:a.Selection,Instructions:a.Instructions)).ToArray();
            var references=d.Messages.SelectMany(m=>(m.Images??[]).Select(image=>new TaskImageReference(d.Id,image))).Concat(active?.ImageReferences??[]).DistinctBy(i=>(i.DiscussionId,i.Image.Id)).ToArray();
            if(references.Length>0&&assignments.Any(a=>a.Selection.Provider=="antigravity"))throw new ArgumentException("圖片任務請分配支援圖片的模型；Antigravity 背景模式目前只接受文字。");
            var task=WorkTask.Create(d.ProjectId,decision.Title!,decision.Description!,decision.AcceptanceCriteria!) with{
                Ai=new(d.Selection,steps[0].Selection),Rules=d.Rules,Policy=new(false,[]),SourceDiscussionId=id,
                Workflow=WorkflowDefinition.Normalize(new(Steps:steps,SimpleMode:true)),Delegation=new(candidates,assignments),ImageReferences=references};
            if(continuing){
                task=active! with{Description=task.Description,AcceptanceCriteria=task.AcceptanceCriteria,Ai=task.Ai,Workflow=task.Workflow,Delegation=task.Delegation,ImageReferences=references,
                    Plan=new(plan,(active!.Plan?.Revision??0)+1,baseCommit),Status=Status.Queued,Version=active.Version+1,
                    RevisionCommit=baseCommit,ResumeCommit=null,RevisionInstructions="依已確認的延續計畫調整：\n"+plan,StepResults=[],ActiveStepId=null,AutoReviewAttempt=0,Recovery=null,Integration=null};
                UpdateTask(c,tx,task,active.Version);Event(c,tx,task.Id,"TaskContinued","接續同一任務與成果 "+baseCommit+"；重新驗證後再交付人工驗收。");
            }else{
                task=task.SetPlan(plan);task=task with{Plan=task.Plan! with{BaseCommit=baseCommit}};task=task.ApprovePlan();
                Execute(c,tx,"INSERT INTO Tasks VALUES($id,$project,$status,$version,$created,$payload)",("$id",task.Id.ToString()),("$project",task.ProjectId.ToString()),("$status",(int)task.Status),("$version",task.Version),("$created",task.CreatedAt.ToString("O")),("$payload",JsonSerializer.Serialize(task)));
                Event(c,tx,task.Id,"TaskCreated","依使用者明確指示，由目前主 AI 分配實作。");
            }
            RecordPlan(c,tx,task,null,continuing?"ConversationContinuation":"Conversation");
            active=task;createdTask=task.Id;
            reply+="\n\n"+string.Join("\n",assignments.Select(a=>$"- {a.Name} → {a.Selection.Provider} / {a.Selection.Model??"CLI 預設"}：{a.Reason}"));
        }else if(decision.Action=="Resume"){
            if(intent!="Resume"||active is null||active.Status is not (Status.AwaitingStepApproval or Status.Failed or Status.Rejected)||active.Version!=taskVersion)throw new ConflictException("暫停問題或任務狀態已變更，請重新討論後再繼續。");
            var results=active.StepResults??[];var last=results.LastOrDefault();var rework=last?.RequiresRework==true||last?.RequiresRework is null&&last?.Summary?.StartsWith("⚠",StringComparison.Ordinal)==true;
            var guidance=WorkTask.Required(decision.ResumeInstructions,12000);
            var human=d.Messages.LastOrDefault(m=>m.Role=="user")?.Content;
            WorkTask resumed;
            if(active.Status==Status.AwaitingStepApproval){
                if(last is null)throw new ArgumentException("此暫停點缺少執行紀錄，請先檢視任務。");
                var effective=rework?results[..^1]:results;var finished=effective.Length==active.Workflow?.Steps?.Length;
                resumed=active with{Status=finished?Status.AwaitingReview:Status.Queued,StepResults=effective,ActiveStepId=null,ResumeCommit=rework?last.ResultCommit:active.ResumeCommit,RevisionCommit=rework?last.ResultCommit??active.RevisionCommit:active.RevisionCommit,Version=active.Version+1};
                if(finished){
                    using var query=Command(c,tx,"SELECT Payload FROM Runs WHERE TaskId=$id ORDER BY rowid DESC LIMIT 1",("$id",active.Id.ToString()));
                    var run=JsonSerializer.Deserialize<TaskRun>((string?)query.ExecuteScalar()??throw new ConflictException("找不到暫停紀錄。"))!;
                    if(run.ResultCommit is null||run.Status!=Status.AwaitingStepApproval.ToString())throw new ConflictException("成果紀錄無法核對。");
                    Execute(c,tx,"UPDATE Runs SET Payload=$payload WHERE Id=$id",("$payload",JsonSerializer.Serialize(run with{Status=Status.AwaitingReview.ToString()})),("$id",run.Id.ToString()));
                }
            }else{
                var preview=RetryStrategy.Preview(active);resumed=active.Retry(guidance);
                if(preview.BaseCommit is not null)resumed=resumed with{RevisionCommit=preview.BaseCommit};
                if(active.Workflow?.Steps is {} steps&&results.Length==steps.Length)resumed=resumed with{StepResults=[],ActiveStepId=null};
            }
            resumed=resumed with{Ai=new(d.Selection,active.Ai?.Implementation),TaskRules=active.TaskRules+"\n使用者明確要求依本次討論繼續。最新訊息："+human+"\n主 AI 歸納的範圍內指示：\n"+guidance};
            if(decision.Assignments is {Length:>0}&&active.Delegation is not null){
                var next=ConversationAgent.ValidateAssignments(decision.Assignments,candidates,false);var completed=resumed.StepResults?.Length??0;
                if(next.Length!=resumed.Workflow!.Steps!.Length-completed)throw new ArgumentException("只能重新分配剩餘工作，變更工作範圍請另行討論。");
                if(next.Where((a,i)=>a.Kind!=resumed.Workflow.Steps[completed+i].Kind).Any())throw new ArgumentException("重新分配不能變更實作／驗收步驟類型。");
                var humanContext=string.Join("\n",d.Messages.Where(m=>m.Role=="user").TakeLast(8).Select(m=>m.Content));
                for(var i=0;i<next.Length;i++){
                    var previous=active.Delegation.Assignments[completed+i];var proposed=next[i].Selection;
                    var changedPin=previous.Pinned&&previous.Selection.Provider!=proposed.Provider||previous.PinnedModel&&previous.Selection.Model!=proposed.Model;
                    if(changedPin){
                        var alias=proposed.Provider=="antigravity"?"(?:agy|antigravity)":System.Text.RegularExpressions.Regex.Escape(proposed.Provider);
                        if(!System.Text.RegularExpressions.Regex.IsMatch(humanContext,"(?:改用|換成|改交給|交給|使用|用).{0,20}"+alias,System.Text.RegularExpressions.RegexOptions.IgnoreCase)||previous.PinnedModel&&proposed.Model is {} model&&!humanContext.Contains(model,StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("此工作先前有明確指定，請在對話明確說明要改用哪個工具或模型，再繼續。");
                    }
                }
                resumed=resumed with{Workflow=resumed.Workflow with{Steps=resumed.Workflow.Steps.Select((s,i)=>i<completed?s:s with{Selection=next[i-completed].Selection}).ToArray()},
                    Delegation=new(candidates,[..active.Delegation.Assignments.Take(completed),..next])};
                reply+="\n\n剩餘分工：\n"+string.Join("\n",next.Select(a=>$"- {a.Name} → {a.Selection.Provider} / {a.Selection.Model??"CLI 預設"}：{a.Reason}"));
            }
            UpdateTask(c,tx,resumed,active.Version);Event(c,tx,active.Id,"WorkflowContinued",guidance);active=resumed;
        }else if(decision.Action is not ("Discuss" or "Propose"))throw new ArgumentException("Invalid main AI action.");
        if(createdTask is not null&&d.Messages.Length>=2){var messages=d.Messages.ToArray();messages[^2]=messages[^2] with{CreatedTaskId=createdTask};d=d with{Messages=messages};}
        var message=new DiscussionMessage(Guid.NewGuid(),"assistant",reply,DateTimeOffset.UtcNow,usedSelection,baseCommit,operation,
            ProposedPlan:proposal?.Plan,ExecutionPlan:proposal is null?null:new(proposal.Title!,proposal.Description!,proposal.AcceptanceCriteria!,[]),CreatedTaskId:createdTask,AgentProposal:proposal);
        var updated=d with{ActiveTaskId=active?.Id,FeedbackContext=createdTask is null?d.FeedbackContext:null,Messages=[..d.Messages,message],ActiveOperation=null,Error=null,Version=d.Version+1,UpdatedAt=DateTimeOffset.UtcNow};
        UpdateDiscussion(c,tx,updated);tx.Commit();return Task.FromResult(updated);
    }
    public Task<WorkTask> ChangeDelegatedModelAsync(Guid taskId,Guid stepId,AiSelection selection,string reason,CancellationToken ct)
    {
        using var c=Open(ct);using var tx=c.BeginTransaction();var task=ReadOne<WorkTask>(c,tx,"Tasks",taskId)??throw new MissingEntityException("Task not found.");
        if(task.Status!=Status.Running||task.ActiveStepId!=stepId||task.Delegation is null)throw new ConflictException("目前無法變更自動分配。");
        stepId=ConversationAgent.ImplementationStep(task)?.Id??throw new ConflictException("找不到可修正的實作工作。");
        var index=Array.FindIndex(task.Workflow!.Steps!,s=>s.Id==stepId);
        if(index<0)throw new ConflictException("找不到正在執行的分工。");
        selection=ConversationAgent.ValidateSelection(selection,task.Delegation.Candidates);
        var assignment=task.Delegation.Assignments[index];
        if(assignment.Pinned&&assignment.Selection.Provider!=selection.Provider||assignment.PinnedModel&&assignment.Selection.Model!=selection.Model)throw new ConflictException("此工作已明確指定工具或模型，需由使用者決定是否更換。");
        var steps=task.Workflow.Steps!.Select(s=>s.Id==stepId?s with{Selection=selection}:s).ToArray();
        var changed=task with{Workflow=task.Workflow with{Steps=steps},Version=task.Version+1};UpdateTask(c,tx,changed,task.Version);Event(c,tx,task.Id,"ModelDelegated",WorkTask.Required(reason,2000)+" → "+selection.Provider+" / "+selection.Model);tx.Commit();return Task.FromResult(changed);
    }
}
