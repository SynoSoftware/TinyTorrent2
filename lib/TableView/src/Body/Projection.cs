using System.Diagnostics;

namespace Syno.TableView.Body;

/// <summary>Prepares the table's display projection from captured values, without accessing WinUI.</summary>
internal sealed class Projection
{
    internal sealed record Rows
    {
        internal required IReadOnlyList<object> Source { get; init; }
        internal required IReadOnlyList<object> Displayed { get; init; }
        internal required ItemIdentity Identity { get; init; }
        internal Type? RowType { get; init; }
        internal Hierarchy.Snapshot? Hierarchy { get; init; }
        internal Hierarchy.Snapshot? PreviousHierarchy { get; init; }
        internal IReadOnlySet<object> Expanded { get; init; } = new HashSet<object>(ReferenceEqualityComparer.Instance);
        internal IReadOnlySet<object> Expandable { get; init; } = new HashSet<object>(ReferenceEqualityComparer.Instance);
    }

    internal sealed record Ordering
    {
        internal IComparer<object>? Comparer { get; init; }
        internal SortDirection Direction { get; init; }
        internal bool Local { get; init; }
        internal bool RowOrder { get; init; }
        internal bool Changed { get; init; }
        internal TimeSpan Interval { get; init; }
        internal long? SettledAt { get; init; }
        internal bool Showing { get; init; }
    }

    internal sealed record Holding
    {
        internal bool Pointed { get; init; }
        internal object? PointedRow { get; init; }
        internal object? MenuRow { get; init; }
        internal required IReadOnlyDictionary<object, (bool Present, long Named)> Released { get; init; }
        internal TimeSpan Lifetime { get; init; }
        internal IReadOnlyList<Release> Releases { get; init; } = [];
    }

    internal sealed record Release(IReadOnlyList<object> Items, IReadOnlyList<object> Source,
        IReadOnlyList<object> Displayed, long Named)
    {
        internal bool WaitingOnly { get; init; }
    }

    internal sealed record Input(Rows Rows, Ordering Ordering, Holding Holding, IReadOnlyList<int> Realized);

    internal sealed record Prepared
    {
        internal required IReadOnlyList<object> Order { get; init; }
        internal required HashSet<object> Held { get; init; }
        internal required HashSet<object> SourceKeys { get; init; }
        internal required IReadOnlyDictionary<object, (bool Present, long Named)> Released { get; init; }
        internal required Dictionary<object, (int Index, int Sibling)> Positions { get; init; }
        internal required Changes Changes { get; init; }
        internal bool Settles { get; init; }
        internal TimeSpan? SettleAfter { get; init; }
        internal bool StructureChanged { get; init; }
        internal bool HasChildren { get; init; }
    }

    private readonly Input _input;
    private readonly IReadOnlyList<object> _view;
    private readonly ItemIdentity _identity;
    private readonly Dictionary<object, (bool Present, long Named)> _released;
    private readonly HashSet<object> _held;
    private readonly Hierarchy.Snapshot? _hierarchy;
    private readonly IReadOnlyDictionary<object, Hierarchy.Node>? _nodes;
    private long? _orderSettledAt;
    private TimeSpan? _settleAfter;

    internal Projection(Input input)
    {
        _input = input;
        _view = input.Rows.Displayed;
        _identity = input.Rows.Identity;
        _released = new(input.Holding.Released, _identity);
        _held = new(_identity);
        _hierarchy = input.Rows.Hierarchy;
        _nodes = _hierarchy?.Nodes.Values.ToDictionary(node => node.Item, ReferenceEqualityComparer.Instance);
        _orderSettledAt = input.Ordering.SettledAt;
    }

    private bool SortsLocally => _input.Ordering.Local;
    private bool SortsOrder => _input.Ordering.RowOrder;
    private bool UsesInterval => SortsLocally && !SortsOrder && _sortInterval > TimeSpan.Zero;
    private TimeSpan _sortInterval => _input.Ordering.Interval;
    private bool _settleShowing => _input.Ordering.Showing;
    private bool IsPointed => _input.Holding.Pointed;
    private object? _pointedRow => _input.Holding.PointedRow;
    private object? _menuRow => _input.Holding.MenuRow;
    private TimeSpan ReleaseLifetime => _input.Holding.Lifetime;

