using System.Runtime.InteropServices;

namespace Syno.TableView;

internal static class DragDistance
{
    private const int SM_CXDRAG = 68;
    private const int SM_CYDRAG = 69;

    // Querying at 96 DPI gives the shared distances in DIPs.
    internal static double Horizontal { get; } = GetSystemMetricsForDpi(SM_CXDRAG, 96);
    internal static double Vertical { get; } = GetSystemMetricsForDpi(SM_CYDRAG, 96);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
