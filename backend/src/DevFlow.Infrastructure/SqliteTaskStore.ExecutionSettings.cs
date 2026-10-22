using System.Text.Json;
using DevFlow.Core;
namespace DevFlow.Infrastructure;

public sealed partial class SqliteTaskStore
{
    public Task<ProjectDefinition> SetExecutionSettingsAsync(Guid id, int expectedVersion, ValidationProfile[]? profiles,
        string? defaultId, PreviewService[]? services, AiConfiguration? ai, CancellationToken ct = default)
    {
        var validations = ProjectExecutionSettings.Normalize(profiles);
        var previews = ProjectExecutionSettings.Normalize(services);
        if (defaultId is not null && !validations.Any(p => p.Id == defaultId)) throw new ArgumentException("預設驗證設定不存在。");
        using var c = Open(ct); using var tx = c.BeginTransaction();
        var project = ReadOne<ProjectDefinition>(c, tx, "Projects", id) ?? throw new MissingEntityException("Project not found.");
        if (project.Version != expectedVersion) throw new ConflictException("專案設定已改變，請重新讀取。");
        var updated = project with { ValidationProfiles = validations, DefaultValidationProfileId = defaultId, PreviewServices = previews,
            AiDefaults = ai is null ? project.AiDefaults : AiConfiguration.Resolve(ai), Version = project.Version + 1 };
        Execute(c, tx, "UPDATE Projects SET Payload=$payload WHERE Id=$id", ("$payload", JsonSerializer.Serialize(updated)), ("$id", id.ToString()));
        tx.Commit(); return Task.FromResult(updated);
    }
}
