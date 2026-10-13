using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;
namespace DevFlow.Tests;

public sealed class ValidationTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "devflow-validation-" + Guid.NewGuid());
    public Task InitializeAsync() { Directory.CreateDirectory(directory); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(directory, true); return Task.CompletedTask; }
    private sealed class ReportProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string w, string? input, CancellationToken ct)
        {
            Assert.Equal("dotnet", e); Assert.Equal("test", a[0]); Assert.Contains("trx", a);
            var index = a.ToList().IndexOf("--results-directory");
            var path = a[index + 1];
            for (int i = 0; i < 2; i++)
                File.WriteAllText(Path.Combine(path, $"{i}.trx"), $"""
                    <TestRun><Results><UnitTestResult testId="test-{i}" outcome="Passed" /></Results>
                    <ResultSummary><Counters total="1" failed="0" passed="1" /></ResultSummary></TestRun>
                    """);
            return Task.FromResult(new ProcessResult(0, "test output", ""));
        }
    }
    [Fact] public async Task Validation_aggregates_all_test_projects_and_saves_evidence()
    {
        var id = Guid.NewGuid();
        var validator = new DotnetValidationRunner(new ReportProcess(), directory);
        var evidence = await validator.ValidateAsync(new(Guid.NewGuid(), "Demo", directory, "main", ["test"]),
            new(directory, "base"), id, TddPhase.Green, CancellationToken.None);
        Assert.Equal(2, evidence.Total);
        Assert.Equal(2, evidence.PassedTests.Length);
        Assert.True(File.Exists(Path.Combine(directory, id.ToString("N"), "Green", "evidence.json")));
    }
    [Theory]
    [InlineData("run")]
    [InlineData("test", "--logger", "trx")]
    [InlineData("test", "--results-directory", "other")]
    public async Task Validation_rejects_commands_that_bypass_report_collection(params string[] args)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new DotnetValidationRunner(new ReportProcess(), directory)
            .ValidateAsync(new(Guid.NewGuid(), "Demo", directory, "main", args), new(directory, "base"),
                Guid.NewGuid(), TddPhase.Red, CancellationToken.None));
    }
}
