using System.Text.Json;
namespace DevFlow.Core;

public static partial class PermissionPolicy
{
    // Glob patterns select paths, unlike a grep regular expression. Check every
    // brace alternative before allowing the CLI to expand it. The complete base
    // tree is separately inspected for links by ReadSet, regardless of the glob.
    internal static PermissionAssessment? SearchPattern(JsonElement args)
    {
        var keys = new[] { "pattern", "Pattern" }.Where(k => args.TryGetProperty(k, out _)).ToArray();
        if (keys.Length == 0) return null;
        if (keys.Length != 1 || args.GetProperty(keys[0]).ValueKind != JsonValueKind.String) return Unknown();
        var pattern = args.GetProperty(keys[0]).GetString()!;
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 2000 || pattern.Any(char.IsControl)) return Unknown();
        if (pattern.Contains('\\')) return WorkspaceFileOperations.Unresolved("搜尋模式的反斜線可能是路徑分隔或跳脫語法；請改用 / 明確指定搜尋路徑。");
        var pending = new Stack<string>(); pending.Push(pattern); var inspected = 0;
        while (pending.TryPop(out var candidate)) {
            if (++inspected > 128) return WorkspaceFileOperations.Unresolved("搜尋模式展開超出檢查上限。");
            var open = candidate.IndexOf('{');
            if (open >= 0) {
                var close = candidate.IndexOf('}', open);
                if (close < 0 || candidate[..open].Contains('}') || candidate[(open + 1)..close].Contains('{'))
                    return WorkspaceFileOperations.Unresolved("搜尋模式含尚未支援的巢狀或不完整大括號。");
                var alternatives = candidate[(open + 1)..close].Split(',');
                if (alternatives.Length < 2 || alternatives.Length > 64)
                    return WorkspaceFileOperations.Unresolved("搜尋模式僅支援明確的大括號選項清單。");
                foreach (var alternative in alternatives) pending.Push(candidate[..open] + alternative + candidate[(close + 1)..]);
                if (pending.Count > 128) return Unknown();
                continue;
            }
            if (candidate.Contains('}') || candidate.IndexOfAny(['(', ')']) >= 0)
                return WorkspaceFileOperations.Unresolved("搜尋模式包含尚未支援的擴充比對語法。");
            if (candidate.StartsWith('/') || candidate.StartsWith('~') || candidate.Contains(':') || candidate.Split('/').Contains(".."))
                return Ask("Boundary", "需確認：搜尋模式可能超出工作副本", "搜尋模式包含絕對路徑、家目錄或上層跳轉，不能只依起始目錄自動核准。", true);
            // Classes cannot hide separators or construct a parent-directory
            // component. Ordinary filename classes such as [ab] remain supported.
            for (var i = 0; i < candidate.Length; i++) {
                if (candidate[i] == ']') return Unknown();
                if (candidate[i] != '[') continue;
                var end = candidate.IndexOf(']', i + 1);
                if (end < 0 || end == i + 1 || candidate[(i + 1)..end].IndexOfAny(['.', '/', '[']) >= 0)
                    return WorkspaceFileOperations.Unresolved("搜尋字元集合無法確認完整路徑邊界。");
                i = end;
            }
        }
        return null;
    }
}
