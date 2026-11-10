using System.Buffers;
using System.Diagnostics;

namespace Syno.TableView.Body;

/// <summary>
/// Prepares one update of the table's display projection from captured values and the rows'
/// <see cref="Slots"/>, without accessing WinUI.
/// </summary>
internal sealed class Projection
{
    internal sealed record Rows
    {
        internal required IReadOnlyList<object> Source { get; init; }
        internal required ItemIdentity Identity { get; init; }
        internal Type? RowType { get; init; }
        internal Hierarchy.Snapshot? Hierarchy { get; init; }
        internal Hierarchy.Snapshot? PreviousHierarchy { get; init; }
        /// <summary>The set a table without a hierarchy passes; nothing writes to it.</summary>
        internal static readonly IReadOnlySet<object> None = new HashSet<object>(ReferenceEqualityComparer.Instance);

        internal IReadOnlySet<object> Expanded { get; init; } = None;
        internal IReadOnlySet<object> Expandable { get; init; } = None;
    }

    internal sealed record Ordering
    {
        /// <summary>Creates the sort column's key store; null when the table does not sort locally.</summary>
        internal Func<SortKeys>? Keys { get; init; }

        /// <summary>The keys a sort or layout request read on the UI thread for that column.</summary>
        internal SortKeys? Read { get; init; }

        internal SortDirection Direction { get; init; }
        internal bool Local { get; init; }
        internal bool Reorders { get; init; }
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

        /// <summary>The keys of the rows the view holds now.</summary>
        internal required HashSet<object> Held { get; init; }

        internal required IReadOnlyDictionary<object, (bool Present, long Named)> Released { get; init; }
        internal TimeSpan Lifetime { get; init; }
        internal IReadOnlyList<Release> Releases { get; init; } = [];

        /// <summary>
        /// The source carries a change the person caused (<c>Table.Withdraw</c>), so it applies
        /// whole.
        /// </summary>
        internal bool Caused { get; init; }
    }

    /// <summary>
    /// Rows <c>Table.Release</c> named, each with whether the view showed it then, and the source
    /// as it was then.
    /// </summary>
    internal sealed record Release(IReadOnlyList<(object Row, bool Shown)> Items, IReadOnlyList<object> Source, long Named)
    {
        internal bool WaitingOnly { get; init; }
    }

    /// <summary>
    /// The rows <c>Table.RefreshView</c> named as changed since a preparation last took them, or,
    /// with <see cref="All"/>, a refresh that named none, so every row may have changed.
    /// </summary>
    internal sealed record Refresh(IReadOnlyList<object> Rows, bool All);

    internal sealed record Input(Rows Rows, Ordering Ordering, Holding Holding, Changes.Screen Screen)
    {
        internal Refresh Refresh { get; init; } = new([], false);
    }

    internal sealed record Prepared
    {
        internal required HashSet<object> Held { get; init; }
        internal required IReadOnlyDictionary<object, (bool Present, long Named)> Released { get; init; }
        internal required Changes Changes { get; init; }
        internal bool Settles { get; init; }
        internal TimeSpan? SettleAfter { get; init; }
        internal bool StructureChanged { get; init; }
        internal bool HasChildren { get; init; }
    }

    private static readonly HashSet<int> NoPins = [];

    private readonly Input _input;
    private readonly Slots _slots;
    private readonly ItemIdentity _identity;
    private readonly Comparison<int> _compare;
    private readonly Dictionary<object, (bool Present, long Named)> _released;
    private HashSet<object> _held;
    private readonly Hierarchy.Snapshot? _hierarchy;
    private readonly IReadOnlyDictionary<object, Hierarchy.Node>? _nodes;
    private IReadOnlyList<object> _source = [];
    private long? _orderSettledAt;
    private TimeSpan? _settleAfter;
    private CancellationToken _cancellation;

    internal Projection(Input input, Slots slots)
    {
        _input = input;
        _slots = slots;
        _identity = input.Rows.Identity;
        _compare = Compare;
        _hierarchy = input.Rows.Hierarchy;
        _released = new(input.Holding.Released, _identity);
        _held = input.Holding.Held;
        _nodes = _hierarchy?.Nodes.Values.ToDictionary(node => node.Item, ReferenceEqualityComparer.Instance);
        _orderSettledAt = input.Ordering.SettledAt;
    }

