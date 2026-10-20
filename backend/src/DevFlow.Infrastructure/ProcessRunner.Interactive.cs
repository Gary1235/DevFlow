using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevFlow.Core;

namespace DevFlow.Infrastructure;

public sealed partial class ProcessRunner
{
    public async Task<ProcessResult> RunInteractiveAsync(string executable, IReadOnlyList<string> arguments, string cwd,
        Func<IProcessSession, CancellationToken, Task> exchange, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("CLI process did not start.");
        var id = Guid.NewGuid(); var activity = ProcessOutput.ActivityObserver;
        var semantic = new ToolActivityParser(summary => activity?.Invoke(new(id, executable, "Work", Summary: summary)));
        activity?.Invoke(new(id, executable, "Started"));
        var error = ReadBoundedAsync(process.StandardError, onRead: n => activity?.Invoke(new(id, executable, "Output", n)));
        var session = new JsonSession(process.StandardInput, process.StandardOutput, line => {
            activity?.Invoke(new(id, executable, "Output", line.Length)); semantic.Append(line + "\n"); ProcessOutput.Observer?.Invoke(line + "\n");
        });
        try
        {
            await exchange(session, ct);
            process.StandardInput.Close();
            var closedAfterResult = false;
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(ct); closedAfterResult = true; }
            return new(closedAfterResult ? 0 : process.ExitCode, session.Output, await error);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await error.WaitAsync(TimeSpan.FromSeconds(5)); throw;
        }
        finally { semantic.Finish(); activity?.Invoke(new(id, executable, "Finished", ExitCode: process.HasExited ? process.ExitCode : null)); }
    }
    private sealed class JsonSession(StreamWriter input, StreamReader output, Action<string> observe) : IProcessSession
    {
        private readonly StringBuilder retained = new();
        private readonly char[] buffer = new char[4096];
        private int position, available;
        public string Output => retained.ToString();
        public async Task SendAsync(object message, CancellationToken ct)
        { await input.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct); await input.FlushAsync(ct); }
        public async Task<JsonElement?> ReadAsync(CancellationToken ct)
        {
            // Bound each frame before parsing; continue handling control frames after the artifact cap.
            var line = new StringBuilder();
            while (true)
            {
                if (position == available) { available = await output.ReadAsync(buffer.AsMemory(), ct); position = 0; }
                if (available == 0) { if (line.Length == 0) return null; break; }
                var character = buffer[position++];
                if (character == '\n') break;
                line.Append(character);
                if (line.Length > 4 * 1024 * 1024) throw new InvalidOperationException("CLI JSON frame exceeds 4 MiB.");
            }
            var text = line.ToString(); observe(text);
            if (retained.Length < 4 * 1024 * 1024) retained.AppendLine(text[..Math.Min(text.Length, 4 * 1024 * 1024 - retained.Length)]);
            using var json = JsonDocument.Parse(text); return json.RootElement.Clone();
        }
    }
}
