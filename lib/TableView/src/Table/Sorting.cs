using System.Diagnostics;

namespace Syno.TableView;

/// <summary>
/// Specification section 9. Header sort state and the optional local projection over the source.
/// </summary>
public sealed partial class Table
{
    /// <summary>
    /// The default settling interval. It is chosen, not derived: no system metric and no theme
    /// resource answers how often a list should reorder. It is long enough that a row does not move
    /// out from under a pointer reaching for it, and short enough that nobody concludes the sort is
    /// broken. A host that knows its own data should say so with
    /// <see cref="SortInterval"/> rather than inherit this.
    /// </summary>
    private static readonly TimeSpan DefaultSortInterval = TimeSpan.FromSeconds(3);

    private EffectiveColumn? _sortColumn;
    private SortDirection _sortDirection;
    private Sort? _pendingSort;
    private bool _hasPendingSort;
    private TimeSpan _sortInterval = DefaultSortInterval;
    private SortMode _sortMode;

    /// <summary>
    /// The <see cref="Stopwatch"/> timestamp of the frame that last showed a settle that changed
    /// the rows, or null when no interval runs. A monotonic clock, so a change to the system clock
    /// cannot stretch or skip the interval.
    /// </summary>
    private long? _orderSettledAt;

    /// <summary>A settle changed the rows and no frame has shown it yet.</summary>
    private bool _settleShowing;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _settleDue;

    private bool SortsOrder => _sortColumn is not null && _sortColumn.Column == _order?.Column;
    private bool SortsLocally => _sortMode == SortMode.Local && _sortColumn is not null;
    private bool Reorders => SortsLocally && !SortsOrder;

    /// <summary>The rows take their sorted places, and their changes apply, once per interval.</summary>
    private bool UsesInterval => Reorders && _sortInterval > TimeSpan.Zero;

    /// <summary>
    /// Setup-only. External sorting keeps source order and reports header choices through
    /// <see cref="LayoutChanged"/>; the host supplies replacement rows in the requested order.
    /// Sort keys still declare which columns have sorting gestures, but are not evaluated.
    /// </summary>
    public SortMode SortMode
    {
        get => _sortMode;
        set
        {
            RequireSetup();
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            _sortMode = value;
        }
    }

    /// <summary>
    /// How often a sort over values the source keeps changing applies the source's changes to the
    /// rows on screen. <see cref="TimeSpan.Zero"/> applies and re-sorts on every update. A sort by
    /// the column registered with Schema.Order never waits.
    /// </summary>
    /// <remarks>
    /// A change applies at once when no change applied within the last interval. Otherwise it
    /// waits, with every later one, for the settle at the end of the interval, where arrivals,
    /// leavers and moves land in one update. This lives in the table rather than in the
    /// host because the table knows which changes may wait: the person's own, a different list,
    /// and the row under the pointer, which keeps its place at the settle. Measured on the original
    /// host's 2,002-row list, per host publish, because with settling off every publish reorders
    /// and the two units are then the same one: sorted by a value the updates never touch, no
    /// reorder at all across sixteen real publishes; sorted by speed, which they always touch,
    /// every publish reordered the whole view, for news that amounted to one row finishing. That
    /// count came from the original host's own publish stream, and the harness that produced it
    /// has been deleted, so specification 9 records the figure and nothing here reproduces it;
    /// what survives is the claim, one reorder per interval rather than one per publish. A table
    /// that reshuffles a whole screen because a single row completed cannot be clicked on, which
    /// is why this exists, rather than the cost.
    /// <para>
    /// Live. Shortening it takes effect on the next update; the sorted order is taken immediately
    /// when it is set to <see cref="TimeSpan.Zero"/>. Negative values are treated as zero.
    /// </para>
    /// </remarks>
    public TimeSpan SortInterval
    {
        get => _sortInterval;
        set
        {
            value = value > TimeSpan.Zero ? value : TimeSpan.Zero;
            if (value == _sortInterval)
            {
                return;
            }

            _sortInterval = value;
            if (_preparingView)
                _viewVersion++;

            // Turning settling off is a request to see the sorted order, not a request to see it
            // whenever the source next moves.
            if (value == TimeSpan.Zero && _schemaCaptured && SortsLocally)
            {
                _orderSettledAt = null;
                RebuildView();
            }
        }
    }

