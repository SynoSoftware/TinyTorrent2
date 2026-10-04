using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Synapse;

/// <summary>
/// A selected packet and the current row, as one value. Read from
/// <see cref="TableView.Selection"/>, and written back to it to ask for a different one.
/// </summary>
/// <remarks>
/// The two travel together because they are decided together: an omitted current item resolves to
/// the first selected row, and pruning a removed row can move it. Held apart, a host could set one
/// and leave the other describing a state that no longer exists.
/// </remarks>
public sealed class TableSelection
{
    /// <summary>Nothing selected and no current row.</summary>
    public static readonly TableSelection Empty = new(Array.Empty<object>());

    /// <summary>
    /// A null or omitted <paramref name="current"/> asks for the first selected row in visual
    /// order. A supplied one may be unselected.
    /// </summary>
    public TableSelection(IEnumerable<object> items, object? current = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items.ToList();
        Current = current;
    }

    /// <summary>The selected packet, in current visual row order when the table produced it.</summary>
    public IReadOnlyList<object> Items { get; }

    /// <summary>The logical current row. It may be selected or unselected.</summary>
    public object? Current { get; }
}

/// <summary>
/// Payload of <see cref="TableView.SelectionStateChanged"/>. An immutable post-mechanics snapshot.
/// </summary>
public sealed class TableSelectionStateChangedEventArgs : EventArgs
{
    public TableSelectionStateChangedEventArgs(TableSelection selection) => Selection = selection;

    public TableSelection Selection { get; }
}

/// <summary>
/// Payload of <see cref="TableView.ItemInvoked"/>. The selection is the one that stands after the
/// invoking input was processed.
/// </summary>
public sealed class TableItemInvokedEventArgs : EventArgs
{
    public TableItemInvokedEventArgs(object item, IReadOnlyList<object> selectedItems)
    {
        Item = item;
        SelectedItems = selectedItems;
    }

    public object Item { get; }

    public IReadOnlyList<object> SelectedItems { get; }
}

/// <summary>
/// Payload of <see cref="TableView.RowContextRequested"/>. The host builds and shows its own menu
/// at <see cref="PlacementTarget"/> while the handler runs; the target is presentation context for
/// that moment, not state to keep.
/// </summary>
public sealed class TableRowContextRequestedEventArgs : EventArgs
{
    public TableRowContextRequestedEventArgs(
        object item,
        IReadOnlyList<object> selectedItems,
        FrameworkElement placementTarget,
        Point? relativePoint)
    {
        Item = item;
        SelectedItems = selectedItems;
        PlacementTarget = placementTarget;
        RelativePoint = relativePoint;
    }

    public object Item { get; }

    public IReadOnlyList<object> SelectedItems { get; }

    /// <summary>The realized row the request came from.</summary>
    public FrameworkElement PlacementTarget { get; }

    /// <summary>Relative to <see cref="PlacementTarget"/>; null for a keyboard invocation.</summary>
    public Point? RelativePoint { get; }
}
