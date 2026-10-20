using DevFlow.Core;
using System.Text.Json;
namespace DevFlow.Infrastructure;
public sealed class DotnetValidationRunner(IProcessRunner process, string artifactsRoot, string? scope = null) : IValidationRunner, IStepValidationRunner
{
    public IValidationRunner ForStep(Guid stepId) => new DotnetValidationRunner(process, artifactsRoot, "Step-" + stepId.ToString("N"));
    public async Task<ValidationEvidence> ValidateAsync(ProjectDefinition project, Workspace workspace, Guid runId,
        TddPhase phase, CancellationToken ct)
    {
        ValidateArguments(project.ValidationArguments);
        var directory = Path.Combine(Path.GetFullPath(artifactsRoot), runId.ToString("N"), scope ?? "", phase.ToString());
        Directory.CreateDirectory(directory);
        var args = project.ValidationArguments.Concat(new[] { "--logger", "trx", "--results-directory", directory }).ToArray();
        var result = await process.RunAsync("dotnet", args, workspace.Path, null, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "stdout.log"), result.StandardOutput, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "stderr.log"), result.StandardError, ct);
        var reports = Directory.GetFiles(directory, "*.trx").Select(path => TrxEvidence.Read(path, result.ExitCode)).ToArray();
        var evidence = new ValidationEvidence(result.ExitCode, reports.Sum(r => r.Total), reports.Sum(r => r.Failed),
            reports.SelectMany(r => r.PassedTests).Distinct().ToArray(), reports.SelectMany(r => r.FailedTests).Distinct().ToArray());
        await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"), JsonSerializer.Serialize(evidence), ct);
        return evidence;
    }
    public static void ValidateArguments(string[]? arguments)
    {
        if (arguments is not { Length: > 0 } || arguments[0] != "test" || arguments.Any(string.IsNullOrWhiteSpace) ||
            arguments.Any(a => a == "-l" || a.StartsWith("--logger", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("--results-directory", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Validation must use dotnet test; logger and results directory are managed by DevFlow.");
    }
}
