using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Graphics;

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

    /// <summary>
    /// A reader of the Windows cursor in DIPs of <c>Drawing</c>, for the game
    /// loop to follow a mouse drag; null when the window's position is unknown.
    /// </summary>
    /// <remarks>
    /// Built on the UI thread; the reader runs on the game loop's thread. It
    /// returns null once the primary button is up and stays null, because a
    /// new press is a new drag that the UI thread starts.
    /// </remarks>
    private Func<Vector2?>? Cursor()
    {
        var corner = new PointInt32();
        if (!ClientToScreen(_windowHandle, ref corner))
        {
            return null;
        }
        var scale = (float)XamlRoot.RasterizationScale;
        var offset = Drawing.TransformToVisual(null).TransformPoint(default);
        var origin = new Vector2(corner.X, corner.Y) + new Vector2((float)offset.X, (float)offset.Y) * scale;
        var button = GetSystemMetrics(SwapButton) != 0 ? RightButton : LeftButton;
        var released = false;
        return () =>
        {
            if (released || (GetAsyncKeyState(button) & 0x8000) == 0)
            {
                released = true;
                return null;
            }
            if (!GetCursorPos(out var cursor))
            {
                return null;
            }
            return (new Vector2(cursor.X, cursor.Y) - origin) / scale;
        };
    }

    private const int SwapButton = 23;
    private const int LeftButton = 0x01;
    private const int RightButton = 0x02;

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref PointInt32 point);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointInt32 point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);

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
