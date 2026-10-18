using DevFlow.Core;
namespace DevFlow.Runners;

// The API and Worker use the same control directory. Open handles also protect
// against a second API process updating a CLI while another process is using it.
public sealed class CliMaintenanceGate(string directory)
{
    public IDisposable EnterExecution() => Open(FileShare.ReadWrite);
    public IDisposable EnterUpdate() => Open(FileShare.None);
    private FileStream Open(FileShare sharing)
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, "cli-maintenance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, sharing); }
        catch (IOException) { throw new ConflictException("CLI 正在執行或更新，請等待作業結束後再試。"); }
    }
}
