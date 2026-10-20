namespace DevFlow.Core;

public sealed partial class ExecutionCoordinator
{
    private async Task ExecuteWorkflowAsync(TaskClaim claim, ProjectDefinition project, Workspace workspace, string executionBase, TaskActivityReporter activity, CancellationToken ct)
    {
        var head = workspace.BaseCommit;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var task = await store.BeginWorkflowStepAsync(claim.Run.Id, ct);
            if (task.Status == TaskStatus.Cancelled) throw new OperationCanceledException("Task cancelled.");
            var index = task.StepResults?.Length ?? 0;
            var step = task.Workflow!.Steps!.ElementAtOrDefault(index);
            if (step is null) break;
            var result = new WorkflowStepResult(step.Id, step.Name, step.Kind, "Running", claim.Run.Id,
                head, null, step.Selection, DateTimeOffset.UtcNow);
            try
            {
                await activity.StageAsync("開始步驟 · " + step.Name, ct);
                var stepBase = workspace with { BaseCommit = head };
                var policy = task.Policy ?? new ExecutionPolicy();
                var stepPolicy = new ExecutionPolicy(policy.Tdd, step.AllowedPaths);
                if ((policy.AllowedPaths is { Length: > 0 } || step.AllowedPaths is { Length: > 0 }) && guard is null)
                    throw new InvalidOperationException("An edit scope guard is required.");
                var scopedTask = task with { Title = task.Title + " · " + step.Name,
                    AcceptanceCriteria = step.AcceptanceCriteria ?? task.AcceptanceCriteria,
                    TaskRules = task.TaskRules + "\nCurrent workflow step: " + step.Name + "\n" + step.Instructions +
                        "\nOnly perform this step. Completed steps and their summaries:\n" +
                        string.Join("\n", (task.StepResults ?? []).Select(r => r.Name + ": " + r.Summary)),
                    Policy = step.AllowedPaths is { Length: > 0 } ? stepPolicy : policy,
                    Ai = new(step.Selection, step.Selection), ArtifactStage = "Step-" + step.Id.ToString("N") };
                var validator = validation is IStepValidationRunner scoped ? scoped.ForStep(step.Id) : validation;
                string? summary = null;
                if (step.Kind == "Implementation")
                {
                    await activity.StageAsync(step.Name + " · 基準測試", ct);
                    var baseline = await ValidateStepAsync(validator, project, stepBase, claim, step, TddPhase.Baseline, ct);
                    if (baseline.ExitCode != 0 || baseline.Failed != 0) throw new InvalidOperationException("Baseline validation failed.");
                    var validationEvidence = new List<string> { $"Baseline: total {baseline.Total}, failed {baseline.Failed}" };
                    string[] red = [];
                    foreach (var phase in policy.Tdd ? new[] { TddPhase.Red, TddPhase.Green, TddPhase.Refactor } : [TddPhase.Implementation])
                    {
                        await store.AppendEventAsync(task.Id, "PhaseStarted", step.Name + " · " + phase, ct);
                        await activity.StageAsync(step.Name + " · AI 實作 · " + phase, ct);
                        await agent.ExecuteAsync(scopedTask, stepBase, claim.Run.Id, phase, ct);
                        await activity.StageAsync(step.Name + " · 檢查變更與測試 · " + phase, ct);
                        if (guard is not null)
                        {
                            await guard.CheckAsync(stepBase, policy, ct);
                            await guard.CheckAsync(stepBase, stepPolicy, ct);
                        }
                        var evidence = await ValidateStepAsync(validator, project, stepBase, claim, step, phase, ct);
                        validationEvidence.Add($"{phase}: exit {evidence.ExitCode}, total {evidence.Total}, failed {evidence.Failed}; passed IDs: {string.Join(", ", evidence.PassedTests.Take(100))}; failed IDs: {string.Join(", ", evidence.FailedTests.Take(100))}");
                        if (phase == TddPhase.Red)
                        {
                            if (evidence.ExitCode == 0 || evidence.Total == 0 || evidence.Failed == 0 || evidence.FailedTests.Length == 0)
                                throw new InvalidOperationException("Red requires actual failing tests; build errors do not qualify.");
                            red = evidence.FailedTests;
                        }
                        else if (evidence.ExitCode != 0 || evidence.Total == 0 || evidence.Failed != 0 ||
                            baseline.PassedTests.Except(evidence.PassedTests, StringComparer.Ordinal).Any() ||
                            red.Except(evidence.PassedTests, StringComparer.Ordinal).Any())
                            throw new InvalidOperationException(phase + " must pass, including baseline and Red tests.");
                    }
                    await activity.StageAsync(step.Name + " · 保存成果", ct);
                    head = await workspaces.SnapshotAsync(stepBase, ct);
                    summary = "實作與驗證完成；成果 " + head + "\n" + string.Join("\n", validationEvidence);
                    if (reviewer is not null && diffs is not null && task.Ai?.Planning is { } plannerSelection)
                    {
                        await activity.StageAsync(step.Name + " · 規劃 AI 檢查與問題分流", ct);
                        var stepDiff = await diffs.ReadAsync(project, claim.Run with { BaseCommit = stepBase.BaseCommit, ResultCommit = head, FinishedAt = DateTimeOffset.UtcNow }, ct);
                        var supervisionTask = scopedTask with
                        {
                            Ai = new(plannerSelection, scopedTask.Ai?.Implementation),
                            Rules = [],
                            ReadOnlyInstruction =
                                "你是本任務的「規劃與總指揮 AI」。請檢查目前步驟的實作成果與疑問，並根據需求、核准計畫與規則庫進行判斷。\n" +
                                "請只回傳 JSON：{\"verdict\":\"Pass|ResolvedWithinRules|NeedsUserConfirmation\",\"summary\":\"...\"}\n" +
                                "- Pass：此步驟已符合計畫與規則，無待決問題，可直接進入下一步。\n" +
                                "- ResolvedWithinRules：實作有未完成細節或疑問，但完全在既有規則與核准計畫範圍內；請在 summary 直接寫出給實作 AI 的明確修改指示，系統會自動指揮實作 AI 修正，不打擾使用者。\n" +
                                "- NeedsUserConfirmation：遇到超出規則庫、與既有需求衝突、或規格未定義的問題；請在 summary 清楚列出需要使用者決定與確認的問題，系統將暫停並交由使用者確認。\n" +
                                TaskInstructions.For(scopedTask, "Acceptance") + "\nApproved plan:\n" + task.Plan?.Text +
                                "\nValidation:\n" + string.Join("\n", validationEvidence) + "\nDiff:\n" + stepDiff.Patch
                        };
                        var supervisionVerdict = stepDiff.Patch.Length > 120000
                            ? new AcceptanceResult("Pass", "成果已通過測試。")
                            : TaskInstructions.ParseJson<AcceptanceResult>(await reviewer.PlanAsync(supervisionTask, workspace with { BaseCommit = head }, claim.Run.Id, ct));
                        await workspaces.AssertUnchangedAsync(workspace with { BaseCommit = head }, ct);
                        if (supervisionVerdict.Verdict == "ResolvedWithinRules")
                        {
                            var guidanceText = WorkTask.Required(supervisionVerdict.Summary, 12000);
                            await store.AppendEventAsync(task.Id, "PlannerGuided", $"{step.Name} · 規則內自動指導：{guidanceText}", ct);
                            await activity.StageAsync(step.Name + " · 依規劃 AI 指導自動修正", ct);
                            var guidedTask = scopedTask with { TaskRules = scopedTask.TaskRules + "\n【規劃 AI 規則內直接指導】：" + guidanceText };
                            var guidedBase = workspace with { BaseCommit = head };
                            await agent.ExecuteAsync(guidedTask, guidedBase, claim.Run.Id, TddPhase.Implementation, ct);
                            if (guard is not null)
                            {
                                await guard.CheckAsync(guidedBase, policy, ct);
                                await guard.CheckAsync(guidedBase, stepPolicy, ct);
                            }
                            var guidedEvidence = await ValidateStepAsync(validator, project, guidedBase, claim, step, TddPhase.Implementation, ct);
                            if (guidedEvidence.ExitCode != 0 || guidedEvidence.Total == 0 || guidedEvidence.Failed != 0)
                                throw new InvalidOperationException("規劃 AI 指揮修正後測試未通過。");
                            head = await workspaces.SnapshotAsync(guidedBase, ct);
                            summary = $"規劃 AI 已於規則內直接回覆並指揮完成修正（{guidanceText}）；成果 {head}";
                        }
                        else if (supervisionVerdict.Verdict is "NeedsUserConfirmation" or "Uncertain")
                        {
                            var questionText = WorkTask.Required(supervisionVerdict.Summary, 12000);
                            summary = "⚠️ 超出規則，需你確認：" + questionText;
                            await store.RecordWorkflowStepAsync(claim.Run.Id, result with
                            {
                                State = "Completed",
                                ResultCommit = head,
                                Summary = summary,
                                FinishedAt = DateTimeOffset.UtcNow
                            }, ct);
                            await store.PauseWorkflowAsync(claim.Run.Id, ct);
                            return;
                        }
                    }
                }
                else if (step.Kind == "Validation")
                {
                    await activity.StageAsync(step.Name + " · 整合測試", ct);
                    var evidence = await ValidateStepAsync(validator, project, stepBase, claim, step, TddPhase.Implementation, ct);
                    if (evidence.ExitCode != 0 || evidence.Total == 0 || evidence.Failed != 0)
                        throw new InvalidOperationException("Integration validation failed or produced no tests.");
                    await workspaces.AssertUnchangedAsync(stepBase, ct);
                    summary = $"測試通過：{evidence.Total} 個測試。";
                }
                else if (step.Kind == "Review")
                {
                    await activity.StageAsync(step.Name + " · AI 審查", ct);
                    if (reviewer is null || diffs is null) throw new InvalidOperationException("AI acceptance services are not configured.");
                    var diff = await diffs.ReadAsync(project, claim.Run with { BaseCommit = executionBase, ResultCommit = head,
                        FinishedAt = DateTimeOffset.UtcNow }, ct);
                    var reviewTask = scopedTask with { Rules = [], ReadOnlyInstruction =
                        "Review the exact current implementation. Do not edit, implement, commit or change files. Return ONLY JSON with verdict (Pass, NeedsChanges, Uncertain) and summary. Check the requirements, approved plan, applicable rules and current step instructions.\n" +
                        TaskInstructions.For(scopedTask, "Acceptance") + "\nApproved plan:\n" + task.Plan?.Text +
                        "\nIndependent validation evidence:\n" + string.Join("\n", (task.StepResults ?? []).Select(r => r.Name + ": " + r.Summary)) +
                        "\nBase: " + executionBase + "\nResult: " + head + "\nExact diff:\n" + diff.Patch };
                    var verdict = diff.Patch.Length > 120000 ? new AcceptanceResult("Uncertain", "Diff exceeds review input limit.") :
                        TaskInstructions.ParseJson<AcceptanceResult>(await reviewer.PlanAsync(reviewTask, stepBase, claim.Run.Id, ct));
                    if (verdict.Verdict is not ("Pass" or "NeedsChanges" or "Uncertain")) throw new ArgumentException("Invalid AI verdict.");
                    await workspaces.AssertUnchangedAsync(stepBase, ct);
                    summary = WorkTask.Required(verdict.Summary, 20000);
                    await store.RecordAiReviewAsync(claim.Run.Id, new(Guid.NewGuid(), 1, index + 1, step.Selection!, head,
                        verdict.Verdict, summary, DateTimeOffset.UtcNow), ct);
                    if (verdict.Verdict != "Pass") throw new InvalidOperationException("AI 審查未通過：" + summary);
                }
                else summary = step.Instructions ?? "等待人工確認。";
                await store.RecordWorkflowStepAsync(claim.Run.Id, result with { State = "Completed", ResultCommit = head,
                    Summary = summary, FinishedAt = DateTimeOffset.UtcNow }, ct);
                if (step.WaitForApproval || step.Kind == "Approval")
                {
                    await store.PauseWorkflowAsync(claim.Run.Id, ct);
                    return;
                }
            }
            catch (Exception ex)
            {
                var current = await store.GetTaskAsync(task.Id, CancellationToken.None);
                if (current?.Status == TaskStatus.Running && current.ActiveStepId == step.Id)
                    await store.RecordWorkflowStepAsync(claim.Run.Id, result with { State = "Failed", Summary = ex.Message,
                        FinishedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
                throw;
            }
        }
        await store.CompleteRunAsync(claim.Run.Id, true, workspace.Path, executionBase, head, null, ct);
    }
    private async Task<ValidationEvidence> ValidateStepAsync(IValidationRunner validator, ProjectDefinition project, Workspace workspace,
        TaskClaim claim, WorkflowStep step, TddPhase phase, CancellationToken ct)
    {
        var evidence = await validator.ValidateAsync(project, workspace, claim.Run.Id, phase, ct);
        await store.AppendEventAsync(claim.Task.Id, "StepValidation", $"{step.Name} · {phase}: exit {evidence.ExitCode}, total {evidence.Total}, failed {evidence.Failed}", ct);
        return evidence;
    }
}
