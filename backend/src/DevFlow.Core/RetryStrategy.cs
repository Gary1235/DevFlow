namespace DevFlow.Core;

public sealed record RetryPreview(string Scope, int ExpectedVersion, string Summary, string[] RetainedSteps,
    string[] RepeatedSteps, string? BaseCommit);

public static class RetryStrategy
{
    public static RetryPreview Preview(WorkTask task, string scope = "Resume")
    {
        if (scope is not ("Resume" or "Restart")) throw new ArgumentException("重試範圍必須是 Resume 或 Restart。");
        if (task.Status is not (TaskStatus.Failed or TaskStatus.Rejected) || task.Plan is null)
            throw new ConflictException("只有失敗或退回且已有核准計畫的任務能重試。");
        var steps = task.Workflow?.Steps;
        var completed = task.StepResults ?? [];
        var partial = scope == "Resume" && steps is { Length: > 0 } && completed.Length < steps.Length;
        var source = scope == "Restart" ? task.ExecutionBaseCommit ?? task.Plan.BaseCommit :
            partial ? completed.LastOrDefault()?.ResultCommit ?? task.RevisionCommit ?? task.Plan.BaseCommit :
            task.RevisionCommit ?? task.Plan.BaseCommit;
        source ??= task.ExecutionBaseCommit;
        return new(scope, task.Version,
            partial ? "保留已完成步驟，從失敗步驟重新開始；該步驟內的測試與實作會重跑。" :
            scope == "Restart" ? "從原始實作基底重新執行全部步驟；不沿用先前的程式變更。" :
            "重新執行完整實作與驗收；若有退回成果，會以該成果為修正起點。",
            partial ? completed.Select(s => s.Name).ToArray() : [],
            steps is { Length: > 0 } ? steps.Skip(partial ? completed.Length : 0).Select(s => s.Name).ToArray() : ["完整實作、測試與 AI 驗收"],
            source);
    }
}
