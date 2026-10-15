using DevFlow.Core;
using System.Diagnostics;
using System.Text;
namespace DevFlow.Infrastructure;
public sealed class ProcessRunner : IProcessRunner
{
    public const string TruncationMarker = "\n[Output truncated at 4 MiB.]";
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        string? standardInput, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        var output = ReadBoundedAsync(process.StandardOutput);
        var error = ReadBoundedAsync(process.StandardError);
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
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        const int limit = 4 * 1024 * 1024;
        var text = new StringBuilder(); var buffer = new char[4096]; bool truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            var retained = Math.Min(count, limit - text.Length);
            text.Append(buffer, 0, retained);
            if (retained < count) truncated = true;
        }
        if (truncated) text.Append(TruncationMarker);
        return text.ToString();
    }
}
