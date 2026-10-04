using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Syno.TableView;

/// <summary>
/// Payload of <see cref="Table.ItemInvoked"/>. The selection is the one that stands after the
/// invoking input was processed.
/// </summary>
public sealed class ItemInvokedEventArgs : EventArgs
{
    public ItemInvokedEventArgs(object item, IReadOnlyList<object> selectedItems)
    {
        Item = item;
        SelectedItems = selectedItems;
    }

    public object Item { get; }

    public IReadOnlyList<object> SelectedItems { get; }
}

/// <summary>
/// Payload of <see cref="Table.ItemContextRequested"/>. The host builds and shows its own menu
/// at <see cref="Target"/> while the handler runs; the target is presentation context for
/// that moment, not state to keep.
/// </summary>
public sealed class ItemContextRequestedEventArgs : EventArgs
{
    public ItemContextRequestedEventArgs(
        object item,
        IReadOnlyList<object> selectedItems,
        FrameworkElement target,
        Point? position)
    {
        Item = item;
        SelectedItems = selectedItems;
        Target = target;
        Position = position;
    }

    public object Item { get; }

    public IReadOnlyList<object> SelectedItems { get; }

    /// <summary>The realized row the request came from.</summary>
    public FrameworkElement Target { get; }

    /// <summary>Relative to <see cref="Target"/>; null for a keyboard invocation.</summary>
    public Point? Position { get; }
}

/// <summary>
/// Payload of <see cref="Table.ReorderRequested"/>. A request, not a transaction: the table
/// has changed no order, and it reads nothing back from the handler.
/// </summary>
public sealed class ReorderRequestedEventArgs : EventArgs
{
    public ReorderRequestedEventArgs(IReadOnlyList<object> items, object? before)
    {
        Items = items;
        Before = before;
    }

    /// <summary>
    /// The rows to move, in row order: the current visual order, or its reverse under the column
    /// that <see cref="Column.DefinesRowOrder"/> sorted downward.
    /// </summary>
    public IReadOnlyList<object> Items { get; }

    /// <summary>
    /// The row the packet goes immediately before, in row order, once the packet itself has been
    /// taken out, or null to append. It is never one of <see cref="Items"/>.
    /// </summary>
    public object? Before { get; }
}
