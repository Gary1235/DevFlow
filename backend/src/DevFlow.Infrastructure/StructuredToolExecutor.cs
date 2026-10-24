using System.Text.Json;
using DevFlow.Core;
namespace DevFlow.Infrastructure;

public sealed class StructuredToolFactory(SqliteTaskStore store, IProcessRunner process, string artifactsRoot) : IStructuredToolFactory
{
    public IStructuredTools Create(WorkTask task, Workspace workspace, Guid runId, IToolApprovalOperation operation) =>
        new StructuredToolExecutor(store, process, artifactsRoot, task, workspace, runId, operation);
}

public sealed class StructuredToolExecutor(SqliteTaskStore store, IProcessRunner process, string artifactsRoot,
    WorkTask task, Workspace workspace, Guid runId, IToolApprovalOperation operation) : IStructuredTools
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static JsonElement Schema(object properties, string[] required) => JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = false });
    private static readonly object StringField = new { type = "string" };
    private static readonly object NumberField = new { type = "integer", minimum = 0 };
    public IReadOnlyList<StructuredToolDefinition> Definitions => operation.ReadOnly ? All.Where(d => d.Name is not ("devflow_write_file" or "devflow_run_validation")).ToArray() : All;
    private static readonly StructuredToolDefinition[] All = [
        new("devflow_read_file", "Read a literal worktree file without a shell. Use offset/limit for pagination; maximum 65536 characters per call.", Schema(new { path = StringField, offset = NumberField, limit = NumberField }, ["path"])),
        new("devflow_list_files", "List tracked and non-ignored worktree files under a literal directory. Generated ignored dependencies are excluded; links are never followed.", Schema(new { path = StringField }, ["path"])),
        new("devflow_search", "Search literal text in tracked/non-ignored files under a literal directory. Case-sensitive, no regex, returns bounded line matches and truncated flag.", Schema(new { path = StringField, text = StringField }, ["path", "text"])),
        new("devflow_write_file", "Write a literal UTF-8 worktree file, at most 12000 characters. Existing scope and protected-control-file restrictions apply.", Schema(new { path = StringField, content = StringField }, ["path", "content"])),
        new("devflow_run_validation", "Execute a saved project validation profile by ID. No executable, argv, cwd or preparation overrides. Returns evidence, not inferred success.", Schema(new { profileId = StringField }, ["profileId"]))
    ];
    public async Task<string> ExecuteAsync(string tool, JsonElement args, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try {
            if (!Definitions.Any(d => d.Name == tool)) throw new ArgumentException("工具不適用於此階段。");
            var definition = All.Single(d => d.Name == tool);
            var properties = definition.InputSchema.GetProperty("properties");
            if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(p => !properties.TryGetProperty(p.Name, out _)) ||
                args.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1)) throw new ArgumentException("工具參數無效。");
            foreach (var p in args.EnumerateObject()) {
                var type = properties.GetProperty(p.Name).GetProperty("type").GetString();
                if (type == "string" && p.Value.ValueKind != JsonValueKind.String || type == "integer" &&
                    (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var number) || number < 0)) throw new ArgumentException("工具參數型別無效：" + p.Name);
            }
            var current = await store.GetTaskAsync(task.Id, ct);
            var step = current?.Workflow?.Steps?.FirstOrDefault(s => s.Id == current.ActiveStepId);
            var scopes = new[] { current?.Policy?.AllowedPaths ?? task.Policy?.AllowedPaths ?? [], step?.AllowedPaths ?? [] };
            string Text(string name, int max = 500) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? WorkTask.Required(v.GetString(), max) : throw new ArgumentException("缺少文字參數：" + name);
            string PathFor(bool writing = false) {
                var path = Text("path");
                var risk = PermissionPolicy.WorkspaceTarget(path, workspace.Path, writing, scopes);
                if (!risk.AutoAllowed) throw new ArgumentException(risk.Reason + "（" + risk.ReasonCode + "）");
                return Path.GetFullPath(path, workspace.Path);
            }
            async Task Approve(string name, object input) {
                if (!await operation.RequestPermissionAsync(name, JsonSerializer.SerializeToElement(input), workspace.Path, ct))
                    throw new InvalidOperationException("[Permission] 使用者或政策未核准本次工具操作。");
            }
            if (tool == "devflow_run_validation") {
                var project = await store.GetProjectAsync(task.ProjectId, ct) ?? throw new MissingEntityException("Project not found.");
                var id = Text("profileId", 60);
                var profile = ProjectExecutionSettings.FindValidation(project, id) ?? throw new ArgumentException("找不到已儲存的驗證設定。");
                if (profile.PrepareArguments is { Length: > 0 }) profile = profile with { PrepareArguments = null };
                await Approve("run_validation", new { ProfileId = id, ProjectVersion = project.Version });
                var latest = await store.GetProjectAsync(project.Id, ct);
                if (latest?.Version != project.Version) throw new ConflictException("驗證設定已變更，請重新提出工具請求。");
                // Each CLI stage creates a fresh executor within the same run.
                // A per-executor counter would overwrite evidence from prior stages.
                var validator = new DotnetValidationRunner(process, artifactsRoot, "Structured-" + Guid.NewGuid().ToString("N"));
                return JsonSerializer.Serialize(await validator.ValidateAsync(project with { DefaultValidationProfileId = id, ValidationProfiles = [profile] }, workspace, runId, TddPhase.Implementation, ct));
            }
            var full = PathFor(tool == "devflow_write_file");
            if (tool == "devflow_write_file") {
                var content = args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : throw new ArgumentException("缺少檔案內容。");
                if (content.Length > 12000) throw new ArgumentException("單次寫入最多 12000 字元。");
                var write = JsonSerializer.SerializeToElement(new { Path = full, Content = content });
                if (write.GetRawText().Length > 18000) write = JsonSerializer.SerializeToElement(new { Path = full,
                    ContentPreview = content[..Math.Min(1000, content.Length)], Characters = content.Length,
                    ContentSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))) });
                await Approve("write_file", write);
                PathFor(true); ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, content, ct); return "File written.";
            }
            if (tool == "devflow_read_file") {
                var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
                var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 32768;
                if (offset < 0 || offset > 16 * 1024 * 1024 || limit is < 1 or > 65536 || new FileInfo(full).Length > 16 * 1024 * 1024) throw new ArgumentException("讀取範圍超出限制。");
                await Approve("read_file", new { Path = full, Offset = offset, Limit = limit }); PathFor();
                using var reader = new StreamReader(full);
                var buffer = new char[Math.Min(offset, 4096)]; var remaining = offset;
                while (remaining > 0) { var skipped = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct); if (skipped == 0) throw new ArgumentException("讀取游標超出檔案長度。"); remaining -= skipped; }
                var output = new char[limit + 1]; var read = await reader.ReadBlockAsync(output.AsMemory(), ct);
                if (read > 0 && char.IsLowSurrogate(output[0])) throw new ArgumentException("讀取游標必須位於完整 Unicode 字元邊界。");
                var count = Math.Min(limit, read);
                if (count > 0 && read > count && char.IsHighSurrogate(output[count - 1]) && char.IsLowSurrogate(output[count])) count += count == 1 ? 1 : -1;
                return JsonSerializer.Serialize(new { text = new string(output, 0, count), nextOffset = offset + count, hasMore = read > count });
            }
            var relative = Path.GetRelativePath(workspace.Path, full).Replace('\\', '/');
            var listing = await process.RunAsync("git", ["--literal-pathspecs", "-c", "core.fsmonitor=false", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", relative], workspace.Path, null, ct);
            if (listing.ExitCode != 0 || listing.StandardOutput.Contains(ProcessRunner.TruncationMarker)) throw new ArgumentException("無法完整列出搜尋檔案，請縮小目錄。");
            var files = listing.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
            if (files.Length > 20000) throw new ArgumentException("搜尋超過 20000 個檔案，請縮小目錄。");
            foreach (var file in files) if (!PermissionPolicy.WorkspaceTarget(file, workspace.Path).AutoAllowed) throw new ArgumentException("搜尋包含越界或連結，無法執行。");
            await Approve("list_dir", new { Path = full, Operation = tool, Files = files.Length });
            if (tool == "devflow_list_files") return JsonSerializer.Serialize(new { files = files.Take(2000), truncated = files.Length > 2000 });
            var needle = Text("text"); var matches = new List<object>(); var truncated = false; var bytes = 0L;
            foreach (var file in files) {
                ct.ThrowIfCancellationRequested();
                if (!PermissionPolicy.WorkspaceTarget(file, workspace.Path).AutoAllowed) throw new ArgumentException("搜尋目標已變更。");
                var path = Path.GetFullPath(file, workspace.Path); if (!File.Exists(path)) continue;
                var size = new FileInfo(path).Length;
                if (size > 1024 * 1024 || (bytes += size) > 16 * 1024 * 1024) { truncated = true; continue; }
                var number = 0;
                foreach (var line in File.ReadLines(path)) {
                    number++; if (!line.Contains(needle, StringComparison.Ordinal)) continue;
                    matches.Add(new { path = file, line = number, text = line[..Math.Min(1000, line.Length)] });
                    if (matches.Count >= 200) { truncated = true; break; }
                }
                if (matches.Count >= 200) break;
            }
            return JsonSerializer.Serialize(new { matches, truncated });
        }
        finally { gate.Release(); }
    }
}
