using System.Runtime.InteropServices;
using DevFlow.Infrastructure;
using Xunit;

namespace DevFlow.Tests;

public sealed class WindowsFolderPickerTests
{
    [Fact]
    public async Task Windows_shell_dialog_accepts_folder_options_and_managed_event_sink_without_showing_a_window()
    {
        if (!OperatingSystem.IsWindows()) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                var dialog = (WindowsFolderPicker.IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), true)!)!;
                try
                {
                    dialog.SetOptions(0x02000868);
                    dialog.GetOptions(out var options);
                    Assert.Equal(0x02000868u, options & 0x02000868u);
                    dialog.SetTitle("DevFlow folder picker verification");
                    dialog.SetOkButtonLabel("Select folder");
                    dialog.Advise(new WindowsFolderPicker.DialogEvents(CancellationToken.None), out var cookie);
                    dialog.Unadvise(cookie);
                }
                finally { Marshal.ReleaseComObject(dialog); }
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
