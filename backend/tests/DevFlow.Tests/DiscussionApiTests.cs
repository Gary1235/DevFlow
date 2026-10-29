using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevFlow.Core;
using DevFlow.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace DevFlow.Tests;

public sealed class DiscussionApiTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-discussion-api-" + Guid.NewGuid());
    private readonly ReadOnlyRunner runner = new();
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private ProjectDefinition project = null!;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["DevFlow:DatabasePath"] = Path.Combine(directory, "api.db"), ["DevFlow:ArtifactsRoot"] = Path.Combine(directory, "artifacts") }))
            .ConfigureTestServices(s => { s.AddSingleton<IPlanningRunner>(runner); s.AddSingleton<IWorkspaceManager, ReadOnlyWorkspace>();s.AddSingleton<DevFlow.Api.IAgentCatalog,AgentCatalogFixture>(); }));
        client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-DevFlow-Action", "1");
        project = new(Guid.NewGuid(), "Demo", directory, "main", ["test"]);
        await factory.Services.GetRequiredService<ITaskStore>().AddProjectAsync(project);
    }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); Directory.Delete(directory, true); }
    private async Task<JsonElement> CreateDiscussion()
    {
        var response = await client.PostAsJsonAsync("/api/discussions", new { projectId = project.Id, title = "Improve checkout", selection = new { provider = "gemini", model = "chosen-model" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, runner.Calls);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact] public async Task Discussion_plan_is_reviewed_then_atomically_started_with_selected_models_without_replanning()
    {
        var d=await CreateDiscussion();var id=d.GetProperty("id").GetGuid();
        (await client.PostAsJsonAsync($"/api/discussions/{id}/messages",new{expectedVersion=0,message="改善列表，先討論"})).EnsureSuccessStatusCode();
        runner.Response="{\"title\":\"改善列表\",\"description\":\"調整版面\",\"acceptanceCriteria\":\"前後端建置通過並人工測試\",\"plan\":\"1. 修改列表樣式。\\n2. 執行專案前後端建置。\",\"openQuestions\":[]}";
        var response=await client.PostAsJsonAsync($"/api/discussions/{id}/execution-plan",new{expectedVersion=2});response.EnsureSuccessStatusCode();
        var plan=(await response.Content.ReadFromJsonAsync<Discussion>())!;
        var message=plan.Messages.Last();Assert.NotNull(message.ExecutionPlan);Assert.NotNull(message.ProposedPlan);
        var store=factory.Services.GetRequiredService<SqliteTaskStore>();Assert.Empty(await store.ListTasksAsync());Assert.Null(await store.ClaimNextAsync());
        var input=new{expectedVersion=plan.Version,planMessageId=message.Id,supervisor=new{provider="codex",model="supervisor",reasoningEffort="high"},implementation=new{provider="antigravity",model="implementer"},reviewer=new{provider="claude",model="opus"}};
        var started=await client.PostAsJsonAsync($"/api/discussions/{id}/start",input);started.EnsureSuccessStatusCode();
        var task=(await store.ListTasksAsync()).Single();
        Assert.Equal(DevFlow.Core.TaskStatus.Queued,task.Status);Assert.True(task.Workflow!.SimpleMode);Assert.False(task.Policy!.Tdd);
        Assert.Equal(message.ProposedPlan,task.Plan!.Text);Assert.Equal("supervisor",task.Ai!.Planning!.Model);Assert.Equal("implementer",task.Ai.Implementation!.Model);
        Assert.Equal("opus",task.Workflow.Steps!.Last().Selection!.Model);Assert.Equal(id,task.SourceDiscussionId);Assert.Equal(2,runner.Calls);
        var duplicate=await client.PostAsJsonAsync($"/api/discussions/{id}/start",input);duplicate.EnsureSuccessStatusCode();
        Assert.Equal(task.Id,(await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());Assert.Single(await store.ListTasksAsync());
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Discussion_start_rejects_unresolved_questions_and_changed_conversation(bool changed)
    {
        var d=await CreateDiscussion();var id=d.GetProperty("id").GetGuid();
        (await client.PostAsJsonAsync($"/api/discussions/{id}/messages",new{expectedVersion=0,message="討論列表"})).EnsureSuccessStatusCode();
        runner.Response="{\"title\":\"列表\",\"description\":\"調整版面\",\"acceptanceCriteria\":\"建置通過\",\"plan\":\"修改列表並建置。\",\"openQuestions\":"+(changed?"[]":"[\"哪些頁面？\"]")+"}";
        var reply=await client.PostAsJsonAsync($"/api/discussions/{id}/execution-plan",new{expectedVersion=2});reply.EnsureSuccessStatusCode();
        var plan=(await reply.Content.ReadFromJsonAsync<Discussion>())!;
        if(changed)(await client.PostAsJsonAsync($"/api/discussions/{id}/messages",new{expectedVersion=plan.Version,message="再改需求"})).EnsureSuccessStatusCode();
        var started=await client.PostAsJsonAsync($"/api/discussions/{id}/start",new{expectedVersion=plan.Version,planMessageId=plan.Messages.Last().Id,supervisor=new{provider="codex"},implementation=new{provider="codex"}});
        Assert.Equal(changed?HttpStatusCode.Conflict:HttpStatusCode.BadRequest,started.StatusCode);
        Assert.Empty(await factory.Services.GetRequiredService<ITaskStore>().ListTasksAsync());
    }

    private async Task<(WorkTask Task, Discussion Discussion)> CreatePlanDiscussion()
    {
        var task = WorkTask.Create(project.Id, "訂單頁面", "改善列表排版", "建置成功")
            with { Ai = new(new("antigravity", "planner-model")) };
        task = task.SetPlan("修改 shipping-list.component.scss，執行 npm run build。");
        await factory.Services.GetRequiredService<ITaskStore>().AddTaskAsync(task);
        var response = await client.PostAsJsonAsync($"/api/tasks/{task.Id}/plan-discussion", new { });
        response.EnsureSuccessStatusCode();
        return (task, (await response.Content.ReadFromJsonAsync<Discussion>())!);
    }
    private sealed class AgentCatalogFixture:DevFlow.Api.IAgentCatalog{
        public Task<AgentCandidate[]> ReadAsync(AiSelection primary,CancellationToken ct)=>Task.FromResult(new[]{new AgentCandidate(primary),new(new("codex","manager")),new(new("antigravity","fast")),new(new("claude","strong"))});
    }
    private async Task<Discussion> CreateAgent(){var response=await client.PostAsJsonAsync("/api/discussions",new{projectId=project.Id,agentMode=true,selection=new{provider="codex",model="manager"}});response.EnsureSuccessStatusCode();return(await response.Content.ReadFromJsonAsync<Discussion>())!;}
    private static string StartDecision()=>JsonSerializer.Serialize(new AgentDecision("我會把畫面交給 agy，完成後建置檢查。","Start","改善畫面","修改畫面","建置通過並人工測試","修改樣式，執行建置並驗證。",[new("畫面","改善樣式",new("antigravity","fast"),"使用者指定畫面交給 agy",true)]));
    [Fact] public async Task Agent_proactively_proposes_without_start_and_followup_invalidates_old_approval()
    {
        var d=await CreateAgent();runner.Response=StartDecision().Replace("\"Start\"","\"Propose\"");
        var response=await client.PostAsJsonAsync($"/api/discussions/{d.Id}/messages",new{expectedVersion=d.Version,message="幫我改善畫面，交給 agy"});response.EnsureSuccessStatusCode();
        d=(await response.Content.ReadFromJsonAsync<Discussion>())!;Assert.NotNull(d.Messages.Last().AgentProposal);Assert.Empty(await store().ListTasksAsync());
        runner.Response=JsonSerializer.Serialize(new AgentDecision("這份計畫會保留原本資料欄位。"));
        response=await client.PostAsJsonAsync($"/api/discussions/{d.Id}/messages",new{expectedVersion=d.Version,message="好，但先解釋會影響哪裡？"});response.EnsureSuccessStatusCode();d=(await response.Content.ReadFromJsonAsync<Discussion>())!;
        Assert.Empty(await store().ListTasksAsync());Assert.Null(d.Messages.Last().AgentProposal);
        // A short acknowledgement after a normal answer is not approval of an older plan.
        runner.Response=StartDecision();response=await client.PostAsJsonAsync($"/api/discussions/{d.Id}/messages",new{expectedVersion=d.Version,message="好"});Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Empty(await store().ListTasksAsync());
    }
    [Fact] public async Task Agent_uses_current_primary_and_starts_without_model_forms_only_after_explicit_intent()
    {
        var d=await CreateAgent();runner.Response=StartDecision();
        var refused=await client.PostAsJsonAsync($"/api/discussions/{d.Id}/messages",new{expectedVersion=d.Version,message="請先解釋方案"});Assert.Equal(HttpStatusCode.BadRequest,refused.StatusCode);Assert.Empty(await store().ListTasksAsync());
        d=(await store().GetDiscussionAsync(d.Id,default))!;
        var started=await client.PostAsJsonAsync($"/api/discussions/{d.Id}/messages",new{expectedVersion=d.Version,message="開始做"});started.EnsureSuccessStatusCode();
        d=(await started.Content.ReadFromJsonAsync<Discussion>())!;Assert.Empty(await store().ListTasksAsync());Assert.NotNull(d.Messages.Last().AgentProposal);
        // Confirmation must execute the displayed plan without asking AI to silently rewrite it.
        runner.Response="unexpected model call";
        started=await client.PostAsJsonAsync($"/api/discussions/{d.Id}/messages",new{expectedVersion=d.Version,message="好，照這份計畫做"});started.EnsureSuccessStatusCode();
        d=(await started.Content.ReadFromJsonAsync<Discussion>())!;var task=Assert.Single(await store().ListTasksAsync());Assert.Equal("修改樣式，執行建置並驗證。",task.Plan!.Text);
        Assert.Equal(d.ActiveTaskId,task.Id);Assert.Equal("manager",task.Ai!.Planning!.Model);Assert.Equal("antigravity",task.Workflow!.Steps![0].Selection!.Provider);Assert.True(task.Delegation!.Assignments[0].Pinned);
        var changed=await client.PutAsJsonAsync($"/api/discussions/{d.Id}/agent",new{expectedVersion=d.Version,selection=new{provider="claude",model="strong"}});changed.EnsureSuccessStatusCode();
        var latest=(await store().GetTaskAsync(task.Id))!;Assert.Equal("strong",latest.Ai!.Planning!.Model);Assert.Equal(JsonSerializer.Serialize(task.Workflow),JsonSerializer.Serialize(latest.Workflow));
    }
    [Fact] public async Task Paused_agent_answers_followup_without_resuming_then_explicitly_continues_the_checkpoint()
    {
        var d=await CreateAgent();runner.Response=StartDecision();(await client.PostAsJsonAsync($"/api/discussions/{d.Id}/messages",new{expectedVersion=d.Version,message="開始做"})).EnsureSuccessStatusCode();
        d=(await store().GetDiscussionAsync(d.Id,default))!;(await client.PostAsJsonAsync($"/api/discussions/{d.Id}/messages",new{expectedVersion=d.Version,message="Proceed",intent="Start"})).EnsureSuccessStatusCode();
        var task=Assert.Single(await store().ListTasksAsync());
        await store().SaveTaskAsync(task with{Status=DevFlow.Core.TaskStatus.AwaitingStepApproval,StepResults=[new(task.Workflow!.Steps![0].Id,"畫面","Implementation","Completed",Guid.NewGuid(),"checkpoint","是否保留舊 API？",new("antigravity","fast"),DateTimeOffset.UtcNow,RequiresRework:true)],Version=task.Version+1},task.Version);
        d=(await store().GetDiscussionAsync(d.Id,default))!;runner.Response=JsonSerializer.Serialize(new AgentDecision("保留舊 API 可避免其他頁面失效。你可以繼續詢問影響。"));
        (await client.PostAsJsonAsync($"/api/discussions/{d.Id}/messages",new{expectedVersion=d.Version,message="什麼意思？會影響哪裡？"})).EnsureSuccessStatusCode();
        Assert.Equal(DevFlow.Core.TaskStatus.AwaitingStepApproval,(await store().GetTaskAsync(task.Id))!.Status);Assert.Contains(JsonSerializer.Serialize("是否保留舊 API？"),runner.Instruction);Assert.Equal("checkpoint",runner.WorkspaceBase);
        d=(await store().GetDiscussionAsync(d.Id,default))!;runner.Response=JsonSerializer.Serialize(new AgentDecision("依討論保留舊介面並繼續。","Resume",ResumeInstructions:"保留舊 API，僅調整畫面。"));
        (await client.PostAsJsonAsync($"/api/discussions/{d.Id}/messages",new{expectedVersion=d.Version,message="繼續",intent="Resume"})).EnsureSuccessStatusCode();
        var resumed=(await store().GetTaskAsync(task.Id))!;Assert.Equal(DevFlow.Core.TaskStatus.Queued,resumed.Status);Assert.Equal("checkpoint",resumed.ResumeCommit);Assert.Empty(resumed.StepResults!);
    }

    private const string Png="iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=";
    [Fact] public async Task Images_are_persisted_served_only_under_their_discussion_and_reused_when_switching_models()
    {
        var created=await CreateDiscussion();var id=created.GetProperty("id").GetGuid();
        var response=await client.PostAsJsonAsync($"/api/discussions/{id}/messages",new{expectedVersion=0,images=new[]{new{name="截圖.png",mimeType="image/png",data=Png}},selection=new{provider="codex"}});
        response.EnsureSuccessStatusCode();var discussion=(await response.Content.ReadFromJsonAsync<Discussion>())!;
        var image=Assert.Single(discussion.Messages[0].Images!);Assert.Equal("請查看附圖。",discussion.Messages[0].Content);
        Assert.DoesNotContain(Png,JsonSerializer.Serialize(discussion));
        Assert.Equal(Png,Assert.Single(runner.Images!).Data);
        Assert.Contains(discussion.Messages[0].Id.ToString(),runner.Transcript);
        var file=await client.GetAsync($"/api/discussions/{id}/images/{image.Id}");file.EnsureSuccessStatusCode();
        Assert.Equal("image/png",file.Content.Headers.ContentType!.MediaType);Assert.Equal(Convert.FromBase64String(Png),await file.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"/api/discussions/{Guid.NewGuid()}/images/{image.Id}")).StatusCode);
        (await client.PostAsJsonAsync($"/api/discussions/{id}/messages",new{expectedVersion=discussion.Version,message="再看這張圖",selection=new{provider="claude"}})).EnsureSuccessStatusCode();
        Assert.Equal(Png,Assert.Single(runner.Images!).Data);
        var saved=(await store().GetDiscussionAsync(id,default))!;
        var denied=await client.PostAsJsonAsync($"/api/discussions/{id}/messages",new{expectedVersion=saved.Version,message="看看",selection=new{provider="antigravity"}});
        Assert.Equal(HttpStatusCode.BadRequest,denied.StatusCode);Assert.Equal(saved.Version,(await store().GetDiscussionAsync(id,default))!.Version);
    }

    [Theory][InlineData("image/svg+xml","PHN2Zz48L3N2Zz4=")][InlineData("image/png","not-base64")][InlineData("image/png","aGVsbG8=")]
    public async Task Invalid_image_upload_does_not_start_a_cli_turn_or_save_a_message(string mime,string data)
    {
        var created=await CreateDiscussion();var id=created.GetProperty("id").GetGuid();
        var reply=await client.PostAsJsonAsync($"/api/discussions/{id}/messages",new{expectedVersion=0,images=new[]{new{name="invalid.png",mimeType=mime,data}}});
        Assert.Equal(HttpStatusCode.BadRequest,reply.StatusCode);Assert.Equal(0,runner.Calls);
        Assert.Empty((await store().GetDiscussionAsync(id,default))!.Messages);
    }
    [Fact] public async Task Image_count_size_and_stale_version_fail_before_running_the_cli()
    {
        var created=await CreateDiscussion();var id=created.GetProperty("id").GetGuid();
        var tooMany=Enumerable.Repeat(new{name="shot.png",mimeType="image/png",data=Png},5).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/discussions/{id}/messages",new{expectedVersion=0,images=tooMany})).StatusCode);
        var oversized=new string('A',4*((DiscussionImageStore.MaxBytes+2)/3)+4);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/discussions/{id}/messages",new{expectedVersion=0,images=new[]{new{name="shot.png",mimeType="image/png",data=oversized}}})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync($"/api/discussions/{id}/messages",new{expectedVersion=99,images=new[]{new{name="shot.png",mimeType="image/png",data=Png}}})).StatusCode);
        Assert.Equal(0,runner.Calls);Assert.Empty((await store().GetDiscussionAsync(id,default))!.Messages);
        var imageStore=factory.Services.GetRequiredService<DiscussionImageStore>();
        await Assert.ThrowsAsync<ArgumentException>(()=>imageStore.SaveAsync(id,[new("shot.png","image/png",Png)],default,DiscussionImageStore.MaxDiscussionBytes));
    }
    [Fact] public async Task Failed_cli_reply_keeps_images_available_for_explicit_retry()
    {
        var created=await CreateDiscussion();var id=created.GetProperty("id").GetGuid();runner.Fail=true;
        Assert.Equal(HttpStatusCode.BadGateway,(await client.PostAsJsonAsync($"/api/discussions/{id}/messages",new{expectedVersion=0,images=new[]{new{name="shot.png",mimeType="image/png",data=Png}}})).StatusCode);
        var failed=(await store().GetDiscussionAsync(id,default))!;Assert.Single(failed.Messages[0].Images!);
        runner.Fail=false;
        (await client.PostAsJsonAsync($"/api/discussions/{id}/reply",new{expectedVersion=failed.Version,selection=new{provider="claude"}})).EnsureSuccessStatusCode();
        Assert.Equal(Png,Assert.Single(runner.Images!).Data);Assert.Equal(2,(await store().GetDiscussionAsync(id,default))!.Messages.Length);
    }

    [Fact] public async Task Plan_discussion_is_task_local_persistent_and_does_not_run_until_a_message_is_sent()
    {
        var (task, discussion) = await CreatePlanDiscussion();
        var again = await client.PostAsJsonAsync($"/api/tasks/{task.Id}/plan-discussion", new { });
        Assert.Equal(discussion.Id, (await again.Content.ReadFromJsonAsync<Discussion>())!.Id);
        Assert.Equal(task.Id, discussion.PlanTaskId);
        Assert.Equal("planner-model", discussion.Selection.Model);
        Assert.Empty(await client.GetFromJsonAsync<Discussion[]>("/api/discussions") ?? []);
        Assert.Equal(0, runner.Calls);
        var reply = await client.PostAsJsonAsync($"/api/discussions/{discussion.Id}/messages", new { expectedVersion = 0, message = "好，先解釋風險", planRevision = 1 });
        reply.EnsureSuccessStatusCode();
        var saved = (await reply.Content.ReadFromJsonAsync<Discussion>())!;
        Assert.Equal(task.Description, saved.Messages[0].PlanContext!.Requirements);
        Assert.Contains("shipping-list.component.scss", runner.Transcript);
        Assert.Contains("execution approval", runner.Instruction);
        Assert.All(saved.Messages, m => Assert.Null(m.ProposedPlan));
        Assert.Equal(task, await factory.Services.GetRequiredService<ITaskStore>().GetTaskAsync(task.Id));
        Assert.Null(await factory.Services.GetRequiredService<ITaskStore>().ClaimNextAsync());
    }

    [Fact] public async Task Plan_proposal_only_creates_a_new_revision_after_explicit_apply_and_never_approves_execution()
    {
        var (task, discussion) = await CreatePlanDiscussion();
        runner.Response = "{\"summary\":\"增加響應式排版及驗證\",\"plan\":\"1. 修改 shipping-list.component.scss 增加手機排版。\\n2. 執行 npm run build，並檢查手機尺寸。\"}";
        var response = await client.PostAsJsonAsync($"/api/discussions/{discussion.Id}/plan-proposal", new { expectedVersion = 0, planRevision = 1 });
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<Discussion>())!;
        var proposal = result.Messages.Last();
        Assert.NotNull(proposal.ProposedPlan);
        Assert.Equal(task.Version, proposal.PlanContext!.TaskVersion);
        var store = factory.Services.GetRequiredService<SqliteTaskStore>();
        Assert.Equal(task.Plan, (await store.GetTaskAsync(task.Id))!.Plan);
        Assert.Equal(2, (await client.GetFromJsonAsync<Discussion>($"/api/discussions/{task.Id}"))!.Messages.Length);
        var apply = await client.PostAsJsonAsync($"/api/tasks/{task.Id}/plan-discussion/apply", new { messageId = proposal.Id, expectedVersion = task.Version });
        apply.EnsureSuccessStatusCode();
        var updated = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(DevFlow.Core.TaskStatus.AwaitingApproval, updated.Status);
        Assert.Equal(2, updated.Plan!.Revision);
        Assert.Equal(proposal.ProposedPlan, updated.Plan.Text);
        Assert.Equal(2, (await store.ListPlansAsync(task.Id)).Count);
        Assert.Null(await store.ClaimNextAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/tasks/{task.Id}/plan-discussion/apply", new { messageId = proposal.Id, expectedVersion = updated.Version })).StatusCode);
    }

    [Fact] public async Task Plan_discussion_rejects_stale_proposals_and_incomplete_output_but_preserves_conversation()
    {
        var (task, discussion) = await CreatePlanDiscussion();
        runner.Response = "{\"summary\":\"尚未完成\",\"plan\":\"正在分析程式碼\"}";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/discussions/{discussion.Id}/plan-proposal", new { expectedVersion = 0 })).StatusCode);
        var store = factory.Services.GetRequiredService<SqliteTaskStore>();
        var saved = (await store.GetDiscussionAsync(task.Id, default))!;
        Assert.Single(saved.Messages); Assert.Null(saved.ActiveOperation); Assert.NotNull(saved.Error);
        runner.Response = "{\"summary\":\"調整樣式\",\"plan\":\"修改樣式後執行 npm run build。\"}";
        var reply = await client.PostAsJsonAsync($"/api/discussions/{discussion.Id}/plan-proposal", new { expectedVersion = saved.Version });
        reply.EnsureSuccessStatusCode();
        var proposal = (await reply.Content.ReadFromJsonAsync<Discussion>())!.Messages.Last();
        var edited = task.SetPlan("另一個編輯者更新的計畫");
        await store.SaveTaskAsync(edited, task.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/tasks/{task.Id}/plan-discussion/apply", new { messageId = proposal.Id, expectedVersion = edited.Version })).StatusCode);
        var historical = await client.PostAsJsonAsync($"/api/discussions/{discussion.Id}/messages", new { expectedVersion = (await store.GetDiscussionAsync(task.Id, default))!.Version, message = "解釋舊版", planRevision = 1 });
        historical.EnsureSuccessStatusCode();
        Assert.Contains("shipping-list.component.scss", runner.Transcript);
        Assert.Equal(1, (await historical.Content.ReadFromJsonAsync<Discussion>())!.Messages.Last().PlanContext!.Revision);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/discussions/{discussion.Id}/drafts", new { expectedVersion = 6 })).StatusCode);
    }

    [Fact] public async Task Latest_failed_discussion_artifacts_are_available_without_starting_another_model()
    {
        var discussion = await CreateDiscussion(); var id = discussion.GetProperty("id").GetGuid();
        var store = factory.Services.GetRequiredService<SqliteTaskStore>();
        var started = await store.BeginDiscussionAsync(id, 0, "Read project", default);
        var operation = started.ActiveOperation!.Value;
        var artifacts = Path.Combine(directory, "artifacts", operation.ToString("N"));
        Directory.CreateDirectory(artifacts);
        await File.WriteAllTextAsync(Path.Combine(artifacts, "Planning-agent.stderr.log"), "test diagnostic");
        await store.FinishDiscussionAsync(id, operation, null, null, null, "test failure", default);
        var files = await client.GetFromJsonAsync<JsonElement>($"/api/discussions/{id}/artifacts");
        Assert.Single(files.EnumerateArray());
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/discussions/{id}/artifacts/Planning-agent.stderr.log");
        Assert.Equal("test diagnostic", page.GetProperty("text").GetString());
        Assert.Equal(0, runner.Calls);
        Assert.Equal(operation, (await store.GetDiscussionAsync(id, default))!.LastOperationId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/discussions/{Guid.NewGuid()}/artifacts/Planning-agent.stderr.log")).StatusCode);
    }
    [Fact] public async Task Discussion_to_editable_draft_to_task_does_not_execute_and_retains_source()
    {
        var d = await CreateDiscussion(); var id = d.GetProperty("id").GetGuid();
        var turn = await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = 0, message = "Consider an audit log, but retention is undecided." });
        Assert.Equal(HttpStatusCode.OK, turn.StatusCode);
        var reply = await turn.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, reply.GetProperty("messages").GetArrayLength());
        Assert.Equal("chosen-model", reply.GetProperty("messages")[1].GetProperty("selection").GetProperty("model").GetString());
        var draftsResponse = await client.PostAsJsonAsync($"/api/discussions/{id}/drafts", new { expectedVersion = reply.GetProperty("version").GetInt32() });
        Assert.Equal(HttpStatusCode.OK, draftsResponse.StatusCode);
        var drafts = await draftsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var draftId = drafts.GetProperty("drafts")[0].GetProperty("id").GetGuid();
        var body = new { projectId = project.Id, title = "Edited audit task", description = "Keep logs for 30 days", acceptanceCriteria = "Retention works", autoPlan = false,
            sourceDiscussionId = id, sourceDraftId = draftId, acknowledgeOpenQuestions = true };
        var unconfirmed = await client.PostAsJsonAsync("/api/tasks", new { projectId = project.Id, title = "Audit", description = "Build", acceptanceCriteria = "Tests", sourceDiscussionId = id, sourceDraftId = draftId });
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        var created = await client.PostAsJsonAsync("/api/tasks", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var task = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AwaitingPlan", task.GetProperty("status").GetString());
        Assert.Equal("Edited audit task", task.GetProperty("title").GetString());
        Assert.Equal(id, task.GetProperty("sourceDiscussionId").GetGuid());
        var duplicate = await client.PostAsJsonAsync("/api/tasks", body);
        Assert.Equal(task.GetProperty("id").GetGuid(), (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(2, runner.Calls);
        Assert.Null(await factory.Services.GetRequiredService<ITaskStore>().ClaimNextAsync());
    }

    [Fact] public async Task Cross_origin_discussion_calls_do_not_start_a_model_or_save_a_message()
    {
        var d = await CreateDiscussion(); var id = d.GetProperty("id").GetGuid();
        client.DefaultRequestHeaders.Add("Origin", "https://other.example");
        var response = await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = 0, message = "Run" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, runner.Calls);
        Assert.Empty((await factory.Services.GetRequiredService<SqliteTaskStore>().GetDiscussionAsync(id, default))!.Messages);
    }

    [Fact] public async Task Failed_reply_is_saved_and_can_be_retried_without_duplicate_user_messages()
    {
        var d = await CreateDiscussion(); var id = d.GetProperty("id").GetGuid(); runner.Fail = true;
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = 0, message = "Hello" })).StatusCode);
        var saved = (await factory.Services.GetRequiredService<SqliteTaskStore>().GetDiscussionAsync(id, default))!;
        Assert.Single(saved.Messages); Assert.Null(saved.ActiveOperation); Assert.NotNull(saved.Error);
        runner.Fail = false;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/discussions/{id}/reply", new { expectedVersion = saved.Version })).StatusCode);
        Assert.Equal(2, (await factory.Services.GetRequiredService<SqliteTaskStore>().GetDiscussionAsync(id, default))!.Messages.Length);
    }

    [Fact] public async Task Project_only_creation_names_from_first_question_and_switches_models_without_losing_history()
    {
        var created = await client.PostAsJsonAsync("/api/discussions", new { projectId = project.Id });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.Equal(0, runner.Calls);
        var d = await created.Content.ReadFromJsonAsync<JsonElement>(); var id = d.GetProperty("id").GetGuid();
        var question = "如何改善訂單 API？\n希望先討論架構";
        var first = await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = 0, message = question, selection = new { provider = "codex", model = "first", reasoningEffort = "high" } });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var saved = (await store().GetDiscussionAsync(id, default))!;
        Assert.Equal("如何改善訂單 API？", saved.Title); Assert.Equal("first", saved.Messages[1].Selection!.Model);
        var second = await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = saved.Version, message = "換個角度分析", selection = new { provider = "antigravity", model = "second", reasoningEffort = "low" } });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        saved = (await store().GetDiscussionAsync(id, default))!;
        Assert.Equal(4, saved.Messages.Length); Assert.Equal("first", saved.Messages[1].Selection!.Model);
        Assert.Equal("antigravity", saved.Messages[3].Selection!.Provider); Assert.Equal("low", saved.Messages[3].Selection!.ReasoningEffort);
        Assert.Contains(question, runner.Transcript); Assert.Contains(saved.Messages[1].Content, runner.Transcript);
        Assert.Equal("如何改善訂單 API？", saved.Title);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/discussions/{id}/title", new { title = "訂單架構", expectedVersion = saved.Version })).StatusCode);
        saved = (await store().GetDiscussionAsync(id, default))!; Assert.False(saved.AutoTitle); Assert.Equal("訂單架構", saved.Title);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/discussions/{id}/title", new { title = "過期操作", expectedVersion = 0 })).StatusCode);
    }

    [Fact] public async Task Background_reply_can_be_stopped_and_stale_operations_cannot_stop_another_turn()
    {
        var d = await CreateDiscussion(); var id = d.GetProperty("id").GetGuid(); runner.Block = true;
        var response = await client.PostAsJsonAsync($"/api/discussions/{id}/messages?background=1", new { expectedVersion = 0, message = "等待回覆", selection = new { provider = "antigravity" } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var active = (await store().GetDiscussionAsync(id, default))!;
        Assert.Single(active.Messages); Assert.NotNull(active.ActiveOperation);
        var progress = await client.GetFromJsonAsync<JsonElement>($"/api/discussions/{id}/progress");
        Assert.Equal("正在分析", progress.GetProperty("content").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/discussions/{id}/stop", new { operationId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = active.Version, message = "重複" })).StatusCode);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/discussions/{id}/stop", new { operationId = active.ActiveOperation })).StatusCode);
        for (var i = 0; i < 100; i++) { active = (await store().GetDiscussionAsync(id, default))!; if (active.ActiveOperation is null) break; await Task.Delay(20); }
        Assert.Null(active.ActiveOperation); Assert.Contains("已停止", active.Error);
        Assert.Contains("回覆已停止", active.Messages.Last().Content);
        runner.Block = false;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = active.Version, message = "繼續" })).StatusCode);
        Assert.Equal(2, runner.Calls);
    }

    [Fact] public async Task Invalid_selection_does_not_save_a_message_or_start_a_model()
    {
        var d = await CreateDiscussion(); var id = d.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/discussions/{id}/messages", new { expectedVersion = 0, message = "Hello", selection = new { provider = "invalid" } })).StatusCode);
        Assert.Empty((await store().GetDiscussionAsync(id, default))!.Messages); Assert.Equal(0, runner.Calls);
    }
    [Fact] public async Task Web_permission_reply_requires_same_origin_and_exact_active_operation()
    {
        var d = await CreateDiscussion(); var id = d.GetProperty("id").GetGuid();
        var operations = factory.Services.GetRequiredService<DevFlow.Api.DiscussionOperations>();
        var operation = operations.Start(id, Guid.NewGuid(), new("antigravity"), default);
        try
        {
            using var args = JsonDocument.Parse("{\"CommandLine\":\"Get-ChildItem -Directory\"}");
            var waiting = operation.RequestPermissionAsync("run_command", args.RootElement, directory, default);
            var request = operation.Permissions.Single();
            var progress = await client.GetFromJsonAsync<JsonElement>($"/api/discussions/{id}/progress");
            Assert.Equal(request.Id, progress.GetProperty("permissions")[0].GetProperty("id").GetGuid());
            var route = $"/api/discussions/{id}/permissions/{request.Id}";
            client.DefaultRequestHeaders.Remove("X-DevFlow-Action");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, new { operationId = operation.Id, allow = true })).StatusCode);
            client.DefaultRequestHeaders.Add("X-DevFlow-Action", "1");
            client.DefaultRequestHeaders.Add("Origin", "https://other.example");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, new { operationId = operation.Id, allow = true })).StatusCode);
            client.DefaultRequestHeaders.Remove("Origin");
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route, new { operationId = Guid.NewGuid(), allow = true })).StatusCode);
            Assert.False(waiting.IsCompleted);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(route, new { operationId = operation.Id, allow = false })).StatusCode);
            Assert.False(await waiting);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route, new { operationId = operation.Id, allow = true })).StatusCode);
        }
        finally { operations.Finish(id, operation); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Background_discussion_waits_for_web_decision_and_resumes_the_same_hook(bool allow)
    {
        await store().SavePermissionPolicyAsync(new(AutomationEnabled: false), 0);
        var d = await CreateDiscussion(); var id = d.GetProperty("id").GetGuid(); runner.UseHook = true;
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/discussions/{id}/messages?background=1",
            new { expectedVersion = 0, message = "確認工具請求", selection = new { provider = "antigravity" } })).StatusCode);
        JsonElement progress = default; Guid requestId = Guid.Empty, operationId = Guid.Empty;
        for (var i = 0; i < 300; i++)
        {
            progress = await client.GetFromJsonAsync<JsonElement>($"/api/discussions/{id}/progress");
            if (progress.GetProperty("permissions").GetArrayLength() > 0)
            {
                requestId = progress.GetProperty("permissions")[0].GetProperty("id").GetGuid();
                operationId = progress.GetProperty("operationId").GetGuid(); break;
            }
            await Task.Delay(20);
        }
        Assert.NotEqual(Guid.Empty, requestId);
        Assert.Single((await store().GetDiscussionAsync(id, default))!.Messages);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/discussions/{id}/permissions/{requestId}", new { operationId, allow })).StatusCode);
        Discussion saved = null!;
        for (var i = 0; i < 300; i++)
        {
            saved = (await store().GetDiscussionAsync(id, default))!;
            if (saved.ActiveOperation is null) break; await Task.Delay(20);
        }
        Assert.Null(saved.ActiveOperation); Assert.Equal(1, runner.Calls);
        if (allow) { Assert.Equal(2, saved.Messages.Length); Assert.Null(saved.Error); }
        else { Assert.Single(saved.Messages); Assert.Contains("你已拒絕", saved.Error); }
        Assert.False(File.Exists(Path.Combine(directory, ".agents", "hooks.json")));
    }

    private SqliteTaskStore store() => factory.Services.GetRequiredService<SqliteTaskStore>();
    private sealed class ReadOnlyWorkspace : IWorkspaceManager
    {
        public Task<Workspace> CreateAsync(ProjectDefinition p, Guid id, CancellationToken ct) => Task.FromResult(new Workspace(p.RepositoryPath, p.BaseBranch=="main"?"base-commit":p.BaseBranch));
        public Task AssertUnchangedAsync(Workspace w, CancellationToken ct) => Task.CompletedTask;
        public Task<string> SnapshotAsync(Workspace w, CancellationToken ct) => throw new InvalidOperationException("Discussion must never commit.");
    }
    private sealed class ReadOnlyRunner : IPlanningRunner
    {
        public int Calls; public bool Fail, Block, UseHook; public string? Transcript, Instruction, Response;
        public PromptImage[]? Images;
        public string? WorkspaceBase;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> PlanAsync(WorkTask task, Workspace w, Guid id, CancellationToken ct)
        {
            Calls++; if (Fail) throw new InvalidOperationException("Provider unavailable");
            Transcript = task.Description;
            Instruction = task.ReadOnlyInstruction;
            Images=task.InputImages;
            WorkspaceBase=w.BaseCommit;
            if (UseHook)
            {
                // Hook installation now belongs to RoutedCliRunner. This fake runner exercises
                // that same hook with the operation supplied by the API's common permission scope.
                await using var permissionHook = await DevFlow.Runners.NativePermissionHook.CreateAsync(
                    CliPermissions.Current!, w.Path, Path.Combine(w.Path, id + "-permissions.jsonl"), ct,
                    typeof(DevFlow.Api.AntigravityPermissionHook).Assembly.Location);
                using var hooks = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(w.Path, ".agents", "hooks.json"), ct));
                var command = hooks.RootElement.EnumerateObject().Single(p => p.Name.StartsWith("devflow-web-approval-")).Value.GetProperty("PreToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
                var input = JsonSerializer.Serialize(new { conversationId = Guid.NewGuid(), workspacePaths = new[] { w.Path }, stepIdx = 1,
                    toolCall = new { name = "run_command", args = new { CommandLine = "Get-ChildItem -Directory", Cwd = w.Path } } });
                var result = OperatingSystem.IsWindows()
                    ? await new ProcessRunner().RunAsync("cmd.exe", ["/d", "/s", "/c", command], w.Path, input, ct)
                    : await new ProcessRunner().RunAsync("/bin/sh", ["-c", command], w.Path, input, ct);
                using var decision = JsonDocument.Parse(result.StandardOutput);
                if (decision.RootElement.GetProperty("decision").GetString() != "auto_approve") throw new InvalidOperationException("工具權限被拒絕");
                return "已收到本次工具核准。";
            }
            if (Block)
            {
                ProcessOutput.Observer?.Invoke("{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"agent_response\",\"text_delta\":\"正在分析\"}}\n");
                Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return Response ?? (task.ReadOnlyInstruction!.StartsWith("Convert", StringComparison.Ordinal)
                ? "{\"tasks\":[{\"title\":\"Audit log\",\"description\":\"Add audit logs\",\"acceptanceCriteria\":\"Events persist\",\"openQuestions\":[\"What is retention?\"]}]}"
                : "We can add an audit log. How long should records be kept?");
        }
    }
}
