namespace DevFlow.Core;
public sealed partial class ExecutionCoordinator
{
    private async Task<WorkTask> RefreshManagedAsync(WorkTask task,CancellationToken ct)
    {
        if(task.Workflow?.SimpleMode!=true)return task;
        var current=await store.GetTaskAsync(task.Id,ct)??throw new MissingEntityException("Task not found.");
        var selected=ConversationAgent.ImplementationStep(current)?.Selection??task.Ai?.Implementation;
        return task with{Ai=new(current.Ai?.Planning,selected)};
    }
    private async Task ExecuteManagedAsync(WorkTask task,Workspace workspace,Guid runId,TddPhase phase,CancellationToken ct)
    {
        var tried=new HashSet<AiSelection>();
        var blockedProviders=new HashSet<string>();
        for(var attempt=0;;attempt++){
            var current=await RefreshManagedAsync(task,ct);var selection=AiSelection.Normalize(current.Ai?.Implementation);
            if(blockedProviders.Contains(selection.Provider))throw new SupervisorInputRequired("此工具的額度或頻率仍受限，同一工具換模型無法避開共用限制。請在對話選擇其他工具。");
            if(!tried.Add(selection))throw new SupervisorInputRequired("實作工具仍無法使用，主 AI 沒有選擇新的可用模型。請在對話討論替代方案。");
            try{await agent.ExecuteAsync(current,workspace,runId,phase,ct);return;}
            catch(InvalidOperationException ex)when(task.Delegation is not null&&(ex.Message.StartsWith("[Quota]")||ex.Message.StartsWith("[ModelUnavailable]"))){
                if(ex.Message.StartsWith("[Quota]"))blockedProviders.Add(selection.Provider);
                if(attempt>=2)throw new SupervisorInputRequired("已嘗試多個實作模型仍無法執行，請在對話確認額度或指定其他工具。\n"+ex.Message);
                var latest=await store.GetTaskAsync(task.Id,ct);var index=Array.FindIndex(latest!.Workflow!.Steps!,s=>s.Id==ConversationAgent.ImplementationStep(latest)?.Id);
                if(index<0||task.Delegation.Assignments[index].PinnedModel||task.Delegation.Assignments[index].Pinned&&ex.Message.StartsWith("[Quota]"))throw new SupervisorInputRequired("此工作已依你的指定交給 "+selection.Provider+"，目前額度或模型受限。請在對話決定是否改用其他工具。\n"+ex.Message);
                await SupervisorCorrectionAsync(current,workspace,runId,"實作 CLI 無法使用，請選擇不同候選模型；同一 provider 額度共用，不要只換同池模型。\n"+ex.Message,ct);
            }
        }
    }
}
