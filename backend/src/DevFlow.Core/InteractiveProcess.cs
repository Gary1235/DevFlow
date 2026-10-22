using System.Text.Json;

namespace DevFlow.Core;

public interface IProcessSession
{
    CancellationToken ProcessExited => CancellationToken.None;
    Task SendAsync(object message, CancellationToken ct);
    Task<JsonElement?> ReadAsync(CancellationToken ct);
}
public interface IInteractiveProcessRunner
{
    Task<ProcessResult> RunInteractiveAsync(string executable, IReadOnlyList<string> arguments, string cwd,
        Func<IProcessSession, CancellationToken, Task> exchange, CancellationToken ct);
}

public static class CliPermissions
{
    private static readonly AsyncLocal<IToolApprovalOperation?> current = new();
    public static IToolApprovalOperation? Current => current.Value;
    public static IDisposable Observe(IToolApprovalOperation operation)
    {
        var old = current.Value; current.Value = operation; return new Scope(() => current.Value = old);
    }
    private sealed class Scope(Action done) : IDisposable { public void Dispose() => done(); }
}
