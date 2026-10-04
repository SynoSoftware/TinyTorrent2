using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Synapse;

/// <summary>
/// Section 16's insertion boundary: the view index a drop asks for, and the marker drawn on it. It
/// reports a position only; what may move there is the table's, because only the table knows the
/// packet and the view.
/// </summary>
internal sealed class TableRowDrag
{
    /// <summary>Moves the marker without a layout pass, as section 11's column marker does.</summary>
    private readonly TranslateTransform _markerOffset = new();

    private ListView? _rows;
    private FrameworkElement? _marker;

    internal void Begin(ListView rows, FrameworkElement? marker)
    {
        _rows = rows;
        _marker = marker;

        if (_marker is not null)
        {
            _marker.RenderTransform = _markerOffset;
        }
    }

    /// <summary>
    /// Follow the pointer, and report the boundary it asks for: the view index the packet would be
    /// inserted before, with the view's count meaning after the last row. Negative when no realized
    /// row can place it.
    /// </summary>
    internal int Track(double y)
    {
        int boundary = BoundaryAt(y);
        MoveMarker(boundary);
        return boundary;
    }

    internal void End()
    {
        if (_marker is not null)
        {
            _marker.Visibility = Visibility.Collapsed;
        }

        _marker = null;
        _rows = null;
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>
    /// The nearer edge of the row the pointer is over, and past either end of the rows, that end.
    /// </summary>
    /// <remarks>
    /// The panel's cache range, not its <c>Children</c>: a container left over from before a scroll
    /// stays in <c>Children</c> and still reports the row it last showed, at the bounds that row
    /// used to have.
    /// </remarks>
    private int BoundaryAt(double y)
    {
        if (_rows?.ItemsPanelRoot is not ItemsStackPanel panel)
        {
            return -1;
        }

        int first = panel.FirstCacheIndex;
        int last = panel.LastCacheIndex;
        if (first < 0 || last < first)
        {
            return -1;
        }

        // The cache reaches about two viewports past each edge, and a row drag has no auto-scroll,
        // so a pointer dragged off the control would otherwise place the packet at a row the user
        // has never seen while the marker showed the edge.
        y = Math.Clamp(y, 0, _rows.ActualHeight);

        for (int index = first; index <= last; index++)
        {
            if (_rows.ContainerFromIndex(index) is not FrameworkElement container)
            {
                continue;
            }

            Rect bounds = BoundsInRows(container);
            if (y < bounds.Top + (bounds.Height / 2))
            {
                return index;
            }
        }

        return last + 1;
    }

    private double EdgeOf(int boundary)
    {
        if (_rows!.ContainerFromIndex(boundary) is FrameworkElement next)
        {
            return BoundsInRows(next).Top;
        }

        return _rows.ContainerFromIndex(boundary - 1) is FrameworkElement previous
            ? BoundsInRows(previous).Bottom
            : 0;
    }

    private Rect BoundsInRows(FrameworkElement container) => container
        .TransformToVisual(_rows)
        .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

    // ------------------------------------------------------------------ marker

    private void MoveMarker(int boundary)
    {
        if (_marker is null || _rows is null || boundary < 0)
        {
            return;
        }

        // Centred on the boundary, and held inside the rows so neither end shows half a marker.
        double centred = EdgeOf(boundary) - (_marker.Height / 2);
        _markerOffset.Y = Math.Clamp(centred, 0, Math.Max(0, _rows.ActualHeight - _marker.Height));
        _marker.Visibility = Visibility.Visible;
    }
}

/// <summary>
/// Payload of <see cref="TableView.RowsReorderRequested"/>. A request, not a transaction: the table
/// has changed no order, and it reads nothing back from the handler.
/// </summary>
public sealed class TableRowsReorderRequestedEventArgs : EventArgs
{
    public TableRowsReorderRequestedEventArgs(
        IReadOnlyList<object> movingItems, object? insertBeforeItem)
    {
        MovingItems = movingItems;
        InsertBeforeItem = insertBeforeItem;
    }

    /// <summary>
    /// The rows to move, in row order: the current visual order, or its reverse under the column
    /// that <see cref="TableColumn.DefinesRowOrder"/> sorted downward.
    /// </summary>
    public IReadOnlyList<object> MovingItems { get; }

    /// <summary>
    /// The row the packet goes immediately before, in row order, once the packet itself has been
    /// taken out, or null to append. It is never one of <see cref="MovingItems"/>.
    /// </summary>
    public object? InsertBeforeItem { get; }
}
