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
        var scopes = new List<string> { "" };
        if (Directory.Exists(runRoot))
        {
            CheckLinks(runRoot);
            scopes.AddRange(Directory.EnumerateDirectories(runRoot, "Step-*").Select(Path.GetFileName).OfType<string>()
                .Where(name => Guid.TryParseExact(name[5..], "N", out _)).OrderBy(name => name));
        }
        foreach (var scope in scopes)
        foreach (var phase in Enum.GetValues<TddPhase>())
        {
            var relative = (scope.Length == 0 ? "" : scope + "/") + $"{phase}/evidence.json";
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
                    scope.Length == 0 ? null : Guid.ParseExact(scope[5..], "N"),
                    json.TryGetProperty("Kind", out var kind) ? kind.GetString() ?? "DotnetTest" : "DotnetTest",
                    json.TryGetProperty("Error", out var error) ? error.GetString() : null));
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
