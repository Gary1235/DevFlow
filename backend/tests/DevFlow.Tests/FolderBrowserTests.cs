using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
namespace DevFlow.Tests;

public sealed class FolderBrowserTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-folders-" + Guid.NewGuid());
    public FolderBrowserTests() { Directory.CreateDirectory(Path.Combine(root, "專案")); File.WriteAllText(Path.Combine(root, "file.txt"), "not a folder"); }
    public void Dispose() { Directory.Delete(root, true); }
    [Fact] public void Lists_child_folders_with_absolute_paths_and_a_parent()
    {
        var listing = LocalFolderBrowser.Browse(root);
        Assert.Equal(root, listing.Path); Assert.Equal(Directory.GetParent(root)!.FullName, listing.ParentPath);
        var folder = Assert.Single(listing.Folders); Assert.Equal("專案", folder.Name); Assert.Equal(Path.Combine(root, "專案"), folder.Path);
        Assert.Empty(LocalFolderBrowser.Browse(folder.Path).Folders);
        Assert.Equal(root, LocalFolderBrowser.Browse(folder.Path + Path.DirectorySeparatorChar).ParentPath);
    }
    [Fact] public void Lists_local_drive_roots_without_a_desktop_dialog()
    {
        var listing = LocalFolderBrowser.Browse(null);
        Assert.Null(listing.Path); Assert.Null(listing.ParentPath);
        Assert.All(listing.Folders, folder => Assert.True(Path.IsPathFullyQualified(folder.Path)));
    }
    [Theory]
    [InlineData("relative/folder")]
    [InlineData("\\\\server\\share")]
    public void Rejects_relative_and_network_paths(string path) => Assert.Throws<ArgumentException>(() => LocalFolderBrowser.Browse(path));
    [Fact] public void Missing_folders_return_an_actionable_error() => Assert.Throws<MissingEntityException>(() => LocalFolderBrowser.Browse(Path.Combine(root, "missing")));
}
