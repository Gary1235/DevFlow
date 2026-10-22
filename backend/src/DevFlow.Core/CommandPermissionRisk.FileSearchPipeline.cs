using System.Text.Json;
using System.Text.RegularExpressions;
namespace DevFlow.Core;

internal static partial class CommandPermissionRisk
{
    // FileInfo pipeline input makes Select-String read file contents. Validate the
    // complete producer and every matching file before permitting that stage.
    private static PermissionAssessment? FileSearchPipeline(JsonElement args, string workspace, List<string> segments, PermissionPolicySettings? policy = null)
    {
        if (!segments.Skip(1).Any(s => Regex.IsMatch(s,@"\ASelect-String(?:\s|\z)",RegexOptions.IgnoreCase))) return null;
        if (segments.Count < 2 || Tokenize(segments[0]) is not {Count:>0} producer || !producer[0].Equals("Get-ChildItem",StringComparison.OrdinalIgnoreCase) ||
            !SafeFileContentSearchStage(segments[1]) || segments.Skip(2).Any(s => !SafePipelineStage(s)))
            return Unsupported("內容搜尋管線必須來自明確篩選的檔案清單；其他來源、參數或階段仍需確認。");
        string? path = null; string[]? includes = null; var files = false; var recursive = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i=1;i<producer.Count;i++) {
            var word=producer[i]; var lower=word.ToLowerInvariant();
            if (word.StartsWith('-') && !seen.Add(lower)) return Unsupported("檔案清單含重複參數。");
            if (lower=="-file") { files=true;continue; }
            if (lower=="-recurse") { recursive=true;continue; }
            if (lower=="-force") continue;
            if (lower is "-path" or "-literalpath") { if (path is not null || ++i==producer.Count) return Unsupported("檔案清單需提供一個明確路徑。"); path=producer[i];continue; }
            if (lower=="-include") {
                if (++i==producer.Count) return Unsupported("檔案類型篩選遺失。");
                includes=producer[i].Split(',');
                if (includes.Length>16 || includes.Any(p => policy is null ? !Regex.IsMatch(p,@"\A\*\.[A-Za-z0-9]+\z") : !Regex.IsMatch(p,@"\A[A-Za-z0-9.*?_-]+\z"))) return Unsupported("檔案篩選需使用簡單檔名或萬用字元，不可包含路徑或腳本。");
                continue;
            }
            if (word.StartsWith('-') || path is not null) return Unsupported("檔案清單含未支援的參數或多個路徑。");
            path=word;
        }
        // The strict/manual policy keeps its original grammar. Automation checks
        // all actual targets, so -File and extension filters need not be mandatory.
        if (policy is null && (!files || includes is null)) return Unsupported("內容搜尋需明確的 -File、-Include 與專案內路徑。");
        path ??= ".";
        if (path.IndexOfAny(['*','?','[',']','~',','])>=0 || policy is null && path.Contains(':')) return Unsupported("內容搜尋需提供明確的專案內路徑。");
        if(args.TryGetProperty("Cwd",out var location) && location.ValueKind!=JsonValueKind.String)return Unsupported("工作目錄格式未受支援。");
        var cwd=location.ValueKind==JsonValueKind.String ? Path.GetFullPath(location.GetString()!,workspace) : workspace;
        var full=Path.GetFullPath(path,cwd);
        PermissionAssessment Check(string target,bool content) => policy is null
            ? PathRisk(content?"read_file":"list_dir",content?"Path":"DirectoryPath",target,workspace)
            : PermissionPolicy.Target(policy,target,workspace,false);
        bool Safe(PermissionAssessment risk)=>policy is null?risk.BatchAllowed:risk.AutoAllowed;
        var cwdRisk=Check(cwd,false);if(!Safe(cwdRisk))return cwdRisk;
        var rootRisk=Check(full,false);if(!Safe(rootRisk))return rootRisk;
        if (!Directory.Exists(full)) return Unsupported("檔案搜尋需提供存在的目錄。");
        if (policy is not null) return PermissionPolicy.ReadSet(policy,full,workspace,recursive,includes);
        var pending=new Stack<string>();pending.Push(full);var count=0;
        while(pending.TryPop(out var directory)) foreach(var entry in Directory.EnumerateFileSystemEntries(directory)) {
            if(++count>20000)return BroadSearch();
            var attributes=File.GetAttributes(entry);
            if(attributes.HasFlag(FileAttributes.ReparsePoint))return Unsupported("搜尋範圍含符號連結，無法確認實際讀取目標。");
            if(attributes.HasFlag(FileAttributes.Directory)){if(recursive)pending.Push(entry);continue;}
            if(!includes!.Any(glob=>System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(glob,Path.GetFileName(entry),true)))continue;
            var risk=Check(entry,true);if(!Safe(risk))return risk;
        }
        return policy is null?General("限定檔案類型的內容搜尋管線"):PermissionPolicy.Allow("GeneralRead","限定檔案類型的內容搜尋管線");
    }
    private static bool SafeFileContentSearchStage(string stage)
    {
        if(Tokenize(stage) is not {Count:>0} words || !words[0].Equals("Select-String",StringComparison.OrdinalIgnoreCase))return false;
        var pattern=false;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(var i=1;i<words.Count;i++) {
            var lower=words[i].ToLowerInvariant();if(!seen.Add(lower.Split(':')[0]))return false;
            if(lower=="-pattern"){if(++i==words.Count)return false;pattern=true;continue;}
            if(new[]{"-simplematch","-list","-quiet","-allmatches","-notmatch","-noemphasis","-casesensitive","-casesensitive:$false","-casesensitive:$true"}.Contains(lower))continue;
            if(lower=="-context"){if(++i==words.Count||!Regex.IsMatch(words[i],@"\A[0-9]{1,4}(,[0-9]{1,4})?\z"))return false;continue;}
            if(lower=="-encoding"){if(++i==words.Count||!new[]{"utf8","utf8bom","utf8nobom","unicode","ascii","bigendianunicode","default","oem"}.Contains(words[i].ToLowerInvariant()))return false;continue;}
            return false;
        }
        return pattern;
    }
}
