using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DevFlow.Core;
using Status = DevFlow.Core.TaskStatus;

namespace DevFlow.Infrastructure;

public sealed record StorageCategory(string Id, string Label, string Path, long Bytes, int Files, bool Complete, string? Warning);
public sealed record StorageOverview(StorageCategory[] Categories, DateTimeOffset ScannedAt);
public sealed record StorageItem(string Key, Guid TaskId, Guid RunId, string Title, string ProjectName, string Kind,
    string Path, long Bytes, int Files, DateTimeOffset LastActivity, bool CanClean, string? Reason);
public sealed record StoragePreview(Guid Id, DateTimeOffset ExpiresAt, int OlderThanDays, StorageItem[] Items, bool Truncated);
public sealed record StorageCleanupItem(string Key, string Title, string Kind, long Bytes, bool Success, string Message);
public sealed record StorageCleanupReport(StorageCleanupItem[] Items, DateTimeOffset CompletedAt);

public sealed class StorageManager(SqliteTaskStore store, IProcessRunner process, string artifactsRoot, string worktreeRoot, TimeProvider? clock = null)
{
    private readonly string artifacts = Full(artifactsRoot), worktrees = Full(worktreeRoot);
    private readonly SemaphoreSlim cleanupGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Plan> previews = new();
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private sealed record FileStamp(string Path, long Bytes, long Modified);
    private sealed record Scan(FileStamp[] Files, bool Complete, string? Warning);
    private sealed record Planned(StorageItem Item, string Signature, int TaskVersion);
    private sealed record Plan(StoragePreview Preview, Dictionary<string, Planned> Items);

