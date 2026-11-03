using System.Runtime.InteropServices;

namespace Syno.TableView;

/// <summary>
/// The Windows drag distance, which the person can raise for steadier clicks. Movement below it is
/// a click, not a drag, a marquee or a column move. Read once, when the table library first uses it.
/// </summary>
internal static class DragDistance
{
    private const int SM_CXDRAG = 68;
    private const int SM_CYDRAG = 69;

    // Querying at 96 DPI gives the distances in DIPs.
    internal static double Horizontal { get; } = GetSystemMetricsForDpi(SM_CXDRAG, 96);
    internal static double Vertical { get; } = GetSystemMetricsForDpi(SM_CYDRAG, 96);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
