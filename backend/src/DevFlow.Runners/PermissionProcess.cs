using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevFlow.Core;

namespace DevFlow.Runners;

// Translate each CLI's native approval protocol. Decisions are always one-use;
// DevFlow owns task/project rules rather than mutating the CLI's global settings.
internal sealed class PermissionProcess(IProcessRunner inner, string provider, IToolApprovalOperation operation, IStructuredTools? structuredTools = null, StructuredMcpBridge? mcp = null, PromptImage[]? images = null) : IProcessRunner
{
    private static string? Option(IReadOnlyList<string> args, string key)
    { for (var i = 0; i + 1 < args.Count; i++) if (args[i] == key) return args[i + 1]; return null; }
    private static string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static JsonElement Object(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : JsonSerializer.SerializeToElement(new { });
    private static void Delta(string text) => ProcessOutput.Observer?.Invoke(JsonSerializer.Serialize(new { @event = "devflow_assistant_delta", text }) + "\n");

    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string cwd, string? standardInput, CancellationToken ct)
    {
        if (inner is not IInteractiveProcessRunner interactive) throw new InvalidOperationException("CLI 權限對話需要雙向程序執行器。");
        var prompt = standardInput ?? "";
        return provider switch
        {
            "codex" => await Codex(interactive, executable, arguments, cwd, prompt, ct),
            "claude" => await Claude(interactive, executable, arguments, cwd, prompt, ct),
            "gemini" => await Gemini(interactive, executable, arguments, cwd, prompt, ct),
            _ => throw new ArgumentException("Unsupported permission protocol.")
        };
    }
    private async Task<bool> Ask(string tool, JsonElement args, string cwd, CancellationToken ct, IProcessSession session)
    {
        var normalized = PermissionTools.Normalize(provider, tool, args, cwd);
        if (operation.ReadOnly && PermissionPolicy.Assess(PermissionPolicySettings.Defaults,normalized.Tool,normalized.Arguments,cwd,readOnly:true).Category is "Write" or "Deletion") return false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, session.ProcessExited);
        bool allowed;
        try { allowed = await operation.RequestPermissionAsync(normalized.Tool, normalized.Arguments, cwd, lifetime.Token); }
        catch (OperationCanceledException) when (session.ProcessExited.IsCancellationRequested && !ct.IsCancellationRequested) {
            throw new InvalidOperationException("[CliInterrupted] CLI 已結束，工具確認通道已中斷；此次沒有取得核准。");
        }
        if (session.ProcessExited.IsCancellationRequested && !ct.IsCancellationRequested)
            throw new InvalidOperationException("[CliInterrupted] CLI 已結束，工具確認通道已中斷；此次沒有取得核准。");
        return allowed;
    }
    private static async Task<JsonElement> Read(IProcessSession session, CancellationToken ct) =>
        await session.ReadAsync(ct) ?? throw new InvalidOperationException("CLI 在完成前結束權限對話。");
    private static Task Send(IProcessSession s, int id, string method, object args, CancellationToken ct) => s.SendAsync(new { jsonrpc = "2.0", id, method, @params = args }, ct);
    private static async Task<JsonElement> Response(IProcessSession s, int id, CancellationToken ct)
    {
        while (true)
        {
            var frame = await Read(s, ct);
            if (frame.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == id)
            {
                if (frame.TryGetProperty("error", out var error)) throw new InvalidOperationException("CLI 協定錯誤：" + error.GetRawText());
                return frame.GetProperty("result").Clone();
            }
            if (frame.TryGetProperty("id", out var requestId) && frame.TryGetProperty("method", out _))
                await s.SendAsync(new { jsonrpc = "2.0", id = requestId.Clone(), error = new { code = -32601, message = "Unsupported client operation" } }, ct);
        }
    }

    private async Task<ProcessResult> Codex(IInteractiveProcessRunner process, string executable, IReadOnlyList<string> args, string cwd, string prompt, CancellationToken ct)
    {
        List<string> command = ["app-server", "--listen", "stdio://"];
        for (var i = 0; i + 1 < args.Count; i++) if (args[i] == "-c") command.AddRange(["-c", args[++i]]);
        string? answer = null;
        var result = await process.RunInteractiveAsync(executable, command, cwd, async (s, token) =>
        {
            var initialization = new Dictionary<string, object> { ["clientInfo"] = new { name = "devflow", title = "DevFlow", version = "0.7.0" } };
            if (structuredTools is not null) initialization["capabilities"] = new { experimentalApi = true };
            await Send(s, 1, "initialize", initialization, token);
            await Response(s, 1, token); await s.SendAsync(new { method = "initialized", @params = new { } }, token);
            var threadOptions = new Dictionary<string, object?> { ["cwd"] = cwd, ["model"] = Option(args, "--model"), ["ephemeral"] = true,
                ["approvalPolicy"] = "on-request", ["sandbox"] = operation.ReadOnly ? "read-only" : "workspace-write" };
            if (structuredTools is not null) threadOptions["dynamicTools"] = structuredTools.Definitions;
            await Send(s, 2, "thread/start", threadOptions, token);
            var thread = (await Response(s, 2, token)).GetProperty("thread").GetProperty("id").GetString()!;
            JsonElement? schema = null;
            if (Option(args, "--output-schema") is { } file) { using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, token)); schema = json.RootElement.Clone(); }
            if (structuredTools is not null) prompt += "\nPrefer devflow_read_file, devflow_list_files and devflow_search for literal worktree operations. " +
                "Use devflow_write_file for scoped file updates and devflow_run_validation with a saved profileId when available. " +
                "These host tools avoid shell parsing; do not wrap them in shell commands. A tool error does not authorize bypassing its restrictions.";
            var input=new List<object>{new{type="text",text=prompt}};
            foreach(var image in images??[])input.Add(new{type="image",url=$"data:{image.MimeType};base64,{image.Data}"});
            await Send(s, 3, "turn/start", new { threadId = thread, input, outputSchema = schema }, token);
            string? turn = null; var deltas = new StringBuilder(); var items = new Dictionary<string, JsonElement>();
            var toolCalls = new HashSet<string>();
            while (true)
            {
                var frame = await Read(s, token);
                if (frame.TryGetProperty("error", out var error)) throw new InvalidOperationException("Codex 協定錯誤：" + error.GetRawText());
                if (!frame.TryGetProperty("method", out var method))
                {
                    if (frame.TryGetProperty("result", out var response) && response.TryGetProperty("turn", out var t)) turn = Text(t, "id");
                    continue;
                }
                var p = Object(frame, "params"); var name = method.GetString();
                if (Text(p, "threadId") is { } otherThread && otherThread != thread) throw new InvalidOperationException("Codex thread mismatch.");
                if (Text(p, "turnId") is { } otherTurn && turn is not null && otherTurn != turn) throw new InvalidOperationException("Codex turn mismatch.");
                if (name is "item/started" or "item/completed")
                {
                    var item = Object(p, "item"); if (Text(item, "id") is { } itemId) items[itemId] = item.Clone();
                    if (name == "item/completed" && Text(item, "type") == "agentMessage") answer = Text(item, "text");
                }
                if (name == "item/agentMessage/delta" && Text(p, "delta") is { } delta) { deltas.Append(delta); Delta(delta); }
                if (frame.TryGetProperty("id", out var request))
                {
                    object decision;
                    if (name == "item/tool/call" && structuredTools is not null)
                    {
                        if (Text(p, "threadId") != thread || turn is null || Text(p, "turnId") != turn || Text(p, "namespace") is not null ||
                            Text(p, "callId") is not { Length: > 0 } callId || !toolCalls.Add(callId))
                            throw new InvalidOperationException("Structured tool request identity mismatch or duplicate call.");
                        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, s.ProcessExited);
                        try {
                            var output = await structuredTools.ExecuteAsync(Text(p, "tool") ?? "", Object(p, "arguments"), lifetime.Token);
                            decision = new { success = true, contentItems = new[] { new { type = "inputText", text = output } } };
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or MissingEntityException or ConflictException) {
                            decision = new { success = false, contentItems = new[] { new { type = "inputText", text = ex.Message } } };
                        }
                    }
                    else if (name == "item/commandExecution/requestApproval")
                    {
                        if (p.TryGetProperty("availableDecisions",out var available) && !available.EnumerateArray().Any(x=>x.ValueKind==JsonValueKind.String&&x.GetString()=="accept"))
                            throw new InvalidOperationException("CLI 未提供單次允許選項，無法核准這次敏感操作。");
                        var commandPayload = JsonNode.Parse(p.GetRawText())!.AsObject();
                        if (Text(p,"itemId") is { } commandId && items.TryGetValue(commandId,out var commandItem))
                            foreach (var key in new[]{"command","cwd"}) if (commandPayload[key] is null && commandItem.TryGetProperty(key,out var value)) commandPayload[key]=JsonNode.Parse(value.GetRawText());
                        var allowed = await Ask("commandExecution", JsonSerializer.SerializeToElement(commandPayload), cwd, token, s);
                        decision = new { decision = allowed ? "accept" : "decline" };
                    }
                    else if (name == "item/fileChange/requestApproval")
                    {
                        var payload = JsonNode.Parse(p.GetRawText())!.AsObject();
                        if (Text(p, "itemId") is { } itemId && items.TryGetValue(itemId, out var item) && item.TryGetProperty("changes", out var changes)) payload["changes"] = JsonNode.Parse(changes.GetRawText());
                        decision = new { decision = await Ask("fileChange", JsonSerializer.SerializeToElement(payload), cwd, token, s) ? "accept" : "decline" };
                    }
                    else if (name == "item/permissions/requestApproval")
                    {
                        // The shortest native scope is a whole turn, which may authorize
                        // later sensitive calls without another prompt. Never label it one-use.
                        await s.SendAsync(new { jsonrpc="2.0", id=request.Clone(),result=new {permissions=new {},scope="turn"}},token);
                        throw new InvalidOperationException("CLI 要求整個回合的權限擴張，無法限制為單次操作，已拒絕；請改用具體指令的單次確認。");
                    }
                    else
                    {
                        await s.SendAsync(new { jsonrpc = "2.0", id = request.Clone(), error = new { code = -32601, message = "Unsupported client operation" } }, token); continue;
                    }
                    await s.SendAsync(new { jsonrpc = "2.0", id = request.Clone(), result = decision }, token);
                }
                if (name == "turn/completed")
                {
                    if (Text(Object(p, "turn"), "status") != "completed") throw new InvalidOperationException("Codex 未完成本次回覆。");
                    answer ??= deltas.ToString(); break;
                }
            }
        }, ct);
        if (Option(args, "--output-last-message") is { } final) await File.WriteAllTextAsync(final, WorkTask.Required(answer, 40000), ct);
        return result;
    }

    private async Task<ProcessResult> Claude(IInteractiveProcessRunner process, string executable, IReadOnlyList<string> args, string cwd, string prompt, CancellationToken ct)
    {
        var command = args.ToList(); var format = command.IndexOf("--output-format"); command[format + 1] = "stream-json";
        command.Remove("--restricted");
        var tools = command.IndexOf("--tools"); command[tools + 1] = operation.ReadOnly ? "Read,Glob,Grep,Bash" : "Read,Glob,Grep,Bash,Edit,Write";
        command[command.IndexOf("--permission-mode") + 1] = operation.ReadOnly ? "plan" : "default";
        command.AddRange(["--input-format", "stream-json", "--permission-prompt-tool", "stdio", "--verbose"]);
        if (mcp is not null) {
            command[command.IndexOf("--mcp-config")+1] = JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { [mcp.Name] = mcp.StdioConfig } });
            // Strict MCP config exposes only our operation-local server, whose
            // executor owns the actual per-call policy check.
            var denied = command.IndexOf("--disallowedTools");
            if (denied >= 0) command.RemoveRange(denied, 2);
            prompt += "\n" + StructuredMcpBridge.Instructions;
        }
        string? final = null;
        var result = await process.RunInteractiveAsync(executable, command, cwd, async (s, token) =>
        {
            await s.SendAsync(new { type = "control_request", request_id = "devflow-initialize", request = new { subtype = "initialize" } }, token);
            var started = false;
            while (true)
            {
                var frame = await Read(s, token); var type = Text(frame, "type");
                if (type == "control_response" && !started)
                {
                    var response = Object(frame, "response");
                    if (Text(response, "request_id") != "devflow-initialize") throw new InvalidOperationException("Claude initialization mismatch.");
                    if (Text(response, "subtype") != "success") throw new InvalidOperationException("Claude 權限對話初始化失敗。");
                    object content=prompt;
                    if(images is {Length:>0}){
                        var imageBlocks=new List<object>{new{type="text",text=prompt}};
                        imageBlocks.AddRange(images.Select(image=>(object)new{type="image",source=new{type="base64",media_type=image.MimeType,data=image.Data}}));content=imageBlocks;
                    }
                    await s.SendAsync(new { type = "user", session_id = "", message = new { role = "user", content }, parent_tool_use_id = (string?)null }, token); started = true;
                }
                if (type == "control_request")
                {
                    var request = Object(frame, "request"); object response;
                    if (Text(request, "subtype") == "can_use_tool" && Text(request, "tool_name") is { } tool)
                    {
                        var input = PermissionTools.BindWorkspace(provider, tool, Object(request, "input"), cwd);
                        var allowed = mcp?.OwnsTool(tool) == true || await Ask(tool, input, cwd, token, s);
                        response = allowed ? new { behavior = "allow", updatedInput = (object)input } : (object)new { behavior = "deny", message = "DevFlow 未核准本次操作。", interrupt = false };
                    }
                    else response = new { behavior = "deny", message = "Unsupported permission request" };
                    await s.SendAsync(new { type = "control_response", response = new { subtype = "success", request_id = frame.GetProperty("request_id").GetString(), response } }, token);
                }
                if (type == "assistant" && Object(frame, "message").TryGetProperty("content", out var blocks))
                    foreach (var block in blocks.EnumerateArray()) if (Text(block, "type") == "text" && Text(block, "text") is { } text) Delta(text);
                if (type == "result") { ClaudeCliRunner.ParseResult(frame.GetRawText()); final = frame.GetRawText(); break; }
            }
        }, ct);
        return result with { StandardOutput = final ?? throw new InvalidOperationException("Claude 未回傳完成結果。") };
    }

    private async Task<ProcessResult> Gemini(IInteractiveProcessRunner process, string executable, IReadOnlyList<string> args, string cwd, string prompt, CancellationToken ct)
    {
        List<string> command = ["--acp", "--sandbox", "--approval-mode", operation.ReadOnly ? "plan" : "default"];
        if (Option(args, "--model") is { } model) command.AddRange(["--model", model]);
        var text = new StringBuilder();
        var result = await process.RunInteractiveAsync(executable, command, cwd, async (s, token) =>
        {
            await Send(s, 1, "initialize", new { protocolVersion = 1, clientCapabilities = new { fs = new { readTextFile = false, writeTextFile = false }, terminal = false }, clientInfo = new { name = "DevFlow", version = "0.7.0" } }, token);
            var init = await Response(s, 1, token);
            if (init.GetProperty("protocolVersion").GetInt32() != 1) throw new InvalidOperationException("Gemini ACP version mismatch.");
            if(images is {Length:>0}&&(!Object(Object(init,"agentCapabilities"),"promptCapabilities").TryGetProperty("image",out var imageCapability)||imageCapability.ValueKind!=JsonValueKind.True))
                throw new InvalidOperationException("目前 Gemini CLI 未宣告支援圖片輸入，圖片已保存，請切換模型工具後重試。");
            await Send(s, 2, "session/new", new { cwd, mcpServers = mcp is null ? Array.Empty<object>() : new[] { mcp.AcpConfig } }, token);
            var session = Text(await Response(s, 2, token), "sessionId") ?? throw new InvalidOperationException("Gemini 未建立對話。");
            if (mcp is not null) prompt += "\n" + StructuredMcpBridge.Instructions;
            var blocks=new List<object>{new{type="text",text=prompt}};
            blocks.AddRange((images??[]).Select(image=>(object)new{type="image",data=image.Data,mimeType=image.MimeType}));
            await Send(s, 3, "session/prompt", new { sessionId = session, prompt = blocks }, token);
            var tools = new Dictionary<string, JsonObject>();
            while (true)
            {
                var frame = await Read(s, token); var name = Text(frame, "method"); var p = Object(frame, "params");
                if (Text(p, "sessionId") is { } other && other != session) throw new InvalidOperationException("Gemini session mismatch.");
                if (frame.TryGetProperty("error", out var error)) throw new InvalidOperationException("Gemini 協定錯誤：" + error.GetRawText());
                if (name == "session/update")
                {
                    var update = Object(p, "update");
                    if (Text(update, "sessionUpdate") == "agent_message_chunk" && Text(Object(update, "content"), "text") is { } delta) { text.Append(delta); Delta(delta); }
                    if (Text(update, "toolCallId") is { } id)
                    {
                        if (!tools.TryGetValue(id, out var cached)) tools[id] = cached = new();
                        foreach (var prop in update.EnumerateObject()) if (prop.Value.ValueKind != JsonValueKind.Null) cached[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
                    }
                }
                if (frame.TryGetProperty("id", out var requestId) && name is not null)
                {
                    if (name != "session/request_permission")
                    { await s.SendAsync(new { jsonrpc = "2.0", id = requestId.Clone(), error = new { code = -32601, message = "Unsupported client operation" } }, token); continue; }
                    var call = Object(p, "toolCall"); var merged = Text(call, "toolCallId") is { } id && tools.TryGetValue(id, out var cached) ? cached.DeepClone().AsObject() : new JsonObject();
                    foreach (var prop in call.EnumerateObject()) if (prop.Value.ValueKind != JsonValueKind.Null) merged[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
                    var toolName = merged["name"]?.GetValue<string>() ?? (merged["kind"]?.GetValue<string>() == "execute" ? "run_shell_command" : "unknown");
                    var input = merged["rawInput"] is JsonObject raw ? JsonSerializer.SerializeToElement(raw) : JsonSerializer.SerializeToElement(new { Request = merged });
                    var options = p.GetProperty("options").EnumerateArray().ToArray();
                    var canAllow = options.Any(o => Text(o, "kind") == "allow_once");
                    var allowed = canAllow && (mcp?.OwnsTool(toolName) == true || await Ask(toolName, input, cwd, token, s));
                    var option = options.FirstOrDefault(o => Text(o, "kind") == (allowed ? "allow_once" : "reject_once"));
                    object outcome = option.ValueKind == JsonValueKind.Undefined ? new { outcome = "cancelled" } : (object)new { outcome = "selected", optionId = option.GetProperty("optionId").GetString() };
                    await s.SendAsync(new { jsonrpc = "2.0", id = requestId.Clone(), result = new { outcome } }, token);
                }
                if (name is null && frame.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == 3)
                {
                    if (Text(Object(frame, "result"), "stopReason") != "end_turn") throw new InvalidOperationException("Gemini 未完成本次回覆。");
                    break;
                }
            }
        }, ct);
        return result with { StandardOutput = JsonSerializer.Serialize(new { response = WorkTask.Required(text.ToString(), 40000) }) };
    }
}
