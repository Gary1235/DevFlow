using DevFlow.Core;
using DevFlow.Infrastructure;
namespace DevFlow.Api;

public sealed record SaveRuleRequest(string Name, string Content, string[] Stages, int ExpectedVersion = 0, bool Archived = false);
public sealed record ProjectRulesRequest(Guid[] RuleIds, int ExpectedVersion);
public sealed record CreateDiscussionRequest(Guid ProjectId, string Title, AiSelection Selection, Guid[]? RuleIds = null);
public sealed record DiscussionTurnRequest(int ExpectedVersion, string? Message = null);
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
        app.MapGet("/api/discussions", (SqliteTaskStore store, CancellationToken ct) => store.ListDiscussionsAsync(ct));
        app.MapGet("/api/discussions/{id:guid}", async (Guid id, SqliteTaskStore store, CancellationToken ct) =>
            Results.Ok(await store.GetDiscussionAsync(id, ct) ?? throw new MissingEntityException("Discussion not found.")));
        app.MapPost("/api/discussions", async (CreateDiscussionRequest input, SqliteTaskStore store, CancellationToken ct) =>
        {
            var p = await store.GetProjectAsync(input.ProjectId, ct) ?? throw new MissingEntityException("Project not found.");
            var d = new Discussion(Guid.NewGuid(), p.Id, WorkTask.Required(input.Title, 200), AiSelection.Normalize(input.Selection),
                await store.ResolveRulesAsync(input.RuleIds ?? p.DefaultRuleIds ?? [], ct), [], [], 0, DateTimeOffset.UtcNow);
            await store.AddDiscussionAsync(d, ct);
            return Results.Created($"/api/discussions/{d.Id}", d);
        });
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
        var current = await store.GetDiscussionAsync(id, ct) ?? throw new MissingEntityException("Discussion not found.");
        if (draft && current.Messages.All(m => m.Role != "assistant")) throw new ArgumentException("Discuss your requirements with AI before creating drafts.");
        if (!draft && input.Message is null && current.Messages.LastOrDefault()?.Role != "user") throw new ArgumentException("There is no pending user message to retry.");
        var d = await store.BeginDiscussionAsync(id, input.ExpectedVersion, input.Message, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var p = await store.GetProjectAsync(d.ProjectId, timeout.Token) ?? throw new MissingEntityException("Project not found.");
            var workspace = await workspaces.CreateAsync(p, d.ActiveOperation!.Value, timeout.Token);
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
            var reply = await runner.PlanAsync(task, workspace, d.ActiveOperation.Value, timeout.Token);
            await workspaces.AssertUnchangedAsync(workspace, timeout.Token);
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
            var message = ex is OperationCanceledException ? "討論已中斷或逾時；訊息已保存，可重新嘗試。" : ex.Message;
            await store.FinishDiscussionAsync(id, d.ActiveOperation!.Value, null, null, null, message, CancellationToken.None);
            return Results.Problem(message, statusCode: ex is ArgumentException ? 400 : 502);
        }
    }
}
