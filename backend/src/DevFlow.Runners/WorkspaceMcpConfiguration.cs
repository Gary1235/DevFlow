using System.Text.Json;
using System.Text.Json.Nodes;
using DevFlow.Core;

namespace DevFlow.Runners;

internal sealed class WorkspaceMcpConfiguration : IAsyncDisposable
{
    private readonly string file, directory, name;
    private readonly byte[]? original;
    private readonly byte[] written;
    private readonly bool createdDirectory;
    private readonly JsonNode server;
    private WorkspaceMcpConfiguration(string file, string directory, string name, byte[]? original, byte[] written, bool createdDirectory, JsonNode server)
    { this.file=file; this.directory=directory; this.name=name; this.original=original; this.written=written; this.createdDirectory=createdDirectory; this.server=server; }

    public static async Task<WorkspaceMcpConfiguration> CreateAsync(string workspace, StructuredMcpBridge bridge, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetFullPath(workspace), ".agents");
        var file = Path.Combine(directory, "mcp_config.json");
        if (Directory.Exists(directory) && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint) ||
            File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            throw new ConflictException("無法在符號連結的 MCP 設定目錄啟用結構化工具。");
        if (File.Exists(file) && new FileInfo(file).Length > 1024*1024) throw new ConflictException("既有 MCP 設定過大。");
        var original = File.Exists(file) ? await File.ReadAllBytesAsync(file, ct) : null;
        var root = original is null ? new JsonObject() : JsonNode.Parse(original) as JsonObject ?? throw new ConflictException("既有 MCP 設定無效。");
        if (root["mcpServers"] is not null and not JsonObject) throw new ConflictException("既有 MCP 伺服器設定無效。");
        var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
        root["mcpServers"] = servers;
        if (servers.ContainsKey(bridge.Name)) throw new ConflictException("MCP 伺服器名稱衝突。");
        var server = JsonSerializer.SerializeToNode(bridge.StdioConfig)!;
        server["timeoutSeconds"] = -1; // CLI tool waits follow the operation lifecycle, not a web deadline.
        server["enabledTools"] = JsonSerializer.SerializeToNode(bridge.ToolNames);
        servers[bridge.Name] = server.DeepClone();
        var written = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        var created = !Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        try { await File.WriteAllBytesAsync(file, written, ct); }
        catch {
            if (original is null) File.Delete(file); else await File.WriteAllBytesAsync(file, original, CancellationToken.None);
            if (created && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            throw;
        }
        return new(file,directory,bridge.Name,original,written,created,server);
    }

    public async ValueTask DisposeAsync()
    {
        // Never follow a replacement link or overwrite concurrent user edits.
        if (!File.Exists(file) || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint) || File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) return;
        var current = await File.ReadAllBytesAsync(file);
        if (current.AsSpan().SequenceEqual(written)) {
            if (original is null) File.Delete(file); else await File.WriteAllBytesAsync(file, original);
        } else {
            try {
                var root = JsonNode.Parse(current) as JsonObject;
                if (root?["mcpServers"] is JsonObject servers && JsonNode.DeepEquals(servers[name], server)) {
                    servers.Remove(name);
                    await File.WriteAllTextAsync(file, root.ToJsonString());
                }
            } catch (JsonException) { } // Leave an unrelated/malformed replacement untouched.
        }
        if (createdDirectory && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
}
