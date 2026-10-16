using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DevFlow.Core;

namespace DevFlow.Infrastructure;

public interface IFolderPicker
{
    Task<string?> PickAsync(string? initialPath, CancellationToken ct);
}

public sealed class WindowsFolderPicker : IFolderPicker
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<string?> PickAsync(string? initialPath, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
            throw new ConflictException("Windows 選擇器需要 API 在目前登入的 Windows 桌面執行；也可使用頁面內瀏覽。");
        if (!await gate.WaitAsync(0, ct)) throw new ConflictException("Windows 資料夾選擇器已開啟，請先完成或取消選擇。");
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var owner = GetForegroundWindow();
        var thread = new Thread(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                result.TrySetResult(Show(initialPath, owner, timeout.Token));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { result.TrySetCanceled(ct); }
            catch (OperationCanceledException) { result.TrySetException(new ConflictException("Windows 選擇器已逾時並關閉，請重新選擇。")); }
            catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
            { result.TrySetException(new ConflictException("無法開啟 Windows 資料夾選擇器，請確認 API 在目前桌面執行，或改用頁面內瀏覽。")); }
            catch (Exception ex) { result.TrySetException(ex); }
            finally { gate.Release(); }
        }) { IsBackground = true, Name = "DevFlow Windows folder picker" };
        try { thread.SetApartmentState(ApartmentState.STA); thread.Start(); }
        catch { gate.Release(); throw; }
        return await result.Task;
    }

    [SupportedOSPlatform("windows")]
    private static string? Show(string? initialPath, IntPtr owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dialog = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), true)!)!;
        uint cookie = 0;
        var events = new DialogEvents(ct);
        try
        {
            // PICKFOLDERS | FORCEFILESYSTEM | PATHMUSTEXIST | NOCHANGEDIR | DONTADDTORECENT.
            dialog.SetOptions(0x02000868);
            dialog.SetTitle("選擇專案資料夾 · DevFlow");
            dialog.SetOkButtonLabel("選擇此資料夾");
            if (!string.IsNullOrWhiteSpace(initialPath) && Path.IsPathFullyQualified(initialPath) && Directory.Exists(initialPath))
            {
                var iid = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(Path.GetFullPath(initialPath), IntPtr.Zero, ref iid, out var folder) >= 0)
                {
                    try { dialog.SetFolder(folder); }
                    finally { Marshal.ReleaseComObject(folder); }
                }
            }
            dialog.Advise(events, out cookie);
            using var cancelled = ct.Register(events.Cancel);
            ct.ThrowIfCancellationRequested();
            var status = dialog.Show(owner);
            events.Closed();
            ct.ThrowIfCancellationRequested();
            if (status == unchecked((int)0x800704C7)) return null;
            Marshal.ThrowExceptionForHR(status);
            dialog.GetResult(out var selected);
            try
            {
                selected.GetDisplayName(0x80058000, out var pointer); // SIGDN_FILESYSPATH
                try { return Path.GetFullPath(Marshal.PtrToStringUni(pointer)!); }
                finally { Marshal.FreeCoTaskMem(pointer); }
            }
            finally { Marshal.ReleaseComObject(selected); }
        }
        finally
        {
            events.Closed();
            if (cookie != 0) dialog.Unadvise(cookie);
            Marshal.ReleaseComObject(dialog);
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class DialogEvents(CancellationToken ct) : IFileDialogEvents
    {
        private readonly object sync = new();
        private IntPtr window;
        public void Cancel() { lock (sync) { if (window != IntPtr.Zero) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero); } }
        public void Closed() { lock (sync) window = IntPtr.Zero; }
        public int OnFolderChange(IFileDialog dialog)
        {
            ((IOleWindow)dialog).GetWindow(out var handle);
            lock (sync) window = handle;
            if (ct.IsCancellationRequested) Cancel();
            else { BringWindowToTop(handle); SetForegroundWindow(handle); }
            return 0;
        }
        public int OnFileOk(IFileDialog dialog) => 0;
        public int OnFolderChanging(IFileDialog dialog, IShellItem folder) => 0;
        public int OnSelectionChange(IFileDialog dialog) => 0;
        public int OnShareViolation(IFileDialog dialog, IShellItem item, out uint response) { response = 0; return 0; }
        public int OnTypeChange(IFileDialog dialog) => 0;
        public int OnOverwrite(IFileDialog dialog, IShellItem item, out uint response) { response = 0; return 0; }
    }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, IntPtr specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IFileDialogEvents events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int status);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow
    {
        void GetWindow(out IntPtr window);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
    }
    [ComVisible(true), Guid("973510DB-7D7F-452B-8975-74A85828D354"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileDialogEvents
    {
        [PreserveSig] int OnFileOk(IFileDialog dialog);
        [PreserveSig] int OnFolderChanging(IFileDialog dialog, IShellItem folder);
        [PreserveSig] int OnFolderChange(IFileDialog dialog);
        [PreserveSig] int OnSelectionChange(IFileDialog dialog);
        [PreserveSig] int OnShareViolation(IFileDialog dialog, IShellItem item, out uint response);
        [PreserveSig] int OnTypeChange(IFileDialog dialog);
        [PreserveSig] int OnOverwrite(IFileDialog dialog, IShellItem item, out uint response);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
