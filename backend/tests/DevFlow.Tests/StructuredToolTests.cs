using System.Text.Json;
using System.Threading.Channels;
using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Runners;
using Xunit;
namespace DevFlow.Tests;
public sealed class StructuredToolTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-structured-" + Guid.NewGuid());
    private SqliteTaskStore store = null!; private ProjectDefinition project = null!; private WorkTask task = null!; private Guid runId;
    public async Task InitializeAsync() {
        store = new(Path.Combine(root, "tasks.db")); await store.InitializeAsync();
        project = new(Guid.NewGuid(), "Tools", root, "main", ["test"], ValidationProfiles: [new("web", "Build", ".", "node", ["build.js"])]);
        await store.AddProjectAsync(project);
        task = WorkTask.Create(project.Id, "Task", "Build", "Pass").SetPlan("Plan").ApprovePlan(); await store.AddTaskAsync(task); runId = (await store.ClaimNextAsync())!.Run.Id;
    }
    public Task DisposeAsync() {
        if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("devflow-structured-", StringComparison.Ordinal)) throw new InvalidOperationException();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true); return Task.CompletedTask;
    }
    private IStructuredTools Tools(IProcessRunner? process = null, IToolApprovalOperation? operation = null) => new StructuredToolExecutor(store, process ?? new ProcessRunner(), Path.Combine(root, "artifacts"), task, new(root, "base"), runId,
        operation ?? new TaskPermissionPlanningRunner.Operation(store, task.Id, runId, default, "codex", false));
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    [Fact] public async Task Literal_files_are_scoped_audited_and_unicode_pagination_does_not_split_a_surrogate_pair()
    {
        var tools = Tools();
        await tools.ExecuteAsync("devflow_write_file", Args(new { path = "src/example.txt", content = "😀hello" }), default);
        var page = JsonDocument.Parse(await tools.ExecuteAsync("devflow_read_file", Args(new { path = "src/example.txt", offset = 0, limit = 1 }), default));
        Assert.Equal("😀", page.RootElement.GetProperty("text").GetString()); Assert.Equal(2, page.RootElement.GetProperty("nextOffset").GetInt32());
        Assert.All(await store.ListToolApprovalsAsync(task.Id), r => Assert.Equal("AllowedByPolicy", r.State));
        await Assert.ThrowsAsync<ArgumentException>(() => tools.ExecuteAsync("devflow_write_file", Args(new { path = "../outside.txt", content = "escape" }), default));
        await Assert.ThrowsAsync<ArgumentException>(() => tools.ExecuteAsync("devflow_write_file", Args(new { path = "AGENTS.md", content = "override" }), default));
    }
    [Fact] public async Task Readonly_sessions_do_not_expose_or_execute_write_and_validation_tools()
    {
        var tools = Tools(operation: new Approval(readOnly: true));
        Assert.DoesNotContain(tools.Definitions, d => d.Name == "devflow_write_file" || d.Name == "devflow_run_validation");
        await Assert.ThrowsAsync<ArgumentException>(() => tools.ExecuteAsync("devflow_write_file", Args(new { path = "bad.txt", content = "bad" }), default));
        Assert.False(File.Exists(Path.Combine(root, "bad.txt")));
    }
    [Fact] public async Task Read_cursor_rejects_out_of_range_and_mid_surrogate_offsets()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "unicode.txt"), "😀tail");
        var tools = Tools();
        foreach (var offset in new[] { 1, 100 })
            await Assert.ThrowsAsync<ArgumentException>(() => tools.ExecuteAsync("devflow_read_file", Args(new { path = "unicode.txt", offset, limit = 3 }), default));
        var tail = JsonDocument.Parse(await tools.ExecuteAsync("devflow_read_file", Args(new { path = "unicode.txt", offset = 2, limit = 4 }), default));
        Assert.Equal("tail", tail.RootElement.GetProperty("text").GetString());
    }
    [Fact] public async Task File_listing_treats_square_brackets_as_a_literal_directory_name()
    {
        var process = new ProcessRunner();
        Assert.Equal(0, (await process.RunAsync("git", ["init"], root, null, default)).ExitCode);
        Directory.CreateDirectory(Path.Combine(root, "src[1]")); Directory.CreateDirectory(Path.Combine(root, "src1"));
        await File.WriteAllTextAsync(Path.Combine(root, "src[1]/literal.txt"), "literal");
        await File.WriteAllTextAsync(Path.Combine(root, "src1/unrelated.txt"), "unrelated");
        var response = await Tools(process).ExecuteAsync("devflow_list_files", Args(new { path = "src[1]" }), default);
        Assert.Contains("literal.txt", response); Assert.DoesNotContain("unrelated.txt", response);
    }
    [Fact] public async Task Large_unicode_writes_use_a_bounded_audit_preview_and_keep_the_exact_content()
    {
        var content = new string('測', 9000);
        await Tools().ExecuteAsync("devflow_write_file", Args(new { path = "unicode.txt", content }), default);
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(root, "unicode.txt")));
        var request = Assert.Single(await store.ListToolApprovalsAsync(task.Id));
        Assert.True(request.Arguments.TryGetProperty("ContentSha256", out _)); Assert.True(request.Arguments.GetRawText().Length < 20000);
    }
    [Fact] public async Task Recent_polling_keeps_older_pending_requests_and_history_pages_do_not_overlap()
    {
        var pending = new TaskToolApproval(Guid.NewGuid(), task.Id, runId, "unknown_tool", Args(new { value = 1 }), root, DateTimeOffset.UtcNow, null);
        await store.AddToolApprovalAsync(pending);
        for (var i = 0; i < 105; i++) await store.AddToolApprovalAsync(new(Guid.NewGuid(), task.Id, runId, "read_file", Args(new { Path = "source.txt" }), root, DateTimeOffset.UtcNow, null));
        var recent = await store.RecentToolApprovalsAsync(task.Id, default);
        Assert.Equal(101, recent.Count); Assert.Contains(recent, r => r.Id == pending.Id && r.State == "Pending");
        var first = await store.PermissionHistoryAsync(task.Id, runId, null, 60);
        var second = await store.PermissionHistoryAsync(task.Id, runId, first.NextBefore, 60);
        Assert.Equal(60, first.Items.Length); Assert.Equal(45, second.Items.Length); Assert.Null(second.NextBefore);
        Assert.Empty(first.Items.Select(r => r.Id).Intersect(second.Items.Select(r => r.Id)));
    }
    [Fact] public async Task Saved_validation_uses_literal_argv_and_setting_change_invalidates_the_pending_grant()
    {
        var capture = new Capture();
        var result = await Tools(capture).ExecuteAsync("devflow_run_validation", Args(new { profileId = "web" }), default);
        Assert.Contains("\"ExitCode\":0", result); Assert.Single(capture.Calls); Assert.Equal(new[] { "build.js" }, capture.Calls[0]);
        var changed = new Approval(onApproval: async () => { await store.SetExecutionSettingsAsync(project.Id, 0, [], null, [], null); });
        await Assert.ThrowsAsync<ConflictException>(() => Tools(capture, changed).ExecuteAsync("devflow_run_validation", Args(new { profileId = "web" }), default));
        Assert.Single(capture.Calls);
    }
    [Fact] public async Task Validation_evidence_from_separate_cli_stages_in_one_run_is_never_overwritten()
    {
        await Tools(new Capture()).ExecuteAsync("devflow_run_validation", Args(new { profileId = "web" }), default);
        await Tools(new Capture()).ExecuteAsync("devflow_run_validation", Args(new { profileId = "web" }), default);
        var evidence = await new RunArtifactReader(Path.Combine(root, "artifacts")).ValidationsAsync(runId);
        Assert.Equal(2, evidence.Count); Assert.Equal(2, evidence.Select(p => p.EvidencePath).Distinct().Count());
        Assert.All(evidence, p => Assert.Equal("StructuredTool", p.Origin));
    }
    [Fact] public async Task Git_file_set_search_excludes_ignored_dependencies_without_scanning_them()
    {
        var process = new ProcessRunner();
        var init = await process.RunAsync("git", ["init", "-b", "main"], root, null, default); Assert.Equal(0, init.ExitCode);
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "node_modules/\n*.db*\n");
        await File.WriteAllTextAsync(Path.Combine(root, "source.txt"), "needle in source");
        Directory.CreateDirectory(Path.Combine(root, "node_modules")); await File.WriteAllTextAsync(Path.Combine(root, "node_modules/ignored.txt"), "needle should not appear");
        var result = await Tools(process).ExecuteAsync("devflow_search", Args(new { path = ".", text = "needle" }), default);
        Assert.Contains("source.txt", result); Assert.DoesNotContain("ignored.txt", result);
    }
    [Fact] public void Structured_validation_preserves_explicit_restrictions_and_does_not_grant_installation_or_arbitrary_programs()
    {
        PermissionAssessment Risk(ProjectDefinition p, bool readOnly = false, PermissionPolicySettings? settings = null) => PermissionPolicy.Assess(settings ?? PermissionPolicySettings.Defaults,
            "run_validation", Args(new { ProfileId = "web", ProjectVersion = p.Version }), root, readOnly, project: p);
        Assert.True(Risk(project).AutoAllowed);
        Assert.False(Risk(project, true).AutoAllowed);
        Assert.False(Risk(project, settings: new(SensitiveCommands: ["node"])).AutoAllowed);
        foreach (var executable in new[] { "psql", "curl", "unknown.exe" }) Assert.False(Risk(project with { ValidationProfiles = [new("web", "Other", ".", executable, ["query"])] }).AutoAllowed);
        Assert.False(Risk(project with { ValidationProfiles = [new("web", "Install", ".", "npm", ["ci"])] }).AutoAllowed);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Real_adapter_advertises_dynamic_tools_and_handles_the_native_call_protocol(bool wrongThread)
    {
        var fixture = new ProtocolFixture(wrongThread);
        var runner = new RoutedCliRunner(fixture, new CliRegistry(codex: Environment.ProcessPath!), root, store,
            structuredTools: new StructuredToolFactory(store, new Capture(), root));
        await File.WriteAllTextAsync(Path.Combine(root, "source.txt"), "literal source evidence");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (wrongThread) await Assert.ThrowsAsync<InvalidOperationException>(() => runner.PlanAsync(task, new(root, "base"), runId, timeout.Token));
        else {
            var plan = await runner.PlanAsync(task, new(root, "base"), runId, timeout.Token);
            Assert.Contains("Update", plan); Assert.Contains("literal source evidence", fixture.ToolOutput);
            Assert.Equal(3, fixture.Definitions); // Planning only receives read tools.
        }
    }
    private sealed class Approval(bool readOnly = false, Func<Task>? onApproval = null) : IToolApprovalOperation {
        public Guid Id => Guid.Empty; public CancellationToken Token => default; public bool ReadOnly => readOnly;
        public async Task<bool> RequestPermissionAsync(string tool, JsonElement args, string workspace, CancellationToken ct) { if (onApproval is not null) await onApproval(); return true; }
    }
    private sealed class Capture : IProcessRunner {
        public List<string[]> Calls = [];
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string cwd, string? input, CancellationToken ct) { Calls.Add(a.ToArray()); return Task.FromResult(new ProcessResult(0, "ok", "")); }
    }
    private sealed class ProtocolFixture(bool wrongThread) : IProcessRunner, IInteractiveProcessRunner {
        public int Definitions; public string? ToolOutput;
        public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string cwd, string? input, CancellationToken ct) => throw new InvalidOperationException();
        public async Task<ProcessResult> RunInteractiveAsync(string e, IReadOnlyList<string> a, string cwd, Func<IProcessSession, CancellationToken, Task> exchange, CancellationToken ct) { await exchange(new Session(this, wrongThread), ct); return new(0, "fixture", ""); }
        private sealed class Session(ProtocolFixture owner, bool wrong) : IProcessSession {
            private readonly Channel<JsonElement> frames = Channel.CreateUnbounded<JsonElement>();
            public CancellationToken ProcessExited => default;
            public async Task<JsonElement?> ReadAsync(CancellationToken ct) => await frames.Reader.ReadAsync(ct);
            private void Frame(object frame) => frames.Writer.TryWrite(Args(frame));
            public Task SendAsync(object value, CancellationToken ct) {
                var frame = Args(value); var method = frame.TryGetProperty("method", out var m) ? m.GetString() : null;
                if (method == "initialize") { Assert.True(frame.GetProperty("params").GetProperty("capabilities").GetProperty("experimentalApi").GetBoolean()); Frame(new { id = 1, result = new { } }); }
                if (method == "thread/start") { owner.Definitions = frame.GetProperty("params").GetProperty("dynamicTools").GetArrayLength(); Frame(new { id = 2, result = new { thread = new { id = "thread" } } }); }
                if (method == "turn/start") {
                    Frame(new { id = 3, result = new { turn = new { id = "turn" } } });
                    Frame(new { id = 90, method = "item/tool/call", @params = new { threadId = wrong ? "foreign" : "thread", turnId = "turn", callId = "call", tool = "devflow_read_file", arguments = new { path = "source.txt" } } });
                }
                if (method is null && frame.TryGetProperty("id", out var id) && id.GetInt32() == 90) {
                    Assert.True(frame.GetProperty("result").GetProperty("success").GetBoolean()); owner.ToolOutput = frame.GetProperty("result").GetProperty("contentItems")[0].GetProperty("text").GetString();
                    Frame(new { method = "item/completed", @params = new { threadId = "thread", turnId = "turn", item = new { type = "agentMessage", text = "{\"plan\":\"Update the feature and run the configured validation.\"}" } } });
                    Frame(new { method = "turn/completed", @params = new { threadId = "thread", turn = new { status = "completed" } } });
                }
                return Task.CompletedTask;
            }
        }
    }
}
