using System.Text.Json;

namespace DevFlow.Infrastructure;

public sealed record WorkerPulse(Guid InstanceId, int ProcessId, string State, DateTimeOffset UpdatedAt);
public sealed record WorkerPresence(bool Online, string State, string Message, DateTimeOffset? LastSeenAt,
    DateTimeOffset CheckedAt, string DatabasePath);

public sealed partial class SqliteTaskStore
{
    public Task RecordWorkerPulseAsync(WorkerPulse pulse, CancellationToken ct = default)
    {
        using var c = Open(ct);
        Execute(c, null, "INSERT INTO WorkerPresence(Id,Payload) VALUES(1,$payload) ON CONFLICT(Id) DO UPDATE SET Payload=excluded.Payload",
            ("$payload", JsonSerializer.Serialize(pulse)));
        return Task.CompletedTask;
    }
    public Task<WorkerPresence> GetWorkerPresenceAsync(CancellationToken ct = default)
    {
        using var c = Open(ct);
        using var command = Command(c, null, "SELECT Payload FROM WorkerPresence WHERE Id=1");
        var pulse = command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<WorkerPulse>(json) : null;
        var now = DateTimeOffset.UtcNow;
        var online = pulse is not null && pulse.State != "Stopped" && now - pulse.UpdatedAt < TimeSpan.FromSeconds(15) && pulse.UpdatedAt <= now.AddSeconds(5);
        var message = !online ? "這份資料庫的 Worker 未連線，排隊任務暫時不會執行。請檢查背景執行器與啟動紀錄。" : pulse!.State switch
        {
            "Waiting" => "Worker 已連線，正在等待 CLI 更新或作業鎖定解除。",
            "Error" => "Worker 已連線，但執行器發生錯誤；請查看 Worker 紀錄。",
            "Working" => "Worker 已連線，正在處理任務。",
            "WaitingPermission" => "Worker 已連線，有任務等待工具核准；其他獨立專案仍可執行。",
            _ => "Worker 已連線，等待領取任務。"
        };
        return Task.FromResult(new WorkerPresence(online, online ? pulse!.State : "Offline", message, pulse?.UpdatedAt, now, path));
    }
}