    /// <summary>
    /// The active sort, or null for natural order. In external mode this is the requested order.
    /// Reading gives what stands; assigning is an idempotent request, and it reports through
    /// <see cref="LayoutChanged"/> only when the
    /// effective sort actually moved, so a host may set it from its own handler.
    /// </summary>
    /// <remarks>
    /// The column must be visible, declared by this table, and carry a sort key from
    /// <see cref="Schema{TRow}"/>; either way round, a request that cannot be met is the host
    /// asking for something impossible rather than compatibility input, so it throws. A saved sort
    /// arriving from storage is the other case and belongs in <see cref="Layout"/>, which recovers
    /// defensively.
    /// </remarks>
    public Sort? Sort
    {
        get
        {
            if (_schemaCaptured)
            {
                return _sortColumn is null ? null : new Sort(_sortColumn.Column, _sortDirection);
            }
            if (_hasPendingSort)
            {
                return _pendingSort;
            }
            return PendingSort();
        }
        set
        {
            if (!_schemaCaptured)
            {
                _pendingSort = value;
                _hasPendingSort = true;
                return;
            }
            if (value is Sort requested && !Enum.IsDefined(requested.Direction))
                throw new ArgumentOutOfRangeException(nameof(value));
            EffectiveColumn? column = value is Sort sort ? RequireSortable(sort.Column) : null;
            SortDirection direction = value?.Direction ?? SortDirection.Ascending;

            if (
                ReferenceEquals(column, _sortColumn)
                && (column is null || direction == _sortDirection)
            )
            {
                return;
            }

            var keys = CaptureSort(column);
            _sortColumn = column;
            _sortDirection = direction;

            // Complete presentation before selection handlers can run or throw.
            _headerStrip?.Panel?.RefreshHeader();
            RebuildView(sortChanged: true, preparedSort: keys);

            RaiseLayoutChanged(LayoutChange.Sort);
        }
    }

    /// <summary>
    /// Reads the requested column's sort keys on the UI thread, so a request whose selector
    /// fails throws here and leaves the table unchanged. Later projections read them on the
    /// worker.
    /// </summary>
    private IComparer<object>? CaptureSort(EffectiveColumn? column)
    {
        if (_sortMode == SortMode.External || column is null)
            return null;
        IReadOnlyCollection<object> rows = _hierarchy is null ? _source.Snapshot : CaptureHierarchy(_source.Snapshot).Nodes.Keys;
        return column.Column.CaptureSort!(rows);
    }

    private Sort? PendingSort()
    {
        if (_pendingLayout?.SortColumnId is not string id)
            return null;
        Column? column = Columns.FirstOrDefault(candidate => candidate.Id == id);
        return column is not null && Enum.IsDefined(_pendingLayout.SortDirection)
            ? new Sort(column, _pendingLayout.SortDirection)
            : null;
    }

    private EffectiveColumn RequireSortable(Column column)
    {
        EffectiveColumn resolved = RequireColumn(column, "value");

        if (!column.CanSort)
        {
            throw new ArgumentException(
                $"Column '{column.DisplayName}' has no sort key. Give it one with "
                    + "Schema<TRow>().SortKey(column, row => …).",
                "value"
            );
        }

        if (!resolved.IsVisible)
            throw new ArgumentException($"Column '{column.DisplayName}' is hidden.", "value");

        return resolved;
    }

    /// <summary>The direction this column is sorted in, or null when it is not the sorted column.</summary>
    internal SortDirection? SortDirectionOf(Column column) =>
        _sortColumn is not null && ReferenceEquals(_sortColumn.Column, column)
            ? _sortDirection
            : null;

    /// <summary>
    /// Section 9's cycle on header activation: unsorted becomes ascending, ascending becomes
    /// descending, and descending returns to natural order. A non-sortable header has no action.
    /// </summary>
    internal void ActivateSort(Column column) => ActivateSort(RequireSortable(column));

    internal void ActivateSort(EffectiveColumn column)
    {
        if (!column.Column.CanSort)
        {
            return;
        }

        // Through the property, so the header and a host request apply a sort by one path.
        Sort =
            !ReferenceEquals(_sortColumn, column) ? new Sort(column.Column)
            : _sortDirection == SortDirection.Ascending
                ? new Sort(column.Column, SortDirection.Descending)
            : null;
    }

    /// <summary>
    /// Section 5: the captured baseline carries no sort criterion, so <see cref="ResetLayout"/>
    /// clears the sort criterion with the rest of the overrides. Silent: the reset reports once.
    /// </summary>
    /// <returns>True when a sort was in force and the view now needs rebuilding.</returns>
    private bool ClearSort()
    {
        if (_sortColumn is null)
        {
            return false;
        }

        _sortColumn = null;
        _sortDirection = SortDirection.Ascending;
        _orderSettledAt = null;
        return true;
    }


    /// <summary>
    /// A settle changed the rows. The interval starts when the frame that shows the change
    /// renders, and until then every publish settles too, so the list lays out all of them as one
    /// change and animates it.
    /// </summary>
    private void ShowSettle()
    {
        if (!UsesInterval || _settleShowing)
        {
            return;
        }

        _settleShowing = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnSettleShown;
    }

    private void OnSettleShown(object? sender, object e)
    {
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnSettleShown;
        _settleShowing = false;
        _orderSettledAt = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Stop the waiting settle and the wait for the frame that shows one, so neither rebuilds a
    /// view that is gone nor holds this table alive through the static rendering event.
    /// </summary>
    private void StopSettling()
    {
        _settleDue?.Stop();
        if (_settleShowing)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnSettleShown;
            _settleShowing = false;
        }
    }

    /// <summary>
    /// Take the sorted order at the end of the current interval, whether or not the source moves
    /// again in the meantime. Without this a table whose source went quiet would hold the order it
    /// was last left in.
    /// </summary>
    private void ScheduleSettle(TimeSpan due)
    {
        if (_detached)
            return;
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

                // Only a reordering sort schedules a settle. A tick left over from one after the
                // sort changed has nothing to settle.
                if (!UsesInterval)
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

}
