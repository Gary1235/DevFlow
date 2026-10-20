using DevFlow.Contracts;
using DevFlow.Core;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Api;

public static class ReviewEndpoints
{
    public static void MapReviewEndpoints(this WebApplication app)
    {
        app.MapPut("/api/tasks/{id:guid}/model", async (Guid id, SetTaskModelRequest input, ITaskStore store, CancellationToken ct) =>
        {
            var task = await FindTask(id, store, ct);
            if (task.Version != input.ExpectedVersion) throw new ConflictException("任務已變更，請重新讀取。");
            if (input.Stage is not ("Planning" or "Implementation")) throw new ArgumentException("不支援的模型階段。");
            if (input.Stage == "Planning" && task.Status is not (Status.AwaitingPlan or Status.AwaitingApproval or Status.PlanningFailed) ||
                input.Stage == "Implementation" && (task.Status is not (Status.Failed or Status.Rejected) || task.Workflow?.Steps is not null))
                throw new ConflictException("目前狀態不能修改此模型；自訂流程請編輯尚未完成步驟。");
            var selection = AiSelection.Normalize(input.Selection);
            var ai = task.Ai ?? new AiConfiguration();
            var updated = task with { Ai = input.Stage == "Planning" ? ai with { Planning = selection } : ai with { Implementation = selection }, Version = task.Version + 1 };
            await store.SaveTaskAsync(updated, task.Version, ct);
            return Results.Ok(updated);
        });
        app.MapPost("/api/tasks/{id:guid}/reviews", async (Guid id, ReviewTaskRequest input, ITaskStore store, CancellationToken ct) =>
        {
            if (!string.Equals(input.Decision, "Approve", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(input.Decision, "Reject", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Decision must be Approve or Reject.");
            var decision = string.Equals(input.Decision, "Approve", StringComparison.OrdinalIgnoreCase)
                ? ReviewDecision.Approve : ReviewDecision.Reject;
            var review = await store.ReviewAsync(id, input.RunId, input.ResultCommit, decision, input.Comments, input.ExpectedVersion, ct);
            return Results.Created($"/api/tasks/{id}/reviews/{review.Id}", review);
        });
        app.MapGet("/api/tasks/{id:guid}/reviews", async (Guid id, ITaskStore store, CancellationToken ct) =>
        {
            await FindTask(id, store, ct);
            return Results.Ok(await store.ListReviewsAsync(id, ct));
        });
        app.MapGet("/api/tasks/{id:guid}/reviews/{reviewId:guid}", async (Guid id, Guid reviewId, ITaskStore store, CancellationToken ct) =>
        {
            await FindTask(id, store, ct);
            var review = (await store.ListReviewsAsync(id, ct)).SingleOrDefault(r => r.Id == reviewId)
                ?? throw new MissingEntityException("Review not found.");
            return Results.Ok(review);
        });
        app.MapPost("/api/tasks/{id:guid}/retry", async (Guid id, RetryTaskRequest input, ITaskStore store, CancellationToken ct) =>
            Results.Ok(await store.RetryAsync(id, input.Instructions, input.ExpectedVersion, ct, input.Scope)));
        app.MapGet("/api/tasks/{id:guid}/retry-preview", async (Guid id, string? scope, ITaskStore store, CancellationToken ct) =>
            Results.Ok(RetryStrategy.Preview(await FindTask(id, store, ct), scope ?? "Resume")));
        app.MapGet("/api/runs/{id:guid}", async (Guid id, ITaskStore store, CancellationToken ct) =>
            Results.Ok(await FindRun(id, store, ct)));
        app.MapGet("/api/runs/{id:guid}/diff", async (Guid id, ITaskStore store, IResultDiffReader reader, CancellationToken ct) =>
        {
            var run = await FindRun(id, store, ct);
            if (run.FinishedAt is null || run.Status != Status.AwaitingReview.ToString() || string.IsNullOrWhiteSpace(run.ResultCommit))
                throw new ConflictException("A validated result is required before reading its diff.");
            var task = await FindTask(run.TaskId, store, ct);
            var project = await store.GetProjectAsync(task.ProjectId, ct) ?? throw new MissingEntityException("Project not found.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return Results.Ok(await reader.ReadAsync(project, run, timeout.Token));
        });
    }
    private static async Task<WorkTask> FindTask(Guid id, ITaskStore store, CancellationToken ct) =>
        await store.GetTaskAsync(id, ct) ?? throw new MissingEntityException("Task not found.");
    private static async Task<TaskRun> FindRun(Guid id, ITaskStore store, CancellationToken ct) =>
        await store.GetRunAsync(id, ct) ?? throw new MissingEntityException("Run not found.");
}