    private bool SortsLocally => _input.Ordering.Local;
    private TimeSpan _sortInterval => _input.Ordering.Interval;
    private bool _settleShowing => _input.Ordering.Showing;
    private bool IsPointed => _input.Holding.Pointed;
    private object? _pointedRow => _input.Holding.PointedRow;
    private object? _menuRow => _input.Holding.MenuRow;
    private TimeSpan ReleaseLifetime => _input.Holding.Lifetime;

    /// <param name="cancellation">
    /// Cancelled by a newer request, which makes this result obsolete. A cancelled preparation
    /// leaves the slots as a discarded one does.
    /// </param>
    internal Prepared Prepare(CancellationToken cancellation)
    {
        _cancellation = cancellation;
        var rows = _input.Rows;
        _source = _hierarchy is null ? rows.Source : Project(null);
        _slots.Begin();
        _slots.Number(_source, rows.RowType, cancellation);
        cancellation.ThrowIfCancellationRequested();
        _slots.SortBy(_input.Ordering.Keys, _input.Ordering.Read, _input.Ordering.Direction);
        if (_input.Refresh.All)
            _slots.NameEvery();
        else
            _slots.Name(_input.Refresh.Rows);
        AddReleases();
        bool structureChanged = _hierarchy is not null && !SameStructure();
        if (structureChanged)
            _orderSettledAt = null;
        (bool arranged, IReadOnlySet<int> pinned, bool settles) = _input.Ordering.Changed
            ? ChangedOrder()
            : ViewOrder();
        cancellation.ThrowIfCancellationRequested();
        var changes = new Changes();
        // A kept order of the source the view already shows has nothing to change.
        if (arranged || !_slots.Shows(_source))
        {
            changes.Prepare(_slots.View, _slots.Shown, arranged ? _slots.Order : _slots.Shown,
                _slots.Targets, _slots.Row, _input.Screen, pinned);
        }
        _slots.Remap();
        return new Prepared
        {
            Held = _held,
            Released = _released,
            Changes = changes,
            Settles = settles,
            SettleAfter = _settleAfter,
            StructureChanged = structureChanged,
            HasChildren = _hierarchy is not null && HasChildren(arranged ? _slots.Order : _slots.Shown),
        };
    }

    /// <summary>
    /// Record what each new <see cref="Release"/> expects: a row the view showed as the source had
    /// it is to leave, a row it did not show is to arrive, and a held or waiting row is to end as
    /// the source had it.
    /// </summary>
    private void AddReleases()
    {
        foreach (var release in _input.Holding.Releases)
        {
            HashSet<object>? live = ReferenceEquals(release.Source, _source) ? null : new(release.Source, _identity);
            foreach ((object row, bool shown) in release.Items)
            {
                bool present = live?.Contains(row) ?? InSource(row);
                if (!release.WaitingOnly || present && !shown)
                    _released[row] = (present == shown ? !present : present, release.Named);
            }
        }
    }

    private object KeyOf(object item) => _identity.KeySelector?.Invoke(item) ?? item;

    private bool InSource(object row) => _slots.SlotOf(row) is var slot && slot >= 0 && _slots.InSource(slot);

    private bool HasChildren(ReadOnlySpan<int> order)
    {
        foreach (int slot in order)
        {
            if (_input.Rows.Expandable.Contains(_slots.Row(slot)))
                return true;
        }
        return false;
    }

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

    private (bool Arranged, IReadOnlySet<int> Pinned, bool Settles) ChangedOrder()
    {
        var (_, held, released) = Hold(sortChanged: true);
        Arrange(Sorted(staying: false, full: true));
        AcceptHold(held, released);
        return (true, NoPins, true);
    }

    /// <summary>The slots' order buffer holds <paramref name="count"/> rows to show; make them the prepared order.</summary>
    private void Arrange(int count)
    {
        _slots.Arrange(count, sorted: _slots.Keys is not null);
        if (_nodes is null)
            return;
        var counts = new Dictionary<object, int>(_identity);
        int roots = 0;
        foreach (int slot in _slots.Order)
        {
            var parent = _nodes[_slots.Row(slot)].Parent;
            int sibling = parent is null ? ++roots : counts.GetValueOrDefault(parent) + 1;
            if (parent is not null)
                counts[parent] = sibling;
            _slots.SetSibling(slot, sibling);
        }
    }

