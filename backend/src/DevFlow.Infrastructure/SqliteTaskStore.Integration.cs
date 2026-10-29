using System.Text.Json;
using DevFlow.Core;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Infrastructure;
public sealed partial class SqliteTaskStore
{
    public Task<WorkTask> CreateIntegrationTaskAsync(Guid taskId, int expectedVersion, Guid runId, string sourceCommit, string targetCommit, CancellationToken ct,bool continueCurrent=false)
    {
        sourceCommit = GitCommit.Require(sourceCommit); targetCommit = GitCommit.Require(targetCommit);
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var source = ReadOne<WorkTask>(c, tx, "Tasks", taskId) ?? throw new MissingEntityException("Task not found.");
        if (source.Version != expectedVersion || source.Plan is null || source.Status is not (Status.Approved or Status.AwaitingReview or Status.Rejected)) throw new ConflictException("只能整合目前已驗證的成果，請重新讀取任務。");
        var run = ReadOne<TaskRun>(c, tx, "Runs", runId);
        using var latest = Command(c, tx, "SELECT Id FROM Runs WHERE TaskId=$id ORDER BY rowid DESC LIMIT 1", ("$id", taskId.ToString()));
        if (run?.TaskId != taskId || run.FinishedAt is null || run.Status != Status.AwaitingReview.ToString() || run.ResultCommit != sourceCommit || (string?)latest.ExecuteScalar() != runId.ToString())
            throw new ConflictException("來源必須是最新、已結束且通過驗證的成果。");
        var title = "整合最新基底 · " + source.Title; title = title[..Math.Min(title.Length, 200)];
        var project = ReadOne<ProjectDefinition>(c, tx, "Projects", source.ProjectId)!;
        var validationIds = (source.Workflow?.Steps ?? []).Where(s => s.Kind is "Implementation" or "Validation").Select(s => s.ValidationProfileId ?? project.DefaultValidationProfileId ?? "legacy-dotnet-test").Distinct().ToArray();
        var reviewers = source.Workflow?.Steps is { } steps ? steps.Where(s => s.Kind == "Review").Select(s => s.Selection!).ToArray() : source.Workflow?.Reviewers ?? [];
        if (reviewers.Length == 0 && source.Ai?.Planning is { } planner) reviewers = [planner];
        var task = WorkTask.Create(source.ProjectId, title, source.Description, source.AcceptanceCriteria) with {
            Status = Status.Queued, Plan = new(source.Plan!.Text, 1, targetCommit), Ai = source.Ai, Rules = source.Rules,
            TaskRules = source.TaskRules + "\nIntegration task: preserve the latest target and original validated result; resolve integration issues only. Original task: " + source.Id,
            Policy = source.Policy, ImageReferences=source.ImageReferences, Workflow = new(false, Reviewers: reviewers.Take(2).ToArray()),
            Integration = new(source.Id, run.Id, sourceCommit, targetCommit, validationIds, reviewers)
        };
        if(continueCurrent){
            task=source with{Status=Status.Queued,Version=source.Version+1,Plan=new(source.Plan.Text,source.Plan.Revision+1,targetCommit),
                TaskRules=task.TaskRules,Workflow=task.Workflow! with{SimpleMode=true},Integration=task.Integration,Delegation=null,
                ExecutionBaseCommit=targetCommit,RevisionCommit=targetCommit,RevisionInstructions="整合最新本地基底並重新驗證。",ResumeCommit=null,Recovery=null,StepResults=[],ActiveStepId=null,AutoReviewAttempt=0};
            UpdateTask(c,tx,task,source.Version);RecordPlan(c,tx,task,null,"IntegrationContinuation");Event(c,tx,task.Id,"IntegrationQueued",$"接續目前任務整合成果 {sourceCommit} 與最新基底 {targetCommit}，需重新驗證及人工驗收。");
            tx.Commit();return Task.FromResult(task);
        }
        Execute(c, tx, "INSERT INTO Tasks VALUES($id,$project,$status,$version,$created,$payload)", ("$id", task.Id.ToString()), ("$project", task.ProjectId.ToString()),
            ("$status", (int)task.Status), ("$version", task.Version), ("$created", task.CreatedAt.ToString("O")), ("$payload", JsonSerializer.Serialize(task)));
        Event(c, tx, task.Id, "IntegrationQueued", $"來源 {source.Id}／{sourceCommit}；新基底 {targetCommit}。整合完成後需重新審核。");
        tx.Commit(); return Task.FromResult(task);
    }
}
