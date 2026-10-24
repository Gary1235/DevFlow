namespace DevFlow.Core;
public sealed partial class ExecutionCoordinator
{
    private async Task ExecuteIntegrationAsync(TaskClaim claim, ProjectDefinition project, Workspace workspace, string executionBase, TaskActivityReporter activity, CancellationToken ct)
    {
        var source = claim.Task.Integration!;
        if (workspaces is not IIntegrationWorkspaceManager merger) throw new InvalidOperationException("整合服務未設定。");
        var profiles = source.ValidationProfileIds.Length > 0 ? source.ValidationProfileIds.Select(id => project with { DefaultValidationProfileId = id == "legacy-dotnet-test" ? null : id }).ToArray() : [project];
        var baselineIds = new Dictionary<string, string[]>();
        IValidationRunner Validator(ProjectDefinition selected, bool final = false) => validation is IStepValidationRunner scoped
            ? scoped.ForStep(new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes((final ? "Final:" : "") + (selected.DefaultValidationProfileId ?? "legacy")))[..16])) : validation;
        foreach (var selected in profiles) {
            await activity.StageAsync("整合前驗證最新基底", ct);
            var evidence = await Validator(selected).ValidateAsync(selected, workspace, claim.Run.Id, TddPhase.Baseline, ct);
            if (!ValidationPassed(evidence, [])) throw new InvalidOperationException("最新基底驗證失敗：" + evidence.Error);
            baselineIds[selected.DefaultValidationProfileId ?? "legacy"] = evidence.PassedTests;
        }
        await activity.StageAsync("在隔離副本整合原成果", ct);
        var conflicts = await merger.IntegrateAsync(workspace, source.ResultCommit, ct);
        var task = TaskInstructions.WithValidationCommands(claim.Task with { TaskRules = claim.Task.TaskRules +
            "\nSource result: " + source.ResultCommit + "\nTarget base: " + workspace.BaseCommit +
            "\nConflicted files: " + string.Join(", ", conflicts) +
            "\nVerify the merged implementation and resolve these conflicts. Preserve both target updates and original acceptance criteria; do not weaken tests." }, project);
        if (task.Policy?.AllowedPaths is { Length: > 0 } && guard is null) throw new InvalidOperationException("An edit scope guard is required.");
        await agent.ExecuteAsync(task, workspace, claim.Run.Id, TddPhase.Implementation, ct);
        async Task Check(CancellationToken token) {
            if (guard is not null) await guard.CheckAsync(workspace, task.Policy ?? new(), token);
        }
        await Check(ct);
        var evidenceSummary = new List<string>();
        foreach (var selected in profiles) {
            var evidence = await ValidateWithRepairsAsync(task, selected, workspace, claim.Run.Id, TddPhase.Implementation, Validator(selected),
                baselineIds[selected.DefaultValidationProfileId ?? "legacy"], Check, ct);
            if (!ValidationPassed(evidence, baselineIds[selected.DefaultValidationProfileId ?? "legacy"])) throw new InvalidOperationException("整合後驗證失敗：" + evidence.Error);
            evidenceSummary.Add($"{selected.DefaultValidationProfileId}: exit {evidence.ExitCode}, total {evidence.Total}, failed {evidence.Failed}");
        }
        // A correction for the last profile can invalidate an earlier one.
        foreach (var selected in profiles) {
            var final = await Validator(selected, true).ValidateAsync(selected, workspace, claim.Run.Id, TddPhase.Implementation, ct);
            if (!ValidationPassed(final, baselineIds[selected.DefaultValidationProfileId ?? "legacy"])) throw new InvalidOperationException("整合最後一輪完整驗證失敗：" + final.Error);
        }
        var commit = await workspaces.SnapshotAsync(workspace, ct);
        var reviewStage = 0;
        foreach (var selection in source.Reviewers ?? claim.Task.Workflow?.Reviewers ?? []) {
            reviewStage++;
            if (reviewer is null || diffs is null) throw new InvalidOperationException("整合成果的 AI 審查服務未設定。");
            var diff = await diffs.ReadAsync(project, claim.Run with { BaseCommit = executionBase, ResultCommit = commit, FinishedAt = DateTimeOffset.UtcNow }, ct);
            var reviewTask = task with { Ai = new(selection, task.Ai?.Implementation), ArtifactStage = "IntegrationReview-" + reviewStage, ReadOnlyInstruction =
                "Review this integration against the original requirements and latest base. Do not edit. Return ONLY JSON {\"verdict\":\"Pass|NeedsChanges|Uncertain\",\"summary\":\"...\"}.\n" +
                TaskInstructions.For(task, "Acceptance") + "\nValidation:\n" + string.Join("\n", evidenceSummary) + "\nDiff:\n" + diff.Patch };
            var verdict = diff.Patch.Length > 120000 ? new AcceptanceResult("Uncertain", "Integration diff exceeds review input limit.") : TaskInstructions.ParseJson<AcceptanceResult>(await reviewer.PlanAsync(reviewTask, workspace with { BaseCommit = commit }, claim.Run.Id, ct));
            if (verdict.Verdict is not ("Pass" or "NeedsChanges" or "Uncertain")) throw new ArgumentException("Invalid integration review verdict.");
            await workspaces.AssertUnchangedAsync(workspace with { BaseCommit = commit }, ct);
            await store.RecordAiReviewAsync(claim.Run.Id, new(Guid.NewGuid(), 1, reviewStage, selection, commit, verdict.Verdict, WorkTask.Required(verdict.Summary, 20000), DateTimeOffset.UtcNow), ct);
            if (verdict.Verdict != "Pass") break;
        }
        await store.CompleteRunAsync(claim.Run.Id, true, workspace.Path, executionBase, commit, null, ct);
    }
}
