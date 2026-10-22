using System.Text.Json;

namespace DevFlow.Core;

public static class PermissionTools
{
    // The returned input must also be supplied to the CLI, so its default search
    // location cannot diverge from the directory used by the approval policy.
    public static JsonElement BindWorkspace(string provider, string tool, JsonElement args, string workspace)
    {
        if (provider != "claude" || tool is not ("Glob" or "Grep") || args.TryGetProperty("path", out _)) return args.Clone();
        var bound = args.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone());
        bound["path"] = Path.GetFullPath(workspace);
        return JsonSerializer.SerializeToElement(bound);
    }

    public static (string Tool, JsonElement Arguments) Normalize(string provider, string tool, JsonElement args, string workspace)
    {
        if (provider == "antigravity") return (tool, args.Clone());
        args = BindWorkspace(provider, tool, args, workspace);
        string? Text(params string[] keys) => keys.Select(key => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null).FirstOrDefault(v => v is not null);
        var command = tool is "Bash" or "run_shell_command" or "commandExecution";
        var canonical = command ? "run_command" : tool switch
        {
            "Read" or "read_file" => "read_file", "Edit" or "Write" or "write_file" or "replace" or "fileChange" => "write_file",
            "Glob" or "glob" => "find_by_name", "Grep" or "grep_search" => "grep_search", "list_directory" => "list_dir",
            _ => "native_" + System.Text.RegularExpressions.Regex.Replace(tool.ToLowerInvariant(), "[^a-z0-9_]", "_")
        };
        var normalized = new Dictionary<string, object?>();
        if (command)
        {
            normalized["CommandLine"] = Text("command", "CommandLine") ?? throw new ArgumentException("CLI 未提供完整指令，不能核准。");
            normalized["Cwd"] = Text("cwd", "directory", "Cwd") ?? workspace;
            // This is adapter-owned metadata, never a model-provided shell claim.
            normalized["Shell"] = tool == "Bash" ? "bash" : "unknown";
            foreach (var key in new[] { "additionalPermissions", "networkApprovalContext", "kind" })
                if (args.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null)
                    normalized[key == "additionalPermissions" ? "AdditionalPermissions" : key == "networkApprovalContext" ? "NetworkApprovalContext" : "Kind"] = value.Clone();
            var extra = args.EnumerateObject().Where(p => p.Name is not ("command" or "CommandLine" or "cwd" or "Cwd" or "directory" or
                "threadId" or "turnId" or "itemId" or "startedAtMs" or "approvalId" or "reason" or "commandActions" or
                "availableDecisions" or "proposedExecpolicyAmendment" or "proposedNetworkPolicyAmendments" or "additionalPermissions" or "networkApprovalContext" or "kind"))
                .ToDictionary(p => p.Name, p => p.Value.Clone());
            if (extra.Count > 0) normalized["Options"] = extra;
            if (extra.Any(p => (p.Key is "dangerouslyDisableSandbox" or "run_in_background" or "is_background") && p.Value.ValueKind == JsonValueKind.True))
                normalized["AdditionalPermissions"] = new { NativeExecutionOptions = extra };
        }
        else
        {
            foreach (var p in args.EnumerateObject())
                if (p.Name is not ("threadId" or "turnId" or "itemId" or "startedAtMs" or "approvalId")) normalized[p.Name] = p.Value.Clone();
            var searchKey = canonical switch { "find_by_name" => "SearchDirectory", "grep_search" => "SearchPath", "list_dir" => "DirectoryPath", _ => null };
            var aliases = searchKey is null ? new[] { "file_path", "absolute_path", "path", "Path" }
                : provider == "gemini" ? new[] { "path", "Path", "dir_path", searchKey } : new[] { "path", "Path", searchKey };
            var supplied = aliases.Where(k => args.TryGetProperty(k, out _)).ToArray();
            if (searchKey is not null && (supplied.Length > 1 || supplied.Length == 1 &&
                (args.GetProperty(supplied[0]).ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(args.GetProperty(supplied[0]).GetString()))))
                throw new ArgumentException("CLI 搜尋目錄重複或格式無效，不能核准。");
            if (provider == "gemini" && searchKey is not null && supplied.Length == 1 && supplied[0] != "dir_path")
                throw new ArgumentException("Gemini 搜尋工具須使用明確的 dir_path，不能以其他路徑欄位推定執行範圍。");
            var path = Text(aliases);
            if (path is not null)
            {
                foreach (var key in aliases) normalized.Remove(key);
                normalized[searchKey ?? "Path"] = Path.GetFullPath(path, workspace);
            }
        }
        return (canonical, JsonSerializer.SerializeToElement(normalized));
    }
}
