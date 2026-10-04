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
public sealed class Schema<TRow> where TRow : class
{
    private readonly Table _table;

    internal Schema(Table table) => _table = table;

    /// <summary>
    /// A stable, non-null, unique key per row, used to reconcile selection, current item, anchor
    /// and focus across a source change. Without one, identity is object reference.
    /// </summary>
    public Schema<TRow> Key<TKey>(Func<TRow, TKey> key) where TKey : notnull
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
    /// Restricts row dragging without restricting selection or other actions.
    /// A selected packet containing a refused row cannot be dragged. After a
    /// predicate input changes, call <see cref="Table.RefreshView"/>.
    /// </summary>
    public Schema<TRow> CanReorder(Func<TRow, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _table.RequireSetup();
        _table.CanReorderItem = item => predicate((TRow)item);
        return this;
    }

    /// <summary>
    /// Make this column sortable, by the key this returns for a row.
    /// </summary>
    public Schema<TRow> SortKey<TKey>(Column column, Func<TRow, TKey> key,
        IComparer<TKey>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(key);
        _table.RequireSetup();
        column.Comparer = new KeyOrder<TKey>(key, comparer ?? Comparer<TKey>.Default);
        return this;
    }

    private sealed class KeyOrder<TKey> : IComparer<object>
    {
        private readonly Func<TRow, TKey> _key;

        private readonly IComparer<TKey> _comparer;

        internal KeyOrder(Func<TRow, TKey> key, IComparer<TKey> comparer)
        {
            _key = key;
            _comparer = comparer;
        }

        public int Compare(object? x, object? y) =>
            _comparer.Compare(_key((TRow)x!), _key((TRow)y!));
    }

    private sealed class KeyEquality<TKey> : IEqualityComparer<object> where TKey : notnull
    {
        public new bool Equals(object? x, object? y) =>
            EqualityComparer<TKey>.Default.Equals((TKey)x!, (TKey)y!);

        public int GetHashCode(object value) => EqualityComparer<TKey>.Default.GetHashCode((TKey)value);
    }
}
