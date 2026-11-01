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
    private readonly List<object> _keptRows = new();
    private SortedOrder? _sortedOrder;
    private QueuedSort? _queuedSort;
    private long _sortRevision;
    private bool _settlePending;

    private sealed record QueuedSort(Sort? Requested, Sort? Committed, long SortRevision, long LifetimeRevision)
    {
        internal SortWork? Work { get; set; }
        internal TimeSpan CaptureTime { get; set; }
        internal TimeSpan CommitTime { get; set; }
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

            // Comparison can invoke host code; finish it before committing the new sort.
            SortedOrder? workspace = CreateSort(column);
            Body.Source.Capture capture = _source.Current;
            IdentityMap<object>? membership = null;
            IReadOnlyList<object>? order = _sortMode == SortMode.Local
                ? PrepareSort(capture, workspace, direction, out membership) : null;
            if (_hierarchy is null)
            {
                workspace?.Accept(order!);
            }
            CommitSort(column, direction, workspace);

            // Complete presentation before selection handlers can run or throw.
            _headerStrip?.Panel?.RefreshHeader();
            if (order is not null)
                RebuildView(capture, order, membership);

            RaiseLayoutChanged(LayoutChange.Sort);
        }
    }

    private SortedOrder? CreateSort(EffectiveColumn? column) =>
        _sortMode != SortMode.Local || column is null ? null
            : ReferenceEquals(column, _sortColumn) && _sortedOrder is not null ? _sortedOrder
            : column.Column.Sort!.Create(_identity);

    private void CommitSort(EffectiveColumn? column, SortDirection direction, SortedOrder? workspace)
    {
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
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (queued.Work is { } work)
                {
                    CommitWork(queued, work);
                }
                else
                {
                    Sort = queued.Requested;
                }
            }
            finally
            {
                queued.CommitTime = System.Diagnostics.Stopwatch.GetElapsedTime(start);
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
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        EffectiveColumn column = RequireSortable(requested.Column);
        SortedOrder workspace = column.Column.Sort!.Create(_identity);
        if (!workspace.CanWork)
        {
            return false;
        }
        try
        {
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
        finally
        {
            queued.CaptureTime = System.Diagnostics.Stopwatch.GetElapsedTime(start);
        }
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

    private void CommitWork(QueuedSort queued, SortWork work)
    {
        Sort requested = queued.Requested!.Value;
        work.Sorting.CountComparisons();
        Body.Source.Capture capture = _source.Current;
        IReadOnlyList<object> order = PrepareSort(capture, work.Workspace, requested.Direction,
            out IdentityMap<object>? membership, work);
        if (_hierarchy is null)
        {
            work.Workspace.Accept(order);
        }
        CommitSort(RequireSortable(requested.Column), requested.Direction, work.Workspace);
        _headerStrip?.Panel?.RefreshHeader();
        RebuildView(capture, order, membership);
        RaiseLayoutChanged(LayoutChange.Sort);
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

        CommitSort(null, SortDirection.Ascending, null);
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
    /// <returns>The order, and the rows that keep their index, whose containers the reconcile keeps.</returns>
    private (IReadOnlyList<object> Order, IdentitySet Pinned, IReadOnlyList<Change.Edit>? Changes) ViewOrder(
        IReadOnlyList<object> snapshot,
        IdentityMap<object> live,
        Body.Source.Preparation? source = null
    )
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool allowMoves = !SortsLocally || SortsOrder || _view.Count == 0
            || _sortInterval == TimeSpan.Zero || now - _orderSettledAt >= _sortInterval;
        HoldChange? delta = source is null ? null : PrepareHold(source);
        if (delta?.Holds == true && allowMoves && _sortedOrder?.NeedsSettle == true)
        {
            delta = null;
        }
        var (shown, held, released) = delta is null ? Hold(snapshot, live, preparation: source)
            : (snapshot, delta.Held, (IReadOnlyList<object>)Array.Empty<object>());
        bool incremental = delta is not null;
        SortedOrder.Preparation prepared = _sortedOrder is null
            ? new(shown, false, incremental ? NaturalChanges(source!, delta!) : null)
            : incremental ? _sortedOrder.Prepare(source!, _sortDirection, allowMoves, delta!.Arrivals)
            : _sortedOrder.Prepare(shown, source is null ? live : source, _sortDirection, allowMoves);
        if (_sortedOrder is null && delta?.Holds == true)
        {
            AcceptHold(held, released);
            return (_view, held, prepared.Changes);
        }
        IReadOnlyList<object> sorted = prepared.Rows;
        bool reuse = prepared.Changes is { Count: 0 } && SamePins(held, live, source!);
        var result = reuse ? (Order: View, Pinned: _pinned) : Pin(sorted, held, live, source);
        _sortedOrder?.Accept(prepared.Rows);
        AcceptHold(held, released);
        if (allowMoves || prepared.UsedFullSort)
        {
            _orderSettledAt = now;
        }
        _settlePending = SortsLocally && !SortsOrder
            && (_sortedOrder!.NeedsSettle || held.Count > 0 || shown.Count != snapshot.Count
                || (reuse ? _settlePending : !ReferenceEquals(result.Order, sorted)
                    && !result.Order.SequenceEqual(sorted, ReferenceEqualityComparer.Instance)));
        if (_settlePending)
        {
            ScheduleSettle(_sortInterval - (now - _orderSettledAt));
        }
        else
        {
            _settleDue?.Stop();
        }
        return (result.Order, result.Pinned, reuse || ReferenceEquals(result.Order, sorted) ? prepared.Changes : null);
    }

    private (IReadOnlyList<object> Order, IdentitySet Pinned, IReadOnlyList<Change.Edit>? Changes)
        ViewOrder(Hierarchy.Projection projection)
    {
        if (!SortsLocally)
        {
            return (projection.Order(null, _sortDirection), _held, null);
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool settled = SortsOrder || _view.Count == 0 || _sortInterval == TimeSpan.Zero
            || now - _orderSettledAt >= _sortInterval;
        IReadOnlyList<object> order;
        if (!settled && KeptOrder(projection.Membership) is { } kept)
        {
            order = kept;
        }
        else
        {
            order = projection.Order(_sortedOrder, _sortDirection);
            settled = true;
        }
        if (settled)
        {
            _orderSettledAt = now;
            _settlePending = false;
            _settleDue?.Stop();
        }
        else
        {
            _settlePending = true;
            ScheduleSettle(_sortInterval - (now - _orderSettledAt));
        }
        return (order, _held, null);
    }

    private IReadOnlyList<Change.Edit> NaturalChanges(Body.Source.Preparation source, HoldChange hold)
    {
        List<Change.Edit> changes = new();
        if (source.Change is Change.Values)
        {
            return changes;
        }
        foreach (object item in source.Removed)
        {
            if (!hold.Holds && !source.Additions.ContainsKey(item, source.Leavers))
            {
                changes.Add(new(item, null, -1));
            }
        }
        int start = ((Change.Collection)source.Change).Notification.NewStartingIndex;
        foreach (int index in hold.Arrivals)
        {
            object item = source.Added[index];
            object? before = hold.Holds ? _view.Positions.Map.Resolve(item, source.Additions)
                : source.Leavers.Resolve(item, source.Additions);
            changes.Add(new(before, item, hold.Holds ? _view.Positions.IndexOf(item, source.Additions) : start + index));
        }
        return changes;
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

    /// <summary>
    /// The current hierarchy instances in their displayed order, or null when visible
    /// membership changed and the sibling groups need a fresh projection.
    /// </summary>
    private List<object>? KeptOrder(IdentityMap<object> live)
    {
        if (live.Count != _view.Count)
        {
            return null;
        }

        // Always the snapshot's instances, never the view's: the host may have replaced a row with
        // an equal-identity instance, and keeping the old one would leave its container bound to an
        // object nothing updates any more.
        List<object> order = _keptRows;
        order.Clear();
        foreach (object row in _view)
        {
            if (!live.TryGetValue(row, _view.Positions.Map, out object? current))
            {
                return null;
            }

            order.Add(current);
        }

        return order;
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

internal abstract class SortedOrder
{
    internal readonly record struct Preparation(IReadOnlyList<object> Rows, bool UsedFullSort,
        IReadOnlyList<Change.Edit>? Changes = null);
    internal long Comparisons { get; private protected set; }
    internal abstract bool NeedsSettle { get; }
    internal abstract Preparation Prepare(IReadOnlyList<object> rows, IIdentityKeys keys,
        SortDirection direction, bool allowMoves);
    internal abstract Preparation Prepare(Body.Source.Preparation source, SortDirection direction, bool allowMoves,
        IReadOnlyList<int>? arrivals = null);
    internal abstract void Accept(IReadOnlyList<object> rows);
    internal abstract void Complete();
    internal abstract IReadOnlyList<object> SortSiblings(IReadOnlyList<object> rows, SortDirection direction);
    internal abstract bool CanWork { get; }
    internal abstract Work CaptureWork(IReadOnlyList<IReadOnlyList<object>> groups, IIdentityKeys keys,
        SortDirection direction);

    internal abstract class Work
    {
        internal abstract Task Completion { get; }
        internal abstract TimeSpan Duration { get; }
        internal abstract IReadOnlyList<object> Prepare(IReadOnlyList<object> rows, IIdentityKeys keys, bool refresh);
        internal abstract void CountComparisons();
        internal abstract IReadOnlyList<object> Project(int group, IReadOnlyList<object> siblings,
            IIdentityKeys keys, bool refresh);
    }
}

internal sealed class SortedOrder<TRow, TKey> : SortedOrder where TRow : class
{
    private readonly record struct Entry(TKey Key, int BaseIndex, int Revision, byte[]? Collation);

    private readonly record struct OrdinalEdit(int From, int Removed, int To, int Added)
    {
        internal OrdinalEdit(Change.Collection change) : this(
            change.Notification.OldStartingIndex, change.Notification.OldItems?.Count ?? 0,
            change.Notification.NewStartingIndex, change.Notification.NewItems?.Count ?? 0)
        {
        }

        internal int Apply(int index)
        {
            if (Removed > 0 && index >= From + Removed)
            {
                index -= Removed;
            }
            if (Added > 0 && index >= To)
            {
                index += Added;
            }
            return index;
        }
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
    private Entry[] _captured = Array.Empty<Entry>();
    private int[] _indices = Array.Empty<int>();
    private int[] _survivors = Array.Empty<int>();
    private int[] _places = Array.Empty<int>();
    private int[] _targets = Array.Empty<int>();
    private readonly List<Change.Edit> _edits = new();
    private readonly List<(int Index, Entry Value)> _keyUpdates = new();
    // Defer source-ordinal shifts so notifications do not visit every retained key.
    private readonly List<OrdinalEdit> _ordinalEdits = new();
    private Body.Source.Preparation? _ordinalSource;
    private IIdentityKeys? _candidateKeys;
    private long _inserts;
    private long _removals;
    private SortDirection? _direction;
    private SortDirection _candidateDirection;
    private bool _needsSettle;
    private bool _candidateNeedsSettle;

    internal SortedOrder(IIdentity identity, Func<TRow, TKey> key, IComparer<TKey> comparer)
    {
        _key = key;
        _identity = identity;
        _comparer = comparer;
        _positions = new(identity);
        _candidatePositions = new(identity);
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
    internal override bool CanWork => _usesKeyEquality;

    private sealed record Association(object[] Rows, IdentityMap<int> Keys);
    private readonly record struct WorkResult(long Comparisons, TimeSpan Duration);

    private sealed class Payload(Entry[][] entries, SortDirection direction, bool collation,
        bool ordinal, bool ignoreCase)
    {
        internal Entry[][] Entries { get; } = entries;
        internal int[][] Indices { get; } = entries.Select(group => Enumerable.Range(0, group.Length).ToArray()).ToArray();

        internal WorkResult Run()
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            IComparer<TKey> comparer = ordinal
                ? (IComparer<TKey>)(object)(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                : Comparer<TKey>.Default;
            long comparisons = 0;
            for (int group = 0; group < Entries.Length; group++)
            {
                comparisons += SortIndices(Entries[group], Indices[group], Indices[group].Length,
                    direction, comparer, collation);
            }
            return new(comparisons, System.Diagnostics.Stopwatch.GetElapsedTime(start));
        }
    }

    private sealed class CapturedWork : Work
    {
        private readonly SortedOrder<TRow, TKey> _owner;
        private readonly Association[] _groups;
        private readonly Payload _payload;
        private readonly SortDirection _direction;
        private readonly Task<WorkResult> _task;

        internal CapturedWork(SortedOrder<TRow, TKey> owner, Association[] groups,
            Payload payload, SortDirection direction)
        {
            _owner = owner;
            _groups = groups;
            _payload = payload;
            _direction = direction;
            _task = Task.Run(payload.Run);
        }

        internal override Task Completion => _task;
        internal override TimeSpan Duration => _task.GetAwaiter().GetResult().Duration;
        internal override void CountComparisons() => _owner.Comparisons += _task.GetAwaiter().GetResult().Comparisons;
        internal override IReadOnlyList<object> Prepare(IReadOnlyList<object> rows, IIdentityKeys keys, bool refresh)
        {
            _task.GetAwaiter().GetResult();
            Seed(_owner, 0);
            if (!refresh)
            {
                return _owner._candidateRows;
            }
            _owner.Accept(_owner._candidateRows);
            return _owner.Prepare(rows, keys, _direction, allowMoves: true).Rows;
        }

        private void Seed(SortedOrder<TRow, TKey> owner, int group)
        {
            Association captured = _groups[group];
            int[] indices = _payload.Indices[group];
            owner._candidateRows.Clear();
            owner._candidateEntries = new Entry[indices.Length];
            for (int index = 0; index < indices.Length; index++)
            {
                owner._candidateRows.Add(captured.Rows[indices[index]]);
                owner._candidateEntries[index] = _payload.Entries[group][indices[index]];
            }
            owner._candidatePositions.Build(owner._candidateRows, captured.Keys);
            owner._candidateDirection = _direction;
            owner._candidateNeedsSettle = false;
            owner._candidateKeys = null;
            owner._ordinalSource = null;
        }

        internal override IReadOnlyList<object> Project(int group, IReadOnlyList<object> siblings,
            IIdentityKeys keys, bool refresh)
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
            order.Accept(order._candidateRows);
            Preparation prepared = order.Prepare(siblings, keys, _direction, allowMoves: true);
            _owner.Comparisons += order.Comparisons;
            return prepared.Rows;
        }
    }

    internal override Work CaptureWork(IReadOnlyList<IReadOnlyList<object>> groups, IIdentityKeys keys,
        SortDirection direction)
    {
        if (!CanWork)
        {
            throw new InvalidOperationException("This comparer requires the UI thread.");
        }
        Association[] associations = new Association[groups.Count];
        Entry[][] entries = new Entry[groups.Count][];
        for (int group = 0; group < groups.Count; group++)
        {
            object[] rows = groups[group].ToArray();
            IdentityMap<int> captured = _identity.CreateMap<int>(keepsKeys: true);
            entries[group] = new Entry[rows.Length];
            for (int index = 0; index < rows.Length; index++)
            {
                TKey value = _key((TRow)rows[index]);
                entries[group][index] = new(value, index, 0, Collation(value));
                captured.TryAdd(rows[index], index, keys);
            }
            associations[group] = new(rows, captured);
        }
        return new CapturedWork(this, associations,
            new(entries, direction, _culture is not null, _ordinal, _ignoreCase), direction);
    }

    internal override Preparation Prepare(IReadOnlyList<object> rows, IIdentityKeys keys,
        SortDirection direction, bool allowMoves)
    {
        _candidateKeys = null;
        _ordinalSource = null;
        _keyUpdates.Clear();
        EnsureCapacity(Math.Max(rows.Count, _rows.Count));
        try
        {
            return PrepareRows(rows, keys, direction, allowMoves);
        }
        finally
        {
            Array.Clear(_captured, 0, rows.Count);
        }
    }

    private sealed class DeltaRows(IReadOnlyList<object> kept, IReadOnlyList<object> added) : IReadOnlyList<object>
    {
        public int Count => kept.Count + added.Count;
        public object this[int index] => index < kept.Count ? kept[index] : added[index - kept.Count];
        public IEnumerator<object> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal override Preparation Prepare(Body.Source.Preparation source, SortDirection direction, bool allowMoves,
        IReadOnlyList<int>? arrivals = null)
    {
        if (!source.IsIncremental || _direction != direction || allowMoves && _needsSettle
            || source.Change is Change.Collection { Notification.Action: System.Collections.Specialized.NotifyCollectionChangedAction.Move })
        {
            return Prepare(source.Capture.Rows, source, direction, allowMoves);
        }
        arrivals = source.Change is Change.Values ? Array.Empty<int>()
            : arrivals ?? Enumerable.Range(0, source.Added.Count).ToArray();
        DeltaRows rows = new(_rows, source.Change is Change.Values ? Array.Empty<object>() : source.Added);
        EnsureCapacity(rows.Count);
        _keyUpdates.Clear();
        _ordinalSource = source;
        if (_usesKeyEquality && source.Change is Change.Collection)
        {
            return PrepareCollection(source, arrivals, direction, allowMoves);
        }
        if (source.Change is Change.Values && PrepareValues(source, direction, allowMoves) is { } unchanged)
        {
            return unchanged;
        }
        if (_candidateEntries.Length < rows.Count)
        {
            Array.Resize(ref _candidateEntries, _captured.Length);
        }
        _candidateKeys = source;
        int previousCount = _candidateRows.Count;
        _candidateRows.Clear();
        _candidateDirection = direction;
        _candidateNeedsSettle = !allowMoves && _needsSettle;
        _edits.Clear();
        for (int index = 0; index < _rows.Count; index++)
        {
            _captured[index] = CurrentEntry(index);
        }
        Array.Clear(_places, 0, rows.Count);
        try
        {
            if (source.Change is Change.Collection collection)
            {
                int from = collection.Notification.OldStartingIndex;
                int to = collection.Notification.NewStartingIndex;
                for (int index = 0; index < _rows.Count; index++)
                {
                    int baseIndex = _captured[index].BaseIndex;
                    if (source.Removed.Count > 0 && baseIndex >= from + source.Removed.Count)
                    {
                        baseIndex -= source.Removed.Count;
                    }
                    if (source.Added.Count > 0 && baseIndex >= to)
                    {
                        baseIndex += source.Added.Count;
                    }
                    _captured[index] = _captured[index] with { BaseIndex = baseIndex, Revision = _ordinalEdits.Count + 1 };
                }
                foreach (object item in source.Removed)
                {
                    int index = _positions.IndexOf(item, source.Leavers);
                    if (index >= 0)
                    {
                        _places[index] = -1;
                        if (!source.Additions.ContainsKey(item, source.Leavers))
                        {
                            _edits.Add(new(item, null, -1));
                        }
                    }
                }
                foreach (int index in arrivals)
                {
                    TKey current = _key((TRow)source.Added[index]);
                    _captured[_rows.Count + index] = new(current, to + index, _ordinalEdits.Count + 1, Collation(current));
                }
            }
            int changed = 0;
            if (source.Change is Change.Values)
            {
                foreach (var update in _keyUpdates)
                {
                    _captured[update.Index] = update.Value;
                    _indices[changed++] = update.Index;
                    _places[update.Index] = 1;
                }
            }
            int surviving = 0;
            int previous = -1;
            bool values = source.Change is Change.Values;
            bool outOfOrder = values;
            for (int index = 0; index < _rows.Count; index++)
            {
                if (_places[index] < 0)
                {
                    continue;
                }
                if (!values && previous >= 0 && (allowMoves || arrivals.Count > 0)
                    && (!_usesKeyEquality || _places[index] == 1 || _places[previous] == 1))
                {
                    outOfOrder |= Compare(_captured[previous], _captured[index], direction) > 0;
                }
                previous = index;
                _survivors[surviving++] = index;
            }
            bool full = !_usesKeyEquality && outOfOrder;
            if (full)
            {
                Array.Copy(_survivors, _indices, surviving);
                for (int index = 0; index < arrivals.Count; index++)
                {
                    _indices[surviving + index] = _rows.Count + arrivals[index];
                }
                SortIndices(surviving + arrivals.Count, direction);
                for (int index = 0; index < surviving + arrivals.Count; index++)
                {
                    Append(_indices[index], rows);
                }
                _candidateNeedsSettle = false;
            }
            else if (outOfOrder || arrivals.Count > 0)
            {
                if (outOfOrder)
                {
                    int keep = 0;
                    for (int index = 0; index < surviving; index++)
                    {
                        if (_places[_survivors[index]] != 1)
                        {
                            _survivors[keep++] = _survivors[index];
                        }
                    }
                    surviving = keep;
                }
                else
                {
                    changed = 0;
                }
                for (int index = 0; index < arrivals.Count; index++)
                {
                    _indices[changed++] = _rows.Count + arrivals[index];
                }
                Place(rows, surviving, changed, direction);
            }
            else
            {
                for (int index = 0; index < surviving; index++)
                {
                    Append(_survivors[index], rows);
                }
            }
            if (!_usesKeyEquality && (allowMoves || arrivals.Count > 0))
            {
                _candidateNeedsSettle = false;
            }
            int affected = source.Change is Change.Values ? source.Added.Count : arrivals.Count;
            for (int added = 0; added < affected; added++)
            {
                int ordinal = source.Change is Change.Values ? added : arrivals[added];
                object item = source.Added[ordinal];
                int index = source.Change is Change.Values
                    ? _positions.IndexOf(item, source.Additions)
                    : _rows.Count + ordinal;
                if (index >= 0)
                {
                    object? before = source.Change is Change.Values ? _rows[index]
                        : source.Leavers.Resolve(item, source.Additions);
                    if (!ReferenceEquals(before, item) || index != _targets[index])
                    {
                        _edits.Add(new(before, item, _targets[index]));
                    }
                }
            }
            if (full)
            {
                _candidateKeys = null;
                _candidatePositions.Build(_candidateRows, source);
            }
            if (source.Change is Change.Collection && ShouldCompact(source))
            {
                PrepareOrdinals(source);
            }
            return new(_candidateRows, full, full ? null : _edits);
        }
        finally
        {
            if (_candidateRows.Count < previousCount)
            {
                Array.Clear(_candidateEntries, _candidateRows.Count, previousCount - _candidateRows.Count);
            }
            Array.Clear(_captured, 0, rows.Count);
        }
    }

    private Preparation PrepareCollection(Body.Source.Preparation source, IReadOnlyList<int> arrivals,
        SortDirection direction, bool allowMoves)
    {
        _candidateKeys = source;
        _candidateDirection = direction;
        _candidateNeedsSettle = !allowMoves && _needsSettle;
        _edits.Clear();
        OrdinalEdit ordinal = new((Change.Collection)source.Change);
        int removed = 0;
        foreach (object item in source.Removed)
        {
            int index = _positions.IndexOf(item, source.Leavers);
            if (index >= 0)
            {
                _survivors[removed++] = index;
                if (!source.Additions.ContainsKey(item, source.Leavers))
                {
                    _edits.Add(new(item, null, -1));
                }
            }
        }
        Array.Sort(_survivors, 0, removed);
        bool compact = ShouldCompact(source);
        if (removed == 0 && arrivals.Count == 0 && !compact)
        {
            return new(_rows, false, _edits);
        }

        try
        {
            for (int index = 0; index < arrivals.Count; index++)
            {
                int added = arrivals[index];
                TKey key = _key((TRow)source.Added[added]);
                _captured[index] = new(key, ordinal.To + added, _ordinalEdits.Count + 1, Collation(key));
                _indices[index] = index;
            }
            SortIndices(arrivals.Count, direction);
            int surviving = _rows.Count - removed;
            Func<int, Entry> entryAt = rank =>
            {
                Entry entry = CurrentEntry(Survivor(rank));
                return entry with { BaseIndex = ordinal.Apply(entry.BaseIndex) };
            };
            for (int index = 0; index < arrivals.Count; index++)
            {
                int added = _indices[index];
                int place = FindPlace(_captured[added], surviving, entryAt, direction);
                _places[index] = place;
                object item = source.Added[arrivals[added]];
                object? before = source.Leavers.Resolve(item, source.Additions);
                _edits.Add(new(before, item, place + index));
            }

            int total = surviving + arrivals.Count;
            if (_candidateEntries.Length < total)
            {
                Array.Resize(ref _candidateEntries, _captured.Length);
            }
            if (_candidateRows.Count > total)
            {
                CollectionsMarshal.AsSpan(_candidateRows)[total..].Clear();
                Array.Clear(_candidateEntries, total, _candidateRows.Count - total);
            }
            CollectionsMarshal.SetCount(_candidateRows, total);
            int next = 0;
            int target = 0;
            for (int index = 0; index < arrivals.Count; index++)
            {
                int count = _places[index] - next;
                CopySurvivors(next, count, target);
                next += count;
                target += count;
                int added = _indices[index];
                _candidateRows[target] = source.Added[arrivals[added]];
                _candidateEntries[target++] = _captured[added];
            }
            CopySurvivors(next, surviving - next, target);
            if (compact)
            {
                PrepareOrdinals(source);
            }
            return new(_candidateRows, false, _edits);
        }
        finally
        {
            Array.Clear(_captured, 0, arrivals.Count);
        }

        int Survivor(int rank)
        {
            int low = 0;
            int high = removed;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (_survivors[middle] - middle <= rank)
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

        void CopySurvivors(int from, int count, int target)
        {
            while (count > 0)
            {
                int old = Survivor(from);
                int removal = ~Array.BinarySearch(_survivors, 0, removed, old);
                int end = removal < removed ? _survivors[removal] : _rows.Count;
                int length = Math.Min(count, end - old);
                CollectionsMarshal.AsSpan(_rows).Slice(old, length)
                    .CopyTo(CollectionsMarshal.AsSpan(_candidateRows).Slice(target, length));
                Array.Copy(_entries, old, _candidateEntries, target, length);
                from += length;
                target += length;
                count -= length;
            }
        }
    }

    private Preparation? PrepareValues(Body.Source.Preparation source, SortDirection direction, bool allowMoves)
    {
        _candidateKeys = null;
        _candidateNeedsSettle = !allowMoves && _needsSettle;
        try
        {
            foreach (object item in source.Added)
            {
                int index = _positions.IndexOf(item, source.Additions);
                if (index < 0)
                {
                    continue;
                }
                TKey value = _key((TRow)item);
                Entry kept = CurrentEntry(index);
                bool changed = !_usesKeyEquality || !EqualityComparer<TKey>.Default.Equals(value, kept.Key);
                _captured[index] = kept;
                if (changed && allowMoves)
                {
                    Entry current = new(value, kept.BaseIndex, kept.Revision, Collation(value));
                    _captured[index] = current;
                    _keyUpdates.Add((index, current));
                }
                else
                {
                    _candidateNeedsSettle |= changed;
                }
            }
            bool outOfOrder = false;
            if (_usesKeyEquality)
            {
                foreach (var update in _keyUpdates)
                {
                    int index = update.Index;
                    outOfOrder |= index > 0 && Compare(Current(index - 1), update.Value, direction) > 0;
                    outOfOrder |= index + 1 < _rows.Count && Compare(update.Value, Current(index + 1), direction) > 0;
                }
            }
            else if (allowMoves && _keyUpdates.Count > 0)
            {
                for (int index = 1; index < _rows.Count; index++)
                {
                    outOfOrder |= Compare(Current(index - 1), Current(index), direction) > 0;
                }
            }
            return outOfOrder ? null : new Preparation(_rows, false, Array.Empty<Change.Edit>());
        }
        finally
        {
            foreach (object item in source.Added)
            {
                int index = _positions.IndexOf(item, source.Additions);
                if (index >= 0)
                {
                    _captured[index] = default;
                }
            }
        }

        Entry Current(int index) => source.Additions.ContainsKey(_rows[index], _positions)
            ? _captured[index] : CurrentEntry(index);
    }

    private Preparation PrepareRows(IReadOnlyList<object> rows, IIdentityKeys keys,
        SortDirection direction, bool allowMoves)
    {
        if (_candidateEntries.Length < rows.Count)
        {
            Array.Resize(ref _candidateEntries, _captured.Length);
        }
        if (_candidateRows.Count > rows.Count)
        {
            Array.Clear(_candidateEntries, rows.Count, _candidateRows.Count - rows.Count);
        }
        _candidateRows.Clear();
        _candidateDirection = direction;
        _candidateNeedsSettle = false;
        Array.Fill(_places, -1, 0, _rows.Count);
        int previousBase = -1;
        IReadOnlyList<object> baseline = keys is Body.Source.Preparation source ? source.Capture.Rows : rows;
        int sourceIndex = 0;
        bool full = _direction != direction;
        int arrivals = 0;
        for (int index = 0; index < rows.Count; index++)
        {
            int old = _positions.IndexOf(rows[index], keys);
            TKey current = _key((TRow)rows[index]);
            byte[]? collation = null;
            if (old >= 0)
            {
                Entry kept = CurrentEntry(old);
                full |= kept.BaseIndex <= previousBase;
                previousBase = kept.BaseIndex;
                _places[old] = index;
                if (_usesKeyEquality && EqualityComparer<TKey>.Default.Equals(current, kept.Key))
                {
                    collation = kept.Collation;
                }
            }
            else
            {
                arrivals++;
            }
            int ordinal = index;
            if (!ReferenceEquals(rows, baseline))
            {
                while (!ReferenceEquals(baseline[sourceIndex], rows[index]))
                {
                    sourceIndex++;
                }
                ordinal = sourceIndex++;
            }
            _captured[index] = new(current, ordinal, 0, old >= 0 ? collation : Collation(current));
        }

        int surviving = 0;
        int changed = 0;
        bool outOfOrder = false;
        bool previousChanged = false;
        int previous = -1;
        for (int old = 0; old < _rows.Count; old++)
        {
            int current = _places[old];
            if (current < 0)
            {
                continue;
            }
            bool differs = !_usesKeyEquality
                || !EqualityComparer<TKey>.Default.Equals(_captured[current].Key, _entries[old].Key);
            if (!full && !allowMoves)
            {
                _candidateNeedsSettle |= differs;
                if (_usesKeyEquality)
                {
                    _captured[current] = _entries[old] with { BaseIndex = _captured[current].BaseIndex, Revision = 0 };
                }
            }
            if (_culture is not null && _captured[current].Key is not null && _captured[current].Collation is null)
            {
                _captured[current] = _captured[current] with { Collation = Collation(_captured[current].Key) };
            }
            if (!full && previous >= 0 && (!_usesKeyEquality || differs || previousChanged)
                && (allowMoves || arrivals > 0))
            {
                outOfOrder |= Compare(_captured[previous], _captured[current], direction) > 0;
            }
            previous = current;
            previousChanged = differs;
            _survivors[surviving++] = current;
            if (differs && allowMoves && _usesKeyEquality)
            {
                _indices[changed++] = current;
            }
        }
        bool usedFullSort = full || (!_usesKeyEquality && outOfOrder);
        if (usedFullSort)
        {
            for (int index = 0; index < rows.Count; index++)
            {
                _indices[index] = index;
            }
            SortIndices(rows.Count, direction);
            for (int index = 0; index < rows.Count; index++)
            {
                Append(_indices[index], rows);
            }
            _candidateNeedsSettle = false;
        }
        else if (arrivals > 0 || outOfOrder)
        {
            if (outOfOrder)
            {
                int kept = 0;
                for (int index = 0; index < surviving; index++)
                {
                    int current = _survivors[index];
                    int old = _positions.IndexOf(rows[current], keys);
                    if (EqualityComparer<TKey>.Default.Equals(_captured[current].Key, _entries[old].Key))
                    {
                        _survivors[kept++] = current;
                    }
                }
                surviving = kept;
            }
            else
            {
                changed = 0;
            }
            for (int index = 0; index < rows.Count; index++)
            {
                if (_positions.IndexOf(rows[index], keys) < 0)
                {
                    _indices[changed++] = index;
                }
            }
            Place(rows, surviving, changed, direction);
        }
        else
        {
            for (int index = 0; index < surviving; index++)
            {
                Append(_survivors[index], rows);
            }
        }
        if (!_usesKeyEquality && (allowMoves || arrivals > 0))
        {
            _candidateNeedsSettle = false;
        }
        _candidatePositions.Build(_candidateRows, keys);
        return new(_candidateRows, usedFullSort);
    }

    private void Place(IReadOnlyList<object> rows, int surviving, int changed, SortDirection direction)
    {
        SortIndices(changed, direction);
        Func<int, Entry> entryAt = index => _captured[_survivors[index]];
        for (int index = 0; index < changed; index++)
        {
            _places[index] = FindPlace(_captured[_indices[index]], surviving, entryAt, direction);
        }
        int next = 0;
        for (int index = 0; index < changed; index++)
        {
            while (next < _places[index])
            {
                Append(_survivors[next++], rows);
            }
            Append(_indices[index], rows);
        }
        while (next < surviving)
        {
            Append(_survivors[next++], rows);
        }
    }

    private int FindPlace(Entry entry, int count, Func<int, Entry> entryAt, SortDirection direction)
    {
        int low = 0;
        int high = count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (Compare(entryAt(middle), entry, direction) < 0)
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

    private void Append(int index, IReadOnlyList<object> rows)
    {
        _targets[index] = _candidateRows.Count;
        _candidateEntries[_candidateRows.Count] = _captured[index];
        _candidateRows.Add(rows[index]);
    }

    internal override void Accept(IReadOnlyList<object> rows)
    {
        if (ReferenceEquals(rows, _rows))
        {
            foreach (var update in _keyUpdates)
            {
                _entries[update.Index] = update.Value;
            }
            _needsSettle = _candidateNeedsSettle;
            _keyUpdates.Clear();
            AcceptOrdinals();
            return;
        }
        if (_candidateKeys is { } keys)
        {
            long removed = 0;
            long inserted = 0;
            foreach (Change.Edit edit in _edits)
            {
                int previous = edit.Before is null ? -1 : _positions.IndexOf(edit.Before, _positions);
                if (edit.Before is not null)
                {
                    _positions.Remove(edit.Before, _positions);
                }
                if (edit.After is not null)
                {
                    _positions.Insert(edit.After, edit.Index, keys);
                }
                if (previous != edit.Index)
                {
                    removed += previous >= 0 ? 1 : 0;
                    inserted += edit.After is not null ? 1 : 0;
                }
            }
            _removals += removed;
            _inserts += inserted;
        }
        (_rows, _candidateRows) = (_candidateRows, _rows);
        (_entries, _candidateEntries) = (_candidateEntries, _entries);
        if (_candidateKeys is null)
        {
            (_positions, _candidatePositions) = (_candidatePositions, _positions);
            _positions.Bind(_rows, () => new(_inserts, _removals));
        }
        else
        {
            _positions.MoveTo(_rows);
            _candidateKeys = null;
        }
        _direction = _candidateDirection;
        _needsSettle = _candidateNeedsSettle;
        AcceptOrdinals();
    }

    private Entry CurrentEntry(int index) => ResolveOrdinal(_entries[index]);

    private Entry ResolveOrdinal(Entry entry)
    {
        int index = entry.BaseIndex;
        for (int revision = entry.Revision; revision < _ordinalEdits.Count; revision++)
        {
            index = _ordinalEdits[revision].Apply(index);
        }
        return entry with { BaseIndex = index, Revision = _ordinalEdits.Count };
    }

    private void AcceptOrdinals()
    {
        if (_ordinalSource is null)
        {
            _ordinalEdits.Clear();
            return;
        }
        if (_ordinalSource.Change is not Change.Collection collection)
        {
            return;
        }
        _ordinalEdits.Add(new(collection));
        if (_rows.Count == 0)
        {
            _ordinalEdits.Clear();
        }
    }

    private bool ShouldCompact(Body.Source.Preparation source) =>
        _ordinalEdits.Count + 1 >= Math.Max(1, Math.Sqrt(source.Capture.Rows.Count));

    private void PrepareOrdinals(Body.Source.Preparation source)
    {
        if (_candidateKeys is not null)
        {
            _candidatePositions.Build(_candidateRows, source);
        }
        IReadOnlyList<object> rows = source.Capture.Rows;
        for (int index = 0; index < rows.Count; index++)
        {
            if (_candidatePositions.Map.TryGetValue(rows[index], source, out int sorted))
            {
                _candidateEntries[sorted] = _candidateEntries[sorted] with { BaseIndex = index, Revision = 0 };
            }
        }
        _candidateKeys = null;
        _ordinalSource = null;
    }

    internal override void Complete()
    {
        _candidateKeys = null;
        _ordinalSource = null;
        _edits.Clear();
        _keyUpdates.Clear();
    }

    internal override IReadOnlyList<object> SortSiblings(IReadOnlyList<object> rows, SortDirection direction)
    {
        EnsureCapacity(rows.Count);
        try
        {
            for (int index = 0; index < rows.Count; index++)
            {
                TKey key = _key((TRow)rows[index]);
                _captured[index] = new(key, index, 0, Collation(key));
                _indices[index] = index;
            }
            SortIndices(rows.Count, direction);
            object[] result = new object[rows.Count];
            for (int index = 0; index < rows.Count; index++)
            {
                result[index] = rows[_indices[index]];
            }
            return result;
        }
        finally
        {
            Array.Clear(_captured, 0, rows.Count);
        }
    }

    private void SortIndices(int count, SortDirection direction) =>
        Comparisons += SortIndices(_captured, _indices, count, direction, _comparer, _culture is not null);

    private static long SortIndices(Entry[] entries, int[] indices, int count, SortDirection direction,
        IComparer<TKey> comparer, bool collation)
    {
        long comparisons = 0;
        Array.Sort(indices, 0, count, Comparer<int>.Create((left, right) =>
        {
            comparisons++;
            return Compare(entries[left], entries[right], direction, comparer, collation);
        }));
        return comparisons;
    }

    private int Compare(Entry left, Entry right, SortDirection direction)
    {
        Comparisons++;
        return Compare(left, right, direction, _comparer, _culture is not null);
    }

    private static int Compare(Entry left, Entry right, SortDirection direction,
        IComparer<TKey> comparer, bool collation)
    {
        int order = !collation ? comparer.Compare(left.Key, right.Key)
            : left.Collation is null ? right.Collation is null ? 0 : -1
            : right.Collation is null ? 1 : left.Collation.AsSpan().SequenceCompareTo(right.Collation);
        if (order != 0)
        {
            return direction == SortDirection.Ascending ? order : order > 0 ? -1 : 1;
        }
        return left.BaseIndex.CompareTo(right.BaseIndex);
    }

    private byte[]? Collation(TKey key) => _culture is not null && key is string text
        ? _culture.GetSortKey(text, _options).KeyData : null;

    private void EnsureCapacity(int count)
    {
        if (_captured.Length >= count)
        {
            return;
        }
        int capacity = Math.Max(count, Math.Max(4, _captured.Length * 2));
        Array.Resize(ref _captured, capacity);
        Array.Resize(ref _indices, capacity);
        Array.Resize(ref _survivors, capacity);
        Array.Resize(ref _places, capacity);
        Array.Resize(ref _targets, capacity);
    }
}
