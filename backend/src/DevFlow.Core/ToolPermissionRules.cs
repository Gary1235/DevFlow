using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFlow.Core;

public sealed record ToolPermissionRule(Guid Id, Guid ProjectId, Guid? TaskId, string Tool, string Kind,
    string Pattern, bool Enabled = true, int Version = 0, DateTimeOffset CreatedAt = default, string Provider = "antigravity", Guid? DiscussionId = null);
public sealed record ToolPermissionOption(string Kind, string Pattern, string Label);
public sealed record ToolPermissionChoice(string Scope = "Once", string Kind = "Exact", string? Pattern = null, string Provider = "*");

public static class ToolPermissionMatching
{
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string? PathKey(string tool) => tool switch
    {
        "read_file" or "write_file" => "Path", "view_file" => "AbsolutePath", "list_dir" => "DirectoryPath",
        "grep_search" => "SearchPath", "find_by_name" => "SearchDirectory", _ => null
    };
    public static ToolPermissionOption[] Options(string tool, JsonElement args, string workspace)
    {
        return PermissionRisk.Assess(tool,args,workspace).BatchAllowed
            ? [new("GeneralRead", ".", "本次專案的一般讀取（程式碼、文件與目錄）")]
            : [];
    }
    public static ToolPermissionRule Validate(ToolPermissionRule rule)
    {
        if (rule.Id == Guid.Empty || rule.ProjectId == Guid.Empty || !Regex.IsMatch(rule.Tool, @"\A[a-z][a-z0-9_]{0,99}\z")) throw new ArgumentException("工具規則格式無效。");
        if (rule.Provider != "*") AiSelection.Normalize(new(rule.Provider));
        if (rule.TaskId is not null && rule.DiscussionId is not null) throw new ArgumentException("規則不能同時綁定任務與對話。");
        var pattern = WorkTask.Required(rule.Pattern, 40000);
        switch (rule.Kind)
        {
            case "GeneralRead":
                if (rule.Tool != "general_read" || pattern != "." || rule.TaskId is null && rule.DiscussionId is null)
                    throw new ArgumentException("一般讀取規則只能限本次任務或對話，不支援專案永久允許。");
                break;
            case "Exact":
                try
                {
                    using var json = JsonDocument.Parse(pattern);
                    if (json.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("完整參數必須是 JSON 物件。");
                    pattern = Canonical(json.RootElement, "", rule.Tool);
                }
                catch (JsonException) { throw new ArgumentException("完整參數必須是有效 JSON。"); }
                break;
            case "CommandPrefix":
                if (rule.Tool != "run_command" || Tokens(pattern) is not { Length: > 0 } tokens || !PrefixValid(tokens[0]))
                    throw new ArgumentException("此前綴包含 shell 語法或直譯器，請改用相同工具與參數。");
                pattern = string.Join(' ', tokens); break;
            case "WorkspacePath":
                if (PathKey(rule.Tool) is null || pattern != "." && (Path.IsPathRooted(pattern) || pattern.Contains('\\') || pattern.Split('/').Any(p => p is "" or "." or ".." || p.IndexOfAny(['*', '?', ':']) >= 0)))
                    throw new ArgumentException("檔案範圍需為專案相對目錄，或以 . 代表工作副本內。");
                break;
            default: throw new ArgumentException("未知的工具比對方式。");
        }
        return rule with { Pattern = pattern };
    }
    public static bool Matches(ToolPermissionRule rule, Guid projectId, Guid taskId, string tool, JsonElement args, string workspace, string provider = "antigravity", Guid? discussionId = null)
    {
        // Risk always wins, including over rules saved by older versions.
        if (!PermissionRisk.Assess(tool,args,workspace).BatchAllowed) return false;
        if (!rule.Enabled || rule.ProjectId != projectId || rule.TaskId is { } id && id != taskId || rule.TaskId is null && rule.DiscussionId is null) return false;
        if (rule.Provider != "*" && rule.Provider != provider || rule.DiscussionId is { } d && d != discussionId) return false;
        return rule.Kind == "GeneralRead" && rule.Tool == "general_read" && rule.Pattern == ".";
    }
    private static string[]? Tokens(string text)
    {
        // Compound commands, expansion and execution flags need an exact one-use decision.
        if (text.IndexOfAny([';', '&', '|', '`', '$', '<', '>', '^', '%', '!', '\r', '\n', '(', ')', '{', '}']) >= 0) return null;
        var matches = Regex.Matches(text.Trim(), "(?:\"[^\"]*\"|'[^']*'|[^\\s\"']+)");
        var tokens = matches.Select(m => m.Value).ToArray();
        if (tokens.Any(t => t is "-c" or "-e" or "--pre" || t.StartsWith("--exec", StringComparison.OrdinalIgnoreCase) || t.StartsWith("--to-command", StringComparison.OrdinalIgnoreCase))) return null;
        return tokens.Length > 0 && Regex.Replace(string.Concat(tokens), @"\s", "") == Regex.Replace(text, @"\s", "") ? tokens : null;
    }
    private static bool PrefixValid(string token) => Regex.IsMatch(token, @"\A[A-Za-z][A-Za-z0-9_.-]*\z") &&
        !new[] { "cmd", "powershell", "pwsh", "bash", "sh", "zsh", "node", "python", "python3", "ruby", "perl", "wscript", "cscript", "invoke-expression", "iex", "eval", "call" }
            .Contains(Path.GetFileNameWithoutExtension(token), StringComparer.OrdinalIgnoreCase);
    public static string Canonical(JsonElement args, string workspace, string tool)
    {
        object? Value(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Where(p => tool != "run_command" || p.Name is not ("toolSummary" or "WaitMsBeforeAsync"))
                .OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => Value(p.Value)),
            JsonValueKind.Array => value.EnumerateArray().Select(Value).ToArray(),
            JsonValueKind.String => Normalize(value.GetString()!),
            _ => (object)value.Clone()
        };
        string Normalize(string text)
        {
            if (string.IsNullOrEmpty(workspace)) return text;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
            if (text.Equals(root, Comparison)) return "{workspace}";
            if (text.StartsWith(root + Path.DirectorySeparatorChar, Comparison) || text.StartsWith(root + '/', Comparison))
                return "{workspace}/" + text[(root.Length + 1)..].Replace('\\', '/');
            return text;
        }
        return JsonSerializer.Serialize(Value(args));
    }
}
