using System.Text;
using System.Text.RegularExpressions;

namespace DevFlow.Core;

// Automation uses operation semantics and complete target sets, independently
// of the legacy read whitelist. This parser never executes PowerShell.
internal static partial class WorkspaceFileOperations
{
    private sealed record Argument(string[] Values, bool Quoted) {
        public bool Parameter => !Quoted && Values.Length == 1 && Values[0].StartsWith('-');
    }
    internal sealed record ReadPlan(string Operation, string[] Paths, bool Literal, bool Recursive, bool NamesOnly, bool CheckExpandedPaths = false);
    internal static PermissionAssessment Unresolved(string reason) => new("Boundary", "需確認：無法確認操作邊界", reason, FixedBoundary:true);
    internal static string NormalizeName(string name) => OperatingSystem.IsWindows() ? name.ToLowerInvariant() switch {
        "gci" or "dir" or "ls"=>"get-childitem", "gc" or "cat" or "type"=>"get-content", "sls"=>"select-string",
        "sc"=>"set-content", "ac"=>"add-content", "ni"=>"new-item",
        "cp" or "copy" or "cpi"=>"copy-item", "mv" or "move" or "mi"=>"move-item",
        "ren" or "rni"=>"rename-item", "rm" or "del" or "ri"=>"remove-item", _=>name.ToLowerInvariant()
    } : name.ToLowerInvariant();
    private static string? Operation(string name) => NormalizeName(name) switch {
        "get-childitem" => "list", "get-content" => "read", "select-string" => "search",
        "get-item" or "test-path" => "info", "get-filehash" => "hash",
        _ => null
    };
    internal static bool IsFileCommand(string name)=>Operation(name) is not null || NormalizeName(name) is "set-content" or "add-content" or "new-item" or "copy-item" or "move-item" or "rename-item" or "remove-item" or "out-file";
    internal static string? CommandName(string command) => Regex.Match(command, @"\A\s*([A-Za-z][A-Za-z0-9-]*)(?=\s|\z)").Groups[1].Value is {Length:>0} name ? name : null;
    internal static string? Canonical(string command) => Parse(command) is {} args?string.Join(' ',args.Select((a,i)=>i==0&&a.Values.Length==1?NormalizeName(a.Values[0]):string.Join(',',a.Values))):null;

    internal static bool TryRead(string command, out ReadPlan? plan, out PermissionAssessment? error, bool fromPipeline = false)
    {
        plan=null; error=null;
        var name=CommandName(command);
        var operation=name is null?null:Operation(name);
        if(operation is null)return false;
        if(Parse(command) is not {} args) {error=Unresolved("檔案操作含動態展開、腳本或無法確認的參數運算，需確認實際邊界。");return true;}
        if(args[0].Values.Length!=1||args[0].Quoted||!args[0].Values[0].Equals(name,StringComparison.OrdinalIgnoreCase)){error=Unresolved("指令名稱不是單一明確操作。");return true;}
        var paths=new List<string>(); var literal=false; var recursive=false; var pattern=false;
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathSeen=false; var positionalPattern=false;
        // Local failures are collected before constructing the assessment.
        string? errorPlaceholder=null;
        for(var i=1;i<args.Count;i++) {
            var arg=args[i];
            if(!arg.Parameter) {
                if(operation=="search"&&!pattern&&!positionalPattern){pattern=true;positionalPattern=true;continue;}
                if(pathSeen){errorPlaceholder="檔案路徑參數重複或位置不明確。";break;}
                paths.AddRange(arg.Values);pathSeen=true;continue;
            }
            var flag=arg.Values[0].ToLowerInvariant();
            if(!seen.Add(flag)){errorPlaceholder="檔案操作包含重複參數，無法確認實際邊界。";break;}
            if(flag is "-path" or "-literalpath") {
                if(pathSeen||++i==args.Count||args[i].Parameter){errorPlaceholder="請提供完整的檔案路徑清單。";break;}
                paths.AddRange(args[i].Values);pathSeen=true;literal=flag=="-literalpath";continue;
            }
            if(operation=="list"&&flag is "-recurse" or "-depth") {
                recursive=true;
                if(flag=="-depth"&&(++i==args.Count||args[i].Values.Length!=1||!int.TryParse(args[i].Values[0],out var depth)||depth<0)){errorPlaceholder="目錄深度必須是非負整數。";break;}
                continue;
            }
            if(operation=="list"&&flag is "-file" or "-directory" or "-name" or "-hidden" || flag=="-force"&&operation is "list" or "read" or "info")continue;
            if(operation=="read"&&flag is "-raw" or "-wait" or "-asbytestream")continue;
            if(operation=="info"&&flag=="-isvalid"&&name!.Equals("Test-Path",StringComparison.OrdinalIgnoreCase))continue;
            if(operation=="search"&&new[]{"-simplematch","-list","-quiet","-allmatches","-notmatch","-noemphasis","-casesensitive","-casesensitive:$false","-casesensitive:$true"}.Contains(flag))continue;
            if(flag is "-verbose" or "-debug")continue;
            if(++i==args.Count||args[i].Parameter){errorPlaceholder="檔案操作的參數值不完整。";break;}
            var values=args[i].Values;
            if(operation=="search"&&flag=="-pattern"){pattern=true;continue;}
            if(flag is "-include" or "-exclude" or "-filter") {
                if(values.Any(v=>v.IndexOfAny(['/','\\',':'])>=0)){errorPlaceholder="檔名篩選不得改變搜尋路徑。";break;}
                continue;
            }
            if(operation=="read"&&flag=="-delimiter"&&values.Length==1)continue;
            if(operation=="search"&&flag=="-culture"&&values.Length==1)continue;
            if(flag is "-erroraction" or "-warningaction" or "-informationaction" && values.Length==1&&new[]{"stop","continue","silentlycontinue","ignore","inquire"}.Contains(values[0].ToLowerInvariant()))continue;
            // Encoding selects a decoder, not another file or executable. Let the
            // actual cmdlet validate version-specific names/code pages.
            if(operation is "read" or "search"&&flag=="-encoding"&&values.Length==1)continue;
            if(operation=="read"&&flag is "-totalcount" or "-tail" or "-readcount"&&values.Length==1&&int.TryParse(values[0],out var number)&&number>=0)continue;
            if(operation=="search"&&flag=="-context"&&values.Length<=2&&values.All(v=>int.TryParse(v,out var contextCount)&&contextCount>=0))continue;
            if(operation=="hash"&&flag=="-algorithm"&&values.Length==1&&new[]{"sha256","sha384","sha512","sha1","md5"}.Contains(values[0].ToLowerInvariant()))continue;
            if(operation=="info"&&flag=="-pathtype"&&values.Length==1&&new[]{"leaf","container","any"}.Contains(values[0].ToLowerInvariant()))continue;
            errorPlaceholder=$"參數 {flag} 的副作用或目標尚無法確認，不能僅憑工作目錄自動授權。";break;
        }
        if(errorPlaceholder is not null){error=Unresolved(errorPlaceholder);return true;}
        if(operation=="search"&&!pattern){error=Unresolved("搜尋缺少搜尋文字。");return true;}
        if(fromPipeline) {
            if(operation!="search"){error=Unresolved("管線階段需另行檢查其檔案來源。");return true;}
        } else if(paths.Count==0) {
            if(operation=="list")paths.Add(".");
            else {error=Unresolved("檔案操作未提供目標路徑。");return true;}
        }
        if(paths.Any(p=>string.IsNullOrWhiteSpace(p)||p.StartsWith('~'))){error=Unresolved("路徑為空或使用家目錄展開，無法確認工作副本邊界。");return true;}
        plan=new(operation,paths.ToArray(),literal,recursive,operation is "list" or "info");return true;
    }

