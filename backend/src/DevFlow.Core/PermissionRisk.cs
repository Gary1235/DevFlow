using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFlow.Core;

public sealed record PermissionAssessment(string Category, string Label, string Reason, bool BatchAllowed = false,
    bool AutoAllowed = false, bool FixedBoundary = false)
{
    public int DeletedFileCount { get; init; }
    public string? DiagnosticCode { get; init; }
    public string ReasonCode => DiagnosticCode ?? (AutoAllowed ? "AllowedByPolicy" : Label.Contains("無法確認操作邊界", StringComparison.Ordinal) ? "UnresolvedOperation" : Category switch {
        "OperationBoundary" => "UnresolvedOperation", "Boundary" => "BoundaryConfirmation",
        "Execution" => "UnapprovedExecution", "Network" => "NetworkConfirmation",
        "Write" => "WriteConfirmation", "Deletion" => "DeletionConfirmation",
        "SensitiveCommand" => "SensitiveCommand", _ => "UnclassifiedOperation"
    });
}

// This class classifies approval requests, never the model's description of its intent.
public static class PermissionRisk
{
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".csproj", ".sln", ".slnx", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".html", ".css", ".scss", ".less", ".vue", ".svelte", ".py", ".go", ".rs", ".java", ".kt", ".swift", ".c", ".cpp", ".h", ".hpp", ".rb", ".php", ".sh", ".ps1", ".md", ".rst" };
    // Business feature names such as customer-management and users are not evidence of
    // actual customer data. Explicit secret/data locations still override code extensions.
    private static readonly Regex PrivatePath = new(@"(^|[/\\._-])(secrets?|credentials?|passwords?|tokens?|private|exports?|backups?|dumps?|personal|pii)([/\\._-]|$)|(^|[/\\])\.(env|aws|ssh|azure|config)([./\\]|$)|appsettings|connectionstrings|id_rsa|id_ed25519", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> CredentialExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".pem", ".key", ".pfx", ".p12", ".p8", ".jks", ".keystore", ".kdbx", ".crt", ".cer" };
    private static readonly HashSet<string> DatabaseExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".db", ".db3", ".sqlite", ".sqlite3", ".mdb", ".accdb", ".mdf", ".ldf", ".bak", ".sql", ".dump", ".bson" };
    private static readonly HashSet<string> DataExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".csv", ".tsv", ".xls", ".xlsx", ".parquet", ".jsonl", ".ndjson" };
    private static readonly Regex Database = new(@"database|sql|sqlite|postgres|mysql|mongodb|redis|query|mcp.*(db|data)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Deletion = new(@"(^|[ _-])(delete|remove|drop|truncate|unlink|purge)([ _-]|$)|(^|[\s;&|])(rm|rmdir|del|Remove-Item)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static bool SensitiveTarget(string relative) => PrivatePath.IsMatch(relative) ||
        relative.Split('/', '\\').Any(p => new[] { ".git", ".aws", ".ssh", ".azure" }.Contains(p, StringComparer.OrdinalIgnoreCase)) ||
        new[] { ".npmrc", ".netrc", ".gitconfig" }.Contains(Path.GetFileName(relative), StringComparer.OrdinalIgnoreCase) ||
        CredentialExtensions.Contains(Path.GetExtension(relative)) || DatabaseExtensions.Contains(Path.GetExtension(relative)) || DataExtensions.Contains(Path.GetExtension(relative));

    public static PermissionAssessment Assess(string tool, JsonElement args, string workspace)
    {
        try
        {
            var instruction = args.ValueKind == JsonValueKind.Object
                ? new[] { "CommandLine", "query", "sql", "statement" }.Select(key=>args.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null).FirstOrDefault(v=>v is not null)
                : null;
            PermissionAssessment? commandRisk=null;
            if(tool=="run_command"&&args.ValueKind==JsonValueKind.Object&&!args.TryGetProperty("AdditionalPermissions",out _)&&!args.TryGetProperty("NetworkApprovalContext",out _)){
                commandRisk=CommandPermissionRisk.Assess(args,workspace);
                // A literal search pattern such as 'DROP TABLE|sqlite' is not a database
                // operation. Prefer the parsed read operation over keyword heuristics.
                if(commandRisk?.BatchAllowed==true||commandRisk?.Category=="SensitiveRead")return commandRisk;
            }
            if (Deletion.IsMatch(tool) || instruction is not null && (Deletion.IsMatch(instruction) || Regex.IsMatch(instruction,@"\bgit\s+(reset\b.*--hard|clean\b.*-[a-z]*f|push\b.*(--force|-f\b))",RegexOptions.IgnoreCase)))
                return new("Deletion", "刪除／高影響操作", "可能刪除檔案或資料，必須逐次確認。");
            if (instruction is not null && Regex.IsMatch(instruction,@"\b(insert\s+into|update\s+\S+\s+set|create\s+table|alter\s+table)\b",RegexOptions.IgnoreCase))
                return new("Write","敏感操作：寫入資料庫","查詢包含資料或結構修改，必須逐次確認。");
            if (tool is "write_file" or "write_to_file" or "replace_file_content" or "multi_replace_file_content" or "notebook_edit" or "sed_file" || Regex.IsMatch(tool,"write|edit|rename|move|insert|update|create",RegexOptions.IgnoreCase))
                return new("Write", "敏感操作：寫入", "新增、修改、覆寫或移動內容，必須逐次確認。");
            if (Database.IsMatch(tool)) return DatabaseRead(instruction);
            if (instruction is not null && Regex.IsMatch(instruction,@"\b(sqlite3?|psql|mysql|sqlcmd|mongosh|redis-cli)\b",RegexOptions.IgnoreCase))
                return DatabaseRead(instruction);
            if (args.ValueKind != JsonValueKind.Object) return Unknown();
            if (args.TryGetProperty("AdditionalPermissions",out _) || args.TryGetProperty("NetworkApprovalContext",out _)) return Unknown("要求額外權限或網路存取，不能批次允許。");
            if(tool=="run_command")return commandRisk??CommandPermissionRisk.Assess(args,workspace)??Unknown("指令語法、參數或組合方式未在已支援的唯讀範圍內，必須逐次確認。");
            var key = tool switch { "read_file"=>"Path", "view_file"=>"AbsolutePath", "list_dir"=>"DirectoryPath", "find_by_name"=>"SearchDirectory", "grep_search"=>"SearchPath", _=>null };
            if (key is null) return Unknown("指令、腳本或未辨識工具無法確定只有一般讀取，必須逐次確認。");
            if (tool == "find_by_name" && PermissionPolicy.SearchPattern(args) is {} patternRisk) return patternRisk;
            if (!args.TryGetProperty(key,out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) return Unknown("工具未提供明確的目標路徑。");
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
            var full = Path.GetFullPath(value.GetString()!,root);
            var relative = Path.GetRelativePath(root,full).Replace('\\','/');
            if (relative == ".." || relative.StartsWith("../") || Path.IsPathRooted(relative))
                return new("SensitiveRead","敏感讀取：專案外檔案","目標不在本次專案工作副本內。");
            for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current)||Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    return Unknown("路徑經過符號連結或目錄連接，無法確認實際讀取範圍。");
                if (current == root) break;
            }
            if (PrivatePath.IsMatch(relative) || relative.Split('/').Any(p=>p.Equals(".git",StringComparison.OrdinalIgnoreCase)))
                return new("SensitiveRead","敏感讀取：機敏資料","路徑可能包含帳密、憑證、個資或敏感設定。");
            if (tool is "read_file" or "view_file")
            {
                var extension = Path.GetExtension(full);
                if (CredentialExtensions.Contains(extension))
                    return new("SensitiveRead","敏感讀取：金鑰／憑證","檔案類型可能包含私鑰、憑證或密碼庫，必須逐次確認。");
                if (DatabaseExtensions.Contains(extension))
                    return new("SensitiveRead","敏感讀取：資料庫／備份","資料庫檔案、SQL 資料或備份可能包含實際紀錄，必須逐次確認。");
                if (DataExtensions.Contains(extension))
                    return new("SensitiveRead","敏感讀取：資料／匯出檔","表格、資料匯出或資料集可能包含實際紀錄，必須逐次確認。");
                if (!CodeExtensions.Contains(extension))
                    return new("SensitiveRead","敏感讀取：資料或設定","目標不是已辨識的程式碼或一般開發文件；資料庫檔、資料匯出與設定需單次確認。");
                return new("GeneralRead","一般讀取：程式碼／文件","目標位於本次工作副本內，副檔名屬於程式碼或開發文件，且未命中機敏路徑規則。",true);
            }
            if (tool == "grep_search") return Unknown("內容搜尋可能同時讀取機敏檔案；目前無法確認搜尋器確實排除敏感內容。");
            if (tool == "find_by_name") {
                var searchRisk = PermissionPolicy.ReadSet(PermissionPolicySettings.Defaults, full, workspace, namesOnly: true);
                if (!searchRisk.AutoAllowed) return searchRisk;
            }
            return new("GeneralRead","一般讀取：專案結構","只列出本次工作副本內的目錄或檔名，不讀取檔案內容。",true);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        { return Unknown("無法確認操作目標與範圍，必須逐次確認。"); }
    }
    private static PermissionAssessment Unknown(string reason = "無法辨識操作，必須逐次確認。") => new("Unknown","需確認：未知操作",reason);
    private static PermissionAssessment DatabaseRead(string? instruction)=>instruction is not null&&Regex.IsMatch(instruction,@"information_schema|sqlite_master|sqlite_schema|pragma\s+table_info|sys\.(tables|columns)|show\s+(tables|columns)|describe\s",RegexOptions.IgnoreCase)
        ?new("SensitiveRead","敏感讀取：資料庫結構","會連線查詢資料表、欄位或結構資訊，仍需單次確認。")
        :new("SensitiveRead","敏感讀取：資料庫資料／查詢","可能讀取實際資料或執行查詢，必須單次確認。");
}
