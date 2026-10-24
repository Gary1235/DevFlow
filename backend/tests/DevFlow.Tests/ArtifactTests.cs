using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
namespace DevFlow.Tests;

public sealed class ArtifactTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-artifacts-" + Guid.NewGuid());
    private readonly Guid runId = Guid.NewGuid();
    private string RunRoot => Path.Combine(root, runId.ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(RunRoot); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }
    [Fact] public async Task Artifact_listing_and_paging_preserve_unicode_content()
    {
        const string text = "A😀中文B";
        await File.WriteAllTextAsync(Path.Combine(RunRoot, "Red-agent.jsonl"), text);
        var reader = new RunArtifactReader(root);
        Assert.Equal("Red-agent.jsonl", Assert.Single(await reader.ListAsync(runId)).Path);
        var first = await reader.ReadAsync(runId, "Red-agent.jsonl", 0, 2);
        Assert.Equal("A", first.Text);
        var second = await reader.ReadAsync(runId, first.Path, first.NextOffset, 3);
        var third = await reader.ReadAsync(runId, first.Path, second.NextOffset, 10);
        Assert.Equal(text, first.Text + second.Text + third.Text);
        Assert.False(third.HasMore);
    }
    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("C:\\secret.txt")]
    [InlineData("/secret.txt")]
    public async Task Paths_cannot_escape_run_directory(string path) =>
        await Assert.ThrowsAsync<ArgumentException>(() => new RunArtifactReader(root).ReadAsync(runId, path));
    [Fact] public async Task Unknown_artifact_and_invalid_cursor_have_clear_errors()
    {
        var reader = new RunArtifactReader(root);
        await Assert.ThrowsAsync<MissingEntityException>(() => reader.ReadAsync(runId, "missing.log"));
        await File.WriteAllTextAsync(Path.Combine(RunRoot, "out.log"), "abc");
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(runId, "out.log", -1));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(runId, "out.log", 4));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(runId, "out.log", 0, 0));
    }
    [Fact] public async Task Validation_summaries_are_read_from_persisted_phase_evidence()
    {
        var phase = Path.Combine(RunRoot, "Green");
        Directory.CreateDirectory(phase);
        await File.WriteAllTextAsync(Path.Combine(phase, "evidence.json"), "{\"ExitCode\":0,\"Total\":4,\"Failed\":0,\"PassedTests\":[\"x\"],\"FailedTests\":[]}");
        var summary = Assert.Single(await new RunArtifactReader(root).ValidationsAsync(runId));
        Assert.Equal(TddPhase.Green, summary.Phase);
        Assert.Equal(4, summary.Total);
        Assert.Equal("Green/evidence.json", summary.EvidencePath);
    }
    [Fact] public async Task One_character_page_still_advances_over_a_surrogate_pair()
    {
        await File.WriteAllTextAsync(Path.Combine(RunRoot, "out.log"), "😀X");
        var page = await new RunArtifactReader(root).ReadAsync(runId, "out.log", 0, 1);
        Assert.Equal("😀", page.Text);
        Assert.Equal(2, page.NextOffset);
        await Assert.ThrowsAsync<ArgumentException>(() => new RunArtifactReader(root).ReadAsync(runId, "out.log", 1, 1));
    }
    [Fact] public async Task Repair_and_structured_tool_evidence_are_visible_without_overwriting_the_original()
    {
        foreach (var scope in new[] { "Implementation", "Attempt-1/Implementation", "Structured-1/Implementation" }) {
            var directory = Path.Combine(RunRoot, scope); Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"), "{\"ExitCode\":0,\"Total\":0,\"Failed\":0,\"Kind\":\"Command\"}");
        }
        var rows = await new RunArtifactReader(root).ValidationsAsync(runId);
        Assert.Equal(3, rows.Count); Assert.Contains(rows, r => r.Origin == "Worker" && r.Attempt == 0);
        Assert.Contains(rows, r => r.Origin == "Repair" && r.Attempt == 1);
        Assert.Contains(rows, r => r.Origin == "StructuredTool");
    }
    [Fact] public async Task Corrupt_evidence_is_not_reported_as_success()
    {
        Directory.CreateDirectory(Path.Combine(RunRoot, "Green"));
        await File.WriteAllTextAsync(Path.Combine(RunRoot, "Green", "evidence.json"), "partial json");
        await Assert.ThrowsAsync<ConflictException>(() => new RunArtifactReader(root).ValidationsAsync(runId));
    }
}
