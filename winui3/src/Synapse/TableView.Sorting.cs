namespace Synapse;

/// <summary>
/// Specification section 9. Sorting is a local table projection over the current source snapshot:
/// it produces the private view and never touches <see cref="ItemsSource"/>.
/// </summary>
public sealed partial class TableView
{
    /// <summary>
    /// The default settling interval. It is chosen, not derived: no system metric and no theme
    /// resource answers how often a list should reorder. It is long enough that a row does not move
    /// out from under a pointer reaching for it, and short enough that nobody concludes the sort is
    /// broken. A host that knows its own data should say so with
    /// <see cref="SortSettleInterval"/> rather than inherit this.
    /// </summary>
    private static readonly TimeSpan DefaultSortSettleInterval = TimeSpan.FromSeconds(3);

    private ResolvedColumn? _sortColumn;
    private TableSortDirection _sortDirection;
    private TimeSpan _sortSettleInterval = DefaultSortSettleInterval;

    /// <summary>When the rows were last allowed to take their sorted places.</summary>
    private DateTimeOffset _orderSettledAt = DateTimeOffset.MinValue;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _settleDue;

    /// <summary>
    /// Section 16: whether a row's place on screen is its place in the host's row order, so that
    /// the boundary a drop names is a place in that order. True unsorted, where the view is the
    /// source order, and under a sort by the column that <see cref="TableColumn.DefinesRowOrder"/>;
    /// under any other sort a boundary is a place in that sort, and the table withholds the drag.
    /// </summary>
    private bool ShowsRowOrder => _sortColumn is null || _sortColumn.Column.DefinesRowOrder;

    /// <summary>The row-order column sorted downward: the view runs opposite to the row order.</summary>
    private bool RowOrderIsReversed =>
        _sortColumn is not null
        && _sortColumn.Column.DefinesRowOrder
        && _sortDirection == TableSortDirection.Descending;

    /// <summary>
    /// How long rows already on screen keep their places while a sort is applied over values the
    /// source keeps changing. <see cref="TimeSpan.Zero"/> re-sorts on every update.
    /// </summary>
    /// <remarks>
    /// Only relative position waits. Membership never does: a row that arrives appears at once, at
    /// the place the sort gives it, and a row that leaves goes at once. The distinction is the
    /// reason this lives in the table rather than in the host — a host publishes one snapshot
    /// carrying membership and position together, so throttling it would delay the arrival that
    /// made it necessary, while the table holds both the old order and the new one and can take one
    /// without the other. Measured on a 2,002-row torrent list, per host publish, because with
    /// settling off every publish reorders and the two units are then the same one: sorted by a
    /// value the updates never touch, no reorder at all across sixteen real publishes; sorted by
    /// speed, which they always touch, every publish reordered the whole view, for news that
    /// amounted to one torrent finishing. That count came from the torrent host's own publish
    /// stream, and the harness that produced it has been deleted, so specification 9 records the
    /// figure and nothing here reproduces it; what survives is the claim, one reorder per interval
    /// rather than one per publish. A table that reshuffles a whole screen because a single
    /// row completed cannot be clicked on, which is why this exists, rather than the cost.
    /// <para>
    /// Live. Shortening it takes effect on the next update; the sorted order is taken immediately
    /// when it is set to <see cref="TimeSpan.Zero"/>. Negative values are treated as zero.
    /// </para>
    /// </remarks>
    public TimeSpan SortSettleInterval
    {
        get => _sortSettleInterval;
        set
        {
            value = value > TimeSpan.Zero ? value : TimeSpan.Zero;
            if (value == _sortSettleInterval)
            {
                return;
            }

            _sortSettleInterval = value;

            // Turning settling off is a request to see the sorted order, not a request to see it
            // whenever the source next moves.
            if (value == TimeSpan.Zero && _schemaCaptured && _sortColumn is not null)
            {
                _orderSettledAt = DateTimeOffset.MinValue;
                RebuildView();
            }
        }
    }

    /// <summary>
    /// The active sort, or null for natural order. Reading gives what stands; assigning is an
    /// idempotent request, and it reports through <see cref="LayoutChanged"/> only when the
    /// effective sort actually moved, so a host may set it from its own handler.
    /// </summary>
    /// <remarks>
    /// The column must be one this table declared and must carry a sort key from
    /// <see cref="Schema{TRow}"/>; either way round, a request that cannot be met is the host
    /// asking for something impossible rather than compatibility input, so it throws. A saved sort
    /// arriving from storage is the other case and belongs in <see cref="Layout"/>, which recovers
    /// defensively.
    /// </remarks>
    public TableSort? Sort
    {
        get => _sortColumn is null ? null : new TableSort(_sortColumn.Column, _sortDirection);
        set
        {
            ResolvedColumn? column = value is TableSort sort ? RequireSortable(sort.Column) : null;
            TableSortDirection direction = value?.Direction ?? TableSortDirection.Ascending;

            if (ReferenceEquals(column, _sortColumn)
                && (column is null || direction == _sortDirection))
            {
                return;
            }

            _sortColumn = column;
            _sortDirection = direction;

            // The host asked for this order, so it is taken now rather than at the next cadence.
            _orderSettledAt = DateTimeOffset.MinValue;
            RebuildView();

            // Sorting changes no geometry, so nothing else republishes the header cells.
            _headerStrip?.Panel?.RefreshHeaderCells();

            RaiseLayoutChanged(TableLayoutChangeKind.Sort);
        }
    }

