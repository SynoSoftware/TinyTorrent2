using System.Runtime.InteropServices;

namespace Syno.TinyTorrent.Torrents;

public sealed partial class PeerMap
{
    private nint _windowHandle;
    private SubclassProc? _procedure;

    private void WatchSettings()
    {
        _windowHandle = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        _procedure ??= OnMessage;
        if (!SetWindowSubclass(_windowHandle, _procedure, 0, 0))
            throw new InvalidOperationException();
    }

    private void StopSettings()
    {
        if (_procedure is not null)
            RemoveWindowSubclass(_windowHandle, _procedure, 0);
        _procedure = null;
    }

    private nint OnMessage(nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint reference)
    {
        // WM_SETTINGCHANGE covers animation and contrast preferences on the
        // app's Windows 10 1809 minimum, before AnimationsEnabledChanged exists.
        if (message == 0x001A)
            DispatcherQueue.TryEnqueue(() =>
            {
                if (IsLoaded)
                {
                    Invalidate();
                    DotBytesChanged?.Invoke(this, EventArgs.Empty);
                }
            });
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint reference);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc procedure, nuint subclassId, nuint reference);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc procedure, nuint subclassId);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
}
