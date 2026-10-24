using System.Text.Json;
using DevFlow.Core;
using Microsoft.Data.Sqlite;
using Status = DevFlow.Core.TaskStatus;

namespace DevFlow.Infrastructure;
public sealed partial class SqliteTaskStore
{
    // Runs and claims share the same immediate transaction. Different project IDs
    // pointing at the same checkout must still serialize.
    private static WorkTask? NextForRepository(SqliteConnection c, SqliteTransaction tx, Status status)
    {
        var projects = new Dictionary<Guid, string>();
        using (var cmd = Command(c, tx, "SELECT Payload FROM Projects"))
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) {
            var p = JsonSerializer.Deserialize<ProjectDefinition>(reader.GetString(0))!;
            projects[p.Id] = RepositoryIdentity.Key(p.RepositoryPath);
        }
        var occupied = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        using (var cmd = Command(c, tx, "SELECT ProjectId FROM Tasks WHERE Status IN ($running,$planning,$merging)",
            ("$running", (int)Status.Running), ("$planning", (int)Status.Planning), ("$merging", (int)Status.Merging)))
        using (var reader = cmd.ExecuteReader()) while (reader.Read())
            if (projects.TryGetValue(Guid.Parse(reader.GetString(0)), out var repository)) occupied.Add(repository);
        using var next = Command(c, tx, "SELECT Payload FROM Tasks WHERE Status=$status ORDER BY CreatedAt,Id", ("$status", (int)status));
        using var candidates = next.ExecuteReader();
        while (candidates.Read()) {
            var task = JsonSerializer.Deserialize<WorkTask>(candidates.GetString(0))!;
            if (projects.TryGetValue(task.ProjectId, out var repository) && !occupied.Contains(repository)) return task;
        }
        return null;
    }
}