    private ResolvedColumn RequireSortable(TableColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (Geometry.Find(column) is not ResolvedColumn resolved)
        {
            throw new ArgumentException(
                _schemaCaptured
                    ? "That column is not one of this table's columns."
                    : "The column schema is captured at the first Loaded, so no column resolves yet.",
                "value");
        }

        if (!column.CanSort)
        {
            throw new ArgumentException(
                $"Column '{column.DisplayName}' has no sort key. Give it one with " +
                "Schema<TRow>().Sort(column, row => …).",
                "value");
        }

        return resolved;
    }

    /// <summary>The direction this column is sorted in, or null when it is not the sorted column.</summary>
    internal TableSortDirection? SortDirectionOf(TableColumn column) =>
        _sortColumn is not null && ReferenceEquals(_sortColumn.Column, column)
            ? _sortDirection
            : null;

    /// <summary>
    /// Section 9's cycle on header activation: unsorted becomes ascending, ascending becomes
    /// descending, and descending returns to natural order. A non-sortable header has no action.
    /// </summary>
    internal void ActivateSort(ResolvedColumn column)
    {
        if (!column.Column.CanSort)
        {
            return;
        }

        // Through the property, so the header and a host request apply a sort by one path.
        Sort = !ReferenceEquals(_sortColumn, column)
            ? new TableSort(column.Column)
            : _sortDirection == TableSortDirection.Ascending
                ? new TableSort(column.Column, TableSortDirection.Descending)
                : null;
    }

    /// <summary>
    /// Section 5: the captured baseline carries no sort criterion, so <see cref="ResetColumnLayout"/>
    /// clears the local sort with the rest of the overrides. Silent: the reset reports once.
    /// </summary>
    /// <returns>True when a sort was in force and the view now needs rebuilding.</returns>
    private bool ClearSort()
    {
        if (_sortColumn is null)
        {
            return false;
        }

        _sortColumn = null;
        _sortDirection = TableSortDirection.Ascending;
        _orderSettledAt = DateTimeOffset.MinValue;
        return true;
    }

    /// <summary>
    /// The order the rows are actually shown in: the sorted snapshot, except that rows already on
    /// screen are left where they are between reorders.
    /// </summary>
    /// <remarks>
    /// A sort over a value the source keeps changing would otherwise re-order the whole table every
    /// time the source publishes. Measured on the torrent host, per publish, because with settling
    /// off every publish reorders and the two units are then the same one: sorted by name, which no
    /// update touches, sixteen real publishes drew no reorder at all; sorted by speed, which every
    /// update touches, every publish reordered the view, for an update that reported one torrent
    /// finishing. The cost is the smaller half of it. That count came from the same deleted
    /// torrent-host harness specification 9 records, so the sixteen cannot be reproduced here
    /// either; what survives is the claim it was taken for.
    /// A table that reshuffles a whole screen because one row completed cannot be clicked on: the
    /// row being reached for moves out from under the pointer. The owner chose a cadence for that
    /// reason, not for the milliseconds.
    /// <para>
    /// Membership is never delayed, only position. A row that arrives appears at once, at the place
    /// the sort gives it among the rows already shown, and a row that leaves goes at once. What
    /// waits is existing rows trading places.
    /// </para>
    /// </remarks>
    private IReadOnlyList<object> ViewOrder()
    {
        // Natural order belongs to the host, which reorders when it means to; an empty view has no
        // established order to preserve; and a zero interval is the host asking for none of this.
        if (_sortColumn is null || _view.Count == 0 || _sortSettleInterval == TimeSpan.Zero)
        {
            _orderSettledAt = DateTimeOffset.UtcNow;
            return SortedSnapshot();
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _orderSettledAt >= _sortSettleInterval)
        {
            _orderSettledAt = now;
            return SortedSnapshot();
        }

        // The base sequence, not the sorted one: a held update keeps the order the view already
        // has, so all it needs from the snapshot is which rows are in it, and sorting to answer a
        // question about membership is work thrown away. At the default interval against a host
        // that publishes about once a second, that is two publishes in three, and a name sort of
        // 2,002 rows is roughly 22,000 culture-aware string comparisons.
        //
        // A membership change is never held back, so it is also never settled: HeldOrder returns
        // null for one, and the sorted order is what the arriving row needs anyway.
        if (HeldOrder(_source.Snapshot) is not List<object> held)
        {
            _orderSettledAt = now;
            return SortedSnapshot();
        }

        ScheduleSettle(_sortSettleInterval - (now - _orderSettledAt));
        return held;
    }

