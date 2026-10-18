using DevFlow.Core;
using DevFlow.Runners;
using Xunit;
namespace DevFlow.Tests;

public sealed class CliMaintenanceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-maintenance-" + Guid.NewGuid());
    private readonly FakeProcess process = new();
    private readonly CliRegistry registry;
    private readonly CliMaintenanceGate gate;
    private readonly CliMaintenance service;
    private bool busy;
    public CliMaintenanceTests()
    {
        var global = Path.Combine(root, "global", "node_modules");
        var package = Path.Combine(global, "@openai", "codex");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@openai/codex\"}");
        var exe = Path.Combine(package, "codex.exe"); File.WriteAllText(exe, "");
        var node = Path.Combine(root, "node.exe"); File.WriteAllText(node, "");
        var npm = Path.Combine(root, "npm-cli.js"); File.WriteAllText(npm, "");
        registry = new(codex: exe, node: node);
        gate = new(Path.Combine(root, "control"));
        service = new(process, registry, gate, _ => Task.FromResult(busy), Path.Combine(root, "control"), node, npm, global);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
    private sealed class FakeProcess : IProcessRunner
    {
        public string Version = "0.155.1";
        public int InstallExit;
        public bool ApplyVersion = true;
        public string Latest = "0.159.2";
        public bool CancelInstall;
        public string InstallError = "sensitive provider output must not reach browser";
        public TaskCompletionSource? Block;
        public readonly List<string[]> Calls = [];
        public async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string cwd, string? input, CancellationToken ct)
        {
            lock (Calls) Calls.Add(args.ToArray());
            if (args.Contains("--version")) return new(0, "codex-cli " + Version, "");
            if (args.Contains("view")) return new(0, System.Text.Json.JsonSerializer.Serialize(Latest), "");
            if (args.Contains("install"))
            {
                if (CancelInstall) throw new OperationCanceledException();
                if (Block is not null) await Block.Task.WaitAsync(ct);
                if (InstallExit == 0 && ApplyVersion) Version = "0.159.2";
                return new(InstallExit, "", InstallError);
            }
            throw new InvalidOperationException("Unexpected process call");
        }
    }
    private async Task<CliUpdateSession> Finished(Guid id)
    {
        for (var i = 0; i < 200; i++)
        {
            var session = service.Get(id);
            if (session.FinishedAt is not null) return session;
            await Task.Delay(10);
        }
        throw new TimeoutException("Fake update did not complete");
    }
    [Fact] public async Task Checking_is_readonly_and_update_installs_the_checked_exact_package_version()
    {
        var check = await service.CheckAsync("codex");
        Assert.Equal("0.155.1", check.InstalledVersion); Assert.Equal("0.159.2", check.LatestVersion);
        Assert.True(check.CanUpdate); Assert.DoesNotContain(process.Calls, a => a.Contains("install"));
        var session = await service.StartAsync("codex", check.Id);
        var done = await Finished(session.Id);
        Assert.Equal("Completed", done.State); Assert.Equal("0.159.2", done.InstalledVersion);
        var install = Assert.Single(process.Calls, a => a.Contains("install"));
        Assert.Contains("@openai/codex@0.159.2", install); Assert.DoesNotContain("@latest", string.Join(" ", install));
        Assert.DoesNotContain("sensitive", done.Message);
        using var resumed = gate.EnterExecution();
    }
    [Fact] public async Task Busy_tasks_or_login_block_installation_before_any_mutation()
    {
        var check = await service.CheckAsync("codex"); busy = true;
        await Assert.ThrowsAsync<ConflictException>(() => service.StartAsync("codex", check.Id));
        Assert.DoesNotContain(process.Calls, a => a.Contains("install"));
    }
    [Fact] public async Task Shared_execution_in_another_gate_instance_blocks_update()
    {
        var check = await service.CheckAsync("codex");
        using var execution = new CliMaintenanceGate(Path.Combine(root, "control")).EnterExecution();
        await Assert.ThrowsAsync<ConflictException>(() => service.StartAsync("codex", check.Id));
        Assert.DoesNotContain(process.Calls, a => a.Contains("install"));
    }
    [Fact] public async Task Update_blocks_new_execution_and_duplicate_updates_until_terminal_state()
    {
        var check = await service.CheckAsync("codex"); process.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = await service.StartAsync("codex", check.Id);
        try
        {
            Assert.Throws<ConflictException>(() => gate.EnterExecution());
            await Assert.ThrowsAsync<ConflictException>(() => service.StartAsync("codex", check.Id));
            Assert.Equal("Updating", service.Get(session.Id).State);
        }
        finally { process.Block.SetResult(); await Finished(session.Id); }
        using var resumed = gate.EnterExecution();
    }
    [Fact] public async Task Changed_version_requires_a_new_explicit_check()
    {
        var check = await service.CheckAsync("codex"); process.Version = "0.156.0";
        await Assert.ThrowsAsync<ConflictException>(() => service.StartAsync("codex", check.Id));
        Assert.DoesNotContain(process.Calls, a => a.Contains("install"));
    }
    [Fact] public async Task Interrupted_update_releases_maintenance_and_requires_manual_recheck()
    {
        var check = await service.CheckAsync("codex"); process.CancelInstall = true;
        var session = await service.StartAsync("codex", check.Id);
        Assert.Equal("TimedOut", (await Finished(session.Id)).State);
        Assert.Single(process.Calls, a => a.Contains("install"));
        using var execution = gate.EnterExecution();
    }
    [Theory]
    [InlineData("0.155.1")]
    [InlineData("0.154.0")]
    [InlineData("0.159.2 --unsafe")]
    public async Task Same_older_or_malformed_registry_version_cannot_trigger_install(string latest)
    {
        process.Latest = latest;
        var check = await service.CheckAsync("codex"); Assert.False(check.CanUpdate);
        await Assert.ThrowsAsync<ConflictException>(() => service.StartAsync("codex", check.Id));
        Assert.DoesNotContain(process.Calls, a => a.Contains("install"));
    }
    [Fact] public async Task Failed_npm_install_releases_gate_without_exposing_output_or_retrying()
    {
        process.InstallExit = 1;
        var check = await service.CheckAsync("codex");
        var done = await Finished((await service.StartAsync("codex", check.Id)).Id);
        Assert.Equal("Failed", done.State); Assert.DoesNotContain("sensitive", done.Message);
        Assert.Single(process.Calls, a => a.Contains("install"));
        using var resumed = gate.EnterExecution();
    }
    [Fact] public async Task Successful_installer_with_wrong_runtime_version_is_not_success()
    {
        process.ApplyVersion = false;
        var check = await service.CheckAsync("codex");
        Assert.Equal("Failed", (await Finished((await service.StartAsync("codex", check.Id)).Id)).State);
    }
    [Theory]
    [InlineData("EPERM sensitive", "檔案占用或權限")]
    [InlineData("ENOTFOUND sensitive", "網路")]
    [InlineData("EBADENGINE sensitive", "Node.js")]
    public async Task Install_failure_explains_the_cause_without_returning_raw_npm_output(string error, string explanation)
    {
        process.InstallExit = 1; process.InstallError = error;
        var check = await service.CheckAsync("codex");
        var done = await Finished((await service.StartAsync("codex", check.Id)).Id);
        Assert.Contains(explanation, done.Message); Assert.DoesNotContain("sensitive", done.Message);
    }
    [Fact] public async Task Non_npm_install_and_mismatched_provider_cannot_be_updated()
    {
        var check = await service.CheckAsync("codex");
        await Assert.ThrowsAsync<ConflictException>(() => service.StartAsync("gemini", check.Id));
        var outside = Path.Combine(root, "standalone.exe"); File.WriteAllText(outside, "");
        using var standalone = new CliMaintenance(process, new(codex: outside), gate, _ => Task.FromResult(false), Path.Combine(root, "control"));
        Assert.False((await standalone.CheckAsync("codex")).CanUpdate);
    }
}

