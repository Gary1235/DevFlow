using DevFlow.Core;
using System.Text.Json;
namespace DevFlow.Infrastructure;
public sealed class DotnetValidationRunner(IProcessRunner process, string artifactsRoot, string? scope = null) : IValidationRunner, IStepValidationRunner, IValidationAttemptRunner
{
    public IValidationRunner ForStep(Guid stepId) => new DotnetValidationRunner(process, artifactsRoot, "Step-" + stepId.ToString("N"));
    public IValidationRunner ForAttempt(int attempt) => new DotnetValidationRunner(process, artifactsRoot, Path.Combine(scope ?? "", "Attempt-" + attempt));
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
        var failureKind = "CommandFailed";
        async Task<ProcessResult> Run(string exe, string[] argv, string stage) {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(profile?.TimeoutSeconds ?? 600));
            try { return await process.RunAsync(exe, argv, working, null, timeout.Token); }
            catch (ProcessInterruptedException ex) when (!ct.IsCancellationRequested) {
                failureKind = "Timeout"; return new(-1, ex.StandardOutput, ex.StandardError + "\n" + stage + "逾時，已停止程序。");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                failureKind = "Timeout"; return new(-1, "", stage + "逾時，已停止程序。");
            }
        }
        ProcessResult result;
        try {
            ProcessResult? preparation = null;
            if (profile?.PrepareArguments is { Length: > 0 } prepare) {
                var setup = LocalExecutable.Resolve(profile.Executable, prepare);
                var cache = PreparationCache(profile, setup, working, runId);
                preparation = cache is not null && File.Exists(cache) && Directory.Exists(Path.Combine(working, "node_modules"))
                    ? new(0, "依賴準備快取命中：本次工作副本的 package.json、lockfile 與執行設定未變更。", "")
                    : await Run(setup.Executable, setup.Arguments, "依賴準備");
                if (cache is not null && preparation.ExitCode == 0 && Directory.Exists(Path.Combine(working, "node_modules"))) {
                    Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                    await File.WriteAllTextAsync(cache, DateTimeOffset.UtcNow.ToString("O"), ct);
                }
                await File.WriteAllTextAsync(Path.Combine(directory, "prepare-stdout.log"), preparation.StandardOutput, ct);
                await File.WriteAllTextAsync(Path.Combine(directory, "prepare-stderr.log"), preparation.StandardError, ct);
                if (preparation.ExitCode != 0) failureKind = "Environment";
            }
            if (preparation is { ExitCode: not 0 }) result = preparation with { StandardError = "依賴準備失敗。\n" + preparation.StandardError };
            else {
                result = await Run(executable.Executable, executable.Arguments, "驗證");
                if (preparation is not null) result = result with { StandardOutput = "[依賴準備]\n" + preparation.StandardOutput + "\n[驗證]\n" + result.StandardOutput,
                    StandardError = preparation.StandardError + "\n" + result.StandardError };
            }
        }
        catch (System.ComponentModel.Win32Exception ex) { failureKind = "Environment"; result = new(-1, "", "無法啟動驗證程式：" + ex.Message); }
        await File.WriteAllTextAsync(Path.Combine(directory, "stdout.log"), result.StandardOutput, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "stderr.log"), result.StandardError, ct);
        var reports = kind == "DotnetTest" ? Directory.GetFiles(directory, "*.trx").Select(path => TrxEvidence.Read(path, result.ExitCode)).ToArray() : [];
        var error = Summarize(result.StandardError + "\n" + result.StandardOutput);
        var evidence = new ValidationEvidence(result.ExitCode, reports.Sum(r => r.Total), reports.Sum(r => r.Failed),
            reports.SelectMany(r => r.PassedTests).Distinct().ToArray(), reports.SelectMany(r => r.FailedTests).Distinct().ToArray(), kind,
            result.ExitCode != 0 ? error : null, result.ExitCode != 0 ? failureKind : null);
        await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"), JsonSerializer.Serialize(evidence), ct);
        return evidence;
    }
    private string? PreparationCache(ValidationProfile profile, (string Executable, string[] Arguments) setup, string working, Guid runId)
    {
        // Only a known package-manager preparation can be reused. Custom scripts
        // retain their explicit every-validation semantics.
        if (!profile.Executable.Equals("npm", StringComparison.OrdinalIgnoreCase) || profile.PrepareArguments is not ["ci"]) return null;
        var inputs = new List<string> { working, setup.Executable, JsonSerializer.Serialize(setup.Arguments) };
        foreach (var name in new[] { "package.json", "package-lock.json", "npm-shrinkwrap.json", ".npmrc" }) {
            var file = Path.Combine(working, name);
            if (!File.Exists(file)) continue;
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) return null;
            inputs.Add(name + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
        }
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", inputs))));
        return Path.Combine(Path.GetFullPath(artifactsRoot), runId.ToString("N"), "environment", digest + ".ready");
    }
    public static string Summarize(string output)
    {
        var lines = output.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        var important = lines.Where(s => s.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("failed", StringComparison.OrdinalIgnoreCase) || s.Contains("錯誤", StringComparison.Ordinal) || s.Contains("失敗", StringComparison.Ordinal));
        var summary = string.Join("\n", important.Take(12).Concat(lines.TakeLast(8)).Distinct());
        return summary[..Math.Min(6000, summary.Length)];
    }
    public static void ValidateArguments(string[]? arguments)
    {
        if (arguments is not { Length: > 0 } || arguments[0] != "test" || arguments.Any(string.IsNullOrWhiteSpace) ||
            arguments.Any(a => a == "-l" || a.StartsWith("--logger", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("--results-directory", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Validation must use dotnet test; logger and results directory are managed by DevFlow.");
    }
}
