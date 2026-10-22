namespace DevFlow.Core;

internal static partial class WorkspaceFileOperations
{
    internal static PermissionAssessment? Mutation(string command, PermissionPolicySettings settings, string cwd, string workspace, bool readOnly, string[][]? scopes, bool fromPipeline=false)
    {
        var originalName=CommandName(command);
        var name=originalName is null?null:NormalizeName(originalName);
        if(name is not ("set-content" or "add-content" or "new-item" or "copy-item" or "move-item" or "rename-item" or "remove-item" or "out-file"))return null;
        if(readOnly)return PermissionPolicy.Ask("Write","需確認：唯讀階段修改","規劃、討論與審查維持唯讀。",true);
        if(Parse(command) is not {} args||args[0].Values.Length!=1||args[0].Quoted||!args[0].Values[0].Equals(originalName,StringComparison.OrdinalIgnoreCase))return Unresolved("檔案修改含腳本或動態參數，無法確認完整目標。");
        var sources=new List<string>();string? destination=null;var pathsSeen=false;var valueSeen=false;var literal=false;
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var transfer=name is "copy-item" or "move-item" or "rename-item";
        for(var i=1;i<args.Count;i++) {
            var arg=args[i];
            if(!arg.Parameter) {
                if(!pathsSeen){sources.AddRange(arg.Values);pathsSeen=true;continue;}
                if(transfer&&destination is null&&arg.Values.Length==1){destination=arg.Values[0];continue;}
                if(name is "set-content" or "add-content" or "new-item"&&!valueSeen){valueSeen=true;continue;}
                return Unresolved("檔案修改的位置參數無法對應到明確目標。");
            }
            var flag=arg.Values[0].ToLowerInvariant();
            if(!seen.Add(flag))return Unresolved("檔案修改含重複參數。");
            if(flag is "-force" or "-nonewline" or "-passthru" or "-append" or "-noclobber") {
                if(flag=="-append"&&name!="out-file"||flag=="-noclobber"&&name!="out-file")return Unresolved("參數不適用目前的檔案操作。");
                continue;
            }
            if(flag=="-recurse")return PermissionPolicy.Ask("Deletion","需確認：遞迴／大量檔案操作","目錄或遞迴修改、複製與刪除需要額外授權。",true);
            if(++i==args.Count||args[i].Parameter)return Unresolved("檔案修改缺少必要參數值。");
            var values=args[i].Values;
            if(flag is "-path" or "-literalpath" or "-filepath") {
                if(pathsSeen||flag=="-filepath"&&name!="out-file")return Unresolved("來源路徑重複或不適用。");
                sources.AddRange(values);pathsSeen=true;literal=flag=="-literalpath";continue;
            }
            if(transfer&&flag is "-destination" or "-newname") {
                if(destination is not null||values.Length!=1||name=="rename-item"&&flag!="-newname"||name!="rename-item"&&flag!="-destination")return Unresolved("目的地必須是單一明確路徑。");
                destination=values[0];continue;
            }
            if(flag=="-value"&&name is "set-content" or "add-content" or "new-item"){if(valueSeen)return Unresolved("檔案內容參數重複。");valueSeen=true;continue;}
            if(flag=="-itemtype"&&name=="new-item"&&values.Length==1&&values[0].ToLowerInvariant() is "file" or "directory")continue;
            if(flag=="-encoding"&&name is "set-content" or "add-content" or "out-file"&&values.Length==1&&new[]{"utf8","utf8bom","utf8nobom","unicode","ascii","default","oem"}.Contains(values[0].ToLowerInvariant()))continue;
            if(flag=="-width"&&name=="out-file"&&values.Length==1&&int.TryParse(values[0],out var width)&&width>0)continue;
            if(flag is "-erroraction" or "-warningaction"&&values.Length==1&&new[]{"stop","continue","silentlycontinue","ignore"}.Contains(values[0].ToLowerInvariant()))continue;
            return Unresolved($"參數 {flag} 的副作用尚無法確認，需要額外授權。");
        }
        if(sources.Count==0||transfer&&destination is null)return Unresolved("檔案修改未提供完整來源與目的地。");
        if(sources.Any(string.IsNullOrWhiteSpace)||destination is not null&&string.IsNullOrWhiteSpace(destination))return Unresolved("來源或目的地不能為空。");
        if(!fromPipeline&&name is "set-content" or "add-content"&&!valueSeen)return Unresolved("寫入未提供內容或管線輸入。");
        if(name=="remove-item"&&sources.Count>1)return PermissionPolicy.Ask("Deletion","需確認：大量刪除","多檔刪除需要額外授權。",true);
        if(name is "out-file" or "rename-item"&&sources.Count!=1)return Unresolved("此操作需要單一目標檔案。");
        foreach(var source in sources) {
            if(source.StartsWith('~')||source.IndexOfAny(['*','?'])>=0||!literal&&source.IndexOfAny(['[',']'])>=0)return Unresolved("修改目標含萬用字元或動態路徑，無法確認全部受影響檔案。");
            var full=Path.GetFullPath(source,cwd);
            var sourceRisk=PermissionPolicy.Target(settings,full,workspace,name!="copy-item",scopes);
            if(!sourceRisk.AutoAllowed)return sourceRisk;
            if(Directory.Exists(full)&&name!="new-item"||name=="remove-item"&&(source.EndsWith('/')||source.EndsWith('\\')))
                return PermissionPolicy.Ask("Deletion","需確認：整個目錄操作","整個目錄操作需要額外授權。",true);
            if(!transfer)continue;
            string target;
            if(name=="rename-item") {
                if(destination!.IndexOfAny(['/','\\',':','*','?'])>=0)return Unresolved("新檔名不能改變來源目錄。");
                target=Path.Combine(Path.GetDirectoryName(full)!,destination);
            } else {
                if(destination!.StartsWith('~')||destination.IndexOfAny(['*','?'])>=0)return Unresolved("目的地含未解析的路徑展開。");
                target=Path.GetFullPath(destination,cwd);
                var locationRisk=PermissionPolicy.Target(settings,target,workspace,false);
                if(!locationRisk.AutoAllowed)return locationRisk;
                if(Directory.Exists(target))target=Path.Combine(target,Path.GetFileName(full));
                else if(sources.Count>1)return Unresolved("多個來源需提供存在的目的目錄。");
            }
            var destinationRisk=PermissionPolicy.Target(settings,target,workspace,true,scopes);
            if(!destinationRisk.AutoAllowed)return destinationRisk;
        }
        return PermissionPolicy.Allow(name=="remove-item"?"Deletion":"Write","檔案操作的全部來源與目的地均符合工作副本邊界") with { DeletedFileCount = name=="remove-item" ? sources.Count : 0 };
    }
}
