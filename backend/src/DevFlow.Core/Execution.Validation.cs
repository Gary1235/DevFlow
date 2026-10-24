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
}
