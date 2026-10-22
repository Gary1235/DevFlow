using System.Text.Json;

namespace DevFlow.Core;

internal static partial class CommandPermissionRisk
{
    private static PermissionAssessment? Sequence(JsonElement args, string workspace, string command)
    {
        // Only simple, top-level semicolon sequences are supported. Never treat a
        // separator inside quotes as a command boundary or interpret shell scripts.
        var segments = new List<string>();
        var start = 0;
        var quote = '\0';
        var complex = false;
        for (var i = 0; i < command.Length; i++)
        {
            var character = command[i];
            if (character == quote) { quote = '\0'; continue; }
            if (quote != '\0') continue;
            if (character is '\'' or '"') { quote = character; continue; }
            if ("(){}\r\n`#&".Contains(character)) complex = true;
            if (character != ';') continue;
            if (segments.Count >= 15) return Unsupported("串接指令超過 16 段，必須單次確認。");
            segments.Add(command[start..i].Trim());
            start = i + 1;
        }
        if (segments.Count == 0) return null;
        segments.Add(command[start..].Trim());
        if (quote != '\0' || complex || segments.Any(string.IsNullOrWhiteSpace))
            return Unsupported("串接指令含空白段、未閉合引號、腳本、跳脫或其他未支援的組合語法，必須單次確認。");

        PermissionAssessment? highest = null;
        var highestIndex = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            // Preserve the original execution context for every segment. Calling the
            // common classifier also retains database, deletion and write detection.
            var source = args.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone());
            source["CommandLine"] = segments[i];
            var risk = PermissionRisk.Assess("run_command", JsonSerializer.SerializeToElement(source), workspace);
            if (highest is null || SequencePriority(risk) > SequencePriority(highest))
            {
                highest = risk;
                highestIndex = i;
            }
        }
        if (highest!.BatchAllowed) return General($"串接指令（共 {segments.Count} 段皆為一般讀取）");
        return highest with { Reason = $"串接指令共 {segments.Count} 段；第 {highestIndex + 1} 段：{highest.Reason} 整組必須單次確認。" };
    }

    private static int SequencePriority(PermissionAssessment risk) => risk.BatchAllowed ? 0 : risk.Category switch
    {
        "Deletion" => 6,
        "Network" => 5,
        "Execution" => 4,
        "Write" => 3,
        "Unknown" => 2,
        _ => 1,
    };
}
