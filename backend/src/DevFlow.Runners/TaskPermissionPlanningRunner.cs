using System.Text.Json;
using DevFlow.Core;

namespace DevFlow.Runners;

// The Worker and API exchange one-use decisions through their shared database.
public sealed class TaskPermissionPlanningRunner(IPlanningRunner inner, ITaskToolApprovalStore store,
    string artifacts, string helperAssembly) : IPlanningRunner
{
    public async Task<string> PlanAsync(WorkTask task, Workspace workspace, Guid runId, CancellationToken ct)
    {
        if (AiSelection.Normalize(task.Ai?.Planning).Provider != "antigravity" || task.ReadOnlyInstruction is not null)
            return await inner.PlanAsync(task, workspace, runId, ct);
        var operation = new Operation(store, task.Id, runId, ct);
        await using var hook = await NativePermissionHook.CreateAsync(operation, workspace.Path,
            Path.Combine(artifacts, runId.ToString("N"), "permission-decisions.jsonl"), ct, helperAssembly);
        try
        {
            var plan = await inner.PlanAsync(task, workspace, runId, ct);
            if (operation.Failure is { } failure) throw new InvalidOperationException(failure);
            return plan;
        }
        catch (Exception ex) when (operation.Failure is not null && !ct.IsCancellationRequested)
        { throw new InvalidOperationException(operation.Failure, ex); }
    }

    public sealed class Operation(ITaskToolApprovalStore store, Guid taskId, Guid id, CancellationToken token, string provider = "antigravity", bool readOnly = true) : IToolApprovalOperation
    {
        public Guid Id => id;
        public CancellationToken Token => token;
        public string Provider => provider;
        public bool ReadOnly => readOnly;
        public string? Failure { get; private set; }
        public async Task<bool> RequestPermissionAsync(string tool, JsonElement arguments, string workspace, CancellationToken ct)
        {
            tool = WorkTask.Required(tool, 100);
            if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 20000)
                throw new ArgumentException("工具參數格式無效或過長。");
            var now = DateTimeOffset.UtcNow;
            var request = new TaskToolApproval(Guid.NewGuid(), taskId, id, tool, arguments.Clone(), workspace, now, now.AddMinutes(2)) { Provider = provider };
            await store.AddToolApprovalAsync(request, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                while (true)
                {
                    var current = (await store.ListToolApprovalsAsync(taskId, timeout.Token)).Single(r => r.Id == request.Id);
                    if (current.State is "Allowed" or "AllowedByRule") return true;
                    if (current.State != "Pending")
                    {
                        Failure = current.State == "Denied" ? "你已拒絕本次規劃工具操作，規劃已停止。" : "工具確認已逾時或規劃已停止；此次沒有取得核准。";
                        return false;
                    }
                    await Task.Delay(200, timeout.Token);
                }
            }
            catch (OperationCanceledException)
            {
                Failure = ct.IsCancellationRequested || token.IsCancellationRequested ? "規劃已取消。" : "等待工具確認逾時（2 分鐘），此次沒有取得核准。請重新規劃後在任務頁回覆。";
                return false;
            }
            finally { await store.ExpireToolApprovalAsync(taskId, request.Id, CancellationToken.None); }
        }
    }
}
