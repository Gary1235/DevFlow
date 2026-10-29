using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DevFlow.Core;

namespace DevFlow.Runners;

// One private MCP server per CLI invocation. The CLI's stdio child forwards
// JSON-RPC over a current-user-only pipe; execution stays in the owning process.
public sealed class StructuredMcpBridge : IAsyncDisposable
{
    private readonly IStructuredTools tools;
    private readonly CancellationTokenSource stop;
    private readonly Task serving;
    private readonly ConcurrentBag<Task> clients = [];
    private readonly ConcurrentDictionary<string, CancellationTokenSource> calls = new();
    public string PipeName { get; } = "devflow-mcp-" + Guid.NewGuid().ToString("N");
    public string Name { get; } = "devflow_" + Guid.NewGuid().ToString("N");
    public string Command { get; }
    public string[] Arguments { get; }
    public const string Instructions = "Prefer the DevFlow MCP tools devflow_read_file, devflow_list_files and devflow_search for worktree reads/searches. " +
        "Use devflow_write_file for scoped changes and devflow_run_validation for saved profile IDs when offered. " +
        "These tools execute in the DevFlow host under the same task policy. Do not wrap them in shell commands or bypass tool errors. " +
        "In read-only sessions only read/list/search tools are available.";

    public StructuredMcpBridge(IStructuredTools tools, string helperAssembly, CancellationToken ct)
    {
        this.tools = tools;
        var assembly = Path.GetFullPath(helperAssembly);
        var appHost = Path.ChangeExtension(assembly, OperatingSystem.IsWindows() ? ".exe" : null);
        Command = File.Exists(appHost) ? appHost : "dotnet";
        Arguments = File.Exists(appHost) ? ["--devflow-mcp", PipeName] : [assembly, "--devflow-mcp", PipeName];
        stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        serving = ServeAsync();
    }

    public object StdioConfig => new { command = Command, args = Arguments };
    public object AcpConfig => new { name = Name, command = Command, args = Arguments, env = Array.Empty<object>() };
    public string[] ToolNames => tools.Definitions.Select(d => d.Name).ToArray();
    public string[] ClaudeToolNames => tools.Definitions.Select(d => $"mcp__{Name}__{d.Name}").ToArray();
    public bool OwnsTool(string tool) => tools.Definitions.Any(d =>
        tool == $"mcp__{Name}__{d.Name}" || tool == $"{Name}__{d.Name}" || tool == $"mcp_{Name}_{d.Name}");
    public bool OwnsTool(string tool, JsonElement args)
    {
        if (OwnsTool(tool)) return true;
        if (tool != "call_mcp_tool" || args.ValueKind != JsonValueKind.Object) return false;
        string? Field(string first, string second) {
            var a = args.TryGetProperty(first, out var value);
            var b = args.TryGetProperty(second, out var alternate);
            if (a == b) return null;
            var field = a ? value : alternate;
            return field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        }
        return Field("ServerName", "server_name") == Name && Field("ToolName", "tool_name") is { } name && ToolNames.Contains(name);
    }

