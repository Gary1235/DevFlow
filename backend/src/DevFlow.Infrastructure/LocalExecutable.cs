namespace DevFlow.Infrastructure;

// Invoke npm through its JavaScript entry point, avoiding shell quoting and .cmd execution.
public static class LocalExecutable
{
    public static (string Executable, string[] Arguments) Resolve(string executable, IEnumerable<string> arguments)
    {
        if (!executable.Equals("npm", StringComparison.OrdinalIgnoreCase)) return (executable, arguments.ToArray());
        var node = Environment.GetEnvironmentVariable("DevFlow__NodeExecutable") ?? Find("node") ?? "node";
        var npm = Environment.GetEnvironmentVariable("DevFlow__NpmCliPath");
        var candidates = new[] { npm,
            Path.Combine(Path.GetDirectoryName(node) ?? "", "node_modules", "npm", "bin", "npm-cli.js"),
            Path.Combine(Path.GetDirectoryName(Find("npm", ".cmd") ?? "") ?? "", "node_modules", "npm", "bin", "npm-cli.js") };
        npm = candidates.FirstOrDefault(p => p is not null && File.Exists(p));
        if (npm is null) throw new ArgumentException("找不到 npm-cli.js，請安裝 Node.js，或設定 DevFlow__NodeExecutable 與 DevFlow__NpmCliPath 後重啟。");
        return (node, new[] { npm }.Concat(arguments).ToArray());
    }
    private static string? Find(string name, string? suffix = null) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator).Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => Path.Combine(p.Trim('"'), name + (suffix ?? (OperatingSystem.IsWindows() ? ".exe" : "")))).FirstOrDefault(File.Exists);
}
