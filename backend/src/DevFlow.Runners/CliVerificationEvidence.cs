using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevFlow.Core;
namespace DevFlow.Runners;

internal static class CliVerificationEvidence
{
    public static string Identity(CliCommand command) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Executable = OperatingSystem.IsWindows() ? command.Executable.ToUpperInvariant() : command.Executable, command.PrefixArguments }))));
    public static async Task RecordAsync(ICliVerificationStore? store, string provider, CliCommand command, bool success, string? model, string source)
    {
        if (store is null) return;
        try { await store.SaveCliVerificationAsync(new(provider, Identity(command), DateTimeOffset.UtcNow, success, model, source)); }
        // Recording a status must not turn a completed model call into a failed task.
        catch (Exception ex) when (ex is IOException or System.Data.Common.DbException or UnauthorizedAccessException) { }
    }
}
