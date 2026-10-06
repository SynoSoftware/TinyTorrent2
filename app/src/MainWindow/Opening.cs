using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private const uint SEE_MASK_CLASSNAME = 0x00000001;
    private const int SW_SHOWNORMAL = 1;

    private void Open(OpenRequestedEventArgs args)
    {
        try
        {
            var open = new ShellExecuteInfo
            {
                cbSize = (uint)Marshal.SizeOf<ShellExecuteInfo>(),
                fMask = string.IsNullOrEmpty(args.Extension) ? 0 : SEE_MASK_CLASSNAME,
                hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this),
                lpFile = args.Path,
                lpClass = args.Extension,
                nShow = SW_SHOWNORMAL
            };
            if (!ShellExecuteExW(ref open)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception error) { Model.Report(error); }
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
}
