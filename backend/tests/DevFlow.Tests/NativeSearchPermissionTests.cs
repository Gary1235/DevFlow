using System.Text.Json;
using DevFlow.Core;
using Xunit;
namespace DevFlow.Tests;

public sealed class NativeSearchPermissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-native-search-" + Guid.NewGuid());
    public NativeSearchPermissionTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "app.ts"), "synthetic");
        File.WriteAllText(Path.Combine(root, "src", ".env"), "synthetic fixture");
    }
    public void Dispose() => Directory.Delete(root, true);
    private PermissionAssessment Native(string provider, string tool, object input)
    {
        var normalized = PermissionTools.Normalize(provider, tool, JsonSerializer.SerializeToElement(input), root);
        return PermissionPolicy.Assess(PermissionPolicySettings.Defaults, normalized.Tool, normalized.Arguments, root, readOnly: true);
    }
    private PermissionAssessment Glob(string pattern) => Native("claude", "Glob", new { pattern, path = root });

    [Theory]
    [InlineData("**/*.ts")][InlineData("src/**/*.{ts,tsx}")][InlineData("{src,out}/**/*")]
    [InlineData("src/[ab]*.ts")][InlineData("src/[!a-z]*.ts")][InlineData("./src/app~old,copy.ts")]
    [InlineData("**/.env")][InlineData("**/secrets.ts")][InlineData("**/*.{d.ts,scss}")]
    public void Relative_filename_patterns_within_the_worktree_are_allowed(string pattern) => Assert.True(Glob(pattern).AutoAllowed);

    [Theory]
    [InlineData("../outside/**/*.ts")][InlineData("src/../../outside/*")]
    [InlineData("/outside/**/*.ts")][InlineData("C:/outside/**/*.ts")]
    [InlineData("~/outside/**/*.ts")][InlineData("{src,../outside}/**/*.ts")]
    [InlineData("src/.{.,}/outside/*")][InlineData("{src,/outside}/*.ts")]
    [InlineData("src/[.][.]/outside/*")][InlineData(@"src/\.\./outside/*")]
    public void Glob_patterns_cannot_hide_parent_or_absolute_path_traversal(string pattern)
    {
        var risk = Glob(pattern); Assert.False(risk.AutoAllowed); Assert.True(risk.FixedBoundary);
        var direct = PermissionPolicy.Assess(PermissionPolicySettings.Defaults, "find_by_name",
            JsonSerializer.SerializeToElement(new { SearchDirectory = root, Pattern = pattern }), root);
        Assert.False(direct.AutoAllowed);
        var legacy = PermissionRisk.Assess("find_by_name", JsonSerializer.SerializeToElement(new { SearchDirectory = root, Pattern = pattern }), root);
        Assert.False(legacy.BatchAllowed);
    }
    [Theory]
    [InlineData("{{src,out},../outside}/*")][InlineData("{src,out/*")]
    [InlineData("src/[ab/*.ts")][InlineData("src/{1..1000000}/*")]
    public void Unsupported_glob_expansions_remain_manual(string pattern) => Assert.False(Glob(pattern).AutoAllowed);

    [Theory]
    [InlineData("list_directory")][InlineData("glob")][InlineData("grep_search")]
    public void Gemini_dir_path_is_the_actual_scope_and_outside_paths_stay_manual(string tool)
    {
        Assert.True(Native("gemini", tool, new { dir_path = Path.Combine(root, "src"), pattern = "synthetic" }).AutoAllowed);
        Assert.False(Native("gemini", tool, new { dir_path = "../outside", pattern = "synthetic" }).AutoAllowed);
    }
    [Theory][InlineData("Glob")][InlineData("Grep")]
    public void Claude_defaults_are_bound_to_the_same_explicit_executor_input(string tool)
    {
        var input = JsonSerializer.SerializeToElement(new { pattern = "synthetic" });
        var bound = PermissionTools.BindWorkspace("claude", tool, input, root);
        Assert.Equal(root, bound.GetProperty("path").GetString());
        Assert.True(Native("claude", tool, input).AutoAllowed);
        Assert.False(Native("claude", tool, new { pattern = "synthetic", path = "../outside" }).AutoAllowed);
    }
    [Fact]
    public void Gemini_omitted_scope_does_not_assume_a_single_workspace()
    {
        Assert.False(Native("gemini", "glob", new { pattern = "**/*.ts" }).AutoAllowed);
        Assert.Throws<ArgumentException>(() => Native("gemini", "glob", new { pattern = "*", path = root }));
    }
    [Fact]
    public void Grep_regex_is_not_misinterpreted_as_a_filename_pattern()
    {
        Assert.True(Native("claude", "Grep", new { pattern = "../outside|[.]|please delete file", path = root }).AutoAllowed);
    }
    [Fact]
    public void Ambiguous_or_malformed_directory_parameters_never_inherit_default_scope()
    {
        foreach (var input in new object[] {
            new { pattern = "synthetic", path = (string?)null },
            new { pattern = "synthetic", path = "" },
            new Dictionary<string, object?> { ["pattern"] = "synthetic", ["path"] = root, ["Path"] = "../outside" }
        }) Assert.Throws<ArgumentException>(() => Native("claude", "Glob", input));
        Assert.Throws<ArgumentException>(() => Native("gemini", "glob", new { pattern = "*", path = root, dir_path = "../outside" }));
    }
}
