using System.Text.RegularExpressions;
namespace DevFlow.Core;

internal static partial class WorkspaceFileOperations
{
    // Recognize literal Get-ChildItem FullName projections, either standalone
    // or as Select-String paths. Never evaluate PowerShell expressions.
    internal static bool TryFullNameProjection(string command, out string? producer, out ReadPlan? plan, out PermissionAssessment? error)
    {
        producer = null; plan = null; error = null;
        var standalone = command.TrimStart().StartsWith('(');
        if (!standalone && (CommandName(command) is not {} name || NormalizeName(name) != "select-string")) return false;
        int open = -1, close = -1; char quote = '\0';
        for (var i = 0; i < command.Length; i++) {
            var c = command[i];
            if (quote != '\0') {
                if (c == quote) {
                    if (quote == '\'' && i + 1 < command.Length && command[i + 1] == '\'') { i++; continue; }
                    quote = '\0';
                }
                continue;
            }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '(') {
                if (open >= 0) { error = Unresolved("檔案清單含巢狀運算，無法確認完整來源。"); return true; }
                open = i;
            }
            if (c == ')' && open >= 0) { close = i; break; }
        }
        if (open < 0) return false;
        error = Unresolved("僅支援明確 Get-ChildItem 檔案清單的 FullName 路徑運算。");
        if (close < 0) return true;
        if (!standalone && (Parse(command[..open]) is not {} prefix || prefix.Count < 2 ||
            !prefix[^1].Parameter || prefix[^1].Values[0].ToLowerInvariant() is not ("-path" or "-literalpath"))) return true;
        var member = Regex.Match(command[(close + 1)..], @"\A\.FullName(?=\s|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!member.Success) return true;
        producer = command[(open + 1)..close].Trim();
        if (!TryRead(producer, out var source, out var sourceError)) return true;
        if (sourceError is not null) { error = sourceError; return true; }
        if (source?.Operation != "list" ||
            Parse(producer) is not {} sourceArgs || sourceArgs.Any(a => a.Parameter && a.Values[0].Equals("-name", StringComparison.OrdinalIgnoreCase))) return true;
        if (standalone) {
            if (!string.IsNullOrWhiteSpace(command[(close + 1 + member.Length)..])) return true;
            // Printing names does not read their contents or interpret returned
            // names as wildcard paths. Keep the source's usual boundary checks.
            plan = source;
            error = null;
            return true;
        }
        const string placeholder = "__devflow_projected_paths__";
        var consumer = command[..open] + placeholder + command[(close + 1 + member.Length)..];
        if (!TryRead(consumer, out var destination, out var destinationError)) return true;
        if (destinationError is not null) { error = destinationError; return true; }
        if (destination is null || destination.Paths.Length != 1 || destination.Paths[0] != placeholder) return true;
        // -Path interprets returned names as wildcard paths. Require literal
        // names throughout the checked source tree; -LiteralPath needs no such
        // restriction. This prevents a bracketed name from reaching a sibling link.
        plan = source with { Operation = "search", NamesOnly = false, CheckExpandedPaths = !destination.Literal };
        error = null;
        return true;
    }
}
