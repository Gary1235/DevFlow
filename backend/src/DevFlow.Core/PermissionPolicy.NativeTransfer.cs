using System.Text.Json;
namespace DevFlow.Core;

public static partial class PermissionPolicy
{
    private static PermissionAssessment NativeTransfer(PermissionPolicySettings settings,string tool,JsonElement args,string workspace,string[][]? scopes)
    {
        if(args.EnumerateObject().Any(p=>p.Name is not ("SourcePath" or "Path" or "DestinationPath" or "TargetPath" or "Overwrite")))return Unknown();
        if(args.TryGetProperty("Overwrite",out var overwrite)&&overwrite.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return Unknown();
        string? ReadPath(params string[] keys) {
            var matches=keys.Where(k=>args.TryGetProperty(k,out _)).ToArray();
            if(matches.Length!=1||args.GetProperty(matches[0]).ValueKind!=JsonValueKind.String)return null;
            return args.GetProperty(matches[0]).GetString();
        }
        var source=ReadPath("SourcePath","Path");var destination=ReadPath("DestinationPath","TargetPath");
        if(string.IsNullOrWhiteSpace(source)||string.IsNullOrWhiteSpace(destination))return Unknown();
        var sourceRisk=Target(settings,source,workspace,tool!="copy_file",scopes);
        if(!sourceRisk.AutoAllowed)return sourceRisk;
        var fullSource=Path.GetFullPath(source,workspace);
        if(Directory.Exists(fullSource))return Ask("Deletion","需確認：整個目錄操作","移動或複製整個目錄需要額外授權。",true);
        var locationRisk=Target(settings,destination,workspace,false);
        if(!locationRisk.AutoAllowed)return locationRisk;
        var fullDestination=Path.GetFullPath(destination,workspace);
        if(Directory.Exists(fullDestination))fullDestination=Path.Combine(fullDestination,Path.GetFileName(fullSource));
        var targetRisk=Target(settings,fullDestination,workspace,true,scopes);
        if(!targetRisk.AutoAllowed)return targetRisk;
        return Allow("Write","原生檔案操作的來源與實際目的檔案符合工作副本邊界");
    }
}
