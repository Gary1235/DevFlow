using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFlow.Core;

internal static partial class CommandPermissionRisk
{
    internal static PermissionAssessment LiteralExecution(string executable, string[] arguments) =>
        OtherCommand([Path.GetFileNameWithoutExtension(executable).ToLowerInvariant(), .. arguments]) ?? PermissionPolicy.Unknown();
    private static readonly string[] MetadataProperties=["Name","FullName","Extension","Length","LastWriteTime","CreationTime","DirectoryName","LineNumber","Line","Filename","Path"];
    private static PermissionAssessment Unsupported(string reason)=>new("Unknown","需確認：未支援的指令形式",reason);
    private static PermissionAssessment UnsupportedFlag(string command,string argument)
    {
        var name=argument.Split('=',':')[0];
        return Unsupported(Regex.IsMatch(name,@"\A-[A-Za-z0-9-]{1,60}\z")?$"尚未支援 {command} 的參數 {name}，必須單次確認。":"含有尚未支援的參數，必須單次確認。");
    }
    private static PermissionAssessment? OtherCommand(List<string> words)
    {
        var name=words[0].ToLowerInvariant();var verb=words.Count>1?words[1].ToLowerInvariant():"";
        // Bare dir lists the current directory in both Windows shells. Do not
        // extend this to arguments: cmd and PowerShell use different grammars.
        if(name=="dir"&&words.Count==1)return General("列出目前目錄／檔名");
        if(name=="npm"&&verb is "config" or "pkg"&&words.Count>2&&new[]{"set","delete","fix"}.Contains(words[2].ToLowerInvariant()))return new("Write","敏感操作：修改套件設定","會變更套件或工具設定，必須逐次確認。");
        if(name=="npm"&&verb=="publish"||name=="dotnet"&&verb=="nuget"&&words.Skip(2).Contains("push"))return new("Network","敏感操作：發布套件","可能上傳並發布套件到外部來源，必須確認目的地後逐次核准。");
        if(new[]{"dotnet","node","npm","rg"}.Contains(name)&&words.Count==2&&verb=="--version")return General("查詢工具版本");
        if(name=="dotnet"&&new[]{"build","test","run","publish","clean","msbuild","format"}.Contains(verb)||name=="npm"&&new[]{"run","test","start","exec"}.Contains(verb)||name=="npx"||new[]{"node","python","python3","pwsh","powershell","bash","sh"}.Contains(name))
            return new("Execution","敏感執行：建置／測試／腳本","可能執行專案或外部腳本、寫入輸出或呼叫其他工具，必須逐次確認。");
        if(name=="npm"&&new[]{"install","i","ci","update","uninstall","remove"}.Contains(verb)||name=="dotnet"&&new[]{"restore","add","remove","tool"}.Contains(verb)||new[]{"pip","pip3","yarn","pnpm"}.Contains(name))
            return new("Write","敏感操作：套件／工具變更","可能連線下載、變更套件或設定並執行安裝腳本，必須逐次確認。");
        if(name=="dotnet"&&verb=="list")return new("Execution","敏感執行：專案資訊查詢","可能評估 MSBuild 專案設定或載入自訂邏輯，暫不批次放行。");
        if(name=="npm"&&new[]{"list","ls","pkg","config","view","outdated"}.Contains(verb))
            return new("SensitiveRead","敏感讀取：專案／套件資訊","可能讀取含憑證的設定、連線套件來源或評估專案設定，暫不批次放行。");
        if(new[]{"curl","curl.exe","wget","invoke-webrequest","invoke-restmethod","iwr","irm"}.Contains(name)){
            if(words.Any(w=>Regex.IsMatch(w,@"\A(DELETE|-XDELETE|--request=DELETE)\z",RegexOptions.IgnoreCase)))return new("Deletion","刪除／高影響操作：外部服務","可能刪除外部資料，必須單次確認。");
            if(words.Any(w=>new[]{"--upload-file","-T","--data","--data-raw","--data-binary","-d","-Body","-InFile","--form","-F"}.Contains(w))||words.Any(w=>new[]{"POST","PUT","PATCH"}.Contains(w.ToUpperInvariant())))
                return new("Network","敏感操作：對外傳送／修改","可能上傳檔案、傳送內容或修改外部服務，必須檢查目的地與內容後單次確認。");
            if(words.Any(w=>Regex.IsMatch(w,@"\A(-X(POST|PUT|PATCH)|--request=(POST|PUT|PATCH)|--(data|data-raw|data-binary|form|upload-file)=.+)\z",RegexOptions.IgnoreCase)))return new("Network","敏感操作：對外傳送／修改","會對外傳送資料或變更服務，必須單次確認。");
            if(words.Any(w=>w.Equals("DELETE",StringComparison.OrdinalIgnoreCase)))return new("Deletion","刪除／高影響操作：外部服務","可能刪除外部資料，必須單次確認。");
            return new("Network","敏感操作：網路存取／下載","會連線外部服務，可能攜帶憑證或寫入下載檔案，必須單次確認。");
        }
        if(new[]{"set-content","add-content","out-file","new-item","copy-item","cp","copy","mkdir"}.Contains(name))return new("Write","敏感操作：新增／覆寫檔案","可能建立、複製或覆寫檔案及資料夾，必須逐次確認。");
        if(new[]{"move-item","rename-item","mv","move","ren","rename"}.Contains(name))return new("Write","敏感操作：移動／重新命名","會改變檔案位置或名稱，可能覆寫目標，必須逐次確認。");
        return Unsupported("尚未支援此指令；不會只憑指令名稱或前綴放行。");
    }
    private static PermissionAssessment? FileInformation(List<string> words,string cwd,string workspace)
    {
        var name=words[0].ToLowerInvariant();string? path=null;
        for(var i=1;i<words.Count;i++){
            var word=words[i].ToLowerInvariant();
            if(word is "-path" or "-literalpath"){
                if(path is not null||++i==words.Count)return null;path=words[i];continue;
            }
            if(name=="test-path"&&word=="-pathtype"){
                if(++i==words.Count||!new[]{"leaf","container","any"}.Contains(words[i].ToLowerInvariant()))return null;continue;
            }
            if(name=="get-filehash"&&word=="-algorithm"){
                if(++i==words.Count||!new[]{"sha256","sha384","sha512","sha1","md5"}.Contains(words[i].ToLowerInvariant()))return null;continue;
            }
            if(name!="get-filehash"&&word=="-force")continue;
            if(name=="test-path"&&word=="-isvalid")continue;
            if(word.StartsWith('-')||path is not null)return Unsupported("檔案資訊查詢包含未支援的參數。");path=words[i];
        }
        if(path is null||path.IndexOfAny(['*','?'])>=0)return Unsupported("檔案資訊查詢需提供一個明確路徑。");
        var risk=Targets(path,true,cwd,workspace,name!="get-filehash");
        return risk.BatchAllowed?General(name=="get-filehash"?"計算程式碼檔案雜湊":"查看檔案存在性／中繼資料"):risk;
    }
    private static PermissionAssessment RecursiveSourceSearch(string path,string glob,string cwd,string workspace)
    {
        var target=Path.GetFullPath(path,cwd);var risk=PathRisk("list_dir","DirectoryPath",target,workspace);
        if(!risk.BatchAllowed)return risk;
        if(File.Exists(target))return Targets(path,true,cwd,workspace,false);
        if(!Directory.Exists(target)||!PathRisk("read_file","Path",Path.Combine(target,"file"+Path.GetExtension(glob)),workspace).BatchAllowed)return BroadSearch();
        var pending=new Stack<string>();pending.Push(target);var inspected=0;
        while(pending.TryPop(out var directory)){
            foreach(var entry in Directory.EnumerateFileSystemEntries(directory)){
                if(++inspected>5000)return new("SensitiveRead","敏感讀取：搜尋範圍過大","無法在檢查上限內確認所有搜尋目標，需單次確認或縮小目錄。");
                var attributes=File.GetAttributes(entry);
                if(attributes.HasFlag(FileAttributes.ReparsePoint))return BroadSearch();
                if(attributes.HasFlag(FileAttributes.Directory)){pending.Push(entry);continue;}
                if(!System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(glob,Path.GetFileName(entry),OperatingSystem.IsWindows()))continue;
                var fileRisk=PathRisk("read_file","Path",entry,workspace);if(!fileRisk.BatchAllowed)return fileRisk;
            }
        }
        return General("限定副檔名的遞迴搜尋");
    }
    private static List<string>? SplitPipeline(string command)
    {
        var segments=new List<string>();char quote='\0';var start=0;
        for(var i=0;i<command.Length;i++){
            if(command[i]==quote){quote='\0';continue;}
            if(quote!='\0')continue;
            if(command[i] is '\'' or '"'){quote=command[i];continue;}
            if(command[i]!='|')continue;
            if(segments.Count>=7)return null;
            segments.Add(command[start..i].Trim());start=i+1;
        }
        if(quote!='\0')return null;
        segments.Add(command[start..].Trim());return segments;
    }
    private static bool HasOutputRedirect(string command)
    {
        char quote='\0';
        foreach(var c in command){
            if(c==quote){quote='\0';continue;}
            if(quote!='\0')continue;
            if(c is '\'' or '"'){quote=c;continue;}
            if(c=='>')return true;
        }
        return false;
    }
    private static PermissionAssessment Pipeline(JsonElement args,string workspace,List<string> segments)
    {
        if(segments.Any(string.IsNullOrWhiteSpace))return Unsupported("管線含空白階段或組合運算子，無法判定為唯讀。");
        if(FileSearchPipeline(args,workspace,segments) is {} search)return search;
        var source=args.EnumerateObject().ToDictionary(p=>p.Name,p=>(object)p.Value.Clone());source["CommandLine"]=segments[0];
        var head=Assess(JsonSerializer.SerializeToElement(source),workspace);
        if(head is null||!head.BatchAllowed)return head??Unsupported("管線來源不是已支援的唯讀操作。");
        for(var i=1;i<segments.Count;i++)if(!SafePipelineStage(segments[i])){
            if(HasOutputRedirect(segments[i]))return new("Write","敏感操作：管線輸出寫檔","管線會建立或覆寫檔案，必須逐次確認。");
            if(Tokenize(segments[i]) is {Count:>0} words&&OtherCommand(words) is {BatchAllowed:false} sensitive&&sensitive.Category!="Unknown")return sensitive;
            return Unsupported($"管線第 {i+1} 段包含未支援的指令、屬性或腳本；僅允許簡單篩選、選欄位、排序與統計。");
        }
        return General("唯讀管線：篩選／排序／統計");
    }
    private static bool Property(string text)=>MetadataProperties.Contains(text,StringComparer.OrdinalIgnoreCase);
    private static bool PropertyList(string text) {
        var values=text.Split(',');
        if(values.Length>1&&values[^1]=="")values=values[..^1];
        return values.Length>0&&values.All(Property);
    }
    private static bool SafePipelineStage(string stage)
    {
        // Script blocks are accepted only in this exact property/operator/literal grammar.
        var where=Regex.Match(stage,"\\AWhere-Object\\s+\\{\\s*\\$_\\.(?<property>[A-Za-z]+)\\s+-(?:eq|ne|gt|ge|lt|le|match|notmatch|like|notlike)\\s+(?:'[^'`$\\r\\n]*'|\"[^\"`$\\r\\n]*\"|[0-9]+)\\s*\\}\\z",RegexOptions.IgnoreCase);
        if(where.Success)return Property(where.Groups["property"].Value);
        var words=Tokenize(stage);if(words is not{Count:>0})return false;
        var name=words[0].ToLowerInvariant();
        if(name=="where-object")return words.Count==4&&Property(words[1])&&new[]{"-eq","-ne","-gt","-ge","-lt","-le","-match","-notmatch","-like","-notlike"}.Contains(words[2].ToLowerInvariant());
        if(name is not("select-object" or "sort-object" or "measure-object"))return false;
        for(var i=1;i<words.Count;i++){
            var lower=words[i].ToLowerInvariant();
            if(lower is "-property" or "-expandproperty"){
                if(lower=="-expandproperty"&&name!="select-object"||++i==words.Count||!PropertyList(words[i]))return false;continue;
            }
            if(name=="select-object"&&lower is "-first" or "-skip" or "-last"){
                if(++i==words.Count||!int.TryParse(words[i],out var count)||count<0)return false;continue;
            }
            if(lower=="-unique"&&name is "select-object" or "sort-object"||name=="sort-object"&&lower=="-descending"||name=="measure-object"&&new[]{"-line","-word","-character","-sum","-minimum","-maximum","-average"}.Contains(lower))continue;
            if(!PropertyList(words[i]))return false;
        }
        return true;
    }
}
