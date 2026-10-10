using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private const uint SEE_MASK_CLASSNAME = 0x00000001;
    private const uint SEE_MASK_NOASYNC = 0x00000100;
    private const int SW_SHOWNORMAL = 1;

    private async void Open(OpenRequestedEventArgs args)
    {
        if (await OpenPath(args.Path, args.Extension) is { } failure)
            Model.Report(failure);
    }

    private Task<Exception?> OpenPath(string path, string? extension = null)
    {
        var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var apartment = CoInitializeEx(0, 0x6);
            try
            {
                Marshal.ThrowExceptionForHR(apartment);
                var open = new ShellExecuteInfo
                {
                    cbSize = (uint)Marshal.SizeOf<ShellExecuteInfo>(),
                    // This STA has no message loop; finish the shell conversation before it exits.
                    fMask = SEE_MASK_NOASYNC | (string.IsNullOrEmpty(extension) ? 0 : SEE_MASK_CLASSNAME),
                    hwnd = window,
                    lpFile = path,
                    lpClass = extension,
                    nShow = SW_SHOWNORMAL,
                };
                if (!ShellExecuteExW(ref open))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                completion.SetResult(null);
            }
            catch (Exception error) { completion.SetResult(error); }
            finally
            {
                if (apartment >= 0)
                    CoUninitialize();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public uint cbSize;
        public uint fMask;
        public nint hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        public string? lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIcon;
        public nint hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo open);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