    /// <summary>
    /// Write the rows to show into the slots' order buffer, sorted when the table sorts, and
    /// return how many there are: every row of the source, or, when <paramref name="staying"/>,
    /// only the rows the view already shows.
    /// </summary>
    /// <remarks>
    /// A sort reads a row's key only when the row is not in the view or the host named it as
    /// changed (<see cref="Slots.IsStale"/>), because every other row's key is the one the last
    /// sort placed it by. So a sort that can trust the view's order merges: the rows that need a
    /// place are sorted on their own and each is placed into the view's order by binary search,
    /// which compares about <c>C log N</c> times for C such rows instead of <c>N log N</c>.
    /// </remarks>
    private int Sorted(bool staying, bool full)
    {
        if (_hierarchy is not null)
            return Tree();
        ReadOnlySpan<int> source = _slots.Source;
        int[] order = _slots.OrderBuffer(source.Length);
        int count = 0;
        if (_slots.Keys is null)
        {
            foreach (int slot in source)
            {
                if (!staying || _slots.InView(slot))
                    order[count++] = slot;
            }
            return count;
        }
        if (!full && !_slots.Unsorted && _slots.Shown.Count > 0)
            return Merge(staying, order);
        foreach (int slot in source)
        {
            if (staying && !_slots.InView(slot))
                continue;
            // Reading a key calls the host's selector, the longest part of a full sort.
            if ((count & 4095) == 0)
            {
                _cancellation.ThrowIfCancellationRequested();
            }

            ReadKey(slot);
            order[count++] = slot;
        }
        order.AsSpan(0, count).Sort(_compare);
        return count;
    }

    private int Merge(bool staying, int[] order)
    {
        int count = 0;
        foreach (int slot in _slots.Shown)
        {
            if (_slots.InSource(slot) && !_slots.IsStale(slot))
                order[count++] = slot;
        }
        ReadOnlySpan<int> source = _slots.Source;
        int[] placing = ArrayPool<int>.Shared.Rent(source.Length);
        try
        {
            int placed = 0;
            foreach (int slot in source)
            {
                if (_slots.InView(slot) ? !_slots.IsStale(slot) : staying)
                    continue;
                _slots.Keys!.Read(slot, _slots.Row(slot));
                placing[placed++] = slot;
            }
            placing.AsSpan(0, placed).Sort(_compare);

            // From the back, so each row of the view's order moves once.
            int end = count;
            int write = count + placed;
            for (int i = placed - 1; i >= 0; i--)
            {
                int slot = placing[i];
                int low = 0;
                int high = end;
                while (low < high)
                {
                    int middle = (low + high) >>> 1;
                    if (Compare(order[middle], slot) > 0)
                        high = middle;
                    else
                        low = middle + 1;
                }
                write -= end - low;
                Array.Copy(order, low, order, write, end - low);
                order[--write] = slot;
                end = low;
            }
            return count + placed;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(placing);
        }
    }

    /// <summary>
    /// A hierarchy's visible rows, ordered within each parent. The tree is captured again for
    /// every update, so it is projected again from that capture.
    /// </summary>
    private int Tree()
    {
        ReadOnlySpan<int> source = _slots.Source;
        int[] order = _slots.OrderBuffer(source.Length);
        if (_slots.Keys is null)
        {
            source.CopyTo(order);
            return source.Length;
        }
        var slots = new Dictionary<object, int>(source.Length, ReferenceEqualityComparer.Instance);
        for (int position = 0; position < source.Length; position++)
        {
            slots[_source[position]] = source[position];
            ReadKey(source[position]);
        }
        var sorted = Project(Comparer<object>.Create((left, right) => Compare(slots[left], slots[right])));
        for (int index = 0; index < sorted.Count; index++)
            order[index] = slots[sorted[index]];
        return sorted.Count;
    }

    private void ReadKey(int slot)
    {
        if (_slots.IsStale(slot) || !_slots.InView(slot))
            _slots.Keys!.Read(slot, _slots.Row(slot));
    }

