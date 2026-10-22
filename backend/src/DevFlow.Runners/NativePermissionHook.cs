using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevFlow.Core;

namespace DevFlow.Runners;

// A private, current-user-only pipe bridges the official PreToolUse hook to the web approval.
// No CLI global settings, wildcard grants or permission-bypass flags are changed.
public sealed class NativePermissionHook : IAsyncDisposable
{
    private readonly IToolApprovalOperation operation;
    private readonly string workspace, hookFile, pipeName, auditFile;
    private readonly byte[]? original;
    private readonly bool createdDirectory;
    private readonly CancellationTokenSource stop;
    private readonly Task server;
    private readonly List<Task> clients = [];
    private readonly SemaphoreSlim auditLock = new(1, 1);
    private string? conversation;
    private readonly object conversationLock = new();

    private NativePermissionHook(IToolApprovalOperation operation, string workspace, string hookFile,
        byte[]? original, bool createdDirectory, string pipeName, string auditFile)
    {
        this.operation = operation; this.workspace = workspace; this.hookFile = hookFile; this.original = original;
        this.createdDirectory = createdDirectory; this.pipeName = pipeName; this.auditFile = auditFile;
        stop = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
        server = ServeAsync();
    }

    public static async Task<NativePermissionHook> CreateAsync(IToolApprovalOperation operation, string workspace, string auditFile, CancellationToken ct, string helperAssembly)
    {
        workspace = Path.GetFullPath(workspace);
        var directory = Path.Combine(workspace, ".agents"); var hookFile = Path.Combine(directory, "hooks.json");
        if (Directory.Exists(directory) && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint) ||
            File.Exists(hookFile) && File.GetAttributes(hookFile).HasFlag(FileAttributes.ReparsePoint))
            throw new ConflictException("無法在符號連結的 hooks 目錄啟用網頁確認。");
        var original = File.Exists(hookFile) ? await File.ReadAllBytesAsync(hookFile, ct) : null;
        if (original?.Length > 1024 * 1024) throw new ConflictException("現有 hooks 設定過大，無法安全加入網頁確認。");
        var hooks = original is null ? new JsonObject() : JsonNode.Parse(original) as JsonObject ?? throw new ConflictException("既有 hooks 格式無效。");
        var pipe = "devflow-agy-" + Guid.NewGuid().ToString("N");
        var assembly = helperAssembly;
        var appHost = Path.Combine(Path.GetDirectoryName(assembly)!, Path.GetFileNameWithoutExtension(assembly) + (OperatingSystem.IsWindows() ? ".exe" : ""));
        static string Quote(string value) => OperatingSystem.IsWindows() ? "\"" + value.Replace("\"", "\"\"") + "\"" : "'" + value.Replace("'", "'\"'\"'") + "'";
        var command = File.Exists(appHost) ? Quote(appHost) : "dotnet " + Quote(assembly);
        command += " --agy-permission-hook " + pipe;
        if (OperatingSystem.IsWindows())
        {
            // An encoded wrapper avoids cmd.exe reinterpreting quoted installation paths.
            // It preserves UTF-8 stdin; it does not change the system execution policy.
            static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
            var executable = File.Exists(appHost) ? PsQuote(appHost) : "'dotnet'";
            var arguments = File.Exists(appHost) ? "" : PsQuote(assembly) + " ";
            var script = "$OutputEncoding=[System.Text.UTF8Encoding]::new($false);[Console]::InputEncoding=$OutputEncoding;[Console]::OutputEncoding=$OutputEncoding;" +
                "$hookInput=[Console]::In.ReadToEnd();$hookInput | & " + executable + " " + arguments + "--agy-permission-hook " + PsQuote(pipe) + ";exit $LASTEXITCODE";
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (powershell.Any(char.IsWhiteSpace)) throw new ConflictException("系統 PowerShell 路徑不適用於 hook 啟動。");
            command = powershell + " -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }
        hooks["devflow-web-approval-" + operation.Id.ToString("N")] = JsonSerializer.SerializeToNode(new
        {
            // Do not impose a DevFlow deadline; the CLI owns the hook lifecycle.
            PreToolUse = new[] { new { matcher = "*", hooks = new[] { new { type = "command", command } } } }
        });
        var created = !Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(auditFile))!);
            await File.WriteAllTextAsync(hookFile, hooks.ToJsonString(), new UTF8Encoding(false), ct);
            return new(operation, workspace, hookFile, original, created, pipe, auditFile);
        }
        catch
        {
            if (original is null) File.Delete(hookFile); else await File.WriteAllBytesAsync(hookFile, original);
            if (created && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            throw;
        }
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(stop.Token); }
                catch { pipe.Dispose(); throw; }
                clients.Add(HandleAsync(pipe));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            try {
            var decision = "deny"; string reason = "確認已取消或失效。"; JsonElement? payload = null;
            try
            {
                var line = await reader.ReadLineAsync(stop.Token);
                if (line is null || line.Length > 64000) throw new ArgumentException("Invalid hook payload.");
                using var json = JsonDocument.Parse(line); payload = json.RootElement.Clone();
                using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var deciding = DecideAsync(json.RootElement, requestLifetime.Token);
                var disconnected = reader.ReadLineAsync(requestLifetime.Token).AsTask();
                try {
                    if (await Task.WhenAny(deciding, disconnected) == disconnected) {
                        requestLifetime.Cancel();
                        await deciding;
                        reason = "CLI 工具確認通道已中斷；未取得核准。";
                    }
                    else (decision, reason) = await deciding;
                }
                finally {
                    requestLifetime.Cancel();
                    try { await disconnected; } catch (OperationCanceledException) { }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ArgumentException or JsonException or InvalidOperationException or IOException or KeyNotFoundException or ConflictException) { }
            await auditLock.WaitAsync();
            try { await File.AppendAllTextAsync(auditFile, JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, operationId = operation.Id, decision, reason, request = payload }) + "\n"); }
            finally { auditLock.Release(); }
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(new { decision, reason })); }
            catch (IOException) { }
            } finally {
                try { await writer.DisposeAsync(); } catch (IOException) { }
            }
        }
    }

    internal async Task<(string Decision, string Reason)> DecideAsync(JsonElement payload, CancellationToken ct)
    {
        var paths = payload.GetProperty("workspacePaths");
        if (paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() != 1 ||
            !Path.GetFullPath(paths[0].GetString()!).Equals(workspace, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return ("deny", "工作目錄與本次對話不符。");
        var conversationId = payload.GetProperty("conversationId").GetString();
        if (!Guid.TryParse(conversationId, out _)) return ("deny", "對話識別無效。");
        lock (conversationLock)
        {
            conversation ??= conversationId;
            if (conversation != conversationId) return ("deny", "不接受其他對話的工具請求。");
        }
        var call = payload.GetProperty("toolCall"); var tool = call.GetProperty("name").GetString()!; var args = call.GetProperty("args");
        if (operation.ReadOnly && tool is ("write_to_file" or "replace_file_content" or "multi_replace_file_content" or "notebook_edit" or "sed_file"))
            return ("deny", "目前是唯讀規劃或討論，不允許修改檔案。");
        if (tool is "ask_permission" or "ask_custom_permission")
            return ("deny", "請直接提出需要的具體工具操作，由網頁逐次確認；不建立持續性權限。");
        if (tool == "ask_question") return ("deny", "請在一般回覆中向使用者提問。");
        // Reads also pass through the shared operation and worktree-boundary
        // policy. The developer is responsible for isolating worktree secrets.
        if (tool is "list_permissions" or "finish") return ("allow", "沿用 CLI 原有規則。");
        var normalizedTool = tool is "write_to_file" or "replace_file_content" or "multi_replace_file_content" ? "write_file" : tool;
        if(normalizedTool=="run_command") {
            var commandArgs=JsonNode.Parse(args.GetRawText())!.AsObject();
            // An unexpected shell override in tool input is not evidence of the
            // actual executor. Do not silently replace it with a trusted claim.
            commandArgs["Shell"]=commandArgs.ContainsKey("Shell")||commandArgs.ContainsKey("shell")?"unknown":OperatingSystem.IsWindows()?"powershell":"posix";
            args=JsonSerializer.SerializeToElement(commandArgs);
        }
        if (normalizedTool == "write_file" && args.TryGetProperty("TargetFile", out var target))
        {
            var normalized = JsonNode.Parse(args.GetRawText())!.AsObject(); normalized["Path"] = JsonValue.Create(target.GetString()); normalized.Remove("TargetFile");
            args = JsonSerializer.SerializeToElement(normalized);
        }
        if(operation.ReadOnly&&PermissionPolicy.Assess(PermissionPolicySettings.Defaults,normalizedTool,args,workspace,readOnly:true).Category is "Write" or "Deletion")
            return ("deny","目前是唯讀規劃或討論，不允許寫入或刪除。");
        var allowed = await operation.RequestPermissionAsync(normalizedTool, args, workspace, ct);
        return allowed ? ("auto_approve", "DevFlow 已依自動化政策、既有規則或本次人工確認核准這次工具與參數，仍遵守明確禁止規則。") : ("deny", $"本次工具未獲准執行：使用者拒絕、CLI 確認通道中斷或執行已停止。指定工作副本：{workspace}。不得藉由改寫指令或替代工具規避此決定，也不要離開工作副本搜尋 DevFlow 原始碼或其他專案排查權限。請回報受阻的工具與目標；如確需副本外資料，向使用者說明用途並取得額外授權。");
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await server; await Task.WhenAll(clients); }
        finally
        {
            if (original is null) File.Delete(hookFile); else await File.WriteAllBytesAsync(hookFile, original);
            var directory = Path.GetDirectoryName(hookFile)!;
            if (createdDirectory && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            stop.Dispose(); auditLock.Dispose();
        }
    }

    public static async Task RunClientAsync(string pipeName)
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        object response = new { decision = "deny", reason = "無法連線到 DevFlow，預設拒絕。" };
        try
        {
            // Bound connection setup only. Once connected, wait for the CLI or web
            // decision; EOF from a dead server is an interruption, never approval.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var input = await Console.In.ReadToEndAsync(timeout.Token);
            if (input.Length > 64000) throw new ArgumentException();
            using var json = JsonDocument.Parse(input);
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000, timeout.Token);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(json.RootElement.GetRawText());
            var line = await reader.ReadLineAsync();
            using var result = JsonDocument.Parse(line ?? "{}");
            var decision = result.RootElement.GetProperty("decision").GetString();
            if (decision is "allow" or "auto_approve" or "deny") response = result.RootElement.Clone();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException) { }
        Console.WriteLine(JsonSerializer.Serialize(response));
    }
}