    internal static PermissionAssessment Assess(ReadPlan plan, PermissionPolicySettings settings, string cwd, string workspace)
    {
        foreach(var path in plan.Paths) {
            // Do not normalize wildcard/.. into a different path before checking.
            if(path.IndexOfAny(['*','?','[',']'])>=0&&path.Replace('\\','/').Split('/').Contains(".."))return Unresolved("萬用字元路徑含上層跳轉，無法確認實際目標。");
            var full=Path.GetFullPath(path,cwd);
            var risk=plan.Literal?PermissionPolicy.ReadSet(settings,full,workspace,plan.Recursive,namesOnly:plan.NamesOnly,rejectWildcardNames:plan.CheckExpandedPaths)
                :PermissionPolicy.ReadPattern(settings,full,workspace,plan.Recursive,namesOnly:plan.NamesOnly,rejectWildcardNames:plan.CheckExpandedPaths);
            if(!risk.AutoAllowed)return risk;
        }
        return PermissionPolicy.Allow("GeneralRead","一般檔案操作的全部目標均在工作副本內");
    }

    // Quoted commas are file-name characters; unquoted commas form literal arrays.
    // Variables, subexpressions, invocation and redirections never become paths.
    private static List<Argument>? Parse(string text)
    {
        if(text.IndexOfAny(['\r','\n'])>=0)return null;
        var result=new List<Argument>();var index=0;
        while(index<text.Length) {
            while(index<text.Length&&char.IsWhiteSpace(text[index]))index++;
            if(index==text.Length)break;
            var values=new List<string>();var quoted=false;
            while(true) {
                if(index==text.Length)return null;
                var builder=new StringBuilder();var quote=text[index] is '\'' or '"'?text[index++]: '\0';
                if(values.Count==0)quoted=quote!='\0';
                if(quote!='\0') {
                    var closed=false;
                    while(index<text.Length) {
                        var c=text[index++];
                        if(c==quote) {
                            if(quote=='\''&&index<text.Length&&text[index]=='\''){builder.Append(c);index++;continue;}
                            closed=true;break;
                        }
                        if(c is '`' or '\r' or '\n'||quote=='"'&&c=='$')return null;
                        builder.Append(c);
                    }
                    if(!closed||index<text.Length&&!char.IsWhiteSpace(text[index])&&text[index]!=',')return null;
                } else {
                    while(index<text.Length&&!char.IsWhiteSpace(text[index])&&text[index]!=',') {
                        var c=text[index++];
                        if(";|&<>%@!^`'\"(){}#".Contains(c))return null;
                        builder.Append(c);
                    }
                }
                if(builder.Length==0&&quote=='\0'||builder.Length>20000||values.Count>=128||quote=='\0'&&builder.ToString().Contains('$')&&!Regex.IsMatch(builder.ToString(),@"\A-CaseSensitive:\$(true|false)\z",RegexOptions.IgnoreCase))return null;
                values.Add(builder.ToString());
                while(index<text.Length&&char.IsWhiteSpace(text[index]))index++;
                if(index==text.Length||text[index]!=',')break;
                index++;while(index<text.Length&&char.IsWhiteSpace(text[index]))index++;
            }
            if(result.Count>=128)return null;
            result.Add(new(values.ToArray(),quoted));
        }
        return result.Count>0?result:null;
    }
}