    /// <summary>
    /// The sort column's order, and the source's order between rows whose keys are equal, so the
    /// result is the one a stable sort of the source gives. A descending sort swaps the keys
    /// rather than negating the result, which would leave <see cref="int.MinValue"/> negative.
    /// </summary>
    private int Compare(int left, int right)
    {
        int order = _input.Ordering.Direction == SortDirection.Descending
            ? _slots.Keys!.Compare(right, left)
            : _slots.Keys!.Compare(left, right);
        return order != 0 ? order : _slots.SourceAt(left).CompareTo(_slots.SourceAt(right));
    }

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

    private static IEnumerable<object> Order(IReadOnlyList<object> rows, IComparer<object>? comparer) =>
        comparer is null ? rows : rows.OrderBy(item => item, comparer);

    /// <summary>
    /// The order the rows are actually shown in: the sorted source, except that rows already on
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
    /// <para>
    /// Inside the interval, a refresh of the source the view already shows changes nothing: the
    /// rows, their instances and the held rows are those of the last update. It only records the
    /// rows the host named, so it costs work in proportion to them.
    /// </para>
    /// </remarks>
    /// <returns>
    /// Whether the slots hold a new order; the rows that keep their index, whose containers the
    /// reconcile keeps; and whether this update is a settle.
    /// </returns>
    private (bool Arranged, IReadOnlySet<int> Pinned, bool Settles) ViewOrder()
    {
        if (_hierarchy is not null && !SortsLocally)
        {
            Arrange(Sorted(staying: false, full: false));
            return (true, NoPins, true);
        }
        TimeSpan elapsed = _orderSettledAt is long settledAt
            ? Stopwatch.GetElapsedTime(settledAt)
            : TimeSpan.MaxValue;

        // Natural order belongs to the host, which reorders when it means to, and the row-order
        // column shows that same order, so holding it would delay the person's own reorder; an
        // empty view has no established order to preserve; and a zero interval is the host asking
        // for none of this. Until a frame shows the last settle, every update is one, so the
        // changes of one turn land in one update.
        bool settles = _sortInterval <= TimeSpan.Zero || _slots.Shown.Count == 0 || _settleShowing
            || elapsed >= _sortInterval;
        if (!settles && _hierarchy is null && _input.Holding.Releases.Count == 0 && _slots.Shows(_source))
        {
            _settleAfter = _sortInterval - elapsed;
            return (false, NoPins, false);
        }
        var (staying, held, released) = Hold(waits: !settles);

        // A kept update keeps the order the view already has, so all it needs from the source is
        // which rows are in it, and sorting to answer a question about membership is work thrown
        // away.
        //
        // A membership change the hold let through is never kept back, so it is also never
        // settled: Kept refuses it, and the sorted order is what the arriving row needs anyway.
        bool keeps = !settles && Kept(staying);
        IReadOnlySet<int> pinned = NoPins;
        if (!keeps)
            Arrange(Pin(Sorted(staying, full: false), staying, held, out pinned));
        // A failed comparison must not consume a release or change held-row interactivity.
        AcceptHold(held, released);
        if (keeps)
            _settleAfter = _sortInterval - elapsed;
        return (!keeps, pinned, !keeps);
    }

