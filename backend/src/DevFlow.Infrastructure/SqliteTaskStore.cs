using System.Text.Json;
using DevFlow.Core;
using Microsoft.Data.Sqlite;
using Status = DevFlow.Core.TaskStatus;
namespace DevFlow.Infrastructure;

// Mutations and events commit together; BEGIN IMMEDIATE serializes queue claims.
public sealed partial class SqliteTaskStore(string databasePath) : ITaskStore
{
    private readonly string path = Path.GetFullPath(databasePath);
    public string DatabasePath => path;

    public Task<DateTimeOffset> StorageLastActivityAsync(Guid taskId, CancellationToken ct = default)
    {
        using var c = Open(ct);
        using var cmd = Command(c, null, "SELECT CreatedAt FROM Events WHERE TaskId=$id AND Kind <> 'StorageCleaned' ORDER BY Sequence DESC LIMIT 1", ("$id", taskId.ToString()));
        var value = cmd.ExecuteScalar() as string;
        return Task.FromResult(value is null ? DateTimeOffset.MaxValue : DateTimeOffset.Parse(value));
    }
    private SqliteConnection Open(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 }.ToString());
        c.Open();
        Execute(c, null, "PRAGMA foreign_keys=ON;");
        return c;
    }
    public Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var c = Open(ct);
        Execute(c, null, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Projects(Id TEXT PRIMARY KEY, Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Tasks(Id TEXT PRIMARY KEY, ProjectId TEXT NOT NULL REFERENCES Projects(Id),
                Status INTEGER NOT NULL, Version INTEGER NOT NULL, CreatedAt TEXT NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Tasks_Queue ON Tasks(Status, CreatedAt, Id);
            CREATE TABLE IF NOT EXISTS Runs(Id TEXT PRIMARY KEY, TaskId TEXT NOT NULL REFERENCES Tasks(Id), Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Runs_Task ON Runs(TaskId);
            CREATE TABLE IF NOT EXISTS Events(Sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                TaskId TEXT NOT NULL REFERENCES Tasks(Id), Kind TEXT NOT NULL, Message TEXT NOT NULL, CreatedAt TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Events_Task ON Events(TaskId, Sequence);
            CREATE TABLE IF NOT EXISTS Reviews(Id TEXT PRIMARY KEY, TaskId TEXT NOT NULL REFERENCES Tasks(Id),
                RunId TEXT NOT NULL UNIQUE REFERENCES Runs(Id), Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Reviews_Task ON Reviews(TaskId);
            CREATE TABLE IF NOT EXISTS Merges(Id TEXT PRIMARY KEY, TaskId TEXT NOT NULL REFERENCES Tasks(Id),
                RepositoryKey TEXT NOT NULL, State TEXT NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Merges_Task ON Merges(TaskId);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Merges_ActiveRepository ON Merges(RepositoryKey)
                WHERE State IN ('Pending','Uncertain');
            CREATE TABLE IF NOT EXISTS Plans(Id TEXT PRIMARY KEY, TaskId TEXT NOT NULL REFERENCES Tasks(Id),
                Revision INTEGER NOT NULL, Payload TEXT NOT NULL, UNIQUE(TaskId,Revision));
            CREATE TABLE IF NOT EXISTS RuleLibrary(Id TEXT PRIMARY KEY, Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Discussions(Id TEXT PRIMARY KEY, ProjectId TEXT NOT NULL REFERENCES Projects(Id), Payload TEXT NOT NULL);
            """);
        var tdd = new RuleDefinition(Guid.Parse("90d978ec-8485-4e0c-a90d-31a333881955"), "TDD 模式",
            "依序執行 Baseline、Red、Green、Refactor；保留既有測試，不得刪除、略過或弱化斷言。Red 必須有實際失敗測試，後續階段必須通過。",
            ["Planning", "Implementation", "Acceptance"], RequiresTdd: true);
        Execute(c, null, "INSERT OR IGNORE INTO RuleLibrary VALUES($id,$payload)", ("$id", tdd.Id.ToString()), ("$payload", JsonSerializer.Serialize(tdd)));
        foreach (var task in ReadList<WorkTask>(c, "SELECT Payload FROM Tasks"))
            if (task.Plan != null) RecordPlan(c, null, task, null, "Legacy", ignoreExisting: true);
        return Task.CompletedTask;
    }
    public Task AddProjectAsync(ProjectDefinition p, CancellationToken ct = default)
    {
        using var c = Open(ct);
        Execute(c, null, "INSERT INTO Projects VALUES($id,$payload)", ("$id", p.Id.ToString()), ("$payload", JsonSerializer.Serialize(p)));
        return Task.CompletedTask;
    }
    public Task<ProjectDefinition?> GetProjectAsync(Guid id, CancellationToken ct = default)
    { using var c = Open(ct); return Task.FromResult(ReadOne<ProjectDefinition>(c, null, "Projects", id)); }
    public Task<IReadOnlyList<ProjectDefinition>> ListProjectsAsync(CancellationToken ct = default)
    { using var c = Open(ct); return Task.FromResult(ReadList<ProjectDefinition>(c, "SELECT Payload FROM Projects ORDER BY Id")); }
    public Task AddTaskAsync(WorkTask t, CancellationToken ct = default)
    {
        using var c = Open(ct);
        using var tx = c.BeginTransaction();
        if (ReadOne<ProjectDefinition>(c, tx, "Projects", t.ProjectId) is null) throw new MissingEntityException("Project not found.");
        Execute(c, tx, "INSERT INTO Tasks VALUES($id,$project,$status,$version,$created,$payload)",
            ("$id", t.Id.ToString()), ("$project", t.ProjectId.ToString()), ("$status", (int)t.Status),
            ("$version", t.Version), ("$created", t.CreatedAt.ToString("O")), ("$payload", JsonSerializer.Serialize(t)));
        Event(c, tx, t.Id, "TaskCreated", t.Status.ToString());
        if (t.Plan != null) RecordPlan(c, tx, t, null, "Manual");
        tx.Commit();
        return Task.CompletedTask;
    }
    public Task<WorkTask?> GetTaskAsync(Guid id, CancellationToken ct = default)
    { using var c = Open(ct); return Task.FromResult(ReadOne<WorkTask>(c, null, "Tasks", id)); }
    public Task<IReadOnlyList<WorkTask>> ListTasksAsync(CancellationToken ct = default)
    { using var c = Open(ct); return Task.FromResult(ReadList<WorkTask>(c, "SELECT Payload FROM Tasks ORDER BY CreatedAt,Id")); }
    public Task SaveTaskAsync(WorkTask t, int expectedVersion, CancellationToken ct = default)
    {
        using var c = Open(ct);
        using var tx = c.BeginTransaction();
        var current = ReadOne<WorkTask>(c, tx, "Tasks", t.Id) ?? throw new MissingEntityException("Task not found.");
        if (current.Version != expectedVersion) throw new ConflictException("Task changed; reload before retrying.");
        if (current == t) return Task.CompletedTask;
        if (t.Version != expectedVersion + 1) throw new ConflictException("Invalid task version.");
        UpdateTask(c, tx, t, expectedVersion);
        if (t.Plan != current.Plan && t.Plan != null) RecordPlan(c, tx, t, null, "Manual");
        Event(c, tx, t.Id, "TaskUpdated", t.Status.ToString());
        tx.Commit();
        return Task.CompletedTask;
    }
    public Task<TaskClaim?> ClaimNextAsync(CancellationToken ct = default)
    {
        using var c = Open(ct);
        using var tx = c.BeginTransaction();
        using var command = Command(c, tx, "SELECT Payload FROM Tasks WHERE Status=$status ORDER BY CreatedAt,Id LIMIT 1", ("$status", (int)Status.Queued));
        var payload = command.ExecuteScalar() as string;
        if (payload is null) return Task.FromResult<TaskClaim?>(null);
        var current = JsonSerializer.Deserialize<WorkTask>(payload)!;
        var running = current with { Status = Status.Running, Version = current.Version + 1 };
        UpdateTask(c, tx, running, current.Version);
        var run = new TaskRun(Guid.NewGuid(), current.Id, "Running", null, null, null, null, DateTimeOffset.UtcNow, null,
            Selection: AiSelection.Normalize(current.Ai?.Implementation));
        Execute(c, tx, "INSERT INTO Runs VALUES($id,$task,$payload)", ("$id", run.Id.ToString()),
            ("$task", run.TaskId.ToString()), ("$payload", JsonSerializer.Serialize(run)));
        Event(c, tx, current.Id, "RunStarted", run.Id.ToString());
        tx.Commit();
        return Task.FromResult<TaskClaim?>(new(running, run));
    }
    public Task CompleteRunAsync(Guid runId, bool success, string? worktree, string? baseCommit, string? resultCommit, string? error, CancellationToken ct = default)
    {
        using var c = Open(ct);
        using var tx = c.BeginTransaction();
        var run = ReadOne<TaskRun>(c, tx, "Runs", runId) ?? throw new MissingEntityException("Run not found.");
        if (run.Kind != RunKind.Implementation) throw new ConflictException("Use planning completion for planning runs.");
        if (run.FinishedAt is not null) throw new ConflictException("Run already finished.");
        var task = ReadOne<WorkTask>(c, tx, "Tasks", run.TaskId)!;
        var status = task.Status == Status.Cancelled ? Status.Cancelled : success ? Status.AwaitingReview : Status.Failed;
        var lastReview = run.AiReviews?.LastOrDefault();
        var correction = status == Status.AwaitingReview && lastReview?.Verdict == "NeedsChanges" && task.AutoReviewAttempt < (task.Workflow?.MaxReviewRetries ?? 0);
        var finished = run with { Status = correction ? "AiChangesRequested" : status.ToString(), WorktreePath = worktree, BaseCommit = baseCommit,
            ResultCommit = resultCommit, Error = error, FinishedAt = DateTimeOffset.UtcNow };
        Execute(c, tx, "UPDATE Runs SET Payload=$payload WHERE Id=$id", ("$payload", JsonSerializer.Serialize(finished)), ("$id", runId.ToString()));
        if (task.Status != Status.Cancelled)
        {
            var updated = task with { Status = status, Version = task.Version + 1 };
            if (correction) updated = updated with { Status = Status.Queued, AutoReviewAttempt = task.AutoReviewAttempt + 1,
                RevisionCommit = resultCommit, RevisionInstructions = "AI acceptance correction:\n" + lastReview!.Summary };
            UpdateTask(c, tx, updated, task.Version);
            if (correction) Event(c, tx, task.Id, "AiCorrectionQueued", "AI requested changes; queued bounded correction round " + updated.AutoReviewAttempt);
        }
        Event(c, tx, task.Id, "RunFinished", finished.Status);
        tx.Commit();
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<TaskRun>> ListRunsAsync(Guid taskId, CancellationToken ct = default)
    { using var c = Open(ct); return Task.FromResult(ReadList<TaskRun>(c, "SELECT Payload FROM Runs WHERE TaskId=$id ORDER BY rowid", ("$id", taskId.ToString()))); }
    public Task<IReadOnlyList<TaskEvent>> GetEventsAsync(Guid taskId, long after = 0, CancellationToken ct = default)
    {
        if (after < 0) throw new ArgumentException("Event cursor cannot be negative.");
        using var c = Open(ct);
        using var cmd = Command(c, null, "SELECT Sequence,Kind,Message,CreatedAt FROM Events WHERE TaskId=$id AND Sequence>$after ORDER BY Sequence LIMIT 1000", ("$id", taskId.ToString()), ("$after", after));
        using var reader = cmd.ExecuteReader();
        var events = new List<TaskEvent>();
        while (reader.Read()) events.Add(new(reader.GetInt64(0), taskId, reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3))));
        return Task.FromResult<IReadOnlyList<TaskEvent>>(events);
    }
    public Task AppendEventAsync(Guid taskId, string kind, string message, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        Event(c, tx, taskId, kind, message); tx.Commit(); return Task.CompletedTask;
    }
    // Caller must own the single-worker lock. Never silently re-run interrupted CLI work.
    public async Task RecoverInterruptedAsync(CancellationToken ct = default)
    {
        foreach (var task in await ListTasksAsync(ct))
            foreach (var run in await ListRunsAsync(task.Id, ct))
                if (run.FinishedAt is null)
                {
                    const string error = "Worker interrupted; inspect retained worktree and any orphan process before retrying.";
                    if (run.Kind == RunKind.Planning) await CompletePlanningAsync(run.Id, null, error, ct);
                    else await CompleteRunAsync(run.Id, false, run.WorktreePath, run.BaseCommit, run.ResultCommit, error, ct);
                }
    }
    public Task SetWorkspaceAsync(Guid runId, Workspace workspace, CancellationToken ct = default)
    {
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var run = ReadOne<TaskRun>(c, tx, "Runs", runId) ?? throw new MissingEntityException("Run not found.");
        var updated = run with { WorktreePath = workspace.Path, BaseCommit = workspace.BaseCommit };
        Execute(c, tx, "UPDATE Runs SET Payload=$payload WHERE Id=$id",
            ("$payload", JsonSerializer.Serialize(updated)), ("$id", runId.ToString()));
        tx.Commit(); return Task.CompletedTask;
    }
    private static void UpdateTask(SqliteConnection c, SqliteTransaction tx, WorkTask task, int expectedVersion)
    {
        if (Execute(c, tx, "UPDATE Tasks SET Status=$status,Version=$version,Payload=$payload WHERE Id=$id AND Version=$expected",
            ("$status", (int)task.Status), ("$version", task.Version), ("$payload", JsonSerializer.Serialize(task)),
            ("$id", task.Id.ToString()), ("$expected", expectedVersion)) != 1) throw new ConflictException("Task changed.");
    }
    private static void Event(SqliteConnection c, SqliteTransaction tx, Guid id, string kind, string message) =>
        Execute(c, tx, "INSERT INTO Events(TaskId,Kind,Message,CreatedAt) VALUES($id,$kind,$message,$time)",
            ("$id", id.ToString()), ("$kind", kind), ("$message", message), ("$time", DateTimeOffset.UtcNow.ToString("O")));
    private static T? ReadOne<T>(SqliteConnection c, SqliteTransaction? tx, string table, Guid id)
    {
        using var cmd = Command(c, tx, $"SELECT Payload FROM {table} WHERE Id=$id", ("$id", id.ToString()));
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json) : default;
    }
    private static IReadOnlyList<T> ReadList<T>(SqliteConnection c, string sql, params (string, object?)[] parameters)
    {
        using var cmd = Command(c, null, sql, parameters);
        using var reader = cmd.ExecuteReader();
        var items = new List<T>();
        while (reader.Read()) items.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))!);
        return items;
    }
    private static int Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    { using var cmd = Command(c, tx, sql, parameters); return cmd.ExecuteNonQuery(); }
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
