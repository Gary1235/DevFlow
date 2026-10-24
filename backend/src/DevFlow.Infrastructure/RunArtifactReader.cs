using DevFlow.Core;
using System.Text;
using System.Text.Json;
namespace DevFlow.Infrastructure;
public sealed class RunArtifactReader(string root) : IRunArtifactReader
{
    private readonly string baseRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    public Task<IReadOnlyList<ArtifactInfo>> ListAsync(Guid runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var directory = RunRoot(runId);
        if (!Directory.Exists(directory)) return Task.FromResult<IReadOnlyList<ArtifactInfo>>([]);
        CheckLinks(directory);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = Directory.EnumerateFiles(directory, "*", options)
            .Select(path => new ArtifactInfo(Path.GetRelativePath(directory, path).Replace('\\', '/'), new FileInfo(path).Length))
            .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        return Task.FromResult<IReadOnlyList<ArtifactInfo>>(files);
    }
    public async Task<ArtifactPage> ReadAsync(Guid runId, string path, int offset = 0, int limit = 65536, CancellationToken ct = default)
    {
        if (offset < 0 || limit is < 1 or > 65536) throw new ArgumentException("Invalid artifact cursor or page size.");
        var fullPath = Resolve(runId, path);
        if (!new[] { ".log", ".txt", ".json", ".jsonl", ".trx" }.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Artifact is not a supported text file.");
        var text = await ReadText(fullPath, ct);
        if (offset > text.Length || (offset < text.Length && char.IsLowSurrogate(text[offset])))
            throw new ArgumentException("Cursor must point to a valid text boundary.");
        var end = Math.Min(text.Length, offset + limit);
        if (end < text.Length && char.IsLowSurrogate(text[end])) end--;
        if (end == offset && end < text.Length && char.IsHighSurrogate(text[end])) end += 2;
        return new(path.Replace('\\', '/'), text[offset..end], offset, end, end < text.Length);
    }
    public async Task<IReadOnlyList<ValidationSummary>> ValidationsAsync(Guid runId, CancellationToken ct = default)
    {
        var results = new List<ValidationSummary>();
        var runRoot = RunRoot(runId);
        if (!Directory.Exists(runRoot)) return results;
        CheckLinks(runRoot);
        var files = Directory.EnumerateFiles(runRoot, "evidence.json", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 5 }).Take(501).ToArray();
        if (files.Length > 500) throw new ConflictException("Validation evidence exceeds the display limit; inspect artifacts.");
        foreach (var candidate in files.OrderBy(File.GetLastWriteTimeUtc).ThenBy(p => p.Contains("Attempt-", StringComparison.Ordinal) ? 1 : 0))
        {
            var relative = Path.GetRelativePath(runRoot, candidate).Replace('\\', '/');
            var parts = relative.Split('/');
            if (parts.Length is < 2 or > 4 || !Enum.TryParse<TddPhase>(parts[^2], out var phase) || !Enum.IsDefined(phase)) continue;
            var prefix = parts[..^2]; var index = 0; Guid? stepId = null; var attempt = 0; var origin = "Worker";
            if (prefix.Length > 0 && prefix[0].StartsWith("Step-", StringComparison.Ordinal) && Guid.TryParseExact(prefix[0][5..], "N", out var step)) { stepId = step; index++; }
            if (index < prefix.Length) {
                var label = prefix[index];
                if (label.StartsWith("Attempt-", StringComparison.Ordinal) && int.TryParse(label[8..], out attempt) && attempt > 0) { origin = attempt >= 10 ? "PlannerCorrection" : "Repair"; if (attempt >= 10) attempt -= 9; index++; }
                else if (index == 0 && prefix.Length == 1 && label.StartsWith("Structured-", StringComparison.Ordinal) &&
                    (int.TryParse(label[11..], out _) || Guid.TryParseExact(label[11..], "N", out _))) { origin = "StructuredTool"; index++; }
                else if (index == 0 && prefix.Length == 1 && label == "SettingsProbe") { origin = "SettingsProbe"; index++; }
            }
            if (index != prefix.Length) continue;
            string path;
            try { path = Resolve(runId, relative); }
            catch (MissingEntityException) { continue; }
            try
            {
                using var document = JsonDocument.Parse(await ReadText(path, ct));
                var json = document.RootElement;
                var total = json.GetProperty("Total").GetInt32();
                var failed = json.GetProperty("Failed").GetInt32();
                if (total < 0 || failed < 0 || failed > total) throw new ConflictException("Invalid validation counters.");
                results.Add(new(phase, json.GetProperty("ExitCode").GetInt32(), total, failed, relative,
                    stepId,
                    json.TryGetProperty("Kind", out var kind) ? kind.GetString() ?? "DotnetTest" : "DotnetTest",
                    json.TryGetProperty("Error", out var error) ? error.GetString() : null, attempt, origin));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            { throw new ConflictException("Validation evidence is incomplete or malformed; retry after the phase finishes."); }
        }
        return results;
    }
    private string RunRoot(Guid id) => Path.Combine(baseRoot, id.ToString("N"));
    private string Resolve(Guid runId, string relative)
    {
        relative = WorkTask.Required(relative, 4096).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/').Any(p => p is "" or "." or ".."))
            throw new ArgumentException("Artifact path must remain inside its run directory.");
        var runRoot = RunRoot(runId);
        var fullPath = Path.GetFullPath(Path.Combine(runRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(runRoot + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Artifact path escaped its run directory.");
        CheckLinks(fullPath);
        if (!File.Exists(fullPath)) throw new MissingEntityException("Artifact not found.");
        return fullPath;
    }
    private void CheckLinks(string path)
    {
        var current = path;
        while (current.Length >= baseRoot.Length)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ConflictException("Symbolic links and junctions are not valid artifact paths.");
            if (current == baseRoot) break;
            current = Path.GetDirectoryName(current)!;
        }
    }
    private static async Task<string> ReadText(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new ConflictException("Artifact exceeds the text display limit.");
        try { return await File.ReadAllTextAsync(path, Encoding.UTF8, ct); }
        catch (IOException) { throw new ConflictException("Artifact is currently being written; retry shortly."); }
    }
}
