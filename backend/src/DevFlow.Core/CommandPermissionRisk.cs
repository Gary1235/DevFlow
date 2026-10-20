using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevFlow.Core;

// A deliberately small shell grammar with separately validated, read-only pipeline stages.
// Unknown switches never inherit the classification of the command's first word.
internal static partial class CommandPermissionRisk
{
    public static PermissionAssessment? Assess(JsonElement args,string workspace)
    {
        if (!args.TryGetProperty("CommandLine",out var command)||command.ValueKind!=JsonValueKind.String) return null;
        if (args.EnumerateObject().Any(p=>p.Name is not ("CommandLine" or "Cwd" or "WaitMsBeforeAsync" or "toolSummary" or "Options" or "Kind"))) return null;
        if(args.TryGetProperty("Kind",out var kind)&&(kind.ValueKind!=JsonValueKind.String||kind.GetString()!="command"))return null;
        if(args.TryGetProperty("Options",out var options)&&(options.ValueKind!=JsonValueKind.Object||options.EnumerateObject().Any(p=>p.Name is not ("description" or "timeout" or "timeout_ms"))))return null;
        if(SplitPipeline(command.GetString()!) is {Count:>1} pipeline)return Pipeline(args,workspace,pipeline);
        if(HasOutputRedirect(command.GetString()!))return new("Write","敏感操作：輸出寫檔","指令包含輸出重新導向，可能建立或覆寫檔案，必須逐次確認。");
        var words=Tokenize(command.GetString()!);if(words is not {Count:>0})return Unsupported("指令包含未支援的語法、參數展開或重新導向。");
        var cwd=workspace;
        if(args.TryGetProperty("Cwd",out var directory)){
            if(directory.ValueKind!=JsonValueKind.String)return null;
            cwd=Path.GetFullPath(directory.GetString()!,workspace);
        }
        var location=PathRisk("list_dir","DirectoryPath",cwd,workspace);
        if(!location.BatchAllowed)return location;
        var name=words[0].ToLowerInvariant();
        return name switch {
            "get-childitem"=>PowerShell(words,cwd,workspace,"list"),
            "get-content"=>PowerShell(words,cwd,workspace,"read"),
            "select-string"=>PowerShell(words,cwd,workspace,"search"),
            "test-path" or "get-item" or "get-filehash"=>FileInformation(words,cwd,workspace),
            "rg"=>Ripgrep(words,cwd,workspace),
            "git"=>Git(words,cwd,workspace),
            _=>OtherCommand(words)
        };
    }
    private static PermissionAssessment? PowerShell(List<string> words,string cwd,string workspace,string mode)
    {
        string? path=null,pattern=null;var literal=false;
        var switches=mode switch{
            "list"=>new[]{"-force","-recurse","-file","-directory","-name","-hidden"},
            "read"=>new[]{"-raw","-force"},
            _=>new[]{"-simplematch","-list","-quiet","-allmatches","-notmatch","-noemphasis","-casesensitive","-casesensitive:$false","-casesensitive:$true"}
        };
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(var i=1;i<words.Count;i++){
            var word=words[i];var lower=word.ToLowerInvariant();
            if(lower.StartsWith('-')){
                if(!seen.Add(lower.Split(':')[0]))return null;
                if(switches.Contains(lower))continue;
                if(lower is "-path" or "-literalpath"){
                    if(path is not null||++i==words.Count||words[i].StartsWith('-'))return null;
                    path=words[i];literal=lower=="-literalpath";continue;
                }
                if(mode=="search"&&lower=="-pattern"){
                    if(++i==words.Count)return null;pattern=words[i];continue;
                }
                if((mode=="list"&&lower=="-depth")||(mode=="read"&&lower is "-totalcount" or "-tail" or "-readcount")){
                    if(++i==words.Count||!int.TryParse(words[i],out var count)||count<0)return null;continue;
                }
                if(mode=="list"&&lower=="-filter"){
                    if(++i==words.Count||words[i].Contains(':')||words[i].Contains('/')||words[i].Contains('\\'))return null;continue;
                }
                if(mode is "read" or "search"&&lower=="-encoding"){
                    if(++i==words.Count||!new[]{"utf8","utf8bom","utf8nobom","unicode","ascii","bigendianunicode","default","oem"}.Contains(words[i].ToLowerInvariant()))return Unsupported("讀檔編碼參數未受支援。");continue;
                }
                if(mode=="search"&&lower=="-context"){
                    if(++i==words.Count||!Regex.IsMatch(words[i],@"\A[0-9]{1,4}(,[0-9]{1,4})?\z"))return Unsupported("搜尋前後文範圍只支援一或兩個非負整數。");continue;
                }
                return UnsupportedFlag(words[0],word);
            }
            if(path is not null)return null;path=word;
        }
        if(mode=="search"&&pattern is null)return null;
        if(mode!="list"&&path is null)return null;
        var result=Targets(path??".",literal,cwd,workspace,mode=="list");
        if(!result.BatchAllowed)return result;
        if(mode=="list"&&(seen.Contains("-recurse")||seen.Contains("-depth"))&&!RecursiveDirectorySafe(path??".",cwd))return BroadSearch();
        return General(mode switch{"list"=>"列出目錄／檔名","read"=>"讀取程式碼／文件",_=>"搜尋程式碼"});
    }
    private static PermissionAssessment? Ripgrep(List<string> words,string cwd,string workspace)
    {
        if(words.Count==2&&words[1]=="--version")return General("查詢搜尋工具版本");
        var positional=new List<string>();var pattern=false;var end=false;string? glob=null;var filesOnly=false;
        for(var i=1;i<words.Count;i++){
            var word=words[i];
            if(!end&&word=="--"){end=true;continue;}
            if(!end&&word.StartsWith('-')){
                if(word=="--files"){filesOnly=true;continue;}
                if(word is "-g" or "--glob"){
                    if(glob is not null||++i==words.Count)return Unsupported("只支援一個明確的程式碼副檔名篩選。");glob=words[i];continue;
                }
                if(word.StartsWith("--glob=")){if(glob is not null)return null;glob=word[7..];continue;}
                if(new[]{"-n","--line-number","-i","--ignore-case","-S","--smart-case","-F","--fixed-strings","-l","--files-with-matches","--no-heading","--hidden"}.Contains(word))continue;
                if(word is "-e" or "--regexp"){
                    if(pattern||++i==words.Count)return null;pattern=true;continue;
                }
                return UnsupportedFlag("rg",word);
            }
            if(!pattern&&!filesOnly){pattern=true;continue;}positional.Add(word);
        }
        if(filesOnly){
            foreach(var path in positional.DefaultIfEmpty(".")){var risk=Targets(path,true,cwd,workspace,true);if(!risk.BatchAllowed)return risk;}
            return General("列出專案檔名");
        }
        if(!pattern)return null;
        if(glob is not null){
            if(!Regex.IsMatch(glob,@"\A\*\.[A-Za-z0-9]+\z"))return Unsupported("遞迴搜尋只支援 *.cs、*.ts 等單一副檔名篩選。");
            foreach(var path in positional.DefaultIfEmpty(".")){var risk=RecursiveSourceSearch(path,glob,cwd,workspace);if(!risk.BatchAllowed)return risk;}
            return General("限定副檔名的遞迴搜尋");
        }
        if(positional.Count==0)return BroadSearch();
        foreach(var path in positional){var risk=Targets(path,true,cwd,workspace,false);if(!risk.BatchAllowed)return risk;}
        return General("搜尋程式碼");
    }
    private static PermissionAssessment? Git(List<string> words,string cwd,string workspace)
    {
        var rest=words.Skip(1).ToList();
        if(rest.Count>0&&rest[0]=="--no-optional-locks")rest.RemoveAt(0);
        if(rest.Count==0)return null;
        if(rest.Any(w=>w=="--output"||w.StartsWith("--output=")))return new("Write","敏感操作：Git 輸出寫檔","Git 查詢會將輸出寫入檔案，必須逐次確認。");
        if(rest[0] is "branch" or "tag"&&rest.Skip(1).Any(w=>w is "-d" or "-D" or "--delete"))return new("Deletion","刪除／高影響操作：Git 參照","會刪除分支或標籤，必須單次確認。");
        if(rest[0]=="--version"&&rest.Count==1)return General("查詢 Git 版本");
        if(rest[0]=="branch"&&(rest.Count==1||rest.Skip(1).All(w=>new[]{"--list","-a","--all","-r","--remotes","--no-color"}.Contains(w))))return General("查看分支清單");
        if(rest[0]=="ls-files"&&rest.Skip(1).All(w=>new[]{"--cached","--stage","-s","-z"}.Contains(w)))return General("查看 Git 追蹤檔名");
        if(rest[0]=="rev-parse"&&(rest.SequenceEqual(new[]{"rev-parse","HEAD"})||rest.SequenceEqual(new[]{"rev-parse","--short","HEAD"})))return General("查看目前提交識別");
        if(rest[0]=="log"||rest[0]=="show")return new("SensitiveRead","敏感讀取：提交歷史／內容","提交訊息或歷史檔案可能包含機敏資訊，必須單次確認。");
        if(new[]{"add","commit","switch","checkout","merge","rebase","push","pull","fetch","restore","reset","branch","tag","stash"}.Contains(rest[0]))return new("Write","敏感操作：Git 變更","此指令會變更工作區、索引、提交、分支或遠端狀態，必須單次確認。");
        if(rest[0]=="status"){
            if(rest.Skip(1).Any(w=>!new[]{"--short","-s","--branch","-b","--porcelain","--porcelain=v1","--porcelain=v2","--untracked-files=no","--untracked-files=normal","--untracked-files=all"}.Contains(w)))return null;
            return General("查看 Git 狀態");
        }
        if(rest[0]!="diff")return null;
        var delimiter=rest.IndexOf("--");
        var flags=rest.Skip(1).Take(delimiter<0?rest.Count-1:delimiter-1).ToArray();
        if(flags.Any(w=>!new[]{"--no-ext-diff","--no-textconv","--stat","--name-only","--name-status","--cached","--staged"}.Contains(w)))return null;
        if(!flags.Contains("--no-ext-diff")||!flags.Contains("--no-textconv"))
            return new("SensitiveRead","敏感讀取：Git 差異","需停用外部 diff／textconv，並限定程式碼檔案，才可批次允許。");
        if(flags.Contains("--stat")||flags.Contains("--name-only")||flags.Contains("--name-status"))return General("查看 Git 差異摘要");
        if(delimiter<0||delimiter==rest.Count-1)return BroadSearch();
        foreach(var path in rest.Skip(delimiter+1)){
            if(path.Contains('*')||path.Contains('?')||path.StartsWith(':'))return BroadSearch();
            var risk=Targets(path,true,cwd,workspace,false);if(!risk.BatchAllowed)return risk;
        }
        return General("查看程式碼差異");
    }
    private static PermissionAssessment Targets(string path,bool literal,string cwd,string workspace,bool namesOnly)
    {
        if(path.Contains(':')&&!Path.IsPathRooted(path)||path.Contains('[')||path.Contains(']')||path.Contains(',')||path.Contains('~'))return BroadSearch();
        var full=Path.GetFullPath(path,cwd);
        var wildcard=full.IndexOfAny(['*','?']);
        if(wildcard<0){
            if(!namesOnly&&Directory.Exists(full))return BroadSearch();
            return PathRisk(namesOnly?"list_dir":"read_file",namesOnly?"DirectoryPath":"Path",full,workspace);
        }
        if(literal)return BroadSearch();
        var parent=Path.GetDirectoryName(full)!;
        if(parent.IndexOfAny(['*','?'])>=0)return BroadSearch();
        var directoryRisk=PathRisk("list_dir","DirectoryPath",parent,workspace);
        if(!directoryRisk.BatchAllowed)return directoryRisk;
        if(namesOnly)return General("列出目錄／檔名");
        var filePattern=Path.GetFileName(full);
        // Never turn an all-files wildcard into a code-only grant, even in an empty directory.
        if(filePattern=="*"||filePattern=="**"||filePattern=="*.*"||Path.GetExtension(filePattern).IndexOfAny(['*','?'])>=0)return BroadSearch();
        if(!PathRisk("read_file","Path",Path.Combine(parent,"file"+Path.GetExtension(filePattern)),workspace).BatchAllowed)return BroadSearch();
        if(!Directory.Exists(parent))return BroadSearch();
        var inspected=0;
        foreach(var file in Directory.EnumerateFiles(parent)){
            if(++inspected>2000)return BroadSearch();
            if(!System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(filePattern,Path.GetFileName(file),OperatingSystem.IsWindows()))continue;
            var risk=PathRisk("read_file","Path",file,workspace);if(!risk.BatchAllowed)return risk;
        }
        return General("搜尋程式碼");
    }
    private static PermissionAssessment PathRisk(string tool,string key,string path,string workspace)=>
        PermissionRisk.Assess(tool,JsonSerializer.SerializeToElement(new Dictionary<string,string>{{key,path}}),workspace);
    private static bool RecursiveDirectorySafe(string path,string cwd)
    {
        var target=Path.GetFullPath(path,cwd);
        if(target.IndexOfAny(['*','?'])>=0)return false;
        if(!Directory.Exists(target))return true;
        var pending=new Stack<string>();pending.Push(target);var count=0;
        while(pending.TryPop(out var directory)){
            foreach(var child in Directory.EnumerateDirectories(directory)){
                if(++count>2000||File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))return false;
                pending.Push(child);
            }
        }
        return true;
    }
    private static PermissionAssessment General(string label)=>new("GeneralRead","一般讀取："+label,"完整指令與參數符合已支援的唯讀操作，目標限本次專案。",true);
    private static PermissionAssessment BroadSearch()=>new("SensitiveRead","敏感讀取：搜尋／讀取範圍過廣","已辨識為讀取或搜尋，但目標可能涵蓋機敏設定、資料庫或非程式碼檔案，必須單次確認。");
    private static List<string>? Tokenize(string text)
    {
        var words=new List<string>();var index=0;
        while(index<text.Length){
            while(index<text.Length&&text[index]==' ')index++;
            if(index==text.Length)break;
            var word=new StringBuilder();
            if(text[index] is '\'' or '"'){
                var quote=text[index++];
                while(index<text.Length&&text[index]!=quote){
                    if(text[index] is '`' or '$' or '%' or '@' or '\r' or '\n')return null;
                    word.Append(text[index++]);
                }
                if(index==text.Length)return null;index++;
                if(index<text.Length&&text[index]!=' ')return null;
            }else{
                while(index<text.Length&&text[index]!=' '){
                    if(";|&<>%@!^`'\"(){}\r\n\t".Contains(text[index]))return null;
                    word.Append(text[index++]);
                }
                if(word.ToString().Contains('$')&&!Regex.IsMatch(word.ToString(),@"\A-CaseSensitive:\$(true|false)\z",RegexOptions.IgnoreCase))return null;
            }
            if(word.Length==0||word.Length>2000||words.Count>=64)return null;
            words.Add(word.ToString());
        }
        return words;
    }
}