    internal Prepared Prepare()
    {
        var rows = _input.Rows;
        var source = _hierarchy is null ? rows.Source : Project(null);
        var unique = new HashSet<object>(_identity);
        foreach (var item in _hierarchy is null ? source : _nodes!.Keys)
        {
            if (rows.RowType is not null && !rows.RowType.IsInstanceOfType(item))
                throw new InvalidOperationException($"The source contains a row outside {rows.RowType}.");
            if (_identity.KeySelector is not null && _identity.KeySelector(item) is null)
                throw new InvalidOperationException("The schema key selector returned null.");
            if (!unique.Add(item))
                throw new InvalidOperationException("The source contains duplicate row identities.");
        }
        foreach (var release in _input.Holding.Releases)
        {
            var live = new HashSet<object>(release.Source, _identity);
            var shown = new HashSet<object>(release.Displayed, _identity);
            foreach (var item in release.Items)
            {
                bool present = live.Contains(item);
                bool displayed = shown.Contains(item);
                if (!release.WaitingOnly || present && !displayed)
                    _released[item] = (present == displayed ? !present : present, release.Named);
            }
        }
        bool structureChanged = _hierarchy is not null && !SameStructure();
        if (structureChanged)
            _orderSettledAt = null;
        (IReadOnlyList<object> order, IReadOnlySet<object> pinned, bool settles) = _input.Ordering.Changed
            ? ChangedOrder(source)
            : ViewOrder(source);
        var changes = new Changes(_view, _identity);
        changes.Prepare(order, _input.Realized, pinned);
        var comparer = _identity.KeySelector is null ? ReferenceEqualityComparer.Instance : _identity.KeyComparer;
        var positions = new Dictionary<object, (int Index, int Sibling)>(comparer);
        var counts = new Dictionary<object, int>(_identity);
        int roots = 0;
        for (int index = 0; index < order.Count; index++)
        {
            var item = order[index];
            var sibling = 0;
            if (_nodes is not null)
            {
                var parent = _nodes[item].Parent;
                sibling = parent is null ? ++roots : counts.GetValueOrDefault(parent) + 1;
                if (parent is not null)
                    counts[parent] = sibling;
            }
            positions.Add(KeyOf(item), (index, sibling));
        }
        return new Prepared
        {
            Order = order,
            Held = _held.Select(KeyOf).ToHashSet(comparer),
            SourceKeys = source.Select(KeyOf).ToHashSet(comparer),
            Released = _released,
            Positions = positions,
            Changes = changes,
            Settles = settles,
            SettleAfter = _settleAfter,
            StructureChanged = structureChanged,
            HasChildren = order.Any(rows.Expandable.Contains),
        };
    }

    private object KeyOf(object item) => _identity.KeySelector?.Invoke(item) ?? item;

    private bool SameStructure()
    {
        if (ReferenceEquals(_hierarchy, _input.Rows.PreviousHierarchy))
            return true;
        if (_input.Rows.PreviousHierarchy is not { } previous || _hierarchy is null)
            return false;
        if (previous.Nodes.Count != _nodes!.Count || !_hierarchy.Roots.SequenceEqual(previous.Roots, _identity))
            return false;
        var nodes = previous.Nodes.Values.ToDictionary(node => node.Item, _identity);
        return _nodes.Values.All(node => nodes.TryGetValue(node.Item, out var before)
            && _identity.Equals(node.Parent, before.Parent)
            && node.Children.SequenceEqual(before.Children, _identity));
    }

    private (IReadOnlyList<object> Order, IReadOnlySet<object> Pinned, bool Settles) ChangedOrder(IReadOnlyList<object> source)
    {
        var (_, held, released) = Hold(source, sortChanged: true);
        var order = SortedSnapshot(source);
        AcceptHold(held, released);
        return (order, held, true);
    }

    private IReadOnlyList<object> SortedSnapshot(IReadOnlyList<object> source)
    {
        if (_hierarchy is not null)
            return Project(_input.Ordering.Comparer);
        return Order(source, _input.Ordering.Comparer).ToArray();
    }

    private IEnumerable<object> Order(IReadOnlyList<object> rows, IComparer<object>? comparer) =>
        comparer is null ? rows
        : _input.Ordering.Direction == SortDirection.Ascending ? rows.OrderBy(item => item, comparer)
        : rows.OrderByDescending(item => item, comparer);

