using System.Text.Json;
namespace DevFlow.Core;

internal static partial class CommandPermissionRisk
{
    internal static PermissionAssessment AssessPolicy(PermissionPolicySettings settings, JsonElement args, string workspace, bool readOnly, string[][]? scopes)
    {
        if (!args.TryGetProperty("CommandLine", out var input) || input.ValueKind != JsonValueKind.String) return PermissionPolicy.Unknown();
        if (args.EnumerateObject().Any(p => p.Name is not ("CommandLine" or "Cwd" or "Shell" or "WaitMsBeforeAsync" or "toolSummary" or "Options" or "Kind"))) return PermissionPolicy.Unknown();
        // Keep the existing literal PowerShell grammar for legacy direct callers.
        // Native adapters explicitly provide their trusted shell context below.
        var powershell=true;
        if(args.TryGetProperty("Shell",out var shell)) {
            if(shell.ValueKind!=JsonValueKind.String||shell.GetString() is not ("powershell" or "bash" or "posix" or "unknown"))return PermissionPolicy.Unknown();
            powershell=shell.GetString()=="powershell";
        }
        if (args.TryGetProperty("Options", out var options) && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any(p => p.Name is not ("description" or "timeout" or "timeout_ms")))) return PermissionPolicy.Unknown();
        if (args.TryGetProperty("Kind", out var kind) && (kind.ValueKind != JsonValueKind.String || kind.GetString() != "command")) return PermissionPolicy.Unknown();
        var text = input.GetString()!;
        static bool MatchesCommand(string pattern,string command) => PermissionPolicy.Match(pattern,command)||command.Equals(pattern,StringComparison.OrdinalIgnoreCase)||command.StartsWith(pattern+' ',StringComparison.OrdinalIgnoreCase);
        bool ListedCommand(string command) => (settings.SensitiveCommands ?? []).Any(p => MatchesCommand(p,command) ||
            WorkspaceFileOperations.Canonical(p) is {} pattern && WorkspaceFileOperations.Canonical(command) is {} canonical && MatchesCommand(pattern,canonical));
        PermissionAssessment SensitiveCommand() => PermissionPolicy.Ask("SensitiveCommand", "需確認：敏感指令", "完整指令命中使用者設定的敏感指令名單。");
        if (ListedCommand(text.Trim()) || WorkspaceFileOperations.Canonical(text) is {} normalized && ListedCommand(normalized)) return SensitiveCommand();
        var cwd = workspace;
        if (args.TryGetProperty("Cwd", out var location)) {
            if (location.ValueKind != JsonValueKind.String) return PermissionPolicy.Unknown();
            cwd = Path.GetFullPath(location.GetString()!, workspace);
        }
        var locationRisk = PermissionPolicy.Target(settings, cwd, workspace, false);
        if (!locationRisk.AutoAllowed) return locationRisk;
        // Only top-level semicolons are interpreted. Script blocks remain manual.
        var parts = new List<string>(); var start = 0; char quote = '\0'; var complex = false;
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c == quote) { quote = '\0'; continue; }
            if (quote != '\0') continue;
            if (c is '\'' or '"') { quote = c; continue; }
            if ("(){}\r\n`#&".Contains(c)) complex = true;
            if (c == ';') { if (parts.Count >= 15) return PermissionPolicy.Unknown(); parts.Add(text[start..i].Trim()); start = i + 1; }
        }
        if (parts.Count > 0) {
            parts.Add(text[start..].Trim());
            if (quote != '\0' || complex || parts.Any(string.IsNullOrWhiteSpace)) return PermissionPolicy.Unknown();
            PermissionAssessment? combined = null;
            for (var i = 0; i < parts.Count; i++) {
                var segment = args.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone()); segment["CommandLine"] = parts[i];
                var assessed = PermissionPolicy.Assess(settings, "run_command", JsonSerializer.SerializeToElement(segment), workspace, readOnly, scopes);
                if (!assessed.AutoAllowed) return assessed with { Reason = $"串接指令第 {i + 1} 段：{assessed.Reason}" };
                combined = combined is null ? assessed : PermissionPolicy.Combine(combined, assessed);
                if (!combined.AutoAllowed) return combined;
            }
            return combined!;
        }
        var pipeline = SplitPipeline(text);
        if (pipeline is { Count: > 1 }) {
            if (pipeline.Any(p => ListedCommand(WorkspaceFileOperations.Canonical(p) ?? p))) return SensitiveCommand();
            var source = args.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone()); source["CommandLine"] = pipeline[0];
            var head=PermissionPolicy.Assess(settings,"run_command",JsonSerializer.SerializeToElement(source),workspace,readOnly,scopes);
            if(!head.AutoAllowed)return head;
            foreach(var stage in pipeline.Skip(1)) {
                if(powershell&&SafePipelineStage(stage))continue;
                if(powershell&&WorkspaceFileOperations.TryRead(stage,out var receiver,out var receiverError,fromPipeline:true)) {
                    if(receiverError is not null)return receiverError;
                    var receiverRisk=WorkspaceFileOperations.Assess(receiver!,settings,cwd,workspace);
                    if(!receiverRisk.AutoAllowed)return receiverRisk;
                    continue;
                }
                if(powershell&&WorkspaceFileOperations.Mutation(stage,settings,cwd,workspace,readOnly,scopes,fromPipeline:true) is {} output) {
                    if(!output.AutoAllowed)return output;
                    head=PermissionPolicy.Combine(head,output);
                    if(!head.AutoAllowed)return head;
                    continue;
                }
                return WorkspaceFileOperations.Unresolved("管線階段可能執行腳本、連線或額外檔案操作，無法確認其全部邊界。");
            }
            return head;
        }
        if(powershell&&WorkspaceFileOperations.TryFullNameProjection(text,out var producer,out var projected,out var projectionError)) {
            if(projectionError is not null)return projectionError;
            var outerName=WorkspaceFileOperations.CommandName(text);
            if(outerName is not null && ListedCommand(WorkspaceFileOperations.NormalizeName(outerName)+text.TrimStart()[outerName.Length..]) ||
                ListedCommand(producer!)||WorkspaceFileOperations.Canonical(producer!) is {} sourceCommand&&ListedCommand(sourceCommand))return SensitiveCommand();
            return WorkspaceFileOperations.Assess(projected!,settings,cwd,workspace);
        }
        if(powershell&&WorkspaceFileOperations.TryRead(text,out var operation,out var operationError))
            return operationError ?? WorkspaceFileOperations.Assess(operation!,settings,cwd,workspace);
        if(powershell&&WorkspaceFileOperations.Mutation(text,settings,cwd,workspace,readOnly,scopes) is {} mutation)return mutation;
        var words = Tokenize(text);
        if (words is not { Count: > 0 }) return PermissionPolicy.Unknown();
        var canonical = string.Join(' ', words);
        if (ListedCommand(canonical)) return SensitiveCommand();
        var name = words[0].ToLowerInvariant();
        if(!powershell&&WorkspaceFileOperations.IsFileCommand(name))return WorkspaceFileOperations.Unresolved("此 Shell 不是已確認的 PowerShell，不套用 PowerShell 指令或別名語意。");
        // Classify known operations independently of the legacy code-extension
        // whitelist, then validate their complete target set with the same policy.
        if (name == "rg") {
            var search=Ripgrep(words,cwd,workspace,settings) ?? PermissionPolicy.Unknown();
            return search.BatchAllowed ? PermissionPolicy.Allow("GeneralRead","一般搜尋工具查詢") : search.Category=="Unknown"?WorkspaceFileOperations.Unresolved(search.Reason):search;
        }
        if (name == "git") {
            var rest = words.Skip(1).ToList();
            if (rest.Count > 0 && rest[0] == "--no-optional-locks") rest.RemoveAt(0);
            if (rest.Count > 0 && rest[0] == "grep") return GitGrep(rest,cwd,workspace,settings);
        }
        if (new[] { "curl", "curl.exe", "invoke-webrequest", "invoke-restmethod", "wget", "iwr", "irm" }.Contains(name))
            return DownloadPolicy(settings, words);
        // The working directory already passed the automation boundary check.
        // Classify command semantics without reapplying the legacy sensitive-name
        // filter to that directory (for example a configured private/ build).
        var strict = (name=="git"?Git(words,cwd,workspace,settings):OtherCommand(words)) ?? PermissionPolicy.Unknown();
        if (strict.Category == "Unknown") return WorkspaceFileOperations.Unresolved(strict.Reason);
        if (strict.Category == "Execution") return strict with {FixedBoundary=true};
        if (name == "git" && !strict.BatchAllowed) return strict;
        if (strict.BatchAllowed) return PermissionPolicy.Allow("GeneralRead", "已解析的一般查詢");
        return strict;
    }
    private static PermissionAssessment DownloadPolicy(PermissionPolicySettings settings, List<string> words)
    {
        // No bodies, uploads, credentials, redirects, config files or shell output files.
        if (words[0] is not ("curl" or "curl.exe") || words.Count < 3 || words[1] is not ("-q" or "--disable"))
            return PermissionPolicy.Ask("Network", "需確認：網路操作", "僅 curl -q 的明確 GET 下載可自動判斷；其餘連線、憑證或腳本須確認。", true);
        var urls = words.Skip(2).Where(w => !new[] { "--fail", "--silent", "--show-error", "--head", "-I" }.Contains(w)).ToArray();
        if (urls.Length != 1 || !Uri.TryCreate(urls[0], UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return PermissionPolicy.Ask("Network", "需確認：對外傳送／未知連線", "含參數、標頭、憑證、上傳或無法解析的連線不自動放行。", true);
        if ((settings.SensitiveDestinations ?? []).Any(p => PermissionPolicy.Match(p, uri.AbsoluteUri) || PermissionPolicy.Match(p, uri.IdnHost) || uri.IdnHost.Equals(p, StringComparison.OrdinalIgnoreCase)))
            return PermissionPolicy.Ask("Network", "需確認：敏感連線目的地", "目的地命中使用者設定的敏感連線名單。");
        if (PermissionRisk.SensitiveTarget(uri.AbsolutePath.TrimStart('/')) || ListedUrlPath(settings, uri.AbsolutePath))
            return PermissionPolicy.Ask("Network", "需確認：敏感網路資源", "網址指向敏感資源。");
        if (uri.IsLoopback || uri.Scheme == "https" && uri.IsDefaultPort && (settings.ApprovedDownloadHosts ?? []).Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            return PermissionPolicy.Allow("GeneralRead", "本機或已核准來源的 GET 下載");
        return PermissionPolicy.Ask("Network", "需確認：未核准的外部連線", "外部下載來源尚未核准；其他外連與對外傳送保留確認。", true);
    }
    private static bool ListedUrlPath(PermissionPolicySettings settings, string path) => PermissionPolicy.ListedPath(settings, path.TrimStart('/'));
}
