using System.Text.Json.Nodes;
using DevFlow.Core;

namespace DevFlow.Runners;

internal sealed class McpPromptProcess(IProcessRunner inner) : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, string cwd, string? input, CancellationToken ct)
    {
        var frame = JsonNode.Parse(input!)!.AsObject();
        var message = frame["message"]!.AsObject();
        message["content"] = message["content"]!.GetValue<string>() + "\n" + StructuredMcpBridge.Instructions;
        // Headless Antigravity needs an explicit workspace to discover .agents MCP config.
        return inner.RunAsync(executable, [..args, "--add-dir", Path.GetFullPath(cwd)], cwd, frame.ToJsonString()+"\n", ct);
    }
}