    private IReadOnlyList<object> Project(IComparer<object>? comparer)
    {
        List<object> visible = [];
        Stack<IEnumerator<object>> levels = new();
        levels.Push(Order(_hierarchy!.Roots, comparer).GetEnumerator());
        try
        {
            while (levels.TryPeek(out var level))
            {
                if (!level.MoveNext())
                {
                    levels.Pop().Dispose();
                    continue;
                }
                var item = level.Current;
                visible.Add(item);
                var children = _nodes![item].Children;
                if (children.Count > 0 && _input.Rows.Expanded.Contains(item))
                    levels.Push(Order(children, comparer).GetEnumerator());
            }
        }
        finally
        {
            foreach (var level in levels)
                level.Dispose();
        }
        return visible;
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
    /// Membership waits with position. WinUI's list animates no add or remove that comes within one
    /// second of its previous change (<c>fastmutationThreshold</c> in its
    /// <c>ModernCollectionBasePanel</c>), so rows that arrived or left one by one between settles
    /// would appear and vanish without their transition. Inside the interval the hold of
    /// section 5.3 therefore keeps a row that leaves and holds back a row that arrives, as it does
    /// while the rows are pointed at, and the settle applies them with the moves in one update.
    /// <see cref="Hold"/> names the changes that apply at once. When the rows do take their sorted
    /// places, the pointed row keeps its own (section 9).
    /// </para>
    /// </remarks>
    /// <returns>
    /// The order; the rows that keep their index, whose containers the reconcile keeps; and
    /// whether this publish is a settle.
    /// </returns>
    private (IReadOnlyList<object> Order, IReadOnlySet<object> Pinned, bool Settles) ViewOrder(
        IReadOnlyList<object> snapshot
    )
    {
        if (_hierarchy is not null && !SortsLocally)
            return (snapshot, _held, true);
        TimeSpan elapsed = _orderSettledAt is long settledAt
            ? Stopwatch.GetElapsedTime(settledAt)
            : TimeSpan.MaxValue;

        // Natural order belongs to the host, which reorders when it means to, and the row-order
        // column shows that same order, so holding it would delay the person's own reorder; an
        // empty view has no established order to preserve; and a zero interval is the host asking
        // for none of this. Until a frame shows the last settle, every publish is one, so the
        // changes of one turn land in one update.
        bool settles = !UsesInterval || _view.Count == 0 || _settleShowing
            || elapsed >= _sortInterval;
        var (shown, held, released) = Hold(snapshot, waits: !settles);
        if (settles)
        {
            return Finish(SortedSnapshot(shown), settled: true);
        }

        // The base sequence, not the sorted one: a kept update keeps the order the view already
        // has, so all it needs from the snapshot is which rows are in it, and sorting to answer a
        // question about membership is work thrown away. At the default interval against a host
        // that publishes about once a second, that is two publishes in three, and a name sort of
        // 2,002 rows is roughly 22,000 culture-aware string comparisons.
        //
        // A membership change the hold let through is never kept back, so it is also never
        // settled: KeptOrder returns null for one, and the sorted order is what the arriving row
        // needs anyway.
        if (KeptOrder(shown, held) is not List<object> kept)
        {
            return Finish(SortedSnapshot(shown), settled: true);
        }

        return Finish(kept, settled: false);

        (IReadOnlyList<object> Order, IReadOnlySet<object> Pinned, bool Settles) Finish(
            IReadOnlyList<object> order,
            bool settled
        )
        {
            var (pinnedOrder, pinned) = Pin(order, held);
            // A failed comparison must not consume a release or change held-row interactivity.
            AcceptHold(held, released);
            if (!settled)
                _settleAfter = _sortInterval - elapsed;
            return (pinnedOrder, pinned, settled);
        }
    }

    /// <summary>
    /// The rows the view already shows, held rows aside, in the order it already shows them, or
    /// null when <paramref name="shown"/> is not that same set of rows.
    /// </summary>
    /// <remarks>
    /// A kept order never merges membership: a snapshot that adds or removes a row is not kept, and
    /// the caller takes the sorted order for it, which puts the arrival where it belongs straight
    /// away. Only a snapshot of exactly the same rows can be kept, and keeping it is then a copy
    /// rather than a merge. While the rows are pointed at, and inside the settling interval, the
    /// hold has already deferred every membership change, so the rows are always the same.
    /// <para>
    /// This is also what keeps the cost bounded. An earlier version placed each arrival into the
    /// held order by scanning it, which is fine for a row or two and is not fine for a filter
    /// change: widening the original host's filter to every row brings about 900 rows back at
    /// once, and scanning for each of them is roughly 1.4 million comparer calls on an order that
    /// is not sorted anyway, so their positions would have been close to arbitrary. Leaving
    /// membership to the hold removes the merge, the cost and the arbitrary placement together.
    /// </para>
    /// </remarks>
    private List<object>? KeptOrder(IReadOnlyList<object> shown, IReadOnlySet<object> held)
    {
        if (shown.Count + held.Count != _view.Count)
        {
            return null;
        }

        // Always the snapshot's instances, never the view's: the host may have replaced a row with
        // an equal-identity instance, and keeping the old one would leave its container bound to an
        // object nothing updates any more.
        Dictionary<object, object> live = new(shown.Count, _identity);
        foreach (object row in shown)
        {
            live[row] = row;
        }

        List<object> order = new(shown.Count);
        foreach (object row in _view)
        {
            if (held.Contains(row))
            {
                continue;
            }

            if (!live.TryGetValue(row, out object? current))
            {
                return null;
            }

            order.Add(current);
        }

        return order;
    }

    /// <summary>
    /// The snapshot rows to show. While the rows are pointed at, or while the change
    /// <paramref name="waits"/> for the settle, that is only the rows already shown, and every
    /// shown row the snapshot no longer has becomes a held row.
    /// </summary>
    /// <remarks>
    /// An empty view takes its rows at once: nothing is on screen to be moved. So does a snapshot
    /// that carries a change named by <see cref="Release"/>, and, with the pointer away, a snapshot
    /// that shares no row with the view: a different list, or an emptied one, does not wait.
    /// </remarks>
    private (
        IReadOnlyList<object> Shown,
        HashSet<object> Held,
        IReadOnlyList<object> Released
    ) Hold(IReadOnlyList<object> snapshot, bool sortChanged = false, bool waits = false)
    {
        HashSet<object> held = new(_identity);
        bool holds = !sortChanged && _hierarchy is null && _view.Count > 0 && (IsPointed || waits);

        // An entry whose change did not come within its lifetime ends here, so it cannot let a
        // later background change through the hold.
        foreach (object row in _released
            .Where(entry => Stopwatch.GetElapsedTime(entry.Value.Named) > ReleaseLifetime)
            .Select(entry => entry.Key).ToList())
        {
            _released.Remove(row);
        }

        if (!holds && _released.Count == 0)
        {
            return (snapshot, held, Array.Empty<object>());
        }

        HashSet<object> live = new(snapshot, _identity);
        List<object> released = _released
            .Where(entry => live.Contains(entry.Key) == entry.Value.Present)
            .Select(entry => entry.Key)
            .ToList();
        if (released.Count > 0 || !holds)
        {
            return (snapshot, held, released);
        }

        HashSet<object> shown = new(_view, _identity);
        List<object> staying = snapshot.Where(shown.Contains).ToList();
        if (staying.Count == 0 && !IsPointed)
        {
            return (snapshot, held, released);
        }

        foreach (object row in _view)
        {
            if (!live.Contains(row))
            {
                held.Add(row);
            }
        }

        return (staying, held, released);
    }

    private void AcceptHold(IReadOnlySet<object> held, IReadOnlyList<object> released)
    {
        _held.Clear();
        _held.UnionWith(held);
        foreach (object row in released)
            _released.Remove(row);
    }

    /// <summary>
    /// Put every held row back at the index it has, and, under a sort that reorders, the pointed row
    /// and the row whose menu is open at theirs; every other row takes the next free place in
    /// <paramref name="order"/>. Also reports those rows, so the reconcile keeps their containers.
    /// </summary>
    /// <remarks>
    /// While the rows are pointed at, the hold keeps membership unchanged, so the result has exactly
    /// the view's count and every pinned index is a place in it.
    /// </remarks>
    private (IReadOnlyList<object> Order, IReadOnlySet<object> Pinned) Pin(
        IReadOnlyList<object> order,
        IReadOnlySet<object> held
    )
    {
        bool reorders = _hierarchy is null && SortsLocally && !SortsOrder;
        object? menu = reorders ? _menuRow : null;
        object? pointed = reorders ? _pointedRow : null;
        if (held.Count == 0 && menu is null && pointed is null)
        {
            return (order, held);
        }

        HashSet<object> pinned = new(_identity);
        object?[] slots = new object?[order.Count + held.Count];
        for (int i = 0; i < _view.Count && i < slots.Length; i++)
        {
            object row = _view[i];
            if (held.Contains(row))
            {
                slots[i] = row;
                pinned.Add(row);
            }
            else if (
                (_identity.Equals(row, menu) || _identity.Equals(row, pointed))
                && order.FirstOrDefault(candidate => _identity.Equals(candidate, row))
                    is { } current
            )
            {
                slots[i] = current;
                pinned.Add(row);
            }
        }

        if (pinned.Count == 0)
        {
            return (order, pinned);
        }

        List<object> result = new(slots.Length);
        int next = 0;
        foreach (object? slot in slots)
        {
            if (slot is not null)
            {
                result.Add(slot);
                continue;
            }

            while (pinned.Contains(order[next]))
            {
                next++;
            }

            result.Add(order[next++]);
        }

        return (result, pinned);
    }
}
