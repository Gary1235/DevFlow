using System.Text.Json;
using DevFlow.Core;

namespace DevFlow.Infrastructure;

public sealed partial class SqliteTaskStore
{
    public Task<WorkTask> StartDiscussionTaskAsync(Guid id, Guid planMessageId, int expectedVersion,
        AiSelection supervisor, AiSelection implementation, AiSelection? reviewer, CancellationToken ct)
    {
        supervisor = AiSelection.Normalize(supervisor); implementation = AiSelection.Normalize(implementation);
        reviewer = reviewer is null ? null : AiSelection.Normalize(reviewer);
        using var c=Open(ct);using var tx=c.BeginTransaction();
        var d=ReadOne<Discussion>(c,tx,"Discussions",id) ?? throw new MissingEntityException("Discussion not found.");
        var message=d.Messages.SingleOrDefault(m=>m.Id==planMessageId) ?? throw new MissingEntityException("Plan not found.");
        if(message.CreatedTaskId is {} existing) return Task.FromResult(ReadOne<WorkTask>(c,tx,"Tasks",existing) ?? throw new MissingEntityException("Task not found."));
        if(d.PlanTaskId is not null || d.Version!=expectedVersion || d.ActiveOperation is not null || d.Messages.LastOrDefault()?.Id!=planMessageId)
            throw new ConflictException("討論或計畫已更新，請重新檢視並整理最新計畫。");
        if(message is not {Role:"assistant",ExecutionPlan:{} plan,ProposedPlan:not null}) throw new ArgumentException("請先整理完整實作計畫。");
        if(plan.OpenQuestions.Length>0) throw new ArgumentException("計畫仍有未決問題，請先在對話釐清並更新計畫。");
        PlanContent.EnsureCompleted(message.ProposedPlan);
        var project=ReadOne<ProjectDefinition>(c,tx,"Projects",d.ProjectId) ?? throw new MissingEntityException("Project not found.");
        var steps=new List<WorkflowStep>{new(Guid.NewGuid(),"實作與建置驗證",Selection:implementation)};
        if(reviewer is not null)steps.Add(new(Guid.NewGuid(),"AI 驗收","Review",reviewer));
        var flow=WorkflowDefinition.Normalize(new(Steps:steps.ToArray(),SimpleMode:true,MaxReviewRetries:reviewer is null?0:2));
        var task=WorkTask.Create(d.ProjectId,plan.Title,plan.Description,plan.AcceptanceCriteria) with {
            Ai=new(supervisor,implementation),Rules=d.Rules,Policy=new(false,[]),Workflow=flow,SourceDiscussionId=id
        };
        task=task.SetPlan(message.ProposedPlan);
        task=task with{Plan=task.Plan! with{BaseCommit=message.BaseCommit}};
        task=task.ApprovePlan();
        Execute(c,tx,"INSERT INTO Tasks(Id,ProjectId,Status,Version,CreatedAt,Payload) VALUES($id,$project,$status,$version,$created,$payload)",
            ("$id",task.Id.ToString()),("$project",project.Id.ToString()),("$status",(int)task.Status),("$version",task.Version),
            ("$created",task.CreatedAt.ToString("O")),("$payload",JsonSerializer.Serialize(task)));
        RecordPlan(c,tx,task,null,"Discussion");Event(c,tx,task.Id,"TaskCreated","已明確核准討論計畫，排隊執行。");
        UpdateDiscussion(c,tx,d with{Messages=d.Messages.Select(m=>m.Id==planMessageId?m with{CreatedTaskId=task.Id}:m).ToArray(),Version=d.Version+1,UpdatedAt=DateTimeOffset.UtcNow});
        tx.Commit();return Task.FromResult(task);
    }
}