    /// <summary>
    /// The view's order can stay: the rows it shows, held rows aside, are exactly the rows to
    /// show. Keeping it then costs nothing but the instance swaps the reconcile makes.
    /// </summary>
    /// <remarks>
    /// A kept order never merges membership: a source that adds or removes a row is not kept, and
    /// the caller takes the sorted order for it, which puts the arrival where it belongs straight
    /// away. While the rows are pointed at, and inside the settling interval, the hold has already
    /// deferred every membership change, so the rows are always the same.
    /// <para>
    /// This is also what keeps the cost bounded. An earlier version placed each arrival into the
    /// held order by scanning it, which is fine for a row or two and is not fine for a filter
    /// change: widening the original host's filter to every row brings about 900 rows back at
    /// once, and scanning for each of them is roughly 1.4 million comparer calls on an order that
    /// is not sorted anyway, so their positions would have been close to arbitrary. Leaving
    /// membership to the hold removes the merge, the cost and the arbitrary placement together.
    /// </para>
    /// </remarks>
    private bool Kept(bool staying)
    {
        if (staying)
            return true;
        if (_slots.Source.Length != _slots.Shown.Count)
            return false;
        foreach (int slot in _slots.Shown)
        {
            if (!_slots.InSource(slot))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether only the rows the view already shows are shown. While the rows are pointed at, or
    /// while the change <paramref name="waits"/> for the settle, that is so, and every shown row
    /// the source no longer has becomes a held row.
    /// </summary>
    /// <remarks>
    /// An empty view takes its rows at once: nothing is on screen to be moved. So does a source
    /// that carries a change named by <see cref="Release"/>, the source a host set after
    /// withdrawing one (<see cref="Holding.Caused"/>), and, with the pointer away, a source
    /// that shares no row with the view: a different list, or an emptied one, does not wait.
    /// </remarks>
    private (bool Staying, List<int> Held, List<object> Released) Hold(bool sortChanged = false, bool waits = false)
    {
        bool holds = !sortChanged && !_input.Holding.Caused && _hierarchy is null
            && _slots.Shown.Count > 0 && (IsPointed || waits);

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
            return (false, [], []);
        }

        List<object> released = _released
            .Where(entry => InSource(entry.Key) == entry.Value.Present)
            .Select(entry => entry.Key)
            .ToList();
        if (released.Count > 0 || !holds)
        {
            return (false, [], released);
        }

        List<int> held = [];
        int staying = 0;
        foreach (int slot in _slots.Shown)
        {
            if (_slots.InSource(slot))
                staying++;
            else
                held.Add(slot);
        }

        if (staying == 0 && !IsPointed)
        {
            return (false, [], released);
        }

        return (true, held, released);
    }

    private void AcceptHold(List<int> held, List<object> released)
    {
        _held = new HashSet<object>(held.Count,
            _identity.KeySelector is null ? ReferenceEqualityComparer.Instance : _identity.KeyComparer);
        foreach (int slot in held)
            _held.Add(KeyOf(_slots.Row(slot)));
        foreach (object row in released)
            _released.Remove(row);
    }

    /// <summary>
    /// Put every held row back at the index it has, and, under a sort that reorders, the pointed row
    /// and the row whose menu is open at theirs; every other row takes the next free place in the
    /// <paramref name="count"/> sorted rows of the order buffer. Also reports those rows, so the
    /// reconcile keeps their containers, and returns the order's new length.
    /// </summary>
    /// <remarks>
    /// While the rows are pointed at, the hold keeps membership unchanged, so the result has exactly
    /// the view's count and every pinned index is a place in it.
    /// </remarks>
    private int Pin(int count, bool staying, List<int> held, out IReadOnlySet<int> pinned)
    {
        pinned = NoPins;
        bool reorders = _hierarchy is null && _input.Ordering.Reorders;
        int menu = reorders ? _slots.SlotOf(_menuRow) : -1;
        int pointed = reorders ? _slots.SlotOf(_pointedRow) : -1;
        if (held.Count == 0 && menu < 0 && pointed < 0)
        {
            return count;
        }

        ReadOnlySpan<int> shown = _slots.Shown;
        int length = count + held.Count;
        int[] at = ArrayPool<int>.Shared.Rent(length);
        int[] sorted = ArrayPool<int>.Shared.Rent(count);
        try
        {
            HashSet<int> pins = [];
            for (int i = 0; i < length; i++)
            {
                at[i] = -1;
                if (i >= shown.Length)
                    continue;
                int slot = shown[i];
                if (staying && !_slots.InSource(slot)
                    || (slot == menu || slot == pointed) && _slots.InSource(slot))
                {
                    at[i] = slot;
                    pins.Add(slot);
                }
            }

            if (pins.Count == 0)
            {
                return count;
            }

            Array.Copy(_slots.OrderBuffer(count), sorted, count);
            int[] order = _slots.OrderBuffer(length);
            int next = 0;
            for (int i = 0; i < length; i++)
            {
                if (at[i] >= 0)
                {
                    order[i] = at[i];
                    continue;
                }

                while (pins.Contains(sorted[next]))
                {
                    next++;
                }

                order[i] = sorted[next++];
            }

            foreach (int slot in pins)
            {
                if (slot == menu || slot == pointed)
                    _slots.Pin(slot);
            }

            pinned = pins;
            return length;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(at);
            ArrayPool<int>.Shared.Return(sorted);
        }
    }
}
