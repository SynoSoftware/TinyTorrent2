namespace Syno.TableView;

/// <summary>
/// A selected packet and the current row, as one value. Read from
/// <see cref="Table.Selection"/>, and written back to it to ask for a different one.
/// </summary>
/// <remarks>
/// The two travel together because they are decided together: an omitted current item resolves to
/// the first selected row, and pruning a removed row can move it. Held apart, a host could set one
/// and leave the other describing a state that no longer exists.
/// </remarks>
public sealed class Selection
{
    /// <summary>Nothing selected and no current row.</summary>
    public static readonly Selection Empty = new(Array.Empty<object>());

    /// <summary>
    /// A null or omitted <paramref name="current"/> asks for the first selected row in visual
    /// order. A supplied one may be unselected.
    /// </summary>
    public Selection(IEnumerable<object> items, object? current = null)
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