    /// <summary>
    /// Take the sorted order at the end of the current interval, whether or not the source moves
    /// again in the meantime. Without this a table whose source went quiet would hold the order it
    /// was last left in.
    /// </summary>
    private void ScheduleSettle(TimeSpan due)
    {
        if (_settleDue is null)
        {
            _settleDue = DispatcherQueue.CreateTimer();
            _settleDue.IsRepeating = false;

            // Stopping the timer does not recall a tick the queue has already picked up, and
            // closing a window does not always unload its content first. Either way a tick can
            // arrive after the template parts' XAML core has gone, and reading anything off them
            // then fails: the owner saw it as a COMException out of get_SelectionMode on exit.
            // There is nothing to settle for a table that is no longer in a tree.
            _settleDue.Tick += (_, _) =>
            {
                if (_detached)
                {
                    return;
                }

                RebuildView();
            };
        }

        if (_settleDue.IsRunning)
        {
            return;
        }

        _settleDue.Interval = due > TimeSpan.Zero ? due : TimeSpan.FromMilliseconds(1);
        _settleDue.Start();
    }

    /// <summary>
    /// The same rows the view already holds, in the order it already holds them, or null when the
    /// snapshot is not the same set of rows.
    /// </summary>
    /// <remarks>
    /// Settling holds position and never membership, so a snapshot that adds or removes a row is
    /// not a settling case at all: the caller takes the sorted order for it, which puts the arrival
    /// where it belongs straight away. Only a snapshot of exactly the same rows can be held, and
    /// holding it is then a copy rather than a merge.
    /// <para>
    /// This is also what keeps the cost bounded. An earlier version placed each arrival into the
    /// held order by scanning it, which is fine for a torrent or two and is not fine for a filter
    /// change: switching the torrent host from downloading to all brings about 900 rows back at
    /// once, and scanning for each of them is roughly 1.4 million comparer calls on an order that
    /// is not sorted anyway, so their positions would have been close to arbitrary. Deferring
    /// nothing about membership removes the merge, the cost and the arbitrary placement together.
    /// </para>
    /// </remarks>
    private List<object>? HeldOrder(IReadOnlyList<object> snapshot)
    {
        if (snapshot.Count != _view.Count)
        {
            return null;
        }

        // Always the snapshot's instances, never the view's: the host may have replaced a row with
        // an equal-identity instance, and keeping the old one would leave its container bound to an
        // object nothing updates any more.
        Dictionary<object, object> live = new(snapshot.Count, _identity);
        foreach (object row in snapshot)
        {
            live[row] = row;
        }

        List<object> order = new(snapshot.Count);
        foreach (object shown in _view)
        {
            if (!live.TryGetValue(shown, out object? current))
            {
                return null;
            }

            order.Add(current);
        }

        return order;
    }

    /// <summary>
    /// The current source snapshot in sorted order. Natural order is the snapshot itself. A sort is
    /// stable, so equal values keep the exact base-sequence order they arrived in — descending
    /// included, because only the comparison is reversed and never the tie-break.
    /// </summary>
    private IReadOnlyList<object> SortedSnapshot()
    {
        IReadOnlyList<object> snapshot = _source.Snapshot;
        if (_sortColumn is null)
        {
            return snapshot;
        }

        // A column becomes the sorted one only by carrying a comparer, so this cannot be null.
        IComparer<object> comparer = _sortColumn.Column.Comparer!;

        return _sortDirection == TableSortDirection.Ascending
            ? snapshot.OrderBy(item => item, comparer).ToList()
            : snapshot.OrderByDescending(item => item, comparer).ToList();
    }

    /// <summary>
    /// Section 18's defensive sort restoration. The saved sort applies only when its column is
    /// currently sortable, visible once the saved visibility has been applied, and its direction
    /// is one of the two valid values; anything else, a missing ID included, is natural order. A
    /// hidden column is refused for section 9's reason: the header is the only place a sort shows
    /// or is changed, so a sort by a hidden column is one the user could neither see nor undo.
    /// </summary>
    /// <returns>True when the effective sort is not the one that was already in force.</returns>
    private bool RestoreSort(TableLayout state, Dictionary<string, ResolvedColumn> byId)
    {
        ResolvedColumn? previousColumn = _sortColumn;
        TableSortDirection previousDirection = _sortDirection;

        _sortColumn = null;
        _sortDirection = TableSortDirection.Ascending;

        if (state.SortColumnId is string id
            && byId.TryGetValue(id, out ResolvedColumn? column)
            && column.Column.CanSort
            && column.IsVisible
            && Enum.IsDefined(state.SortDirection))
        {
            _sortColumn = column;
            _sortDirection = state.SortDirection;
        }

        bool moved = !ReferenceEquals(previousColumn, _sortColumn)
            || (_sortColumn is not null && previousDirection != _sortDirection);

        if (moved)
        {
            // A restored sort is a new order the host asked for, not a drift of the old one.
            _orderSettledAt = DateTimeOffset.MinValue;
        }

        return moved;
    }
}
