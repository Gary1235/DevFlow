using DevFlow.Core;
namespace DevFlow.Runners;

public sealed record CliCommand(string Executable, string[] PrefixArguments);
public sealed record CliProvider(string Id, bool Installed, string? Executable, string[] Models, bool ModelsVerified = false);
public sealed class CliRegistry
{
    private readonly Dictionary<string, CliCommand?> commands;
    private readonly Dictionary<string, string[]> models;
    public CliRegistry(string? codex = null, string? gemini = null, string? node = null,
        string[]? codexModels = null, string[]? geminiModels = null, string? claude = null, string[]? claudeModels = null)
    {
        node ??= FindOnPath("node");
        commands = new(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = Resolve(codex ?? FindCodex(), node),
            ["gemini"] = Resolve(gemini ?? FindGemini(), node),
            ["claude"] = Resolve(claude ?? FindClaude(), node)
        };
        models = new(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = ValidateModels(codexModels ?? []),
            ["gemini"] = ValidateModels(geminiModels ?? ["auto", "pro", "flash", "flash-lite"]),
            ["claude"] = ValidateModels(claudeModels ?? ["sonnet", "opus", "haiku"])
        };
    }
    public IReadOnlyList<CliProvider> List() => commands.Select(p => new CliProvider(p.Key,
        p.Value != null, p.Value?.Executable, models[p.Key])).ToArray();
    public CliCommand Require(string provider)
    {
        provider = AiSelection.Normalize(new(provider)).Provider;
        return commands[provider] ?? throw new ConflictException($"{provider} CLI is not installed or configured. Configure its native executable or JavaScript entry point.");
    }
    private static string[] ValidateModels(string[] values) => values.Select(v => AiSelection.Normalize(new("codex", v)).Model!)
        .Where(v => v != null).Distinct(StringComparer.Ordinal).ToArray();
    private static CliCommand? Resolve(string? path, string? node)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = Path.IsPathRooted(path) ? Path.GetFullPath(path) : FindOnPath(path);
        if (path is null || !File.Exists(path)) return null;
        if (Path.GetExtension(path).Equals(".js", StringComparison.OrdinalIgnoreCase))
            return node != null && File.Exists(node) ? new(node, [path]) : null;
        if (Path.GetExtension(path).ToLowerInvariant() is ".cmd" or ".bat" or ".ps1") return null;
        if (OperatingSystem.IsWindows() && !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return new(path, []);
    }
    private static string? FindOnPath(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var suffix in OperatingSystem.IsWindows() ? new[] { ".exe", "" } : new[] { "" })
            {
                var candidate = Path.Combine(directory.Trim('"'), name + suffix);
                if (OperatingSystem.IsWindows() && !Path.GetExtension(candidate).Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        return null;
    }
    private static string? FindCodex()
    {
        if (FindOnPath("codex") is { } native) return native;
        if (!OperatingSystem.IsWindows()) return null;
        var package = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex");
        var exe = Path.Combine(package, "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
        return File.Exists(exe) ? exe : null;
    }
    private static string? FindGemini()
    {
        if (FindOnPath("gemini") is { } native) return native;
        var js = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@google", "gemini-cli", "dist", "index.js");
        return File.Exists(js) ? js : null;
    }
    private static string? FindClaude()
    {
        if (FindOnPath("claude") is { } native) return native;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var executable = Path.Combine(home, ".local", "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        if (File.Exists(executable)) return executable;
        var js = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        return File.Exists(js) ? js : null;
    }
}
internal sealed class PrefixedProcess(IProcessRunner inner, CliCommand command) : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, string? standardInput, CancellationToken ct) =>
        inner.RunAsync(command.Executable, [.. command.PrefixArguments, .. arguments], workingDirectory, standardInput, ct);
}
