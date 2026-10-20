namespace DevFlow.Core;

// Scoped to the current async operation; concurrent discussions never share output.
public static class ProcessOutput
{
    private static readonly AsyncLocal<Action<string>?> current = new();
    private static readonly AsyncLocal<Action<ProcessActivity>?> activity = new();
    public static Action<string>? Observer => current.Value;
    public static Action<ProcessActivity>? ActivityObserver => activity.Value;
    public static IDisposable ObserveActivity(Action<ProcessActivity> observer)
    {
        var previous = activity.Value;
        activity.Value = observer;
        return new Scope(() => activity.Value = previous);
    }
    public static IDisposable Observe(Action<string> observer)
    {
        var previous = current.Value;
        current.Value = observer;
        return new Scope(() => current.Value = previous);
    }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
}

// Metadata only: command arguments, tool output and model reasoning are not included.
public sealed record ProcessActivity(Guid Id, string Executable, string Kind, int Characters = 0, int? ExitCode = null, string? Summary = null);
