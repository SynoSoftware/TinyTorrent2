using System.Globalization;

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

    private sealed record QueuedSort(Sort? Requested, Sort? Committed, long SortRevision, long LifetimeRevision);

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
                workspace?.Accept();
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
            _sortRevision++;
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

    private bool IsValid(QueuedSort queued) => !_detached && IsLoaded
        && queued.SortRevision == _sortRevision && queued.LifetimeRevision == _lifetimeRevision
        && queued.Committed == Sort
        && (queued.Requested is not Sort requested
            || EffectiveLayout.Order.Any(column => ReferenceEquals(column.Column, requested.Column) && column.IsVisible));

    private void ConsumeSort(QueuedSort queued)
    {
        if (!ReferenceEquals(_queuedSort, queued))
            return;
        _queuedSort = null;
        try
        {
            if (IsValid(queued))
                Sort = queued.Requested;
        }
        finally
        {
            _headerStrip?.Panel?.RefreshHeader();
        }
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
    private (IReadOnlyList<object> Order, IdentitySet Pinned) ViewOrder(
        IReadOnlyList<object> snapshot,
        IdentityMap<object> live
    )
    {
        if (_hierarchy is not null && !SortsLocally)
            return (snapshot, _held);
        var (shown, held, released) = Hold(snapshot, live);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool allowMoves = !SortsLocally || SortsOrder || _view.Count == 0
            || _sortInterval == TimeSpan.Zero || now - _orderSettledAt >= _sortInterval;
        if (_hierarchy is null)
        {
            SortedOrder.Preparation prepared = _sortedOrder is null ? new(shown, false)
                : _sortedOrder.Prepare(shown, live, _sortDirection, allowMoves);
            IReadOnlyList<object> sorted = prepared.Rows;
            var result = Pin(sorted, held, live);
            _sortedOrder?.Accept();
            AcceptHold(held, released);
            if (allowMoves || prepared.UsedFullSort)
                _orderSettledAt = now;
            _settlePending = SortsLocally && !SortsOrder
                && (_sortedOrder!.NeedsSettle || held.Count > 0 || shown.Count != snapshot.Count
                    || !result.Order.SequenceEqual(sorted, ReferenceEqualityComparer.Instance));
            if (_settlePending)
                ScheduleSettle(_sortInterval - (now - _orderSettledAt));
            else
                _settleDue?.Stop();
            return result;
        }

        // Natural order belongs to the host, which reorders when it means to, and the row-order
        // column shows that same order, so holding it would delay the person's own reorder; an
        // empty view has no established order to preserve; and a zero interval is the host asking
        // for none of this.
        if (!SortsLocally || SortsOrder || _view.Count == 0 || _sortInterval == TimeSpan.Zero)
        {
            return Finish(SortedSnapshot(), settled: true);
        }

        if (now - _orderSettledAt >= _sortInterval)
        {
            return Finish(SortedSnapshot(), settled: true);
        }

        // Hierarchy order is sorted per sibling group; the flat placement routine cannot
        // search it without separating children from their parents.
        if (KeptOrder(shown, held, live) is not List<object> kept)
        {
            return Finish(SortedSnapshot(), settled: true);
        }

        return Finish(kept, settled: false);

        (IReadOnlyList<object> Order, IdentitySet Pinned) Finish(
            IReadOnlyList<object> order,
            bool settled
        )
        {
            var result = Pin(order, held, live);
            // A failed comparison must not consume a release or change held-row interactivity.
            AcceptHold(held, released);
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
            return result;
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
    private List<object>? KeptOrder(IReadOnlyList<object> shown, IdentitySet held, IdentityMap<object> live)
    {
        if (shown.Count + held.Count != _view.Count)
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
            if (held.Contains(row, _view.Positions.Map))
            {
                continue;
            }

            if (!live.TryGetValue(row, _view.Positions.Map, out object? current))
            {
                return null;
            }

            order.Add(current);
        }

        return order;
    }

    /// <summary>
    /// The current hierarchy in stable sibling order. Descending reverses the key comparison,
    /// preserving the base-order tie-break within each group.
    /// </summary>
    private IReadOnlyList<object> SortedSnapshot() =>
        _hierarchy!.Project((_preparedHierarchy ?? _hierarchy.Captured)!, _sortedOrder, _sortDirection);
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
    internal readonly record struct Preparation(IReadOnlyList<object> Rows, bool UsedFullSort);
    internal long Comparisons { get; private protected set; }
    internal abstract bool NeedsSettle { get; }
    internal abstract Preparation Prepare(IReadOnlyList<object> rows, IIdentityKeys keys,
        SortDirection direction, bool allowMoves);
    internal abstract void Accept();
    internal abstract IReadOnlyList<object> SortSiblings(IReadOnlyList<object> rows, SortDirection direction);
}

internal sealed class SortedOrder<TRow, TKey> : SortedOrder where TRow : class
{
    private readonly record struct Entry(TKey Key, int BaseIndex, byte[]? Collation);

    private readonly Func<TRow, TKey> _key;
    private readonly IComparer<TKey> _comparer;
    private readonly bool _usesKeyEquality;
    private readonly CompareInfo? _culture;
    private readonly CompareOptions _options;
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
    private SortDirection? _direction;
    private SortDirection _candidateDirection;
    private bool _needsSettle;
    private bool _candidateNeedsSettle;

    internal SortedOrder(IIdentity identity, Func<TRow, TKey> key, IComparer<TKey> comparer)
    {
        _key = key;
        _comparer = comparer;
        _positions = new(identity);
        _candidatePositions = new(identity);
        if (comparer is StringComparer strings)
        {
            _usesKeyEquality = StringComparer.IsWellKnownOrdinalComparer(strings, out _);
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

    internal override Preparation Prepare(IReadOnlyList<object> rows, IIdentityKeys keys,
        SortDirection direction, bool allowMoves)
    {
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

    private Preparation PrepareRows(IReadOnlyList<object> rows, IIdentityKeys keys,
        SortDirection direction, bool allowMoves)
    {
        if (_candidateEntries.Length < rows.Count)
            Array.Resize(ref _candidateEntries, _captured.Length);
        if (_candidateRows.Count > rows.Count)
            Array.Clear(_candidateEntries, rows.Count, _candidateRows.Count - rows.Count);
        _candidateRows.Clear();
        _candidateDirection = direction;
        _candidateNeedsSettle = false;
        Array.Fill(_places, -1, 0, _rows.Count);
        int previousBase = -1;
        bool full = _direction != direction;
        int arrivals = 0;
        for (int index = 0; index < rows.Count; index++)
        {
            int old = _positions.IndexOf(rows[index], keys);
            TKey current = _key((TRow)rows[index]);
            byte[]? collation = null;
            if (old >= 0)
            {
                Entry kept = _entries[old];
                full |= kept.BaseIndex <= previousBase;
                previousBase = kept.BaseIndex;
                _places[old] = index;
                if (_usesKeyEquality && EqualityComparer<TKey>.Default.Equals(current, kept.Key))
                    collation = kept.Collation;
            }
            else
                arrivals++;
            _captured[index] = new(current, index, old >= 0 ? collation : Collation(current));
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
                continue;
            bool differs = !_usesKeyEquality
                || !EqualityComparer<TKey>.Default.Equals(_captured[current].Key, _entries[old].Key);
            if (!full && !allowMoves)
            {
                _candidateNeedsSettle |= differs;
                if (_usesKeyEquality)
                    _captured[current] = _entries[old] with { BaseIndex = current };
            }
            if (_culture is not null && _captured[current].Key is not null && _captured[current].Collation is null)
                _captured[current] = _captured[current] with { Collation = Collation(_captured[current].Key) };
            if (!full && previous >= 0 && (!_usesKeyEquality || differs || previousChanged)
                && (allowMoves || arrivals > 0))
                outOfOrder |= Compare(_captured[previous], _captured[current], direction) > 0;
            previous = current;
            previousChanged = differs;
            _survivors[surviving++] = current;
            if (differs && allowMoves && _usesKeyEquality)
                _indices[changed++] = current;
        }
        bool usedFullSort = full || (!_usesKeyEquality && outOfOrder);
        if (usedFullSort)
        {
            for (int index = 0; index < rows.Count; index++)
                _indices[index] = index;
            SortIndices(rows.Count, direction);
            for (int index = 0; index < rows.Count; index++)
                Append(_indices[index], rows);
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
                        _survivors[kept++] = current;
                }
                surviving = kept;
            }
            else
                changed = 0;
            for (int index = 0; index < rows.Count; index++)
                if (_positions.IndexOf(rows[index], keys) < 0)
                    _indices[changed++] = index;
            Place(rows, surviving, changed, direction);
        }
        else
        {
            for (int index = 0; index < surviving; index++)
                Append(_survivors[index], rows);
        }
        if (!_usesKeyEquality && (allowMoves || arrivals > 0))
            _candidateNeedsSettle = false;
        _candidatePositions.Build(_candidateRows, keys);
        return new(_candidateRows, usedFullSort);
    }

    private void Place(IReadOnlyList<object> rows, int surviving, int changed, SortDirection direction)
    {
        SortIndices(changed, direction);
        for (int index = 0; index < changed; index++)
        {
            int low = 0;
            int high = surviving;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (Compare(_captured[_survivors[middle]], _captured[_indices[index]], direction) < 0)
                    low = middle + 1;
                else
                    high = middle;
            }
            _places[index] = low;
        }
        int next = 0;
        for (int index = 0; index < changed; index++)
        {
            while (next < _places[index])
                Append(_survivors[next++], rows);
            Append(_indices[index], rows);
        }
        while (next < surviving)
            Append(_survivors[next++], rows);
    }

    private void Append(int index, IReadOnlyList<object> rows)
    {
        _candidateEntries[_candidateRows.Count] = _captured[index];
        _candidateRows.Add(rows[index]);
    }

    internal override void Accept()
    {
        (_rows, _candidateRows) = (_candidateRows, _rows);
        (_entries, _candidateEntries) = (_candidateEntries, _entries);
        (_positions, _candidatePositions) = (_candidatePositions, _positions);
        _direction = _candidateDirection;
        _needsSettle = _candidateNeedsSettle;
    }

    internal override IReadOnlyList<object> SortSiblings(IReadOnlyList<object> rows, SortDirection direction)
    {
        EnsureCapacity(rows.Count);
        try
        {
            for (int index = 0; index < rows.Count; index++)
            {
                TKey key = _key((TRow)rows[index]);
                _captured[index] = new(key, index, Collation(key));
                _indices[index] = index;
            }
            SortIndices(rows.Count, direction);
            object[] result = new object[rows.Count];
            for (int index = 0; index < rows.Count; index++)
                result[index] = rows[_indices[index]];
            return result;
        }
        finally
        {
            Array.Clear(_captured, 0, rows.Count);
        }
    }

    private void SortIndices(int count, SortDirection direction) =>
        Array.Sort(_indices, 0, count, Comparer<int>.Create((left, right) =>
            Compare(_captured[left], _captured[right], direction)));

    private int Compare(Entry left, Entry right, SortDirection direction)
    {
        Comparisons++;
        int order = _culture is null ? _comparer.Compare(left.Key, right.Key)
            : left.Collation is null ? right.Collation is null ? 0 : -1
            : right.Collation is null ? 1 : left.Collation.AsSpan().SequenceCompareTo(right.Collation);
        if (order != 0)
            return direction == SortDirection.Ascending ? order : order > 0 ? -1 : 1;
        return left.BaseIndex.CompareTo(right.BaseIndex);
    }

    private byte[]? Collation(TKey key) => _culture is not null && key is string text
        ? _culture.GetSortKey(text, _options).KeyData : null;

    private void EnsureCapacity(int count)
    {
        if (_captured.Length >= count)
            return;
        int capacity = Math.Max(count, Math.Max(4, _captured.Length * 2));
        Array.Resize(ref _captured, capacity);
        Array.Resize(ref _indices, capacity);
        Array.Resize(ref _survivors, capacity);
        Array.Resize(ref _places, capacity);
    }
}
