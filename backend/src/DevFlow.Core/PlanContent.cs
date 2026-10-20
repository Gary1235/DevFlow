using System.Text.RegularExpressions;

namespace DevFlow.Core;

public static class PlanContent
{
    public const string CompletionInstruction = " Return the completed, actionable implementation plan, not a progress announcement or promise to analyze later. Include actual affected files, concrete changes, ordered implementation steps, validation commands, risks and unresolved questions. If required access is blocked, explain the blocker; never present incomplete analysis as a completed plan.";
    public static string? Problem(string text)
    {
        var value = text.Trim();
        if (string.IsNullOrWhiteSpace(value)) return "尚未產生實作計畫。";
        // A narrow guard for progress-only responses; it is not a quality score or length threshold.
        if (Regex.IsMatch(value, @"\A(?:正在|目前正在|我正在|我會先|我将先)[^\r\n]*(?:分析|讀取|檢查|瀏覽|搜尋)[^\r\n]*\z", RegexOptions.None, TimeSpan.FromMilliseconds(50)) ||
            Regex.IsMatch(value, @"\A(?:(?:I am|I'm|I will|I'll|Let me)\s+(?:first\s+)?(?:analy[sz]|inspect|read|review|explor)|Analy[sz]ing|Inspecting|Reading|Reviewing|Exploring)[^\r\n]*\z", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)))
            return "AI 尚未完成實作計畫。";
        return null;
    }
    public static void EnsureCompleted(string text)
    {
        if (Problem(text) is { } problem) throw new ArgumentException(problem);
    }
}
