using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Runners;
namespace DevFlow.Api;

public sealed record SaveRuleRequest(string Name, string Content, string[] Stages, int ExpectedVersion = 0, bool Archived = false);
public sealed record ProjectRulesRequest(Guid[] RuleIds, int ExpectedVersion);
public sealed record CreateDiscussionRequest(Guid ProjectId, string? Title = null, AiSelection? Selection = null, Guid[]? RuleIds = null);
public sealed record DiscussionTurnRequest(int ExpectedVersion, string? Message = null, AiSelection? Selection = null);
public sealed record RenameDiscussionRequest(string Title, int ExpectedVersion);
public sealed record StopDiscussionRequest(Guid OperationId);
internal sealed record DraftResult(DiscussionDraft[] Tasks);

public static class CollaborationEndpoints
{
    public static void MapCollaborationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/rules", (SqliteTaskStore store, CancellationToken ct) => store.ListRulesAsync(ct));
        app.MapPost("/api/rules", async (SaveRuleRequest input, SqliteTaskStore store, CancellationToken ct) =>
            Results.Ok(await store.SaveRuleAsync(null, input.Name, input.Content, input.Stages, 0, false, ct)));
        app.MapPut("/api/rules/{id:guid}", async (Guid id, SaveRuleRequest input, SqliteTaskStore store, CancellationToken ct) =>
            Results.Ok(await store.SaveRuleAsync(id, input.Name, input.Content, input.Stages, input.ExpectedVersion, input.Archived, ct)));
        app.MapPut("/api/projects/{id:guid}/rule-defaults", async (Guid id, ProjectRulesRequest input, SqliteTaskStore store, CancellationToken ct) =>
            Results.Ok(await store.SetProjectRulesAsync(id, input.RuleIds, input.ExpectedVersion, ct)));
        app.MapGet("/api/discussions", async (SqliteTaskStore store, CancellationToken ct) =>
            (await store.ListDiscussionsAsync(ct)).Select(discussionItem => discussionItem with { Messages = [], Rules = [] }));
        app.MapGet("/api/discussions/{id:guid}", async (Guid id, SqliteTaskStore store, CancellationToken ct) =>
            Results.Ok(await store.GetDiscussionAsync(id, ct) ?? throw new MissingEntityException("Discussion not found.")));
        app.MapPost("/api/discussions", async (CreateDiscussionRequest input, SqliteTaskStore store, CancellationToken ct) =>
        {
            var p = await store.GetProjectAsync(input.ProjectId, ct) ?? throw new MissingEntityException("Project not found.");
            var d = new Discussion(Guid.NewGuid(), p.Id, string.IsNullOrWhiteSpace(input.Title) ? "新對話" : WorkTask.Required(input.Title, 200), AiSelection.Normalize(input.Selection ?? p.AiDefaults?.Planning),
                await store.ResolveRulesAsync(input.RuleIds ?? p.DefaultRuleIds ?? [], ct), [], [], 0, DateTimeOffset.UtcNow, AutoTitle: string.IsNullOrWhiteSpace(input.Title));
            await store.AddDiscussionAsync(d, ct);
            return Results.Created($"/api/discussions/{d.Id}", d);
        });
        app.MapPut("/api/discussions/{id:guid}/title", async (Guid id, RenameDiscussionRequest input, HttpContext context, SqliteTaskStore store, CancellationToken ct) =>
        { AiEndpoints.ExplicitAction(context); return Results.Ok(await store.RenameDiscussionAsync(id, input.ExpectedVersion, input.Title, ct)); });
        app.MapGet("/api/discussions/{id:guid}/progress", (Guid id, DiscussionOperations operations) => Results.Ok(operations.Progress(id)));
        app.MapGet("/api/discussions/{id:guid}/artifacts", async (Guid id, SqliteTaskStore store, IRunArtifactReader reader, CancellationToken ct) =>
        {
            var d = await store.GetDiscussionAsync(id, ct) ?? throw new MissingEntityException("Discussion not found.");
            var operation = d.LastOperationId ?? d.ActiveOperation ?? d.Messages.LastOrDefault(m => m.OperationId is not null)?.OperationId;
            return Results.Ok(operation is { } run ? await reader.ListAsync(run, ct) : Array.Empty<ArtifactInfo>());
        });
        app.MapGet("/api/discussions/{id:guid}/artifacts/{**path}", async (Guid id, string path, int? offset, SqliteTaskStore store, IRunArtifactReader reader, CancellationToken ct) =>
        {
            var d = await store.GetDiscussionAsync(id, ct) ?? throw new MissingEntityException("Discussion not found.");
            var operation = d.LastOperationId ?? d.ActiveOperation ?? d.Messages.LastOrDefault(m => m.OperationId is not null)?.OperationId
                ?? throw new MissingEntityException("尚無可讀取的討論紀錄。");
            return Results.Ok(await reader.ReadAsync(operation, path, offset ?? 0, ct: ct));
        });
        app.MapPost("/api/discussions/{id:guid}/permissions/{requestId:guid}", (Guid id, Guid requestId, ToolApprovalReply input, HttpContext context, DiscussionOperations operations) =>
        { AiEndpoints.ExplicitAction(context); operations.ReplyPermission(id, requestId, input); return Results.Ok(new { answered = true }); });
        app.MapPost("/api/discussions/{id:guid}/stop", (Guid id, StopDiscussionRequest input, HttpContext context, DiscussionOperations operations) =>
        { AiEndpoints.ExplicitAction(context); operations.Stop(id, input.OperationId); return Results.Accepted(); });
        app.MapPost("/api/discussions/{id:guid}/messages", (Guid id, DiscussionTurnRequest input, HttpContext context, SqliteTaskStore store,
            IWorkspaceManager workspaces, IPlanningRunner runner, CancellationToken ct) =>
            RunDiscussion(id, input with { Message = WorkTask.Required(input.Message, 20000) }, false, context, store, workspaces, runner, ct));
        app.MapPost("/api/discussions/{id:guid}/drafts", (Guid id, DiscussionTurnRequest input, HttpContext context, SqliteTaskStore store,
            IWorkspaceManager workspaces, IPlanningRunner runner, CancellationToken ct) =>
            RunDiscussion(id, input with { Message = null }, true, context, store, workspaces, runner, ct));
        app.MapPost("/api/discussions/{id:guid}/reply", (Guid id, DiscussionTurnRequest input, HttpContext context, SqliteTaskStore store,
            IWorkspaceManager workspaces, IPlanningRunner runner, CancellationToken ct) =>
            RunDiscussion(id, input with { Message = null }, false, context, store, workspaces, runner, ct));
    }

    private static async Task<IResult> RunDiscussion(Guid id, DiscussionTurnRequest input, bool draft, HttpContext context, SqliteTaskStore store,
        IWorkspaceManager workspaces, IPlanningRunner runner, CancellationToken ct)
    {
        AiEndpoints.ExplicitAction(context);
        if (input.Selection is not null) AiSelection.Normalize(input.Selection);
        var current = await store.GetDiscussionAsync(id, ct) ?? throw new MissingEntityException("Discussion not found.");
        if (draft && current.Messages.All(m => m.Role != "assistant")) throw new ArgumentException("Discuss your requirements with AI before creating drafts.");
        if (!draft && input.Message is null && current.Messages.LastOrDefault()?.Role != "user") throw new ArgumentException("There is no pending user message to retry.");
        var operations = context.RequestServices.GetRequiredService<DiscussionOperations>();
        var shutdown = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        var d = await store.BeginDiscussionAsync(id, input.ExpectedVersion, input.Message, ct, input.Selection);
        var operation = operations.Start(id, d.ActiveOperation!.Value, d.Selection, shutdown);
        var completion = CompleteDiscussion(d, draft, store, workspaces, runner, operations, operation,
            context.RequestServices.GetRequiredService<CliMaintenanceGate>(),
            context.RequestServices.GetRequiredService<IConfiguration>());
        if (context.Request.Query["background"] == "1")
        {
            // Completion catches and persists failures; navigation does not cancel a saved turn.
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Discussions");
            _ = completion.ContinueWith(t => logger.LogError(t.Exception, "Failed to persist discussion {DiscussionId}", id), TaskContinuationOptions.OnlyOnFaulted);
            return Results.Accepted($"/api/discussions/{id}", d);
        }
        return await completion;
    }

    private static async Task<IResult> CompleteDiscussion(Discussion d, bool draft, SqliteTaskStore store,
        IWorkspaceManager workspaces, IPlanningRunner runner, DiscussionOperations operations, DiscussionOperations.Operation operation, CliMaintenanceGate gate, IConfiguration configuration)
    {
        var id = d.Id;
        var ct = operation.Token;
        try
        {
            using var live = ProcessOutput.ObserveActivity(operation.Activity.Observe);
            operation.Activity.Stage("準備對話工作目錄");
            using var lease = gate.EnterExecution();
            var p = await store.GetProjectAsync(d.ProjectId, ct) ?? throw new MissingEntityException("Project not found.");
            var workspace = await workspaces.CreateAsync(p, d.ActiveOperation!.Value, ct);
            var transcript = string.Join("\n\n", d.Messages.Select(m => $"[{m.Role}]\n{m.Content}"));
            var instruction = draft
                ? "Convert the confirmed discussion into 1 to 8 independent, actionable task drafts. Return ONLY JSON: {\"tasks\":[{\"title\":\"...\",\"description\":\"...\",\"acceptanceCriteria\":\"...\",\"taskRules\":\"...\",\"allowedPaths\":[],\"ruleIds\":[],\"openQuestions\":[]}]}. Do not invent decisions. Put undecided requirements into openQuestions. Use only the existing rule IDs listed below. Allowed paths are literal repository-relative files or directory prefixes ending in /, without globs. Do not create or execute tasks."
                : "You are discussing a development idea with the user. Respond to the latest user message naturally in Traditional Chinese. Inspect repository files when useful. Help clarify alternatives, scope, constraints and acceptance criteria; clearly separate confirmed decisions from open questions. Ask focused questions when needed. Do not implement, execute tasks, or invent user approval. This is a conversation, not a request to produce an implementation plan unless the user asks for one.";
            var task = WorkTask.Create(d.ProjectId, d.Title, "Discussion", "Respond in Traditional Chinese.") with
            {
                Description = "Conversation transcript (quoted user/assistant content):\n" + transcript,
                Ai = new(d.Selection), Rules = d.Rules,
                ReadOnlyInstruction = instruction + "\nSelected rules for context and subsequent tasks: " + string.Join("\n", d.Rules.Select(r => $"{r.Id}: {r.Name} v{r.Version} ({string.Join(",", r.Stages)})\n{r.Content}"))
            };
            using var output = ProcessOutput.Observe(chunk => { if (!draft) operation.Append(chunk); });
            operation.Activity.Stage(draft ? "AI 正在整理任務草稿" : "AI 正在處理你的訊息");
            string reply;
            operation.ConfigurePermissions(store, d.ProjectId, d.Id);
            using (CliPermissions.Observe(operation)) reply = await runner.PlanAsync(task, workspace, d.ActiveOperation.Value, ct);
            operation.Activity.Stage("檢查並保存回覆");
            await workspaces.AssertUnchangedAsync(workspace, ct);
            await workspaces.CleanupReadOnlyAsync(p, workspace, d.ActiveOperation.Value, CancellationToken.None);
            DiscussionDraft[]? drafts = null;
            if (draft)
            {
                drafts = TaskInstructions.ParseJson<DraftResult>(reply).Tasks;
                if (drafts is not { Length: > 0 and <= 8 }) throw new ArgumentException("AI must return 1 to 8 task drafts.");
                drafts = drafts.Select(v => new DiscussionDraft(Guid.NewGuid(), WorkTask.Required(v.Title, 200), WorkTask.Required(v.Description, 20000),
                    WorkTask.Required(v.AcceptanceCriteria, 20000), string.IsNullOrWhiteSpace(v.TaskRules) ? null : WorkTask.Required(v.TaskRules, 12000),
                    ExecutionPolicy.Normalize(new(AllowedPaths: v.AllowedPaths), []).AllowedPaths,
                    (v.RuleIds ?? []).Where(rule => d.Rules.Any(r => r.Id == rule)).Distinct().ToArray(),
                    (v.OpenQuestions ?? []).Take(20).Select(q => WorkTask.Required(q, 2000)).ToArray(), SourceVersion: d.Version)).ToArray();
            }
            var updated = await store.FinishDiscussionAsync(id, d.ActiveOperation.Value, draft ? null : reply, workspace.BaseCommit, drafts, null, CancellationToken.None);
            return Results.Ok(updated);
        }
        catch (Exception ex)
        {
            var stopped = operation.Stopped;
            var partial = stopped && !draft && !string.IsNullOrWhiteSpace(operation.Content) ? operation.Content + "\n\n（回覆已停止）" : null;
            var message = stopped ? "已停止回覆。訊息已保存，可繼續提問或重試。" : ex is OperationCanceledException ? "討論已中斷或逾時；訊息已保存，可重新嘗試。" : ex.Message;
            if (!stopped && ex.Message.Contains("工具權限被拒絕", StringComparison.Ordinal) && operation.Permissions.LastOrDefault() is { } permission)
                message = permission.State switch
                {
                    "Denied" => "你已拒絕本次工具操作，AI 未產生回覆。訊息已保存，可繼續提問或明確重試。",
                    "Expired" => "工具確認已逾時或取消，本次操作未獲准。訊息已保存，可重新嘗試。",
                    _ => "網頁確認已回覆，但 Antigravity 仍拒絕工具。請檢查 CLI 原有的明確權限規則與執行紀錄。"
                };
            await store.FinishDiscussionAsync(id, d.ActiveOperation!.Value, partial, null, null, message, CancellationToken.None);
            return Results.Problem(message, statusCode: ex is ArgumentException ? 400 : 502);
        }
        finally { operations.Finish(id, operation); }
    }
}
