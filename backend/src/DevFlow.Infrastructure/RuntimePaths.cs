namespace DevFlow.Infrastructure;

public sealed record RuntimePaths(string Database, string Artifacts, string Worktrees, string CliControl)
{
    public static RuntimePaths Resolve(string currentDirectory, string applicationDirectory,
        string? database = null, string? artifacts = null, string? worktrees = null, string? cliControl = null)
    {
        var repository = FindRepository(currentDirectory) ?? FindRepository(applicationDirectory);
        // Keep the development database already used by the API; no copy or migration.
        var data = repository is null ? Path.Combine(currentDirectory, ".data") : Path.Combine(repository, "backend", "src", "DevFlow.Api", ".data");
        var db = Path.GetFullPath(database ?? Path.Combine(data, "devflow.db"), currentDirectory);
        data = Path.GetDirectoryName(db)!;
        var defaultWorktrees = database is null && repository is not null ? repository + ".worktrees" : Path.Combine(data, "worktrees");
        return new(db, Path.GetFullPath(artifacts ?? Path.Combine(data, "artifacts"), currentDirectory),
            Path.GetFullPath(worktrees ?? defaultWorktrees, currentDirectory),
            Path.GetFullPath(cliControl ?? Path.Combine(data, "cli-control"), currentDirectory));
    }
    private static string? FindRepository(string start)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend", "src", "DevFlow.Api", "DevFlow.Api.csproj")) &&
                File.Exists(Path.Combine(directory.FullName, "backend", "src", "DevFlow.Worker", "DevFlow.Worker.csproj"))) return directory.FullName;
        return null;
    }
}
