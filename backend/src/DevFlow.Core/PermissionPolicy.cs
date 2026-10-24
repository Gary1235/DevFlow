using System.Text.Json;
using System.Text.RegularExpressions;
namespace DevFlow.Core;

public sealed record PermissionPolicySettings(int Version = 0, bool AutomationEnabled = true,
    string[]? SensitivePaths = null, string[]? SensitiveCommands = null, string[]? SensitiveDestinations = null,
    string[]? ApprovedDownloadHosts = null)
{
    public static PermissionPolicySettings Defaults => new(ApprovedDownloadHosts: ["registry.npmjs.org", "api.nuget.org"]);
    public static PermissionPolicySettings Normalize(PermissionPolicySettings value)
    {
        static string[] Lines(string[]? values) {
            if ((values?.Length ?? 0) > 100) throw new ArgumentException("各敏感名單最多 100 項。");
            return (values ?? []).Select(s => WorkTask.Required(s, 500).Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        var paths = Lines(value.SensitivePaths);
        if (paths.Any(p => Path.IsPathRooted(p) || p.Contains(':') || p.Split('/').Any(s => s == ".."))) throw new ArgumentException("敏感路徑請填專案相對路徑或 glob。");
        var hosts = Lines(value.ApprovedDownloadHosts);
        if (hosts.Any(h => !Uri.CheckHostName(h).Equals(UriHostNameType.Dns) || h.Contains('*'))) throw new ArgumentException("下載來源請填完整網域名稱，不含網址路徑或萬用字元。");
        return value with { SensitivePaths = paths, SensitiveCommands = Lines(value.SensitiveCommands), SensitiveDestinations = Lines(value.SensitiveDestinations), ApprovedDownloadHosts = hosts.Select(h => h.ToLowerInvariant()).ToArray() };
    }
}

public static partial class PermissionPolicy
{
    public static PermissionAssessment WorkspaceTarget(string path, string workspace, bool writing = false, string[][]? scopes = null) =>
        Target(PermissionPolicySettings.Defaults, path, workspace, writing, scopes);
    internal static PermissionAssessment Ask(string category, string label, string reason, bool boundary = false) => new(category, label, reason, FixedBoundary: boundary);
    internal static PermissionAssessment Allow(string category, string label) => new(category, "自動允許：" + label,
        "操作限本次工作副本且符合固定邊界；副本內檔案視為已授權存取，不依敏感名稱攔截。保留原 CLI 政策與稽核紀錄。", AutoAllowed: true);
    internal static PermissionAssessment Unknown() => WorkspaceFileOperations.Unresolved("無法確認所有目標或副作用；可能執行額外腳本或越過工作副本邊界，需要額外授權。");
    // Compose effects across the entire request, including nested sequences and
    // pipelines. Splitting deletions into single-file stages is still bulk deletion.
    internal static PermissionAssessment Combine(PermissionAssessment first, PermissionAssessment next)
    {
        var deleted = first.DeletedFileCount + next.DeletedFileCount;
        if (deleted > 1) return Ask("Deletion", "需確認：大量刪除", "同一次指令請求包含多筆刪除，需要額外授權。", true) with { DeletedFileCount = deleted };
        var category = first.Category == "Deletion" || next.Category == "Deletion" ? "Deletion"
            : first.Category == "Write" || next.Category == "Write" ? "Write" : "GeneralRead";
        return Allow(category, category == "Deletion" ? "工作副本內的單檔刪除及其他操作"
            : category == "Write" ? "工作副本內的修改及其他操作" : "工作副本內的一般查詢") with { DeletedFileCount = deleted };
    }
    internal static bool Match(string pattern, string text) {
        var expression = Regex.Escape(pattern.Replace('\\', '/')).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
        return Regex.IsMatch(text.Replace('\\', '/'), "\\A" + expression + "\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
    internal static bool ListedPath(PermissionPolicySettings settings, string relative) => (settings.SensitivePaths ?? []).Any(p =>
        Match(p, relative) || !p.Contains('/') && Match(p, Path.GetFileName(relative)) ||
        relative.Equals(p.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) || relative.StartsWith(p.TrimEnd('/') + '/', StringComparison.OrdinalIgnoreCase));

    public static PermissionAssessment Assess(PermissionPolicySettings settings, string tool, JsonElement args, string workspace,
        bool readOnly = false, string[][]? editScopes = null, ProjectDefinition? project = null)
    {
        if (!settings.AutomationEnabled) return PermissionRisk.Assess(tool, args, workspace);
        try {
            if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != args.EnumerateObject().Count()) return Unknown();
            if (args.TryGetProperty("AdditionalPermissions", out _) || args.TryGetProperty("NetworkApprovalContext", out _))
                return Ask("Boundary", "需確認：額外權限／網路擴張", "原生 CLI 要求擴張權限，不能由自動化政策代為開放。", true);
            if (tool == "run_validation") return SavedValidation(settings, args, workspace, project, readOnly);
            if (tool == "run_command") {
                var commandRisk = CommandPermissionRisk.AssessPolicy(settings, args, workspace, readOnly, editScopes);
                return project is null ? commandRisk : TrustedValidation(commandRisk, args, workspace, project, readOnly);
            }
            var writing = tool is "write_file" or "edit_file" or "write_to_file" or "replace_file_content" or "multi_replace_file_content" or "notebook_edit" or "sed_file" or "move_file" or "rename_file" or "copy_file";
            var deleting = tool is "delete_file" or "remove_file";
            var reading = tool is "read_file" or "view_file" or "list_dir" or "find_by_name" or "grep_search";
            if (!writing && !deleting && !reading) return PermissionRisk.Assess(tool, args, workspace);
            if ((writing || deleting) && readOnly) return Ask("Write", "需確認：唯讀階段的修改", "規劃、討論及 AI 審查維持唯讀，不自動授權寫入或刪除。", true);
            if(tool is "copy_file" or "move_file" or "rename_file")return NativeTransfer(settings,tool,args,workspace,editScopes);
            var paths = new List<string>();
            var deletedFiles=0;
            foreach (var key in new[] { "Path", "AbsolutePath", "TargetFile", "DirectoryPath", "SearchDirectory", "SearchPath", "SourcePath", "DestinationPath", "TargetPath" })
                if (args.TryGetProperty(key, out var value)) {
                    if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) return Unknown();
                    paths.Add(value.GetString()!);
                }
            if (args.TryGetProperty("changes", out var changes)) {
                if (changes.ValueKind != JsonValueKind.Array || changes.GetArrayLength() == 0) return Unknown();
                foreach (var change in changes.EnumerateArray()) {
                    if (change.ValueKind != JsonValueKind.Object) return Unknown();
                    if (!change.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) return Unknown();
                    paths.Add(path.GetString()!);
                    if (change.TryGetProperty("move_path", out var directMove) && directMove.ValueKind == JsonValueKind.String) paths.Add(directMove.GetString()!);
                    if (change.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.Object && kind.TryGetProperty("move_path", out var move) && move.ValueKind == JsonValueKind.String) paths.Add(move.GetString()!);
                    if (kind.ValueKind != JsonValueKind.Object || !kind.TryGetProperty("type", out var type) || type.GetString() is not ("add" or "update" or "delete")) return Unknown();
                    if(type.GetString()=="delete"&&++deletedFiles>1)return Ask("Deletion","需確認：大量刪除","原生檔案變更包含多筆刪除，需要額外授權。",true);
                    if (type.GetString() == "delete" && Directory.Exists(Path.GetFullPath(path.GetString()!, workspace))) return Unknown();
                }
            }
            if (paths.Count == 0 || args.EnumerateObject().Any(p => new[] { "command", "CommandLine", "AdditionalPermissions", "permissions", "mode" }.Contains(p.Name))) return Unknown();
            if (args.TryGetProperty("grantRoot", out _) && !args.TryGetProperty("changes", out _)) return Unknown();
            if (deleting && paths.Count > 1) return Ask("Deletion", "需確認：大量刪除", "同一次工具請求包含多筆刪除，需要額外授權。", true);
            if (tool == "find_by_name" && SearchPattern(args) is {} patternRisk) return patternRisk;
            foreach (var path in paths) {
                var risk = Target(settings, path, workspace, writing || deleting, editScopes);
                if (!risk.AutoAllowed) return risk;
                var full = Path.GetFullPath(path, workspace);
                if (deleting && (Directory.Exists(full) || path.EndsWith('/') || path.EndsWith('\\')))
                    return Ask("Deletion", "需確認：目錄／大量刪除", "刪除整個目錄需逐次確認；一般檔案刪除才可在授權範圍內自動執行。", true);
            }
            if (tool is "grep_search" or "find_by_name") foreach (var path in paths) {
                var searchRisk = ReadSet(settings, path, workspace, namesOnly: tool == "find_by_name");
                if (!searchRisk.AutoAllowed) return searchRisk;
            }
            return Allow(deleting||deletedFiles>0 ? "Deletion" : writing ? "Write" : "GeneralRead", deleting||deletedFiles>0 ? "工作副本內的一般檔案刪除" : writing ? "工作副本內的一般修改" : "工作副本內的一般讀取") with { DeletedFileCount = deleting ? paths.Count : deletedFiles };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException) { return Unknown(); }
    }

    internal static PermissionAssessment Target(PermissionPolicySettings settings, string path, string workspace, bool writing, string[][]? scopes = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (path.StartsWith('~') || path.IndexOfAny(['*', '?', '\0']) >= 0 || path.Contains(':') && !Path.IsPathRooted(path)) return WorkspaceFileOperations.Unresolved("目標含未解析的路徑展開或特殊命名空間。");
        if (OperatingSystem.IsWindows() && path.Split('/', '\\').Any(p => p is not ("." or "..") && p != p.TrimEnd(' ', '.'))) return Unknown();
        var full = Path.GetFullPath(path, root);
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../") || Path.IsPathRooted(relative))
            return Ask("Boundary", "需確認：工作副本外存取", $"目標 {full} 超出本次任務工作副本 {root}；尚未自動授權，請改用副本內路徑，或說明外部存取用途以取得額外授權。", true);
        if (relative.Contains(':') || OperatingSystem.IsWindows() && relative.Split('/').Any(p => p != "." && p != p.TrimEnd(' ', '.'))) return Unknown();
        if (OperatingSystem.IsWindows() && relative.Split('/').Any(p => Regex.IsMatch(p.Split('.')[0], @"\A(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) return Unknown();
        for (var current = full; current is not null; current = Path.GetDirectoryName(current)) {
            try {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return Ask("Boundary", "需確認：符號連結／目錄連接", "無法證實實際存取仍在工作副本內。", true);
            } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            if (current.Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }
        // The developer supplies an isolated worktree. File names/extensions and
        // legacy sensitive-path entries are not access boundaries inside it.
        if (writing) {
            if (relative == "." || relative.Split('/').Any(p => new[] { ".git", ".agents", ".codex", ".claude", ".gemini", ".antigravity", ".agy", ".devflow" }.Contains(p, StringComparer.OrdinalIgnoreCase)) ||
                new[] { "AGENTS.md", "AGENTS.override.md", "CLAUDE.md", "CLAUDE.local.md", "GEMINI.md", ".mcp.json", ".gitignore", ".gitattributes", ".gitmodules" }.Contains(Path.GetFileName(relative), StringComparer.OrdinalIgnoreCase))
                return Ask("Boundary", "需確認：權限／代理設定修改", "修改代理指示、權限設定、Git 控制檔或工作副本根目錄必須由使用者確認。", true);
            foreach (var scope in scopes ?? []) if (scope.Length > 0 && !scope.Any(p => relative.Equals(p.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) || p.EndsWith('/') && relative.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return Ask("Boundary", "需確認：超出任務修改範圍", "目標未包含於任務及目前步驟允許修改的路徑。", true);
        }
        return Allow(writing ? "Write" : "GeneralRead", "工作副本內目標");
    }

    internal static PermissionAssessment ReadPattern(PermissionPolicySettings settings, string path, string workspace, bool recursive = false, bool namesOnly = false, bool rejectWildcardNames = false)
    {
        if(path.IndexOfAny(['[',']'])>=0)return WorkspaceFileOperations.Unresolved("路徑含字元集合比對，尚無法確認完整展開目標；可改用 LiteralPath。");
        if (path.IndexOfAny(['*','?']) < 0) return ReadSet(settings,path,workspace,recursive,namesOnly:namesOnly,rejectWildcardNames:rejectWildcardNames);
        var wildcard=path.IndexOfAny(['*','?']);
        var separator=path.LastIndexOfAny(['/','\\'],wildcard);
        if(separator<0)return Unknown();
        var parent=path[..(separator+1)];
        var boundary=Target(settings,parent,workspace,false);
        if(!boundary.AutoAllowed)return boundary;
        if(!Directory.Exists(parent))return Unknown();
        if(recursive) {
            // PowerShell recurses below directories even when the final filename
            // glob matches nothing at the root. Validate the entire anchor tree.
            var treeRisk=ReadSet(settings,parent,workspace,recursive:true,namesOnly:true,rejectWildcardNames:rejectWildcardNames);
            if(!treeRisk.AutoAllowed)return treeRisk;
        }
        var components=path[(separator+1)..].Split(['/','\\'],StringSplitOptions.RemoveEmptyEntries);
        var pending=new Stack<(string Directory,int Component)>();pending.Push((parent,0));var count=0;
        while(pending.TryPop(out var current))foreach(var entry in Directory.EnumerateFileSystemEntries(current.Directory)) {
            if(++count>20000)return WorkspaceFileOperations.Unresolved("萬用字元展開超出檢查上限，無法確認全部目標。");
            if(!System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(components[current.Component],Path.GetFileName(entry),OperatingSystem.IsWindows()))continue;
            var entryRisk=Target(settings,entry,workspace,false);
            if(!entryRisk.AutoAllowed)return entryRisk;
            if(current.Component+1<components.Length) {
                if(Directory.Exists(entry))pending.Push((entry,current.Component+1));
            } else {
                var risk=ReadSet(settings,entry,workspace,recursive,namesOnly:namesOnly,rejectWildcardNames:rejectWildcardNames);
                if(!risk.AutoAllowed)return risk;
            }
        }
        return Allow("GeneralRead","工作副本內的檔案篩選讀取");
    }

    // Inspect for links and traversal boundaries, regardless of file names/types.
    internal static PermissionAssessment ReadSet(PermissionPolicySettings settings, string path, string workspace,
        bool recursive = true, string[]? includes = null, bool namesOnly = false, bool rejectWildcardNames = false)
    {
        var rootRisk = Target(settings, path, workspace, false);
        if (!rootRisk.AutoAllowed) return rootRisk;
        var full = Path.GetFullPath(path, workspace);
        if (rejectWildcardNames && full.IndexOfAny(['*','?','[',']']) >= 0)
            return WorkspaceFileOperations.Unresolved("FullName 清單包含會被 -Path 展開的字元，請改用 -LiteralPath。");
        if (!Directory.Exists(full)) return rootRisk;
        var pending = new Stack<string>(); pending.Push(full); var count = 0;
        while (pending.TryPop(out var directory)) foreach (var entry in Directory.EnumerateFileSystemEntries(directory)) {
            if (++count > 20000) return Ask("Boundary", "需確認：搜尋範圍過大", $"已檢查超過 20,000 個項目，目前目錄：{directory}。請直接指定 src、tests 等較小搜尋根，或單次確認；不因工具的 ignore 設定就略過邊界檢查。", true) with { DiagnosticCode = "SearchLimit" };
            var attributes = File.GetAttributes(entry);
            if (rejectWildcardNames && entry.IndexOfAny(['*','?','[',']']) >= 0)
                return WorkspaceFileOperations.Unresolved("FullName 清單包含會被 -Path 展開的字元，請改用 -LiteralPath。");
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                return Ask("Boundary", "需確認：符號連結／目錄連接", "搜尋範圍包含可能跳出工作副本的連結。", true);
            if (attributes.HasFlag(FileAttributes.Directory)) { if (recursive) pending.Push(entry); continue; }
            if (namesOnly || includes is not null && !includes.Any(glob => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(glob, Path.GetFileName(entry), true))) continue;
            var risk = Target(settings, entry, workspace, false);
            if (!risk.AutoAllowed) return risk;
        }
        return Allow("GeneralRead", namesOnly ? "工作副本內的目錄／檔名查詢" : "已檢查完整目標的一般內容搜尋");
    }
}
