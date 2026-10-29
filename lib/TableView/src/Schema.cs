namespace Syno.TableView;

/// <summary>
/// The typed half of the setup-only schema: the row type, stated once, carrying the identity
/// selector, the interaction predicate, and every column's sort key. The declarative half is
/// <see cref="Table.Columns"/>.
/// </summary>
/// <remarks>
/// WinUI 3 XAML cannot instantiate an open generic, so <see cref="Table"/> stays non-generic —
/// but a non-generic class can have a generic method, and XAML never sees one. That is the whole
/// mechanism: the host names its row type here, and the casts back to it happen inside this class
/// rather than once per selector and comparer at the host.
/// <para>
/// A column joins to its sort key through the field the XAML compiler already generates for
/// <c>x:Name</c>, so renaming a column is a build break rather than a dictionary lookup that
/// silently stops matching.
/// </para>
/// </remarks>
public sealed class Schema<TRow>
    where TRow : class
{
    private readonly Table _table;

    internal Schema(Table table) => _table = table;

    /// <summary>
    /// Setup-only. Keep this column first and visible, with child rows and expansion owned by
    /// the host. Children must be finite with globally unique row identities. After changing
    /// expansion in the host, call <see cref="Table.RefreshView"/> once for the batch.
    /// </summary>
    /// <remarks>
    /// <paramref name="hasUnrealizedChildren"/> marks a row whose children the host has not
    /// supplied yet. The row shows a collapsed expander and can be expanded. The table calls
    /// <paramref name="setExpanded"/> only for the person's request, and never loads children
    /// itself: the host starts its load from that call and later supplies the children through
    /// <paramref name="children"/>. Supplied children take precedence over the flag. Omit it
    /// when every row's children are supplied.
    /// </remarks>
    public Schema<TRow> Hierarchy(
        Column column,
        Func<TRow, IEnumerable<TRow>> children,
        Func<TRow, bool> isExpanded,
        Action<TRow, bool> setExpanded,
        Func<TRow, bool>? hasUnrealizedChildren = null
    )
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(isExpanded);
        ArgumentNullException.ThrowIfNull(setExpanded);
        _table.RequireSetup();
        _table.SetHierarchy(
            new Hierarchy(
                column,
                item => children((TRow)item),
                item => isExpanded((TRow)item),
                (item, value) => setExpanded((TRow)item, value),
                hasUnrealizedChildren is null ? null : item => hasUnrealizedChildren((TRow)item)
            )
        );
        return this;
    }

    /// <summary>
    /// A stable, non-null, unique key per row, used to reconcile selection, current item, anchor
    /// and focus across a source change. Without one, identity is object reference.
    /// </summary>
    public Schema<TRow> Key<TKey>(Func<TRow, TKey> key)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        _table.RequireSetup();
        _table.ItemKey = item => key((TRow)item);
        _table.KeyComparer = new KeyEquality<TKey>();
        return this;
    }

    /// <summary>
    /// Which rows the user may act on. A row this refuses still renders, and nothing else: it
    /// cannot be selected, invoked, context-clicked, or joined to a reorder packet, and keyboard
    /// navigation steps over it.
    /// </summary>
    public Schema<TRow> CanInteract(Func<TRow, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _table.RequireSetup();
        _table.CanInteract = item => predicate((TRow)item);
        return this;
    }

    /// <summary>
    /// Defines the numbered order edited by the drag wheel, independently of the view's sort.
    /// Positions must be unique; null excludes an item from moves, not from selection or dragging.
    /// Items supplies the complete destination set, including any rows the host's filter hides.
    /// Each item's ToString supplies its label beside the position.
    /// Call RefreshView after a batch changes membership or positions. Column sorting is separate.
    /// </summary>
    public Schema<TRow> Order(
        Column column,
        Func<TRow, int?> position,
        Func<IEnumerable<TRow>> items
    )
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(items);
        _table.RequireSetup();
        _table.SetOrder(new Order(column, item => position((TRow)item), () => items()));
        return this;
    }

    /// <summary>
    /// Make this column sortable, by the key this returns for a row.
    /// </summary>
    public Schema<TRow> SortKey<TKey>(
        Column column,
        Func<TRow, TKey> key,
        IComparer<TKey>? comparer = null
    )
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(key);
        _table.RequireSetup();
        column.Sort = (items, direction) =>
            direction == SortDirection.Ascending
                ? items.OrderBy(item => key((TRow)item), comparer)
                : items.OrderByDescending(item => key((TRow)item), comparer);
        return this;
    }

    private sealed class KeyEquality<TKey> : IEqualityComparer<object>
        where TKey : notnull
    {
        public new bool Equals(object? x, object? y) =>
            EqualityComparer<TKey>.Default.Equals((TKey)x!, (TKey)y!);

        public int GetHashCode(object value) =>
            EqualityComparer<TKey>.Default.GetHashCode((TKey)value);
    }
}
