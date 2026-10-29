namespace DevFlow.Core;

public sealed partial class ExecutionCoordinator
{
    private static bool ValidationPassed(ValidationEvidence evidence, IEnumerable<string> required) =>
        evidence.ExitCode == 0 && evidence.Failed == 0 && (evidence.Kind != "DotnetTest" || evidence.Total > 0) &&
        !required.Except(evidence.PassedTests, StringComparer.Ordinal).Any();

    private async Task<ValidationEvidence> ValidateWithRepairsAsync(WorkTask task, ProjectDefinition project, Workspace workspace,
        Guid runId, TddPhase phase, IValidationRunner validator, IEnumerable<string> required,
        Func<CancellationToken, Task> checkScope, CancellationToken ct)
    {
        if(task.Workflow?.SimpleMode==true)
            return await ValidateSupervisedAsync(task,project,workspace,runId,phase,validator,required,checkScope,ct);
        var evidence = await validator.ValidateAsync(project, workspace, runId, phase, ct);
        string? previousError = null;
        // Environment failures, missing test evidence, and permission/agent errors do not
        // enter this loop. Baseline/Red remain independent TDD gates.
        for (var attempt = 1; phase is not (TddPhase.Baseline or TddPhase.Red) && attempt <= 2 &&
            !ValidationPassed(evidence, required) && evidence.ExitCode > 0 && !string.IsNullOrWhiteSpace(evidence.Error) &&
            evidence.FailureKind is null or "CommandFailed"; attempt++)
        {
            if (evidence.Error == previousError) break;
            previousError = evidence.Error;
            await store.AppendEventAsync(task.Id, "ValidationRepairStarted", $"{phase} · 自動修正 {attempt}/2：{evidence.Error}", ct);
            var repair = task with {
                ArtifactStage = task.ArtifactStage + "-Repair-" + phase + "-" + attempt,
                TaskRules = task.TaskRules + "\nValidation failed. Repair only the approved implementation within the existing edit scope. " +
                    "Preserve all baseline and Red tests; do not delete, skip, or weaken tests, change permissions, or install dependencies.\n" +
                    "Required test IDs: " + string.Join(", ", required.Take(100)) + "\nExact validation diagnostics:\n" + evidence.Error
            };
            await agent.ExecuteAsync(repair, workspace, runId, phase, ct);
            await checkScope(ct);
            var attemptValidator = validator is IValidationAttemptRunner scoped ? scoped.ForAttempt(attempt) : validator;
            evidence = await attemptValidator.ValidateAsync(project, workspace, runId, phase, ct);
            await store.AppendEventAsync(task.Id, "ValidationRepairFinished", $"{phase} · {attempt}/2 · exit {evidence.ExitCode}", ct);
        }
        return evidence;
    }

    private sealed class SupervisorInputRequired(string message) : Exception(message);
    private async Task<string> SupervisorCorrectionAsync(WorkTask task,Workspace workspace,Guid runId,string diagnostics,CancellationToken ct)
    {
        var latest=await store.GetTaskAsync(task.Id,ct);
        if(task.Workflow?.SimpleMode==true&&latest?.Ai?.Planning is {} primary)task=task with{Ai=new(primary,task.Ai?.Implementation)};
        if(reviewer is null || task.Ai?.Planning is null) throw new SupervisorInputRequired("主管模型未設定，無法自動處理驗證問題。");
        await store.AppendEventAsync(task.Id,"SupervisorReviewing","主管 AI 正在判斷驗證／驗收問題。",ct);
        var reviewTask=task with{Ai=new(task.Ai.Planning),ArtifactStage=task.ArtifactStage+"-Supervisor-"+Guid.NewGuid().ToString("N"),
            ReadOnlyInstruction="你是主管 AI，請判斷以下問題是否可在已核准計畫內修正。只能讀取，不能自行實作或授權額外權限。"+
                "只回傳 JSON：{\"verdict\":\"ResolvedWithinRules|NeedsUserConfirmation\",\"summary\":\"具體修正指示或待決問題\",\"implementation\":null}。"+
                "範圍內請提供實作 AI 可執行的明確指示；需求變更、超出計畫或需要額外授權則詢問使用者。不可要求移除或削弱測試。\n"+
                TaskInstructions.For(task,"Acceptance")+"\n核准計畫：\n"+task.Plan?.Text+"\n實際診斷：\n"+diagnostics+
                (task.Delegation is null?"":"\n自動分配資料："+System.Text.Json.JsonSerializer.Serialize(task.Delegation)+"\n若實作模型能力不足或額度受限，可在 implementation 填候選的 provider/model 升級或換工具；明確 pinned 不得變更，應解釋原因詢問使用者。不可替換主 AI、擴張權限或盲重試相同模型。")};
        var decision=TaskInstructions.ParseJson<AcceptanceResult>(await reviewer.PlanAsync(reviewTask,workspace,runId,ct));
        var guidance=WorkTask.Required(decision.Summary,12000);
        if(decision.Verdict=="NeedsUserConfirmation"||decision.Verdict=="Uncertain")throw new SupervisorInputRequired(guidance);
        if(decision.Verdict!="ResolvedWithinRules")throw new ArgumentException("主管必須提供範圍內修正指示或請求人工確認。");
        if(task.Delegation is not null&&decision.Implementation is {} next&&latest?.ActiveStepId is {} stepId&&store is IAutoDelegationStore managed){
            try{await managed.ChangeDelegatedModelAsync(task.Id,stepId,next,guidance,ct);}
            catch(Exception ex)when(ex is ConflictException or ArgumentException){throw new SupervisorInputRequired(ex.Message+"\n"+guidance);}
        }
        await store.AppendEventAsync(task.Id,"PlannerGuided",guidance,ct);return guidance;
    }
    private async Task<ValidationEvidence> ValidateSupervisedAsync(WorkTask task,ProjectDefinition project,Workspace workspace,Guid runId,TddPhase phase,
        IValidationRunner validator,IEnumerable<string> required,Func<CancellationToken,Task> checkScope,CancellationToken ct)
    {
        string? previous=null;var repeated=0;
        for(var attempt=0;;attempt++){
            ct.ThrowIfCancellationRequested();
            var runner=attempt>0&&validator is IValidationAttemptRunner attempts?attempts.ForAttempt(attempt):validator;
            var evidence=await runner.ValidateAsync(project,workspace,runId,phase,ct);
            if(ValidationPassed(evidence,required))return evidence;
            if(evidence.ExitCode<0||evidence.FailureKind is not (null or "CommandFailed"))return evidence;
            var diagnostic=evidence.Error??"既有測試未完整通過，不能略過或移除原測試。";
            repeated=diagnostic==previous?repeated+1:1;previous=diagnostic;
            if(repeated>=3)throw new SupervisorInputRequired("連續三次出現相同驗證問題，已暫停以避免無效循環。\n"+diagnostic);
            var guidance=await SupervisorCorrectionAsync(task,workspace,runId,diagnostic,ct);
            var repair=task with{ArtifactStage=task.ArtifactStage+"-Repair-"+phase+"-"+(attempt+1),
                TaskRules=task.TaskRules+"\n主管核准計畫內的修正指示：\n"+guidance+"\n保留既有測試與驗收條件，不得更改權限或自行安裝額外套件。"};
            await store.AppendEventAsync(task.Id,"ValidationRepairStarted","主管已分派修正，完成後重新驗證。",ct);
            await ExecuteManagedAsync(repair,workspace,runId,TddPhase.Implementation,ct);
            await checkScope(ct);
        }
    }
}
