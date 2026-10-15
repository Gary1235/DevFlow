using DevFlow.Core;
using System.Text.Json;
namespace DevFlow.Infrastructure;
public sealed partial class SqliteTaskStore
{
    public Task<ProjectDefinition> SetProjectAiAsync(Guid id, AiConfiguration defaults, int expectedVersion, CancellationToken ct = default)
    {
        var normalized = AiConfiguration.Resolve(defaults ?? throw new ArgumentException("AI defaults are required."));
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var project = ReadOne<ProjectDefinition>(c, tx, "Projects", id) ?? throw new MissingEntityException("Project not found.");
        if (project.Version != expectedVersion) throw new ConflictException("Project changed; reload before saving.");
        var updated = project with { AiDefaults = normalized, Version = project.Version + 1 };
        Execute(c, tx, "UPDATE Projects SET Payload=$payload WHERE Id=$id", ("$payload", JsonSerializer.Serialize(updated)), ("$id", id.ToString()));
        tx.Commit(); return Task.FromResult(updated);
    }
}