    private async Task ServeAsync()
    {
        try {
            while (!stop.IsCancellationRequested) {
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(stop.Token); }
                catch { pipe.Dispose(); throw; }
                clients.Add(HandleAsync(pipe));
            }
        } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        await using (pipe) {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            try {
                var line = await reader.ReadLineAsync(lifetime.Token);
                if (line is null || line.Length > 128000) return;
                var dispatch = DispatchAsync(line, lifetime.Token);
                var disconnected = reader.ReadLineAsync(lifetime.Token).AsTask();
                try {
                    if (await Task.WhenAny(new Task[] { dispatch, disconnected }) == disconnected) lifetime.Cancel();
                    var response = await dispatch;
                    await writer.WriteLineAsync(response);
                } finally {
                    lifetime.Cancel();
                    try { await disconnected; } catch (OperationCanceledException) { }
                }
            } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            finally { try { await writer.DisposeAsync(); } catch (IOException) { } }
        }
    }

    private async Task<string> DispatchAsync(string line, CancellationToken ct)
    {
        JsonElement? id = null;
        string Error(int code, string message) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });
        try {
            using var document = JsonDocument.Parse(line);
            var frame = document.RootElement;
            if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty("jsonrpc", out var rpc) || rpc.ValueKind != JsonValueKind.String || rpc.GetString() != "2.0" ||
                !frame.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String) return Error(-32600, "Invalid request");
            var p = frame.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object
                ? parameters : JsonSerializer.SerializeToElement(new { });
            if (!frame.TryGetProperty("id", out var requestId)) {
                if (method.GetString() == "notifications/cancelled" && p.TryGetProperty("requestId", out var cancelled) && calls.TryGetValue(cancelled.GetRawText(), out var running))
                    await running.CancelAsync();
                return "null";
            }
            if (requestId.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) return Error(-32600, "Invalid request ID");
            id = requestId.Clone();
            object result;
            switch (method.GetString()) {
                case "initialize":
                    var requested = p.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
                    result = new { protocolVersion = requested is "2024-11-05" or "2025-03-26" or "2025-06-18" ? requested : "2025-06-18",
                        capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "DevFlow", version = "0.7.0" }, instructions = Instructions };
                    break;
                case "ping": result = new { }; break;
                case "tools/list":
                    result = new { tools = tools.Definitions.Select(d => new { name = d.Name, description = d.Description, inputSchema = d.InputSchema,
                        annotations = new { readOnlyHint = d.Name is "devflow_read_file" or "devflow_list_files" or "devflow_search",
                            destructiveHint = d.Name is "devflow_write_file" or "devflow_run_validation", openWorldHint = d.Name == "devflow_run_validation" } }).ToArray() };
                    break;
                case "tools/call":
                    if (!p.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                        !p.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Object) return Error(-32602, "Tool name and object arguments are required");
                    using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                        var key = requestId.GetRawText();
                        if (!calls.TryAdd(key, lifetime)) return Error(-32600, "Duplicate active request ID");
                        try {
                            var output = await tools.ExecuteAsync(name.GetString()!, args.Clone(), lifetime.Token);
                            result = new { content = new[] { new { type = "text", text = output } }, isError = false };
                        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or MissingEntityException or ConflictException) {
                            result = new { content = new[] { new { type = "text", text = ex.Message } }, isError = true };
                        } finally { calls.TryRemove(key, out _); }
                    }
                    break;
                default: return Error(-32601, "Method not found");
            }
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
        } catch (JsonException) { return Error(-32700, "Parse error"); }
        catch (OperationCanceledException) { return Error(-32800, "Tool call cancelled"); }
    }

    public static async Task RunClientAsync(string pipeName)
    {
        if (!pipeName.StartsWith("devflow-mcp-", StringComparison.Ordinal) || !Guid.TryParseExact(pipeName[12..], "N", out _))
            throw new ArgumentException("Invalid DevFlow MCP pipe");
        using var stop = new CancellationTokenSource();
        using var output = new SemaphoreSlim(1, 1);
        using var slots = new SemaphoreSlim(16, 16);
        var pending = new List<Task>();
        async Task Forward(string line) {
            try {
                await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(stop.Token);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(line);
                var response = await reader.ReadLineAsync(stop.Token);
                if (response is null or "null") return;
                await output.WaitAsync(stop.Token);
                try { await Console.Out.WriteLineAsync(response); await Console.Out.FlushAsync(); }
                finally { output.Release(); }
            } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            finally { slots.Release(); }
        }
        try {
            while (await Console.In.ReadLineAsync(stop.Token) is { } line) {
                if (line.Length > 128000) throw new ArgumentException("MCP message too large");
                await slots.WaitAsync(stop.Token);
                pending.RemoveAll(t => t.IsCompletedSuccessfully);
                pending.Add(Forward(line));
            }
        } finally { await stop.CancelAsync(); await Task.WhenAll(pending); }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        try { await serving; await Task.WhenAll(clients); }
        finally { stop.Dispose(); }
    }
}
