using System.Text.RegularExpressions;
namespace DevFlow.Core;

internal static partial class CommandPermissionRisk
{
    private static PermissionAssessment GitGrep(List<string> words, string cwd, string workspace, PermissionPolicySettings? policy = null)
    {
        var pattern = false; var paths = new List<string>(); var pathMode = false;
        for (var i = 1; i < words.Count; i++) {
            var word = words[i];
            if (!pathMode && word == "--") { if (!pattern) return Unsupported("請先提供搜尋文字，再以 -- 分隔路徑。"); pathMode = true; continue; }
            if (!pathMode && word.StartsWith('-')) {
                if (new[]{"--ignore-case","--line-number","--files-with-matches","--files-without-match","--fixed-strings","--extended-regexp","--basic-regexp","--word-regexp","--invert-match","--count","--quiet","--no-color","--no-textconv"}.Contains(word) || Regex.IsMatch(word,@"\A-[inlLIFEGwvchoHq]+\z")) continue;
                if (word is "-e" or "--regexp") { if (++i == words.Count) return Unsupported("搜尋文字遺失。"); pattern = true; continue; }
                if (word is "-A" or "-B" or "-C" or "--after-context" or "--before-context" or "--context") {
                    if (++i == words.Count || !int.TryParse(words[i],out var count) || count is < 0 or > 1000) return Unsupported("搜尋前後文範圍未受支援。"); continue;
                }
                if (Regex.IsMatch(word,@"\A-[ABC][0-9]{1,3}\z")) continue;
                return UnsupportedFlag("git grep",word);
            }
            if (!pattern) { pattern = true; continue; }
            paths.Add(word);
        }
        if (!pattern) return Unsupported("搜尋文字遺失。");
        if (paths.Count == 0) paths.Add(".");
        var inspected = 0;
        PermissionAssessment Check(string path) => policy is null
            ? PathRisk("read_file","Path",path,workspace)
            : PermissionPolicy.Target(policy,path,workspace,false);
        bool Safe(PermissionAssessment risk) => policy is null ? risk.BatchAllowed : risk.AutoAllowed;
        foreach (var path in paths) {
            // Only literal, existing worktree targets. Revision searches, pathspec
            // magic, globs, alternate indexes and external textconv/pagers stay manual.
            // Embedded tildes and commas are literal Git path characters, not
            // pathspec magic. Keep the legacy manual policy unchanged.
            var literalPath = policy is not null && OperatingSystem.IsWindows() && Path.IsPathFullyQualified(path) &&
                Regex.IsMatch(path, @"\A[A-Za-z]:[\\/]") ? path[2..] : path;
            if (literalPath.IndexOfAny(['*','?','[',']',':']) >= 0 || path.StartsWith('~') ||
                policy is null && path.IndexOfAny(['~',',']) >= 0)
                return Unsupported("Git 搜尋只自動允許明確的工作副本路徑。");
            var full = Path.GetFullPath(path,cwd);
            var location = policy is null ? PathRisk("list_dir","DirectoryPath",full,workspace) : PermissionPolicy.Target(policy,full,workspace,false);
            if (!Safe(location)) return location;
            if (File.Exists(full)) { var risk = Check(full); if (!Safe(risk)) return risk; continue; }
            if (!Directory.Exists(full)) return Unsupported("搜尋目標不存在或可能是 Git 歷史參照，需單次確認。");
            var pending = new Stack<string>(); pending.Push(full);
            while (pending.TryPop(out var directory)) foreach (var entry in Directory.EnumerateFileSystemEntries(directory)) {
                if (++inspected > 20000) return BroadSearch();
                if (Path.GetFileName(entry).Equals(".git",StringComparison.OrdinalIgnoreCase)) continue;
                if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) return Unsupported("搜尋範圍含符號連結，需單次確認。");
                if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                var risk = Check(entry); if (!Safe(risk)) return risk;
            }
        }
        return policy is null ? General("Git 工作副本內容搜尋") : PermissionPolicy.Allow("GeneralRead","Git 工作副本內容搜尋");
    }
}
