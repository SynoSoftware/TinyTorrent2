using System.Globalization;
using System.Runtime.InteropServices;

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
    private readonly List<object> _naturalRows = new();
    private SortedOrder? _sortedOrder;
    private QueuedSort? _queuedSort;
    private long _sortRevision;
    private bool _settlePending;

    private sealed record QueuedSort(Sort? Requested, Sort? Committed, long SortRevision, long LifetimeRevision)
    {
        internal SortWork? Work { get; set; }
    }

    private sealed record SortWork(SortedOrder Workspace, SortedOrder.Work Sorting,
        Hierarchy.Work? Hierarchy, long PublishVersion);

    /// <summary>When the rows were last allowed to take their sorted places.</summary>
    private DateTimeOffset _orderSettledAt = DateTimeOffset.MinValue;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _settleDue;

    private bool SortsOrder => _sortColumn is not null && _sortColumn.Column == _order?.Column;
    private bool SortsLocally => _sortMode == SortMode.Local && _sortColumn is not null;

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
    /// How long rows already on screen keep their places while a sort is applied over values the
    /// source keeps changing. <see cref="TimeSpan.Zero"/> re-sorts on every update. A sort by the
    /// column registered with Schema.Order never waits.
    /// </summary>
    /// <remarks>
    /// Only relative position waits for the interval. Membership does not: a row that arrives
    /// appears at once, at the place the sort gives it, and a row that leaves goes at once, unless
    /// the rows are pointed at, when section 5.3's hold keeps both back. The distinction is the
    /// reason this lives in the table rather than in the host — a host publishes one snapshot
    /// carrying membership and position together, so throttling it would delay the arrival that
    /// made it necessary, while the table holds both the old order and the new one and can take one
    /// without the other. Measured on the original host's 2,002-row list, per host publish, because with
    /// settling off every publish reorders and the two units are then the same one: sorted by a
    /// value the updates never touch, no reorder at all across sixteen real publishes; sorted by
    /// speed, which they always touch, every publish reordered the whole view, for news that
    /// amounted to one row finishing. That count came from the original host's own publish
    /// stream, and the harness that produced it has been deleted, so specification 9 records the
    /// figure and nothing here reproduces it; what survives is the claim, one reorder per interval
    /// rather than one per publish. A table that reshuffles a whole screen because a single
    /// row completed cannot be clicked on, which is why this exists, rather than the cost.
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

            // Turning settling off is a request to see the sorted order, not a request to see it
            // whenever the source next moves.
            if (value == TimeSpan.Zero && _schemaCaptured && SortsLocally)
            {
                _orderSettledAt = DateTimeOffset.MinValue;
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

            SetSort(column, direction);
        }
    }

    /// <summary>
    /// Commit a sort the host or the person asked for, show it, and report it.
    /// </summary>
    /// <param name="work">A queued first sort's detached work, which already ordered the keys.</param>
    private void SetSort(EffectiveColumn? column, SortDirection direction, SortWork? work = null)
    {
        var prepared = CommitSort(column, direction, prepare: true, work);

        // Complete presentation before selection handlers can run or throw.
        _headerStrip?.Panel?.RefreshHeader();
        if (prepared is { } sorted)
        {
            RebuildView(sorted: sorted.Order, prepared: sorted.Source);
        }

        RaiseLayoutChanged(LayoutChange.Sort);
    }

    private SortedOrder? CreateSort(EffectiveColumn? column) =>
        _sortMode != SortMode.Local || column is null ? null
            : ReferenceEquals(column, _sortColumn) && _sortedOrder is not null ? _sortedOrder
            : column.Column.Sort!.Create(_identity);

    /// <summary>
    /// The one commit of a sort criterion. With <paramref name="prepare"/>, a local sort first
    /// prepares the whole order the criterion gives; comparison can invoke host code, so it
    /// finishes before anything is committed, and a failure leaves the committed sort in force.
    /// </summary>
    /// <returns>The prepared order and source change for the caller to publish, or null when
    /// nothing was prepared.</returns>
    private (IReadOnlyList<object> Order, Body.Source.Preparation? Source)? CommitSort(
        EffectiveColumn? column, SortDirection direction, bool prepare, SortWork? work = null)
    {
        SortedOrder? workspace = work?.Workspace ?? CreateSort(column);
        Body.Source.Preparation? source = null;
        IReadOnlyList<object>? order = prepare && _sortMode == SortMode.Local
            ? PrepareSort(workspace, direction, out source, work) : null;
        if (order is not null && _hierarchy is null)
        {
            workspace?.Accept();
        }
        if (!ReferenceEquals(column, _sortColumn) || (column is not null && direction != _sortDirection))
        {
            _sortRevision++;
        }
        _sortColumn = column;
        _sortDirection = direction;
        _sortedOrder = workspace;
        _orderSettledAt = DateTimeOffset.MinValue;
        _settlePending = false;
        _settleDue?.Stop();
        // The kept sibling groups are ordered by the sort they were kept under.
        _hierarchy?.ForgetOrder();
        return order is null ? null : (order, source);
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
        PresentedSort is Sort sort && ReferenceEquals(sort.Column, column)
            ? sort.Direction
            : null;

    private Sort? PresentedSort => _queuedSort is { } queued && IsValid(queued) ? queued.Requested : Sort;

    private bool IsValid(QueuedSort queued) => !_detached
        && queued.SortRevision == _sortRevision && queued.LifetimeRevision == _lifetimeRevision && IsLoaded
        && queued.Committed == Sort
        && (queued.Requested is not Sort requested
            || EffectiveLayout.Order.Any(column => ReferenceEquals(column.Column, requested.Column) && column.IsVisible));

    private void ConsumeSort(QueuedSort queued)
    {
        if (!ReferenceEquals(_queuedSort, queued))
        {
            return;
        }
        try
        {
            if (!IsValid(queued))
            {
                _queuedSort = null;
                return;
            }
            if (queued.Work is null && StartWork(queued))
            {
                return;
            }
            if (queued.Work is { Sorting.Completion.IsCompleted: false })
            {
                return;
            }
            _queuedSort = null;
            if (queued.Work is { } work)
            {
                Sort requested = queued.Requested!.Value;
                SetSort(RequireSortable(requested.Column), requested.Direction, work);
            }
            else
            {
                Sort = queued.Requested;
            }
        }
        catch
        {
            if (ReferenceEquals(_queuedSort, queued))
            {
                _queuedSort = null;
            }
            throw;
        }
        finally
        {
            _headerStrip?.Panel?.RefreshHeader();
        }
    }

    private bool StartWork(QueuedSort queued)
    {
        if (_sortMode != SortMode.Local || queued.Requested is not Sort requested)
        {
            return false;
        }
        EffectiveColumn column = RequireSortable(requested.Column);
        SortedOrder workspace = column.Column.Sort!.Create(_identity);
        if (!workspace.CanWork)
        {
            return false;
        }
        queued.Work = CaptureSort(workspace, requested.Direction);
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher = DispatcherQueue;
        _ = queued.Work.Sorting.Completion.ContinueWith(completed =>
        {
            _ = completed.Exception;
            if (!dispatcher.TryEnqueue(() => ResumeSort(queued)))
            {
                Interlocked.CompareExchange(ref _queuedSort, null, queued);
            }
        }, TaskScheduler.Default);
        return true;
    }

    private void ResumeSort(QueuedSort queued)
    {
        if (!ReferenceEquals(_queuedSort, queued))
        {
            return;
        }
        if (!IsValid(queued))
        {
            _queuedSort = null;
            if (!_detached && IsLoaded)
            {
                _headerStrip?.Panel?.RefreshHeader();
            }
            return;
        }
        RequestFrame();
    }

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

        Sort? presented = PresentedSort;
        Sort? requested =
            !ReferenceEquals(presented?.Column, column.Column) ? new Sort(column.Column)
            : presented?.Direction == SortDirection.Ascending
                ? new Sort(column.Column, SortDirection.Descending)
            : null;
        _queuedSort = new(requested, Sort, _sortRevision, _lifetimeRevision);
        _headerStrip?.Panel?.RefreshHeader();
        RequestFrame();
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

        CommitSort(null, SortDirection.Ascending, prepare: false);
        return true;
    }

    /// <summary>
    /// The order the rows are actually shown in: the sorted snapshot, except that rows already on
    /// screen are left where they are between reorders, and that held rows and the pointed row keep
    /// their index.
    /// </summary>
    /// <remarks>
    /// A sort over a value the source keeps changing would otherwise re-order the whole table every
    /// time the source publishes. Measured on the original host, per publish, because with settling
    /// off every publish reorders and the two units are then the same one: sorted by name, which no
    /// update touches, sixteen real publishes drew no reorder at all; sorted by speed, which every
    /// update touches, every publish reordered the view, for an update that reported one row
    /// finishing. The cost is the smaller half of it. That count came from the same deleted
    /// original-host harness specification 9 records, so the sixteen cannot be reproduced here
    /// either; what survives is the claim it was taken for.
    /// A table that reshuffles a whole screen because one row completed cannot be clicked on: the
    /// row being reached for moves out from under the pointer. The owner chose a cadence for that
    /// reason, not for the milliseconds.
    /// <para>
    /// Membership waits only while the rows are pointed at, by the hold of section 5.3. Otherwise a
    /// row that arrives appears at once, at the place the sort gives it among the rows already
    /// shown, and a row that leaves goes at once. When the rows do take their sorted places, the
    /// pointed row keeps its own (section 9).
    /// </para>
    /// </remarks>
    /// <returns>
    /// The order; the rows that keep their index, whose containers the reconcile keeps; and the
    /// source change when the hold passed it on by its rows, otherwise null. Selection then
    /// reconciles only those rows: a held leaver is still shown and is pruned as not interactive.
    /// </returns>
    private (IReadOnlyList<object> Order, IdentitySet Pinned, Body.Source.Preparation? Affected,
        Body.View.Origin Origin) ViewOrder(
        Body.Source.Preparation source)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool allowMoves = MayMove(now);
        HoldStep hold = Hold(source);
        IReadOnlyList<object> shown;
        IReadOnlyList<object> order;
        IdentitySet pinned;
        Body.View.Origin origin;
        if (_sortedOrder is null && hold.Holds && hold.Shown.Shown is null)
        {
            (order, pinned, origin) = HeldOrder(hold, source);
            shown = order;
        }
        else
        {
            shown = _sortedOrder is null ? NaturalOrder(hold, source)
                : _sortedOrder.Prepare(hold.Shown, _sortDirection, allowMoves);
            (order, pinned) = Pin(shown, hold, source);
            origin = !ReferenceEquals(order, shown) ? default
                : _sortedOrder?.Origin ?? NaturalOrigin(order, source);
        }
        _sortedOrder?.Accept();
        AcceptHold(hold);
        Settle(now, allowMoves || _sortedOrder?.Resorted == true, _sortedOrder?.NeedsSettle == true || hold.Held.Count > 0
            || shown.Count != source.Capture.Rows.Count
            || !ReferenceEquals(order, shown) && !order.SequenceEqual(shown, ReferenceEqualityComparer.Instance));
        return (order, pinned, hold.Shown.Shown is null ? source : null, origin);
    }

    /// <summary>
    /// A hierarchy's order: each expanded parent's children sorted as one group. Inside the
    /// interval a group keeps the order it was shown in unless its own list changed.
    /// </summary>
    private (IReadOnlyList<object> Order, IdentitySet Pinned, Body.Source.Preparation? Affected,
        Body.View.Origin Origin) ViewOrder(
        Hierarchy.Preparation hierarchy)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool allowMoves = MayMove(now);
        IReadOnlyList<object> order = _hierarchy!.Project(hierarchy, SortsLocally ? _sortedOrder : null,
            _sortDirection, allowMoves && (hierarchy.IsWhole || _settlePending), allowMoves, out bool kept);
        Settle(now, allowMoves, kept && !allowMoves);
        return (order, _held, null, default);
    }

    /// <summary>
    /// The order a sort the person or host asked for gives. It moves every row, so nothing is held
    /// through it.
    /// </summary>
    private (IReadOnlyList<object> Order, IdentitySet Pinned, Body.Source.Preparation? Affected,
        Body.View.Origin Origin) SortedView(
        Body.Source.Preparation? source, IReadOnlyList<object> sorted)
    {
        IdentitySet held = _held;
        if (source is not null)
        {
            HoldStep hold = Hold(source, sortChanged: true);
            AcceptHold(hold);
            held = hold.Held;
        }
        Settle(DateTimeOffset.UtcNow, allowMoves: true, pending: false);
        return (sorted, held, null, default);
    }

    /// <summary>Whether rows may take their sorted places now, rather than at the next settle.</summary>
    private bool MayMove(DateTimeOffset now) => !SortsLocally || SortsOrder || _view.Count == 0
        || _sortInterval == TimeSpan.Zero || now - _orderSettledAt >= _sortInterval;

    /// <summary>
    /// The one settle decision. A publish that allowed moves starts the interval again; a publish
    /// that left rows off their sorted places, by a withheld key, a held or waiting row or a
    /// pinned row, schedules the settle at the end of the interval.
    /// </summary>
    private void Settle(DateTimeOffset now, bool allowMoves, bool pending)
    {
        if (allowMoves)
        {
            _orderSettledAt = now;
        }
        _settlePending = SortsLocally && !SortsOrder && pending;
        if (_settlePending)
        {
            ScheduleSettle(_sortInterval - (now - _orderSettledAt));
        }
        else
        {
            _settleDue?.Stop();
        }
    }

    /// <summary>
    /// The shown rows in source order, for a table without a local sort, unless rows are held:
    /// see <see cref="HeldOrder"/>.
    /// </summary>
    private static IReadOnlyList<object> NaturalOrder(HoldStep hold, Body.Source.Preparation source) =>
        hold.Shown.Shown ?? source.Capture.Rows;

    /// <summary>
    /// The natural order while the rows are pointed at and the change names its rows. Membership
    /// waits, so the order is the view as it stands with each row the source replaced, and each
    /// returning row in its held row's place, as new instances. It states those instances
    /// against the view itself, so the view does not walk, and copies the view only when an
    /// instance changes.
    /// </summary>
    private (IReadOnlyList<object> Order, IdentitySet Pinned, Body.View.Origin Origin) HeldOrder(
        HoldStep hold, Body.Source.Preparation source)
    {
        List<object>? order = null;
        List<(int, int)> replacing = new(source.Reread.Count + hold.Returning.Count);
        foreach (object row in source.Reread.Concat(hold.Returning))
        {
            int index = _view.Positions.IndexOf(row);
            if (index >= 0 && !ReferenceEquals(_view[index], row))
            {
                if (order is null)
                {
                    order = _naturalRows;
                    order.Clear();
                    order.AddRange(_view);
                }
                order[index] = row;
                replacing.Add((index, index));
            }
        }
        return (order ?? (IReadOnlyList<object>)_view, hold.Held, new(null, _view, new([], [], replacing)));
    }

    /// <summary>
    /// The natural order's origin. Its version is the source capture it shows, so a change that
    /// names its rows gives the difference from the capture before, as the notification placed
    /// them: rows it removed at their old indices, rows it added at their new ones, and a row it
    /// replaced with a new instance of itself in place.
    /// </summary>
    private Body.View.Origin NaturalOrigin(IReadOnlyList<object> order, Body.Source.Preparation source)
    {
        Body.Source.Capture previous = _source.Current;
        if (!ReferenceEquals(order, source.Capture.Rows))
        {
            return default;
        }
        if (ReferenceEquals(source.Capture, previous))
        {
            // A notification since the capture was accepted leaves it unvalidated, and an indexable
            // source is read in place, so only a validated capture still holds the rows shown.
            return previous.Validated ? new(previous, previous, Body.View.Difference.None) : new(previous);
        }
        if (source.IsWhole)
        {
            return new(source.Capture);
        }
        Body.Source.Shift shift = source.Shift;
        List<int> leaving = new();
        List<int> arriving = new(source.Arrivals.Count);
        List<(int, int)> replacing = new(source.Reread.Count);
        int arrival = 0;
        for (int offset = 0; offset < Math.Max(shift.Removed, shift.Added); offset++)
        {
            bool arrives = arrival < source.Arrivals.Count && source.Arrivals[arrival].Index == shift.To + offset;
            if (arrives)
            {
                arriving.Add(shift.To + offset);
                arrival++;
            }
            if (offset < shift.Removed)
            {
                if (offset < shift.Added && !arrives)
                {
                    replacing.Add((shift.From + offset, shift.To + offset));
                }
                else
                {
                    leaving.Add(shift.From + offset);
                }
            }
        }
        return new(source.Capture, previous, new(leaving, arriving, replacing));
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
                if (!SortsLocally || SortsOrder || !_settlePending)
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

internal abstract class SortDefinition
{
    internal abstract SortedOrder Create(IIdentity identity);
}

internal sealed class SortDefinition<TRow, TKey>(Func<TRow, TKey> key, IComparer<TKey> comparer)
    : SortDefinition where TRow : class
{
    internal override SortedOrder Create(IIdentity identity) => new SortedOrder<TRow, TKey>(identity, key, comparer);
}

/// <summary>
/// What the shown rows did since the kept order was accepted. A whole change lists every shown
/// row in source order; any other change names the rows that left, arrived or are read again.
/// </summary>
internal readonly record struct SortChange(
    IReadOnlyList<object>? Shown,
    IReadOnlyList<object> Source,
    IReadOnlyList<object> Leavers,
    IReadOnlyList<Body.Source.Arrival> Arrivals,
    IReadOnlyList<object> Reread,
    Body.Source.Shift Shift)
{
    /// <param name="shown">The rows to order, in source order.</param>
    /// <param name="source">The source rows <paramref name="shown"/> is taken from, which give each its source index.</param>
    internal static SortChange Whole(IReadOnlyList<object> shown, IReadOnlyList<object> source) =>
        new(shown, source, Array.Empty<object>(), Array.Empty<Body.Source.Arrival>(), Array.Empty<object>(), default);
}

/// <summary>
/// Specification section 9's local sort as a kept order: the shown rows sorted by the keys they
/// had when they took their places. Rows keep those places, and the keys that put them there,
/// until moves are allowed; a row read again inside the interval keeps its kept key, and
/// <see cref="NeedsSettle"/> remembers that it differs.
/// </summary>
internal abstract class SortedOrder
{
    internal abstract bool NeedsSettle { get; }
    /// <summary>
    /// Whether the last accepted change placed every row again by its current key, because the kept
    /// order no longer held. That is a settle, wherever the interval stands.
    /// </summary>
    internal abstract bool Resorted { get; }
    /// <summary>
    /// The prepared order's origin: its version, the accepted order's version, and how the two
    /// differ unless every row was placed again.
    /// </summary>
    internal abstract Body.View.Origin Origin { get; }

    /// <summary>The kept order after <paramref name="change"/>, without accepting it.</summary>
    internal abstract IReadOnlyList<object> Prepare(SortChange change, SortDirection direction, bool allowMoves);
    internal abstract void Accept();
    internal abstract void Complete();
    internal abstract IReadOnlyList<object> SortSiblings(IReadOnlyList<object> rows, SortDirection direction);
    internal abstract bool CanWork { get; }
    internal abstract Work CaptureWork(IReadOnlyList<IReadOnlyList<object>> groups, SortDirection direction);

    internal abstract class Work
    {
        internal abstract Task Completion { get; }
        internal abstract IReadOnlyList<object> Prepare(IReadOnlyList<object> rows, bool refresh);
        internal abstract IReadOnlyList<object> Project(int group, IReadOnlyList<object> siblings, bool refresh);
    }
}

internal sealed class SortedOrder<TRow, TKey> : SortedOrder where TRow : class
{
    private readonly record struct Entry(TKey Key, int BaseIndex, int Revision, byte[]? Collation);

    /// <summary>What a publish does to one kept row.</summary>
    private enum Fate : byte
    {
        Stays,
        /// <summary>It stays, with a new entry or instance.</summary>
        Changes,
        /// <summary>It stays only if its new key keeps it in order with its neighbours.</summary>
        Rekeyed,
        Leaves,
        /// <summary>It leaves its place and is placed again.</summary>
        Moves,
    }

    private readonly Func<TRow, TKey> _key;
    private readonly IIdentity _identity;
    private readonly IComparer<TKey> _comparer;
    private readonly bool _usesKeyEquality;
    private readonly CompareInfo? _culture;
    private readonly CompareOptions _options;
    private readonly bool _ordinal;
    private readonly bool _ignoreCase;
    private List<object> _rows = new();
    private List<object> _candidateRows = new();
    private Entry[] _entries = Array.Empty<Entry>();
    private Entry[] _candidateEntries = Array.Empty<Entry>();
    private PositionIndex _positions;
    private PositionIndex _candidatePositions;
    // Source-index shifts not yet applied to the entries; see ResolveBase.
    private readonly List<Body.Source.Shift> _shifts = new();
    private long _inserts;
    private long _removals;
    private SortDirection? _direction;
    private bool _needsSettle;
    private bool _resorted;
    private object _version = new();

    // The prepared candidate.
    private object _candidateVersion = new();
    private Body.View.Difference? _difference;
    private SortDirection _candidateDirection;
    private bool _candidateNeedsSettle;
    private bool _candidateResorted;
    private bool _candidateRebuilt;
    private Body.Source.Shift _candidateShift;
    private readonly List<(object Row, int Index)> _placedRows = new();
    private readonly List<object> _removedRows = new();

    // Working state, indexed by kept index except where noted.
    private Fate[] _fates = Array.Empty<Fate>();
    private Entry[] _next = Array.Empty<Entry>();
    private object?[] _nextRows = Array.Empty<object?>();
    private readonly List<int> _touched = new();
    private int[] _removed = Array.Empty<int>();
    // Indexed by placed row.
    private Entry[] _placed = Array.Empty<Entry>();
    private object[] _placedItems = Array.Empty<object>();
    private int[] _order = Array.Empty<int>();
    private int[] _places = Array.Empty<int>();

    internal SortedOrder(IIdentity identity, Func<TRow, TKey> key, IComparer<TKey> comparer)
    {
        _key = key;
        _identity = identity;
        _comparer = comparer;
        _positions = new(identity, () => new(_inserts, _removals));
        _candidatePositions = new(identity, () => new(_inserts, _removals));
        if (comparer is StringComparer strings)
        {
            _ordinal = StringComparer.IsWellKnownOrdinalComparer(strings, out bool ignoreCase);
            _ignoreCase = ignoreCase;
            _usesKeyEquality = _ordinal;
            if (StringComparer.IsWellKnownCultureAwareComparer(strings, out CompareInfo? culture, out CompareOptions options))
            {
                _usesKeyEquality = true;
                _culture = culture;
                _options = options;
            }
        }
        else
        {
            Type type = Nullable.GetUnderlyingType(typeof(TKey)) ?? typeof(TKey);
            _usesKeyEquality = ReferenceEquals(comparer, Comparer<TKey>.Default)
                && (type.IsPrimitive || type == typeof(decimal) || type == typeof(DateTime)
                    || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)
                    || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Guid));
        }
    }

    internal override bool NeedsSettle => _needsSettle;
    internal override bool Resorted => _resorted;
    internal override Body.View.Origin Origin => new(_candidateVersion, _version, _difference);
    internal override bool CanWork => _usesKeyEquality;

    /// <summary>
    /// A queued first sort's keys, detached from their rows. The worker takes each text key's
    /// collation key and orders the entries; neither runs host code.
    /// </summary>
    private sealed class Payload(Entry[][] entries, SortDirection direction, CompareInfo? culture,
        CompareOptions options, bool ordinal, bool ignoreCase)
    {
        internal Entry[][] Entries { get; } = entries;
        internal int[][] Indices { get; } = new int[entries.Length][];

        internal void Run()
        {
            IComparer<TKey> comparer = ordinal
                ? (IComparer<TKey>)(object)(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                : Comparer<TKey>.Default;
            for (int group = 0; group < Entries.Length; group++)
            {
                Entry[] keys = Entries[group];
                if (culture is not null)
                {
                    for (int index = 0; index < keys.Length; index++)
                    {
                        keys[index] = keys[index] with { Collation = Collation(keys[index].Key, culture, options) };
                    }
                }
                Indices[group] = Enumerable.Range(0, keys.Length).ToArray();
                SortIndices(keys, Indices[group], keys.Length, direction, comparer, culture is not null);
            }
        }
    }

    private sealed class CapturedWork : Work
    {
        private readonly SortedOrder<TRow, TKey> _owner;
        private readonly object[][] _groups;
        private readonly Payload _payload;
        private readonly SortDirection _direction;
        private readonly Task _task;

        internal CapturedWork(SortedOrder<TRow, TKey> owner, object[][] groups,
            Payload payload, SortDirection direction)
        {
            _owner = owner;
            _groups = groups;
            _payload = payload;
            _direction = direction;
            _task = Task.Run(payload.Run);
        }

        internal override Task Completion => _task;

        internal override IReadOnlyList<object> Prepare(IReadOnlyList<object> rows, bool refresh)
        {
            _task.GetAwaiter().GetResult();
            Seed(_owner, 0);
            if (!refresh)
            {
                return _owner._candidateRows;
            }
            _owner.Accept();
            return _owner.Prepare(SortChange.Whole(rows, rows), _direction, allowMoves: true);
        }

        private void Seed(SortedOrder<TRow, TKey> owner, int group)
        {
            object[] rows = _groups[group];
            int[] indices = _payload.Indices[group];
            owner._candidateRows.Clear();
            owner._candidateEntries = new Entry[indices.Length];
            for (int index = 0; index < indices.Length; index++)
            {
                owner._candidateRows.Add(rows[indices[index]]);
                owner._candidateEntries[index] = _payload.Entries[group][indices[index]];
            }
            owner._candidatePositions.Build(owner._candidateRows);
            owner._candidateDirection = _direction;
            owner._candidateNeedsSettle = false;
            owner._candidateResorted = false;
            owner._candidateRebuilt = true;
            owner._candidateVersion = new();
            owner._difference = null;
        }

        internal override IReadOnlyList<object> Project(int group, IReadOnlyList<object> siblings, bool refresh)
        {
            _task.GetAwaiter().GetResult();
            if (group < 0)
            {
                return _owner.SortSiblings(siblings, _direction);
            }
            SortedOrder<TRow, TKey> order = new(_owner._identity, _owner._key, _owner._comparer);
            Seed(order, group);
            if (!refresh)
            {
                return order._candidateRows;
            }
            order.Accept();
            return order.Prepare(SortChange.Whole(siblings, siblings), _direction, allowMoves: true);
        }
    }

    internal override Work CaptureWork(IReadOnlyList<IReadOnlyList<object>> groups, SortDirection direction)
    {
        if (!CanWork)
        {
            throw new InvalidOperationException("This comparer requires the UI thread.");
        }
        object[][] rows = new object[groups.Count][];
        Entry[][] entries = new Entry[groups.Count][];
        for (int group = 0; group < groups.Count; group++)
        {
            rows[group] = groups[group].ToArray();
            entries[group] = new Entry[rows[group].Length];
            for (int index = 0; index < rows[group].Length; index++)
            {
                entries[group][index] = new(_key((TRow)rows[group][index]), index, 0, null);
            }
        }
        return new CapturedWork(this, rows,
            new(entries, direction, _culture, _options, _ordinal, _ignoreCase), direction);
    }

    /// <remarks>
    /// One placement for every change. Each kept row stays, changes in place, leaves, or moves;
    /// the rows that move and the rows that arrive are sorted and placed among the rows that stay
    /// by binary search, and the rows that stay are copied between those places in blocks.
    /// <para>
    /// A row is read again when the change names it, and every kept row is read again when the
    /// change is whole, the direction changed, or a withheld key is due. While moves are allowed,
    /// a row read again takes its new key and moves only when that puts it out of order with its
    /// neighbours. While they are not, it keeps its kept key; a whole change whose rows changed
    /// source order still sorts again, because the kept keys no longer order ties.
    /// </para>
    /// </remarks>
    internal override IReadOnlyList<object> Prepare(SortChange change, SortDirection direction, bool allowMoves)
    {
        int count = _rows.Count;
        bool whole = change.Shown is not null;
        bool everyKey = whole || _direction != direction || allowMoves && _needsSettle;
        bool resort = _direction != direction;
        EnsureCapacity(count, (change.Shown?.Count ?? 0) + change.Arrivals.Count);
        _candidateDirection = direction;
        _candidateShift = whole ? default : change.Shift;
        _candidateRebuilt = whole || resort;
        _placedRows.Clear();
        _removedRows.Clear();
        int nextRevision = _shifts.Count + (Shifts(change.Shift) ? 1 : 0);
        int placed = 0;
        bool differs = false;
        try
        {
            if (whole)
            {
                Array.Fill(_fates, Fate.Leaves, 0, count);
                IReadOnlyList<object> shown = change.Shown!;
                int ordinal = -1;
                int previousBase = -1;
                for (int index = 0; index < shown.Count; index++)
                {
                    object row = shown[index];
                    if (ReferenceEquals(shown, change.Source))
                    {
                        ordinal = index;
                    }
                    else
                    {
                        do
                        {
                            ordinal++;
                        }
                        while (!ReferenceEquals(change.Source[ordinal], row));
                    }
                    int kept = _positions.IndexOf(row);
                    if (kept < 0)
                    {
                        Place(row, Read(row, ordinal, 0), ref placed);
                        continue;
                    }
                    Entry before = ResolveBase(_entries[kept]);
                    resort |= before.BaseIndex <= previousBase;
                    previousBase = before.BaseIndex;
                    _fates[kept] = Fate.Changes;
                    _nextRows[kept] = row;
                    _next[kept] = before with { BaseIndex = ordinal, Revision = 0 };
                    differs |= ReadAgain(kept, row, allowMoves);
                }
            }
            else
            {
                foreach (object row in change.Leavers)
                {
                    int kept = _positions.IndexOf(row);
                    if (kept >= 0)
                    {
                        Touch(kept, Fate.Leaves);
                    }
                }
                foreach (object row in change.Reread)
                {
                    int kept = _positions.IndexOf(row);
                    if (kept >= 0 && _fates[kept] == Fate.Stays)
                    {
                        Touch(kept, Fate.Changes);
                        _nextRows[kept] = row;
                        _next[kept] = Shifted(kept, change.Shift, nextRevision);
                    }
                }
                if (everyKey)
                {
                    for (int kept = 0; kept < count; kept++)
                    {
                        if (_fates[kept] == Fate.Stays)
                        {
                            Touch(kept, Fate.Changes);
                            _nextRows[kept] = _rows[kept];
                            _next[kept] = Shifted(kept, change.Shift, nextRevision);
                        }
                    }
                }
                foreach (int kept in _touched)
                {
                    if (_fates[kept] == Fate.Changes)
                    {
                        differs |= ReadAgain(kept, _nextRows[kept]!, allowMoves);
                    }
                }
                foreach (Body.Source.Arrival arrival in change.Arrivals)
                {
                    Place(arrival.Item, Read(arrival.Item, arrival.Index, nextRevision), ref placed);
                }
            }

            // Without key equality nothing shows which kept keys still hold their order, so binary
            // placement first proves the order with the actual comparer (specification section 9).
            _candidateResorted = !resort && !_usesKeyEquality && (placed > 0 || allowMoves && differs)
                && !KeptOrderHolds(count, change.Shift);
            if (_candidateResorted)
            {
                for (int kept = 0; kept < count; kept++)
                {
                    if (_fates[kept] == Fate.Stays)
                    {
                        Touch(kept, Fate.Changes);
                        _nextRows[kept] = _rows[kept];
                        _next[kept] = Shifted(kept, change.Shift, nextRevision);
                    }
                    if (_fates[kept] == Fate.Changes)
                    {
                        _next[kept] = Read(_nextRows[kept]!, _next[kept].BaseIndex, _next[kept].Revision);
                    }
                }
                resort = true;
                _candidateRebuilt = true;
            }
            if (resort)
            {
                // A changed direction, ties the kept keys no longer order, or a kept order that no
                // longer holds: every kept row is placed again.
                for (int kept = 0; kept < count; kept++)
                {
                    if (_fates[kept] is Fate.Changes or Fate.Rekeyed)
                    {
                        Move(kept, ref placed);
                    }
                }
            }
            else if (allowMoves && _usesKeyEquality)
            {
                MoveOutOfOrder(count, change.Shift, ref placed);
            }
            _candidateNeedsSettle = !_candidateResorted && !allowMoves && (differs || !everyKey && _needsSettle);
            Build(count, placed, resort, whole, change.Shift);
            _candidateVersion = _difference is { IsEmpty: true } ? _version : new();
            if (!whole && !_candidateRebuilt && Shifts(change.Shift)
                && _shifts.Count + 1 >= Math.Max(1, Math.Sqrt(change.Source.Count)))
            {
                Rebase(change.Source);
            }
            return _candidateRows;
        }
        finally
        {
            if (whole || resort || _touched.Count > count / 4)
            {
                Array.Clear(_fates, 0, count);
                Array.Clear(_nextRows, 0, count);
            }
            else
            {
                foreach (int kept in _touched)
                {
                    _fates[kept] = Fate.Stays;
                    _nextRows[kept] = null;
                }
            }
            _touched.Clear();
            Array.Clear(_placedItems, 0, placed);
        }
    }

    private static bool Shifts(Body.Source.Shift shift) => shift.Removed + shift.Added > 0;

    private void Touch(int kept, Fate fate)
    {
        if (_fates[kept] == Fate.Stays)
        {
            _touched.Add(kept);
        }
        _fates[kept] = fate;
    }

    private Entry Read(object row, int baseIndex, int revision)
    {
        TKey key = _key((TRow)row);
        return new(key, baseIndex, revision, Collation(key));
    }

    /// <summary>
    /// Read kept row <paramref name="kept"/>'s key again into its next entry. Returns whether the
    /// key differs from its kept key; while moves are not allowed the kept key stays. Without key
    /// equality a key always counts as different, because a mutable key compares equal to itself
    /// after its value changed.
    /// </summary>
    private bool ReadAgain(int kept, object row, bool allowMoves)
    {
        TKey key = _key((TRow)row);
        Entry before = _next[kept];
        bool differs = !_usesKeyEquality || !EqualityComparer<TKey>.Default.Equals(key, before.Key);
        if (differs && allowMoves)
        {
            _next[kept] = before with { Key = key, Collation = Collation(key) };
            _fates[kept] = Fate.Rekeyed;
        }
        return differs;
    }

    private Entry Shifted(int kept, Body.Source.Shift shift, int revision)
    {
        Entry entry = ResolveBase(_entries[kept]);
        return entry with { BaseIndex = shift.Apply(entry.BaseIndex), Revision = revision };
    }

    private void Place(object row, Entry entry, ref int placed)
    {
        _placedItems[placed] = row;
        _placed[placed] = entry;
        _order[placed] = placed;
        placed++;
    }

    private void Move(int kept, ref int placed)
    {
        _fates[kept] = Fate.Moves;
        Place(_nextRows[kept]!, _next[kept], ref placed);
    }

    /// <summary>
    /// The kept rows whose key changed stay where they are when each is still in order with its
    /// neighbours. Otherwise every one of them moves.
    /// </summary>
    private void MoveOutOfOrder(int count, Body.Source.Shift shift, ref int placed)
    {
        bool outOfOrder = false;
        foreach (int kept in Changed(count))
        {
            if (_fates[kept] != Fate.Rekeyed)
            {
                continue;
            }
            for (int other = kept - 1; other >= 0; other--)
            {
                if (Stays(other))
                {
                    outOfOrder |= Compare(EntryOf(other, shift), _next[kept], _candidateDirection) > 0;
                    break;
                }
            }
            for (int other = kept + 1; other < count && !outOfOrder; other++)
            {
                if (Stays(other))
                {
                    outOfOrder |= Compare(_next[kept], EntryOf(other, shift), _candidateDirection) > 0;
                    break;
                }
            }
            if (outOfOrder)
            {
                break;
            }
        }
        if (!outOfOrder)
        {
            return;
        }
        foreach (int kept in Changed(count))
        {
            if (_fates[kept] == Fate.Rekeyed)
            {
                Move(kept, ref placed);
            }
        }
    }

    private bool Stays(int kept) => _fates[kept] is Fate.Stays or Fate.Changes or Fate.Rekeyed;

    /// <summary>Whether the kept rows that stay are in order by the keys they have after this change.</summary>
    private bool KeptOrderHolds(int count, Body.Source.Shift shift)
    {
        int previous = -1;
        for (int kept = 0; kept < count; kept++)
        {
            if (!Stays(kept))
            {
                continue;
            }
            if (previous >= 0)
            {
                // The keys decide; source indices, which can cost a walk of the pending shifts,
                // only break ties.
                int order = CompareKeys(KeyOf(previous), KeyOf(kept), _candidateDirection, _comparer, _culture is not null);
                if (order > 0 || order == 0
                    && EntryOf(previous, shift).BaseIndex > EntryOf(kept, shift).BaseIndex)
                {
                    return false;
                }
            }
            previous = kept;
        }
        return true;

        Entry KeyOf(int kept) => _fates[kept] is Fate.Changes or Fate.Rekeyed ? _next[kept] : _entries[kept];
    }

    /// <summary>The kept rows that stay with a new entry or instance.</summary>
    private IEnumerable<int> Changed(int count)
    {
        if (_candidateRebuilt)
        {
            for (int kept = 0; kept < count; kept++)
            {
                if (_fates[kept] is Fate.Changes or Fate.Rekeyed)
                {
                    yield return kept;
                }
            }
            yield break;
        }
        for (int index = 0; index < _touched.Count; index++)
        {
            if (_fates[_touched[index]] is Fate.Changes or Fate.Rekeyed)
            {
                yield return _touched[index];
            }
        }
    }

    /// <summary>Kept row <paramref name="kept"/>'s entry after this change.</summary>
    private Entry EntryOf(int kept, Body.Source.Shift shift) =>
        _fates[kept] is Fate.Changes or Fate.Rekeyed ? _next[kept] : Shifted(kept, shift, _shifts.Count);

    /// <summary>
    /// Build the candidate: the rows that stay, copied in blocks, with the placed rows sorted and
    /// put in among them at the places binary search finds. Unless <paramref name="resort"/>
    /// placed every row again, also record how the candidate differs from the kept order: a row
    /// that moved leaves its old place and arrives at its new one.
    /// </summary>
    private void Build(int count, int placed, bool resort, bool whole, Body.Source.Shift shift)
    {
        int removed = 0;
        if (whole || _candidateRebuilt)
        {
            for (int kept = 0; kept < count; kept++)
            {
                if (_fates[kept] is Fate.Leaves or Fate.Moves)
                {
                    _removed[removed++] = kept;
                }
            }
        }
        else
        {
            foreach (int kept in _touched)
            {
                if (_fates[kept] is Fate.Leaves or Fate.Moves)
                {
                    _removed[removed++] = kept;
                }
            }
            Array.Sort(_removed, 0, removed);
        }
        int surviving = count - removed;
        SortIndices(_placed, _order, placed, _candidateDirection, _comparer, _culture is not null);
        for (int index = 0; index < placed; index++)
        {
            _places[index] = FindPlace(_placed[_order[index]], surviving, rank => EntryOf(Survivor(rank), shift));
        }

        int total = surviving + placed;
        if (_candidateEntries.Length < total)
        {
            Array.Resize(ref _candidateEntries, Math.Max(total, _candidateEntries.Length * 2));
        }
        if (_candidateRows.Count > total)
        {
            CollectionsMarshal.AsSpan(_candidateRows)[total..].Clear();
        }
        Array.Clear(_candidateEntries, total, Math.Max(0, _candidateRows.Count - total));
        CollectionsMarshal.SetCount(_candidateRows, total);
        List<int>? arriving = resort ? null : new(placed);
        List<(int, int)>? replacing = resort ? null : new();
        int next = 0;
        int target = 0;
        for (int index = 0; index < placed; index++)
        {
            int length = _places[index] - next;
            CopySurvivors(next, length, target);
            next += length;
            target += length;
            int item = _order[index];
            _candidateRows[target] = _placedItems[item];
            // Accepting a rebuilt order clears the shift list, and each placed entry's source index
            // already includes this change's shift.
            _candidateEntries[target] = _candidateRebuilt ? _placed[item] with { Revision = 0 } : _placed[item];
            _placedRows.Add((_placedItems[item], target));
            arriving?.Add(target);
            target++;
        }
        CopySurvivors(next, surviving - next, target);

        // Rows that stay with a new entry or instance; the copy took the old ones.
        foreach (int kept in Changed(count))
        {
            int rank = kept - LowerBound(_removed, removed, kept);
            int at = rank + UpperBound(_places, placed, rank);
            _candidateRows[at] = _nextRows[kept]!;
            _candidateEntries[at] = _next[kept];
            if (!ReferenceEquals(_nextRows[kept], _rows[kept]))
            {
                replacing?.Add((kept, at));
                if (!_candidateRebuilt)
                {
                    _placedRows.Add((_nextRows[kept]!, at));
                }
            }
        }
        for (int index = 0; index < removed; index++)
        {
            _removedRows.Add(_rows[_removed[index]]);
        }
        _difference = resort ? null : new(_removed[..removed], arriving!, replacing!);
        if (_candidateRebuilt)
        {
            _candidatePositions.Build(_candidateRows);
        }

        int Survivor(int rank)
        {
            int low = 0;
            int high = removed;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (_removed[middle] - middle <= rank)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return rank + low;
        }

        void CopySurvivors(int from, int length, int at)
        {
            while (length > 0)
            {
                int old = Survivor(from);
                int removal = ~Array.BinarySearch(_removed, 0, removed, old);
                int end = removal < removed ? _removed[removal] : count;
                int run = Math.Min(length, end - old);
                CollectionsMarshal.AsSpan(_rows).Slice(old, run)
                    .CopyTo(CollectionsMarshal.AsSpan(_candidateRows).Slice(at, run));
                Array.Copy(_entries, old, _candidateEntries, at, run);
                from += run;
                at += run;
                length -= run;
            }
        }
    }

    private static int LowerBound(int[] sorted, int count, int value)
    {
        int index = Array.BinarySearch(sorted, 0, count, value);
        return index >= 0 ? index : ~index;
    }

    private static int UpperBound(int[] sorted, int count, int value)
    {
        int low = 0;
        int high = count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (sorted[middle] <= value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    /// <summary>
    /// Give every candidate entry its source index again, so the shifts recorded since the last
    /// rebase are dropped. Run once every √n shifts, which keeps both the shift list and its cost
    /// per notification at O(√n).
    /// </summary>
    private void Rebase(IReadOnlyList<object> source)
    {
        _candidatePositions.Build(_candidateRows);
        for (int index = 0; index < source.Count; index++)
        {
            int sorted = _candidatePositions.IndexOf(source[index]);
            if (sorted >= 0)
            {
                _candidateEntries[sorted] = _candidateEntries[sorted] with { BaseIndex = index, Revision = 0 };
            }
        }
        _candidateRebuilt = true;
        _candidateShift = default;
    }

    private int FindPlace(Entry entry, int count, Func<int, Entry> entryAt)
    {
        int low = 0;
        int high = count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (Compare(entryAt(middle), entry, _candidateDirection) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    internal override void Accept()
    {
        if (_candidateRebuilt)
        {
            (_positions, _candidatePositions) = (_candidatePositions, _positions);
            _shifts.Clear();
        }
        else
        {
            foreach (object row in _removedRows)
            {
                _positions.Remove(row);
            }
            foreach (var (row, index) in _placedRows)
            {
                _positions.Insert(row, index);
            }
            if (Shifts(_candidateShift))
            {
                _shifts.Add(_candidateShift);
            }
        }
        _removals += _removedRows.Count;
        _inserts += _placedRows.Count;
        (_rows, _candidateRows) = (_candidateRows, _rows);
        (_entries, _candidateEntries) = (_candidateEntries, _entries);
        if (_candidateRebuilt)
        {
            _positions.Bind(_rows);
        }
        else
        {
            _positions.Follow(_rows);
        }
        _direction = _candidateDirection;
        _needsSettle = _candidateNeedsSettle;
        _resorted = _candidateResorted;
        _version = _candidateVersion;
        Complete();
    }

    private Entry ResolveBase(Entry entry)
    {
        int index = entry.BaseIndex;
        for (int revision = entry.Revision; revision < _shifts.Count; revision++)
        {
            index = _shifts[revision].Apply(index);
        }
        return entry with { BaseIndex = index, Revision = _shifts.Count };
    }

    internal override void Complete()
    {
        _placedRows.Clear();
        _removedRows.Clear();
    }

    internal override IReadOnlyList<object> SortSiblings(IReadOnlyList<object> rows, SortDirection direction)
    {
        EnsureCapacity(0, rows.Count);
        try
        {
            for (int index = 0; index < rows.Count; index++)
            {
                _placed[index] = Read(rows[index], index, 0);
                _order[index] = index;
            }
            SortIndices(_placed, _order, rows.Count, direction, _comparer, _culture is not null);
            object[] result = new object[rows.Count];
            for (int index = 0; index < rows.Count; index++)
            {
                result[index] = rows[_order[index]];
            }
            return result;
        }
        finally
        {
            Array.Clear(_placed, 0, rows.Count);
        }
    }

    private static void SortIndices(Entry[] entries, int[] indices, int count, SortDirection direction,
        IComparer<TKey> comparer, bool collation) =>
        Array.Sort(indices, 0, count, Comparer<int>.Create((left, right) =>
            Compare(entries[left], entries[right], direction, comparer, collation)));

    private int Compare(Entry left, Entry right, SortDirection direction) =>
        Compare(left, right, direction, _comparer, _culture is not null);

    private static int Compare(Entry left, Entry right, SortDirection direction,
        IComparer<TKey> comparer, bool collation)
    {
        int order = CompareKeys(left, right, direction, comparer, collation);
        return order != 0 ? order : left.BaseIndex.CompareTo(right.BaseIndex);
    }

    private static int CompareKeys(Entry left, Entry right, SortDirection direction,
        IComparer<TKey> comparer, bool collation)
    {
        int order = !collation ? comparer.Compare(left.Key, right.Key)
            : left.Collation is null ? right.Collation is null ? 0 : -1
            : right.Collation is null ? 1 : left.Collation.AsSpan().SequenceCompareTo(right.Collation);
        return direction == SortDirection.Ascending || order == 0 ? order : order > 0 ? -1 : 1;
    }

    private byte[]? Collation(TKey key) => Collation(key, _culture, _options);

    private static byte[]? Collation(TKey key, CompareInfo? culture, CompareOptions options) =>
        culture is not null && key is string text ? culture.GetSortKey(text, options).KeyData : null;

    private void EnsureCapacity(int kept, int placed)
    {
        placed += kept;
        if (_fates.Length < kept)
        {
            int capacity = Math.Max(kept, Math.Max(4, _fates.Length * 2));
            Array.Resize(ref _fates, capacity);
            Array.Resize(ref _next, capacity);
            Array.Resize(ref _nextRows, capacity);
            Array.Resize(ref _removed, capacity);
        }
        if (_placed.Length < placed)
        {
            int capacity = Math.Max(placed, Math.Max(4, _placed.Length * 2));
            Array.Resize(ref _placed, capacity);
            Array.Resize(ref _placedItems, capacity);
            Array.Resize(ref _order, capacity);
            Array.Resize(ref _places, capacity);
        }
    }
}
