namespace Synapse;

/// <summary>
/// The typed half of the setup-only schema: the row type, stated once, carrying the identity
/// selector, the interaction predicate, and every column's sort key. The declarative half is
/// <see cref="TableView.Columns"/>.
/// </summary>
/// <remarks>
/// WinUI 3 XAML cannot instantiate an open generic, so <see cref="TableView"/> stays non-generic —
/// but a non-generic class can have a generic method, and XAML never sees one. That is the whole
/// mechanism: the host names its row type here, and the casts back to it happen inside this class
/// rather than once per selector and comparer at the host.
/// <para>
/// A column joins to its sort key through the field the XAML compiler already generates for
/// <c>x:Name</c>, so renaming a column is a build break rather than a dictionary lookup that
/// silently stops matching.
/// </para>
/// </remarks>
public sealed class TableSchema<TRow>
{
    private readonly TableView _table;

    internal TableSchema(TableView table) => _table = table;

    /// <summary>
    /// A stable, non-empty, unique key per row, used to reconcile selection, current item, anchor
    /// and focus across a source change. Without one, identity is object reference.
    /// </summary>
    public TableSchema<TRow> Key(Func<TRow, string> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _table.ItemKey = item => key((TRow)item);
        return this;
    }

    /// <summary>
    /// Which rows the user may act on. A row this refuses still renders, and nothing else: it
    /// cannot be selected, invoked, context-clicked, or joined to a reorder packet, and keyboard
    /// navigation steps over it.
    /// </summary>
    public TableSchema<TRow> CanInteract(Func<TRow, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _table.CanInteract = item => predicate((TRow)item);
        return this;
    }

    /// <summary>
    /// Make this column sortable, by the key this returns for a row.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="TKey"/> must order itself, which is what makes an unorderable key a
    /// compile error rather than a sort that quietly does nothing. It also refuses a nullable value
    /// type — <c>DateTimeOffset?</c> does not implement <c>IComparable&lt;DateTimeOffset?&gt;</c> —
    /// so the host says where its nulls sort instead of the table deciding invisibly.
    /// </remarks>
    public TableSchema<TRow> Sort<TKey>(TableColumn column, Func<TRow, TKey> key)
        where TKey : IComparable<TKey>
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(key);
        column.Comparer = new KeyOrder<TKey>(key);
        return this;
    }

    /// <summary>
    /// The library's one comparer adapter, over the row type this schema named.
    /// </summary>
    /// <remarks>
    /// The cast is hard on purpose. Both hosts wrote their own adapter and both returned 0 for a
    /// row of the wrong type, which is a sort that silently does nothing over a source the host
    /// believes is sorted. A wrong row type is a configuration error and now says so.
    /// <para>
    /// <see cref="Comparer{T}.Default"/> rather than <c>CompareTo</c> so that a null key — which a
    /// reference-typed key still allows — orders first instead of throwing.
    /// </para>
    /// </remarks>
    private sealed class KeyOrder<TKey> : IComparer<object>
        where TKey : IComparable<TKey>
    {
        private readonly Func<TRow, TKey> _key;

        internal KeyOrder(Func<TRow, TKey> key) => _key = key;

        public int Compare(object? x, object? y) =>
            Comparer<TKey>.Default.Compare(_key((TRow)x!), _key((TRow)y!));
    }
}
