using DevFlow.Core;
namespace DevFlow.Infrastructure;

public sealed record FolderEntry(string Name, string Path);
public sealed record FolderListing(string? Path, string? ParentPath, FolderEntry[] Folders, bool Truncated = false);

public static class LocalFolderBrowser
{
    public static FolderListing Browse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var roots = DriveInfo.GetDrives().Where(IsReady)
                .Select(d => new FolderEntry(d.Name, d.RootDirectory.FullName)).ToArray();
            return new(null, null, roots);
        }
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("請輸入本機磁碟上的完整資料夾路徑。");
        var fullPath = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (!Directory.Exists(fullPath)) throw new MissingEntityException("資料夾不存在或無法存取，請選擇其他資料夾。");
        try
        {
            var directories = Directory.EnumerateDirectories(fullPath).Take(2001).ToArray();
            var entries = directories.Take(2000).Select(p => new FolderEntry(new DirectoryInfo(p).Name, p))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return new(fullPath, Directory.GetParent(fullPath)?.FullName, entries, directories.Length > 2000);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { throw new ConflictException("無法讀取這個資料夾，請返回上層或選擇其他路徑。"); }
    }
    private static bool IsReady(DriveInfo drive)
    {
        try { return drive.DriveType != DriveType.Network && drive.IsReady; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return false; }
    }
}
