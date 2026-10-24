namespace DevFlow.Infrastructure;
public static class RepositoryIdentity
{
    // Git worktrees share commondir. Resolve directory links component by component
    // so distinct project paths cannot acquire independent execution slots.
    public static string Key(string repository)
    {
        var root = Canonical(repository); var git = Path.Combine(root, ".git");
        if (File.Exists(git) && new FileInfo(git).Length <= 4096) {
            var value = File.ReadAllText(git).Trim();
            if (value.StartsWith("gitdir: ", StringComparison.Ordinal)) git = Path.GetFullPath(value[8..], root);
        }
        if (!Directory.Exists(git)) return root;
        var common = Path.Combine(git, "commondir");
        if (File.Exists(common) && new FileInfo(common).Length <= 4096) git = Path.GetFullPath(File.ReadAllText(common).Trim(), git);
        return Canonical(git);
    }
    private static string Canonical(string path)
    {
        var full = Path.GetFullPath(path); var prefix = Path.GetPathRoot(full)!;
        foreach (var component in full[prefix.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)) {
            prefix = Path.Combine(prefix, component);
            var directory = new DirectoryInfo(prefix);
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) prefix = directory.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Repository 連結無法解析。");
        }
        return Path.TrimEndingDirectorySeparator(prefix);
    }
}