    public Task<StorageOverview> OverviewAsync(CancellationToken ct) => Task.Run(() =>
    {
        var database = new List<FileStamp>();
        string? error = null;
        foreach (var path in new[] { store.DatabasePath, store.DatabasePath + "-wal", store.DatabasePath + "-shm" })
            try { if (File.Exists(path)) database.Add(Stamp(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "部分資料庫檔案暫時無法讀取。"; }
        var artifactScan = ScanTree(artifacts, ct);
        var worktreeScan = ScanTree(worktrees, ct);
        return new StorageOverview([
            new("database", "資料庫與紀錄", store.DatabasePath, database.Sum(f => f.Bytes), database.Count, error is null, error),
            Category("artifacts", "執行日誌與測試產物", artifacts, artifactScan),
            Category("worktrees", "工作副本與編譯檔案", worktrees, worktreeScan)
        ], Now);
    }, ct);

    public async Task<StoragePreview> PreviewAsync(int olderThanDays, bool includeArtifacts, bool includeWorktrees, CancellationToken ct)
    {
        if (olderThanDays is < 0 or > 3650 || (!includeArtifacts && !includeWorktrees)) throw new ArgumentException("請選擇清理類型與有效的保留天數（0–3650）。");
        foreach (var entry in previews.Where(p => p.Value.Preview.ExpiresAt <= Now)) previews.TryRemove(entry.Key, out _);
        if (previews.Count >= 10 && previews.OrderBy(p => p.Value.Preview.ExpiresAt).FirstOrDefault() is var oldest)
            previews.TryRemove(oldest.Key, out _);
        var projects = (await store.ListProjectsAsync(ct)).ToDictionary(p => p.Id);
        var items = new Dictionary<string, Planned>();
        var cutoff = Now.AddDays(-olderThanDays);
        var truncated = false;
        foreach (var task in (await store.ListTasksAsync(ct)).Where(t => t.Status is Status.Merged or Status.Cancelled))
        {
            var runs = await store.ListRunsAsync(task.Id, ct);
            if (runs.Count == 0 || runs.Any(NeedsInspection) || !projects.TryGetValue(task.ProjectId, out var project)) continue;
            var activity = new[] { await store.StorageLastActivityAsync(task.Id, ct), runs.Max(r => r.FinishedAt!.Value) }.Max();
            if (activity > cutoff) continue;
            foreach (var run in runs)
            {
                if (items.Count >= 500) { truncated = true; break; }
                if (includeArtifacts) await Add("artifacts", Path.Combine(artifacts, run.Id.ToString("N")), artifacts);
                if (includeWorktrees && run.WorktreePath is not null) await Add("worktree", run.WorktreePath, worktrees);

                async Task Add(string kind, string path, string root)
                {
                    ct.ThrowIfCancellationRequested();
                    if (items.Count >= 500) { truncated = true; return; }
                    if (!Directory.Exists(path)) return;
                    var scan = new Scan([], false, null);
                    string? reason = null;
                    try
                    {
                        ValidateTarget(path, root, run.Id, kind, projects.Values);
                        scan = ScanTree(path, ct);
                        if (!scan.Complete) throw new ConflictException(scan.Warning!);
                        if (kind == "worktree")
                        {
                            CheckNestedRepositories(path, scan.Files);
                            await CheckWorktree(project, run, ct);
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or ConflictException or IOException or UnauthorizedAccessException)
                    { reason = ex.Message; }
                    var files = kind == "artifacts" ? scan.Files.Where(f => !KeepEvidence(path, f.Path)).ToArray() : scan.Files;
                    if (files.Length == 0 && reason is null && kind == "artifacts") return;
                    var key = run.Id.ToString("N") + ":" + kind;
                    var item = new StorageItem(key, task.Id, run.Id, task.Title, project.Name, kind, Full(path), files.Sum(f => f.Bytes), files.Length, activity, reason is null, reason);
                    items[key] = new(item, Fingerprint(scan.Files), task.Version);
                }
            }
            if (truncated) break;
        }
        var preview = new StoragePreview(Guid.NewGuid(), Now.AddMinutes(5), olderThanDays, items.Values.Select(v => v.Item).ToArray(), truncated);
        if (preview.Items.Any(i => i.CanClean)) previews[preview.Id] = new(preview, items);
        return preview;
    }

    public async Task<StorageCleanupReport> CleanAsync(Guid previewId, string[] keys, CancellationToken ct)
    {
        if (keys is not { Length: > 0 and <= 100 } || keys.Distinct().Count() != keys.Length) throw new ArgumentException("每次請選擇 1–100 個不重複項目。");
        await cleanupGate.WaitAsync(ct);
        try
        {
            if (!previews.TryGetValue(previewId, out var plan) || plan.Preview.ExpiresAt <= Now) throw new ConflictException("預覽已過期或已使用，請重新預覽。");
            if (keys.Any(k => !plan.Items.TryGetValue(k, out var item) || !item.Item.CanClean)) throw new ArgumentException("選取項目不在可清理的預覽中。");
            ct.ThrowIfCancellationRequested();
            previews.TryRemove(previewId, out _);
            // Once confirmed, finish the mutation and its audit record even if the browser disconnects.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var results = new List<StorageCleanupItem>();
            foreach (var key in keys)
            {
                var planned = plan.Items[key]; var item = planned.Item;
                long freed = 0;
                var success = false;
                var message = "已清理";
                try
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var task = await store.GetTaskAsync(item.TaskId, timeout.Token) ?? throw new ConflictException("任務已不存在。");
                    var runs = await store.ListRunsAsync(task.Id, timeout.Token);
                    if (task.Version != planned.TaskVersion || task.Status is not (Status.Merged or Status.Cancelled) || runs.Any(NeedsInspection))
                        throw new ConflictException("任務狀態已改變，請重新預覽。");
                    var run = runs.Single(r => r.Id == item.RunId);
                    var project = await store.GetProjectAsync(task.ProjectId, timeout.Token) ?? throw new ConflictException("專案已不存在。");
                    ValidateTarget(item.Path, item.Kind == "artifacts" ? artifacts : worktrees, run.Id, item.Kind, await store.ListProjectsAsync(timeout.Token));
                    if (!Directory.Exists(item.Path)) throw new ConflictException("資料夾已不存在，請重新預覽。");
                    var scan = ScanTree(item.Path, timeout.Token);
                    if (!scan.Complete || planned.Signature != Fingerprint(scan.Files)) throw new ConflictException("檔案在預覽後有變動或無法完整檢查，已跳過。請重新預覽。");
                    if (item.Kind == "worktree")
                    {
                        CheckNestedRepositories(item.Path, scan.Files);
                        await CheckWorktree(project, run, timeout.Token);
                        var removed = await Git(project.RepositoryPath, ["worktree", "remove", "--", item.Path], timeout.Token);
                        if (removed.ExitCode != 0) throw new ConflictException("Git 拒絕移除工作副本，可能有變更、鎖定或子模組；請先檢查。");
                        if (Directory.Exists(item.Path)) throw new ConflictException("工作副本仍存在，請重新掃描確認。");
                        freed = item.Bytes;
                    }
                    else
                    {
                        foreach (var file in scan.Files.Where(f => !KeepEvidence(item.Path, f.Path)))
                        {
                            EnsureNoLinks(file.Path);
                            if (Stamp(file.Path) != file) throw new ConflictException("檔案在清理時有變動，已停止此項目。");
                            File.Delete(file.Path);
                            freed += file.Bytes;
                        }
                    }
                    success = true;
                }
                catch (Exception ex) when (ex is ArgumentException or ConflictException or IOException or UnauthorizedAccessException or OperationCanceledException)
                { message = ex is OperationCanceledException ? "清理逾時；已完成部分會列出，請重新掃描。" : ex.Message; }
                await store.AppendEventAsync(item.TaskId, "StorageCleaned", $"{item.Kind}: {freed} bytes; {(success ? "completed" : "partial/skipped")}. {message}", CancellationToken.None);
                results.Add(new(key, item.Title, item.Kind, freed, success, message));
            }
            return new(results.ToArray(), Now);
        }
        finally { cleanupGate.Release(); }
    }

    private async Task CheckWorktree(ProjectDefinition project, TaskRun run, CancellationToken ct)
    {
        var path = Full(run.WorktreePath!);
        var listing = await Git(project.RepositoryPath, ["worktree", "list", "--porcelain", "-z"], ct);
        if (listing.ExitCode != 0) throw new ConflictException("無法核對 Git 工作副本。");
        var blocks = listing.StandardOutput.Split("\0\0", StringSplitOptions.RemoveEmptyEntries);
        var fields = blocks.Select(b => b.Split('\0', StringSplitOptions.RemoveEmptyEntries)).SingleOrDefault(b =>
            b.Any(f => f.StartsWith("worktree ", StringComparison.Ordinal) && Full(f[9..]).Equals(path, Comparison)));
        if (fields is null || fields.Any(f => f is "bare" or "detached" || f.StartsWith("locked", StringComparison.Ordinal) || f.StartsWith("prunable", StringComparison.Ordinal)) ||
            !fields.Contains("branch refs/heads/devflow/" + run.Id.ToString("N"))) throw new ConflictException("工作副本的路徑、分支或鎖定狀態不符合 DevFlow 紀錄。");
        var expectedHead = run.ResultCommit ?? run.BaseCommit;
        if (expectedHead is null || !fields.Contains("HEAD " + GitCommit.Require(expectedHead))) throw new ConflictException("工作副本的 commit 已改變，請先檢查。");
        var status = await Git(path, ["status", "--porcelain=v1", "--untracked-files=all"], ct);
        if (status.ExitCode != 0 || !string.IsNullOrWhiteSpace(status.StandardOutput)) throw new ConflictException("工作副本有未提交或未追蹤檔案，已保留。");
    }

    private async Task<ProcessResult> Git(string directory, string[] args, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { return await process.RunAsync("git", args, directory, null, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ConflictException("Git 檢查逾時，已保留工作副本。"); }
    }

    private static void CheckNestedRepositories(string root, FileStamp[] files)
    {
        if (files.Any(f => { var relative = Path.GetRelativePath(root, f.Path).Replace('\\', '/'); return relative != ".git" && relative.Split('/').Contains(".git", StringComparer.OrdinalIgnoreCase); }))
            throw new ConflictException("工作副本內含子模組或其他 Git Repository，已保留。");
    }
    private void ValidateTarget(string path, string root, Guid runId, string kind, IEnumerable<ProjectDefinition> projects)
    {
        path = Full(path); root = Full(root);
        if (root.Equals(Full(Path.GetPathRoot(root)!), Comparison)) throw new ConflictException("不能以磁碟根目錄作為清理範圍。");
        if (!path.Equals(Path.Combine(root, runId.ToString("N")), Comparison)) throw new ConflictException("資料夾不在設定的 DevFlow 執行目錄，已保留。");
        if (Within(store.DatabasePath, path) || path.Equals(Full(store.DatabasePath), Comparison)) throw new ConflictException("資料夾包含資料庫，不能清理。");
        foreach (var project in projects)
        {
            var repository = Full(project.RepositoryPath);
            EnsureNoLinks(repository);
            if (path.Equals(repository, Comparison) || Within(repository, path) || (kind == "worktree" && Within(path, repository)))
                throw new ConflictException("資料夾與來源專案範圍重疊，不能清理。");
        }
        var otherRoot = kind == "worktree" ? artifacts : worktrees;
        if (path.Equals(otherRoot, Comparison) || Within(otherRoot, path) || Within(path, otherRoot)) throw new ConflictException("清理目錄與其他資料類型重疊，不能清理。");
        EnsureNoLinks(path);
    }

    private static StorageCategory Category(string id, string label, string path, Scan scan) => new(id, label, path, scan.Files.Sum(f => f.Bytes), scan.Files.Length, scan.Complete, scan.Warning);
    private static bool NeedsInspection(TaskRun run) => run.FinishedAt is null || run.Error?.Contains("Worker interrupted;", StringComparison.OrdinalIgnoreCase) == true;
    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Within(string path, string parent) => Full(path).StartsWith(Full(parent) + Path.DirectorySeparatorChar, Comparison);
    private static FileStamp Stamp(string path) { var file = new FileInfo(path); return new(Full(path), file.Length, file.LastWriteTimeUtc.Ticks); }
    private static string Fingerprint(FileStamp[] files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Path)); hash.AppendData([0]);
            hash.AppendData(BitConverter.GetBytes(file.Bytes)); hash.AppendData(BitConverter.GetBytes(file.Modified));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static bool KeepEvidence(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return Enum.GetNames<TddPhase>().Any(phase => relative.Equals(phase + "/evidence.json", StringComparison.OrdinalIgnoreCase));
    }
    private static void EnsureNoLinks(string path)
    {
        for (string? current = Full(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ConflictException("路徑含有符號連結或 junction，已保留。");
    }
    private static Scan ScanTree(string root, CancellationToken ct)
    {
        var files = new List<FileStamp>();
        if (!Directory.Exists(root)) return new([], true, null);
        var pending = new Stack<string>(); pending.Push(Full(root));
        var timer = Stopwatch.StartNew();
        try
        {
            EnsureNoLinks(root);
            while (pending.TryPop(out var directory))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    if (files.Count >= 200000 || timer.Elapsed > TimeSpan.FromSeconds(15)) return new(files.ToArray(), false, "掃描達到上限，顯示已讀取的容量；此項目暫不清理。");
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ConflictException("目錄內含符號連結或 junction，顯示部分容量並保留檔案。");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                    else files.Add(Stamp(path));
                }
            }
            return new(files.ToArray(), true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConflictException)
        { return new(files.ToArray(), false, "部分檔案無法讀取或含有連結；此項目暫不清理。"); }
    }
}
