using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DevFlow.Core;

namespace DevFlow.Api;

public sealed partial class DiscussionOperations
{
    private readonly ConcurrentDictionary<Guid, Operation> running = new();
    public Operation Start(Guid discussionId, Guid operationId, AiSelection selection, CancellationToken shutdown)
    {
        var operation = new Operation(operationId, selection, shutdown);
        if (!running.TryAdd(discussionId, operation)) { operation.Dispose(); throw new ConflictException("Discussion is already running."); }
        return operation;
    }
    public object Progress(Guid id) => running.TryGetValue(id, out var operation)
        ? new { operationId = (Guid?)operation.Id, content = operation.Content, selection = (AiSelection?)operation.Selection, permissions = operation.Permissions, activity = (RuntimeSnapshot?)operation.Activity.Snapshot() }
        : new { operationId = (Guid?)null, content = "", selection = (AiSelection?)null, permissions = Array.Empty<ToolApproval>(), activity = (RuntimeSnapshot?)null };
    public void Stop(Guid id, Guid operationId)
    {
        if (!running.TryGetValue(id, out var operation) || operation.Id != operationId)
            throw new ConflictException("This reply has already finished. Refresh the conversation.");
        operation.Stop();
    }
    public void Finish(Guid id, Operation operation)
    {
        running.TryRemove(new KeyValuePair<Guid, Operation>(id, operation));
        operation.Dispose();
    }

    public sealed partial class Operation : IDisposable, IToolApprovalOperation
    {
        private readonly CancellationTokenSource cancellation;
        private readonly CancellationToken token;
        private readonly object sync = new();
        private readonly StringBuilder pending = new();
        private string content = "";
        private bool disposed;
        public Guid Id { get; }
        public AiSelection Selection { get; }
        public string Provider => Selection.Provider;
        public bool ReadOnly => true;
        public RuntimeActivity Activity { get; } = new();
        public bool Stopped { get; private set; }
        public CancellationToken Token => token;
        public string Content { get { lock (sync) return content; } }
        public Operation(Guid id, AiSelection selection, CancellationToken shutdown)
        { Id = id; Selection = selection; cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown); token = cancellation.Token; }
        public void Stop() { lock (sync) { if (disposed) throw new ConflictException("This reply has already finished."); Stopped = true; cancellation.Cancel(); } }
        public void Dispose() { lock (sync) { if (disposed) return; disposed = true; cancellation.Cancel(); cancellation.Dispose(); } }

        // Expose only assistant text. Tool output, reasoning and stderr stay in run artifacts.
        public void Append(string chunk)
        {
            lock (sync)
            {
                pending.Append(chunk);
                if (pending.Length > 1024 * 1024) { pending.Clear(); return; }
                int end;
                while ((end = pending.ToString().IndexOf('\n')) >= 0)
                {
                    var line = pending.ToString(0, end); pending.Remove(0, end + 1);
                    try
                    {
                        using var document = JsonDocument.Parse(line); var json = document.RootElement;
                        if (json.TryGetProperty("event", out var synthetic) && synthetic.GetString() == "devflow_assistant_delta" && json.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                            content = (content + text.GetString())[..Math.Min(40000, content.Length + (text.GetString()?.Length ?? 0))];
                        else if (Selection.Provider == "antigravity" && json.TryGetProperty("event", out var kind) && kind.GetString() == "step_update" &&
                            json.TryGetProperty("step_update", out var step) && step.TryGetProperty("step_type", out var type) && type.GetString() == "agent_response" &&
                            step.TryGetProperty("text_delta", out var delta) && delta.ValueKind == JsonValueKind.String)
                            content = (content + delta.GetString())[..Math.Min(40000, content.Length + (delta.GetString()?.Length ?? 0))];
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
                }
            }
        }
    }
}
