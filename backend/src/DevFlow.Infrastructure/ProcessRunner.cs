using DevFlow.Core;
using System.Diagnostics;
using System.Text;
namespace DevFlow.Infrastructure;
public sealed partial class ProcessRunner : IProcessRunner, IInteractiveProcessRunner
{
    public const string TruncationMarker = "\n[Output truncated at 4 MiB.]";
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        string? standardInput, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        var activity = ProcessOutput.ActivityObserver;
        var processId = Guid.NewGuid();
        var streamedAgent = arguments.Contains("exec") && arguments.Contains("--json") ||
            arguments.Contains("--output-format") && arguments.Contains("stream-json");
        var semantic = streamedAgent ? new ToolActivityParser(summary => activity?.Invoke(new(processId, executable, "Work", Summary: summary))) : null;
        activity?.Invoke(new(processId, executable, "Started"));
        var observer = ProcessOutput.Observer;
        var output = ReadBoundedAsync(process.StandardOutput, chunk => { semantic?.Append(chunk); observer?.Invoke(chunk); }, count => activity?.Invoke(new(processId, executable, "Output", count)));
        var error = ReadBoundedAsync(process.StandardError, onRead: count => activity?.Invoke(new(processId, executable, "Output", count)));
        try
        {
            if (standardInput is not null) await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            return new(process.ExitCode, await output, await error);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
            throw;
        }
        finally { semantic?.Finish(); activity?.Invoke(new(processId, executable, "Finished", ExitCode: process.HasExited ? process.ExitCode : null)); }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, Action<string>? observer = null, Action<int>? onRead = null)
    {
        const int limit = 4 * 1024 * 1024;
        var text = new StringBuilder(); var buffer = new char[4096]; bool truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            onRead?.Invoke(count);
            var retained = Math.Min(count, limit - text.Length);
            text.Append(buffer, 0, retained);
            if (retained > 0) observer?.Invoke(new string(buffer, 0, retained));
            if (retained < count) truncated = true;
        }
        if (truncated) text.Append(TruncationMarker);
        return text.ToString();
    }
}
