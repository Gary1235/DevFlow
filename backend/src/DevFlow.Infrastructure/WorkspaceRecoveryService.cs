using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevFlow.Core;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Infrastructure;
public sealed record RecoveryPreview(Guid Id, Guid TaskId, Guid RunId, int ExpectedVersion, string HeadCommit, string[] Files, long Bytes, DateTimeOffset ExpiresAt);
public sealed class WorkspaceRecoveryService(SqliteTaskStore store, IProcessRunner process, string worktreeRoot, string artifactsRoot)
{
    private sealed record Snapshot(RecoveryPreview Preview, string Patch, Dictionary<string, byte[]> Untracked, string Fingerprint);
    private readonly ConcurrentDictionary<Guid, Snapshot> previews = new();
    public async Task<RecoveryPreview> PreviewAsync(Guid taskId, CancellationToken ct)
    {
        foreach (var p in previews.Where(p => p.Value.Preview.ExpiresAt < DateTimeOffset.UtcNow)) previews.TryRemove(p.Key, out _);
        if (previews.Count >= 5) throw new ConflictException("恢復預覽已達上限，請稍後再試。");
        var snapshot = await Capture(taskId, ct); previews[snapshot.Preview.Id] = snapshot; return snapshot.Preview;
    }
    private async Task<Snapshot> Capture(Guid taskId, CancellationToken ct)
    {
        var task = await store.GetTaskAsync(taskId, ct) ?? throw new MissingEntityException("Task not found.");
        if (task.Status is not (Status.Failed or Status.Cancelled) || task.Plan is null) throw new ConflictException("只有已結束且有計畫的失敗／取消任務可以恢復副本。");
        var run = (await store.ListRunsAsync(taskId, ct)).LastOrDefault(r => r.Kind == RunKind.Implementation) ?? throw new ConflictException("沒有實作副本。");
        if (run.FinishedAt is null || run.WorktreePath is null) throw new ConflictException("舊執行尚未結束。");
        var path = Path.GetFullPath(run.WorktreePath); var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreeRoot));
        if (!path.Equals(Path.Combine(root, run.Id.ToString("N")), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !PermissionPolicy.WorkspaceTarget(path, path).AutoAllowed) throw new ConflictException("副本不在目前管理根目錄，或包含根目錄連結。");
        var project = await store.GetProjectAsync(task.ProjectId, ct) ?? throw new MissingEntityException("Project not found.");
        async Task<string> Git(string cwd, params string[] args) {
            var result = await process.RunAsync("git", args, cwd, null, ct);
            if (result.ExitCode != 0 || result.StandardOutput.Contains(ProcessRunner.TruncationMarker)) throw new ConflictException("無法完整讀取恢復副本。");
            return result.StandardOutput;
        }
        var common = (await Git(path, "rev-parse", "--path-format=absolute", "--git-common-dir")).Trim();
        var originalCommon = (await Git(project.RepositoryPath, "rev-parse", "--path-format=absolute", "--git-common-dir")).Trim();
        if (!common.Equals(originalCommon, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ConflictException("副本 Repository 身分不符。");
        var head = GitCommit.Require((await Git(path, "rev-parse", "HEAD")).Trim());
        var conflictFiles = await Git(path, "diff", "--name-only", "--diff-filter=U", "-z");
        if (conflictFiles.Length > 0) throw new ConflictException("副本仍有未解決衝突，請先人工檢查。");
        var tracked = (await Git(path, "diff", "HEAD", "--name-only", "-z", "--no-renames")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var untrackedNames = (await Git(path, "ls-files", "--others", "--exclude-standard", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var names = tracked.Concat(untrackedNames).Distinct().ToArray();
        if (names.Length > 200) throw new ConflictException("恢復超過 200 個檔案，請先人工檢查。");
        var failedStep = task.Workflow?.Steps?.ElementAtOrDefault(task.StepResults?.Length ?? 0);
        foreach (var name in names) {
            var risk = PermissionPolicy.WorkspaceTarget(name, path, true, [task.Policy?.AllowedPaths ?? [], failedStep?.AllowedPaths ?? []]);
            if (!risk.AutoAllowed) throw new ConflictException("不能自動恢復：" + name + "；" + risk.Reason);
        }
        long bytes = 0;
        foreach (var name in names) {
            var full = Path.GetFullPath(name, path);
            if (Directory.Exists(full)) throw new ConflictException("不能自動恢復巢狀 Repository 或目錄項目。");
            if (File.Exists(full)) bytes += new FileInfo(full).Length;
            if (bytes > 20 * 1024 * 1024) throw new ConflictException("恢復內容超過 20 MiB。");
        }
        var patch = await Git(path, "diff", "HEAD", "--binary", "--no-ext-diff", "--no-textconv", "--no-renames");
        bytes += Encoding.UTF8.GetByteCount(patch);
        if (bytes > 20 * 1024 * 1024) throw new ConflictException("恢復內容與 patch 超過 20 MiB。");
        var untracked = new Dictionary<string, byte[]>();
        foreach (var name in untrackedNames) {
            var full = Path.GetFullPath(name, path);
            untracked[name] = await File.ReadAllBytesAsync(full, ct);
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(head + patch + string.Join('\n', untracked.OrderBy(p => p.Key).Select(p => p.Key + ":" + Convert.ToHexString(SHA256.HashData(p.Value)))))));
        return new(new(Guid.NewGuid(), task.Id, run.Id, task.Version, head, names, bytes, DateTimeOffset.UtcNow.AddMinutes(10)), patch, untracked, fingerprint);
    }
    public async Task<WorkTask> RecoverAsync(Guid taskId, Guid previewId, bool oldProcessStopped, CancellationToken ct)
    {
        if (!oldProcessStopped) throw new ArgumentException("請先確認舊 CLI 與驗證程序已停止。");
        if (!previews.TryRemove(previewId, out var snapshot) || snapshot.Preview.TaskId != taskId || snapshot.Preview.ExpiresAt < DateTimeOffset.UtcNow) throw new ConflictException("恢復預覽已失效。");
        var current = await Capture(taskId, ct);
        if (current.Preview.ExpectedVersion != snapshot.Preview.ExpectedVersion || current.Preview.RunId != snapshot.Preview.RunId || current.Fingerprint != snapshot.Fingerprint) throw new ConflictException("舊副本或任務已變更，請重新預覽。");
        var task = (await store.GetTaskAsync(taskId, ct))!; var project = (await store.GetProjectAsync(task.ProjectId, ct))!;
        var manager = new GitWorkspaceManager(process, worktreeRoot); var scratchId = Guid.NewGuid();
        var workspace = await manager.CreateAsync(project with { BaseBranch = snapshot.Preview.HeadCommit }, scratchId, ct);
        try {
            if (snapshot.Patch.Length > 0) {
                var apply = await process.RunAsync("git", ["apply", "--binary", "--index", "--whitespace=nowarn", "-"], workspace.Path, snapshot.Patch, ct);
                if (apply.ExitCode != 0) throw new ConflictException("無法將恢復快照套用至新副本。");
            }
            foreach (var file in snapshot.Untracked) {
                if (!PermissionPolicy.WorkspaceTarget(file.Key, workspace.Path, true).AutoAllowed) throw new ConflictException("恢復目標包含連結或控制檔。");
                var full = Path.GetFullPath(file.Key, workspace.Path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); await File.WriteAllBytesAsync(full, file.Value, ct);
            }
            var checkpoint = await manager.SnapshotAsync(workspace, ct);
            // Keep an immutable ref even after the temporary worktree is removed.
            var anchor = await process.RunAsync("git", ["branch", "devflow/recovery/" + scratchId.ToString("N"), checkpoint], project.RepositoryPath, null, ct);
            if (anchor.ExitCode != 0) throw new ConflictException("無法保存恢復 checkpoint。");
            var required = new HashSet<string>();
            var reader = new RunArtifactReader(artifactsRoot);
            var failedStep = task.Workflow?.Steps?.ElementAtOrDefault(task.StepResults?.Length ?? 0);
            foreach (var artifact in await reader.ListAsync(snapshot.Preview.RunId, ct)) if (artifact.Path.EndsWith("/evidence.json", StringComparison.Ordinal) &&
                (failedStep is null || artifact.Path.StartsWith("Step-" + failedStep.Id.ToString("N") + "/", StringComparison.Ordinal)) &&
                (artifact.Path.Contains("Baseline/", StringComparison.Ordinal) || artifact.Path.Contains("Red/", StringComparison.Ordinal))) {
                var page = await reader.ReadAsync(snapshot.Preview.RunId, artifact.Path, 0, 65536, ct);
                if (page.HasMore) throw new ConflictException("測試證據過大，請人工檢查恢復方式。");
                var evidence = JsonSerializer.Deserialize<ValidationEvidence>(page.Text);
                if (evidence is not null) required.UnionWith(artifact.Path.Contains("Red/", StringComparison.Ordinal) ? evidence.FailedTests : evidence.PassedTests);
            }
            return await store.CreateRecoveryTaskAsync(taskId, task.Version, snapshot.Preview.RunId, checkpoint, snapshot.Preview.HeadCommit, required.ToArray(), ct);
        }
        finally {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await manager.RemoveScratchAsync(project, workspace, scratchId, cleanup.Token); } catch (Exception) { /* Snapshot ref and original worktree remain available. */ }
        }
    }
}
