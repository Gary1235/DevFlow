using DevFlow.Contracts;
using DevFlow.Core;
using DevFlow.Infrastructure;
namespace DevFlow.Api;
public sealed record IntegrateTaskRequest(int ExpectedVersion, Guid RunId, string ResultCommit, string ExpectedTargetCommit,bool ContinueCurrent=false);

public static class MergeEndpoints
{
    public static void MapMergeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/tasks/{id:guid}/integrate", async (Guid id, IntegrateTaskRequest input, HttpContext context, SqliteTaskStore store, IGitMergeService git, CancellationToken ct) => {
            AiEndpoints.ExplicitAction(context);
            var task = await store.GetTaskAsync(id, ct) ?? throw new MissingEntityException("Task not found.");
            var project = await store.GetProjectAsync(task.ProjectId, ct) ?? throw new MissingEntityException("Project not found.");
            var state = await git.InspectAsync(project, ct);
            if (state.Branch != project.BaseBranch || state.HeadCommit != input.ExpectedTargetCommit) throw new ConflictException("目標分支或 HEAD 已變更，請重新讀取。");
            var created = await store.CreateIntegrationTaskAsync(id, input.ExpectedVersion, input.RunId, input.ResultCommit, input.ExpectedTargetCommit, ct,input.ContinueCurrent);
            return Results.Created("/api/tasks/" + created.Id, created);
        });
        app.MapGet("/api/projects/{id:guid}/git-state", async (Guid id, ITaskStore store, IGitMergeService git, CancellationToken ct) =>
        {
            var project = await store.GetProjectAsync(id, ct) ?? throw new MissingEntityException("Project not found.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return Results.Ok(await git.InspectAsync(project, timeout.Token));
        });
        app.MapPost("/api/tasks/{id:guid}/merge", async (Guid id, MergeTaskRequest input, MergeCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.MergeAsync(id, input.RunId, input.ResultCommit, input.ExpectedTargetCommit, input.ExpectedVersion, ct)));
        app.MapPost("/api/tasks/{id:guid}/accept-and-merge",async(Guid id,MergeTaskRequest input,HttpContext context,SqliteTaskStore store,IGitMergeService git,MergeCoordinator coordinator,TaskPreviewManager previews,CancellationToken ct)=>{
            AiEndpoints.ExplicitAction(context);
            var task=await store.GetTaskAsync(id,ct)??throw new MissingEntityException("Task not found.");
            if(task.Status==DevFlow.Core.TaskStatus.Merged)return Results.Ok(await coordinator.MergeAsync(id,input.RunId,input.ResultCommit,input.ExpectedTargetCommit,input.ExpectedVersion,ct));
            if(task.Version!=input.ExpectedVersion||task.Status is not (DevFlow.Core.TaskStatus.AwaitingReview or DevFlow.Core.TaskStatus.Approved))throw new ConflictException("成果已更新，請重新驗收目前版本。");
            var preview=previews.Get(id);
            if(preview.State is "Starting" or "Running"&&preview.ResultCommit!=input.ResultCommit)throw new ConflictException("目前預覽的是較舊成果，請停止並開啟最新成果後再驗收。");
            var project=await store.GetProjectAsync(task.ProjectId,ct)??throw new MissingEntityException("Project not found.");
            var state=await git.InspectAsync(project,ct);
            if(!state.Clean||state.Branch!=project.BaseBranch||state.HeadCommit!=input.ExpectedTargetCommit)throw new ConflictException("目標分支或本地修改已變更，請先確認本地狀態。");
            var version=task.Version;
            if(task.Status==DevFlow.Core.TaskStatus.AwaitingReview){
                await store.ReviewAsync(id,input.RunId,input.ResultCommit,ReviewDecision.Approve,"使用者確認此版本人工驗收通過並合併。",version,ct);version++;
            }
            return Results.Ok(await coordinator.MergeAsync(id,input.RunId,input.ResultCommit,input.ExpectedTargetCommit,version,ct));
        });
        app.MapGet("/api/tasks/{id:guid}/merges", async (Guid id, ITaskStore store, CancellationToken ct) =>
        {
            if (await store.GetTaskAsync(id, ct) is null) throw new MissingEntityException("Task not found.");
            return Results.Ok(await store.ListMergesAsync(id, ct));
        });
        app.MapPost("/api/tasks/{id:guid}/merge/reconcile", async (Guid id, MergeCoordinator coordinator, CancellationToken ct) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return Results.Ok(await coordinator.ReconcileAsync(id, timeout.Token));
        });
    }
}
