using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Status=DevFlow.Core.TaskStatus;
namespace DevFlow.Tests;

public sealed class ConversationDeliveryTests:IAsyncLifetime
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"devflow-delivery-"+Guid.NewGuid());
    private WebApplicationFactory<Program> factory=null!;
    private HttpClient client=null!;
    private SqliteTaskStore Store=>factory.Services.GetRequiredService<SqliteTaskStore>();
    private readonly Planner planner=new();
    private readonly Git git=new();
    private ProjectDefinition project=null!;
    private static string Base=>new('a',40);
    private static string Result=>new('b',40);
    private const string Png="iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=";
    public async Task InitializeAsync(){
        Directory.CreateDirectory(root);
        factory=new WebApplicationFactory<Program>().WithWebHostBuilder(b=>b.ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{
            ["DevFlow:DatabasePath"]=Path.Combine(root,"test.db"),["DevFlow:ArtifactsRoot"]=Path.Combine(root,"artifacts")
        })).ConfigureTestServices(s=>{s.AddSingleton<IPlanningRunner>(planner);s.AddSingleton<IWorkspaceManager,WorkspaceFixture>();s.AddSingleton<IAgentCatalog,Catalog>();s.AddSingleton<IGitMergeService>(git);s.AddSingleton(new DiscussionImageStore(Path.Combine(root,"artifacts")));}));
        client=factory.CreateClient();client.DefaultRequestHeaders.Add("X-DevFlow-Action","1");
        project=new(Guid.NewGuid(),"Demo",root,"main",["build"]);await Store.AddProjectAsync(project);
    }
    public async Task DisposeAsync(){client.Dispose();await factory.DisposeAsync();Directory.Delete(root,true);}
    private static AgentDecision Proposal()=>new("先修正並驗證。","Propose","改善畫面","保留原功能並修正畫面","建置通過，按鈕正常","修正樣式、執行建置並驗證。",
        [new("畫面實作","修正樣式",new("codex","manager"),"處理實作"),new("AI 驗收","檢查需求及圖片",new("claude","strong"),"獨立驗收",Kind:"Review")]);
    private async Task<Discussion> NewDiscussion(){
        var r=await client.PostAsJsonAsync("/api/discussions",new{projectId=project.Id,agentMode=true,selection=new{provider="codex",model="manager"}});r.EnsureSuccessStatusCode();return(await r.Content.ReadFromJsonAsync<Discussion>())!;
    }
    private async Task<Discussion> Send(Discussion d,string message){
        var r=await client.PostAsJsonAsync($"/api/discussions/{d.Id}/messages",new{expectedVersion=d.Version,message});r.EnsureSuccessStatusCode();return(await r.Content.ReadFromJsonAsync<Discussion>())!;
    }
    private async Task<(WorkTask Task,TaskRun Run,Discussion Discussion)> Seed(){
        var d=await NewDiscussion();
        var step=new WorkflowStep(Guid.NewGuid(),"畫面",Selection:new("codex","manager"));
        var task=WorkTask.Create(project.Id,"原功能","原本需求","原本條件").SetPlan("實作畫面並驗證。").ApprovePlan() with{SourceDiscussionId=d.Id,Ai=new(d.Selection,step.Selection),Policy=new(false,[]),Workflow=new(Steps:[step],SimpleMode:true),ExecutionBaseCommit=Base};
        await Store.AddTaskAsync(task);var claim=(await Store.ClaimNextAsync())!;
        await Store.BeginWorkflowStepAsync(claim.Run.Id);await Store.RecordWorkflowStepAsync(claim.Run.Id,new(step.Id,step.Name,"Implementation","Completed",claim.Run.Id,Result,"完成",step.Selection,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow));
        await Store.CompleteRunAsync(claim.Run.Id,true,root,Base,Result,null);
        task=(await Store.GetTaskAsync(task.Id))!;d=await Store.EnsureTaskConversationAsync(task.Id,default);
        return(task,(await Store.GetRunAsync(claim.Run.Id))!,d);
    }
    [Fact] public async Task Feedback_and_confirmed_revision_continue_the_same_task_from_the_tested_result(){
        var (task,run,d)=await Seed();
        var opened=await client.PostAsJsonAsync($"/api/tasks/{task.Id}/feedback",new{expectedVersion=task.Version,runId=run.Id,resultCommit=Result});opened.EnsureSuccessStatusCode();d=(await opened.Content.ReadFromJsonAsync<Discussion>())!;
        Assert.Equal(new(task.Id,run.Id,Result),d.FeedbackContext);
        planner.Response=JsonSerializer.Serialize(Proposal());d=await Send(d,"按鈕跑版，請修正。");
        var proposal=d.Messages.Last().AgentProposal!;Assert.Equal(task.Id,proposal.TargetTaskId);Assert.Equal(task.Version,proposal.TargetTaskVersion);Assert.Equal(Result,planner.BaseCommit);
        Assert.Equal(run.Id,d.Messages[^2].FeedbackContext!.RunId);Assert.Null(d.FeedbackContext);
        Assert.Equal(Status.AwaitingReview,(await Store.GetTaskAsync(task.Id))!.Status);
        d=await Send(d,"好，照這份計畫做");
        var updated=Assert.Single(await Store.ListTasksAsync());Assert.Equal(task.Id,updated.Id);Assert.Equal(Status.Queued,updated.Status);Assert.Equal(Result,updated.RevisionCommit);Assert.Equal(Base,updated.ExecutionBaseCommit);Assert.Empty(updated.StepResults!);
        Assert.Equal(2,updated.Plan!.Revision);Assert.Equal("Review",updated.Workflow!.Steps![1].Kind);Assert.Equal(task.Id,d.ActiveTaskId);
        Assert.Equal(2,(await Store.ListPlansAsync(task.Id)).Count);Assert.Equal(Result,(await Store.GetRunAsync(run.Id))!.ResultCommit);
    }
    [Fact] public async Task Changed_task_version_invalidates_an_old_revision_proposal(){
        var (task,_,d)=await Seed();planner.Response=JsonSerializer.Serialize(Proposal());d=await Send(d,"修正畫面");
        await Store.SaveTaskAsync(task with{Version=task.Version+1},task.Version);
        var response=await client.PostAsJsonAsync($"/api/discussions/{d.Id}/messages",new{expectedVersion=d.Version,message="Proceed"});Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
        Assert.Single(await Store.ListTasksAsync());Assert.Equal(Status.AwaitingReview,(await Store.GetTaskAsync(task.Id))!.Status);
    }
    [Fact] public async Task Feedback_questions_preserve_review_state_and_reject_a_wrong_run_or_commit(){
        var (task,run,d)=await Seed();
        var bad=await client.PostAsJsonAsync($"/api/tasks/{task.Id}/feedback",new{expectedVersion=task.Version,runId=run.Id,resultCommit=Base});Assert.Equal(HttpStatusCode.Conflict,bad.StatusCode);
        planner.Response=JsonSerializer.Serialize(new AgentDecision("此修改只影響樣式。"));d=await Send(d,"這會影響資料嗎？");
        Assert.Equal(Status.AwaitingReview,(await Store.GetTaskAsync(task.Id))!.Status);Assert.Null(d.Messages.Last().AgentProposal);
    }
    [Fact] public async Task Human_acceptance_and_merge_are_one_action_and_retries_do_not_merge_twice(){
        var (task,run,_)=await Seed();var input=new{expectedVersion=task.Version,runId=run.Id,resultCommit=Result,expectedTargetCommit=Base};
        (await client.PostAsJsonAsync($"/api/tasks/{task.Id}/accept-and-merge",input)).EnsureSuccessStatusCode();
        Assert.Equal(Status.Merged,(await Store.GetTaskAsync(task.Id))!.Status);Assert.Single(await Store.ListReviewsAsync(task.Id));Assert.Equal(1,git.Calls);
        (await client.PostAsJsonAsync($"/api/tasks/{task.Id}/accept-and-merge",input)).EnsureSuccessStatusCode();Assert.Equal(1,git.Calls);
    }
    [Fact] public async Task Changed_target_is_rejected_before_recording_acceptance(){
        var (task,run,_)=await Seed();
        var response=await client.PostAsJsonAsync($"/api/tasks/{task.Id}/accept-and-merge",new{expectedVersion=task.Version,runId=run.Id,resultCommit=Result,expectedTargetCommit=new string('c',40)});
        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);Assert.Empty(await Store.ListReviewsAsync(task.Id));Assert.Equal(0,git.Calls);
    }
    [Fact] public async Task Integration_from_the_simplified_flow_preserves_task_identity_and_previous_result(){
        var (task,run,d)=await Seed();var target=new string('c',40);
        var integrated=await Store.CreateIntegrationTaskAsync(task.Id,task.Version,run.Id,Result,target,default,continueCurrent:true);
        Assert.Equal(task.Id,integrated.Id);Assert.Equal(d.Id,integrated.SourceDiscussionId);Assert.Single(await Store.ListTasksAsync());
        Assert.Equal(Status.Queued,integrated.Status);Assert.Equal(target,integrated.ExecutionBaseCommit);Assert.Equal(target,integrated.RevisionCommit);Assert.Equal(2,integrated.Plan!.Revision);
        Assert.Equal(run.Id,integrated.Integration!.RunId);Assert.Equal(Result,(await Store.GetRunAsync(run.Id))!.ResultCommit);
    }
    [Fact] public async Task Archived_task_navigation_and_feedback_do_not_rebind_the_current_task(){
        var (task,run,d)=await Seed();
        (await client.PostAsJsonAsync($"/api/tasks/{task.Id}/accept-and-merge",new{expectedVersion=task.Version,runId=run.Id,resultCommit=Result,expectedTargetCommit=Base})).EnsureSuccessStatusCode();
        planner.Response=JsonSerializer.Serialize(Proposal());d=await Send(d,"新增另一項功能");Assert.Null(d.Messages.Last().AgentProposal!.TargetTaskId);d=await Send(d,"Proceed");
        var current=d.ActiveTaskId;Assert.NotEqual(task.Id,current);
        var navigation=await client.PostAsJsonAsync($"/api/tasks/{task.Id}/conversation",new{});navigation.EnsureSuccessStatusCode();var source=(await navigation.Content.ReadFromJsonAsync<Discussion>())!;
        Assert.Equal(d.Id,source.Id);Assert.Equal(current,source.ActiveTaskId);
        var merged=(await Store.GetTaskAsync(task.Id))!;
        var feedback=await Store.OpenFeedbackAsync(task.Id,run.Id,Result,merged.Version,default);
        Assert.NotEqual(d.Id,feedback.Id);Assert.Equal(current,(await Store.GetDiscussionAsync(d.Id,default))!.ActiveTaskId);Assert.Equal(d.Id,(await Store.GetTaskAsync(task.Id))!.SourceDiscussionId);
    }
    [Fact] public async Task Confirmed_images_survive_persistence_and_load_for_worker_implementation_and_review(){
        var d=await NewDiscussion();planner.Response=JsonSerializer.Serialize(Proposal());
        var response=await client.PostAsJsonAsync($"/api/discussions/{d.Id}/messages",new{expectedVersion=d.Version,message="依圖片調整",images=new[]{new{name="畫面.png",mimeType="image/png",data=Png}}});response.EnsureSuccessStatusCode();d=(await response.Content.ReadFromJsonAsync<Discussion>())!;
        d=await Send(d,"Proceed");var task=Assert.Single(await Store.ListTasksAsync());Assert.Single(task.ImageReferences!);Assert.Null(task.InputImages);
        var loaded=await new TaskImageLoader(new DiscussionImageStore(Path.Combine(root,"artifacts"))).LoadAsync(task,default);
        Assert.Equal(Png,Assert.Single(loaded.InputImages!).Data);Assert.DoesNotContain(Png,JsonSerializer.Serialize(task));Assert.Equal("Review",task.Workflow!.Steps!.Last().Kind);
    }
    private sealed class Catalog:IAgentCatalog {public Task<AgentCandidate[]> ReadAsync(AiSelection primary,CancellationToken ct)=>Task.FromResult(new AgentCandidate[]{new(new("codex","manager")),new(new("claude","strong"))});}
    private sealed class Planner:IPlanningRunner {
        public string Response="",BaseCommit="";
        public Task<string> PlanAsync(WorkTask task,Workspace workspace,Guid id,CancellationToken ct){BaseCommit=workspace.BaseCommit;return Task.FromResult(Response);}
    }
    private sealed class WorkspaceFixture:IWorkspaceManager {
        public Task<Workspace> CreateAsync(ProjectDefinition p,Guid id,CancellationToken ct)=>Task.FromResult(new Workspace(p.RepositoryPath,p.BaseBranch));
        public Task<string> SnapshotAsync(Workspace w,CancellationToken ct)=>Task.FromResult(Result);
        public Task AssertUnchangedAsync(Workspace w,CancellationToken ct)=>Task.CompletedTask;
    }
    private sealed class Git:IGitMergeService {
        public int Calls;
        public Task<RepositoryState> InspectAsync(ProjectDefinition p,CancellationToken ct=default)=>Task.FromResult(new RepositoryState("main",Base,true));
        public Task<string> MergeAsync(ProjectDefinition p,TaskMerge merge,CancellationToken ct=default){Calls++;return Task.FromResult(Result);}
        public Task<MergeObservation> ObserveAsync(ProjectDefinition p,TaskMerge merge,CancellationToken ct=default)=>Task.FromResult(MergeObservation.Applied);
    }
}
