using DevFlow.Core;
using System.Text.Json;
namespace DevFlow.Infrastructure;
public sealed class DotnetValidationRunner(IProcessRunner process, string artifactsRoot, string? scope = null) : IValidationRunner, IStepValidationRunner
{
    public IValidationRunner ForStep(Guid stepId) => new DotnetValidationRunner(process, artifactsRoot, "Step-" + stepId.ToString("N"));
    public async Task<ValidationEvidence> ValidateAsync(ProjectDefinition project, Workspace workspace, Guid runId,
        TddPhase phase, CancellationToken ct)
    {
        var profile = project.DefaultValidationProfileId is { } selected
            ? (project.ValidationProfiles ?? []).FirstOrDefault(p => p.Id == selected) ?? throw new ArgumentException("找不到所選驗證設定。")
            : null;
        if (profile is null) ValidateArguments(project.ValidationArguments);
        else ProjectExecutionSettings.Normalize(new[] { profile });
        var directory = Path.Combine(Path.GetFullPath(artifactsRoot), runId.ToString("N"), scope ?? "", phase.ToString());
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.GetFiles(directory, "*.trx")) File.Delete(old);
        var kind = profile?.Kind ?? "DotnetTest";
        var working = ProjectExecutionSettings.ResolveDirectory(workspace.Path, profile?.WorkingDirectory ?? ".");
        var arguments = profile?.Arguments ?? project.ValidationArguments;
        if (kind == "DotnetTest") arguments = arguments.Concat(new[] { "--logger", "trx", "--results-directory", directory }).ToArray();
        var executable = LocalExecutable.Resolve(profile?.Executable ?? "dotnet", arguments);
        await File.WriteAllTextAsync(Path.Combine(directory, "command.json"), JsonSerializer.Serialize(new { kind, workingDirectory = working, executable = executable.Executable, arguments = executable.Arguments, prepareArguments = profile?.PrepareArguments }), ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile?.TimeoutSeconds ?? 600));
        ProcessResult result;
        try {
            ProcessResult? preparation = null;
            if (profile?.PrepareArguments is { Length: > 0 } prepare) {
                var setup = LocalExecutable.Resolve(profile.Executable, prepare);
                preparation = await process.RunAsync(setup.Executable, setup.Arguments, working, null, timeout.Token);
            }
            if (preparation is { ExitCode: not 0 }) result = preparation with { StandardError = "依賴準備失敗。\n" + preparation.StandardError };
            else {
                result = await process.RunAsync(executable.Executable, executable.Arguments, working, null, timeout.Token);
                if (preparation is not null) result = result with { StandardOutput = "[依賴準備]\n" + preparation.StandardOutput + "\n[驗證]\n" + result.StandardOutput,
                    StandardError = preparation.StandardError + "\n" + result.StandardError };
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result = new(-1, "", "驗證逾時，已停止程序。"); }
        catch (System.ComponentModel.Win32Exception ex) { result = new(-1, "", "無法啟動驗證程式：" + ex.Message); }
        await File.WriteAllTextAsync(Path.Combine(directory, "stdout.log"), result.StandardOutput, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "stderr.log"), result.StandardError, ct);
        var reports = kind == "DotnetTest" ? Directory.GetFiles(directory, "*.trx").Select(path => TrxEvidence.Read(path, result.ExitCode)).ToArray() : [];
        var error = string.Join("\n", (result.StandardError + "\n" + result.StandardOutput).Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)).Take(8)).Trim();
        var evidence = new ValidationEvidence(result.ExitCode, reports.Sum(r => r.Total), reports.Sum(r => r.Failed),
            reports.SelectMany(r => r.PassedTests).Distinct().ToArray(), reports.SelectMany(r => r.FailedTests).Distinct().ToArray(), kind,
            result.ExitCode != 0 ? error[..Math.Min(2000, error.Length)] : null);
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
