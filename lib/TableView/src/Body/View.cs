using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation.Collections;

namespace Syno.TableView.Body;

/// <summary>
/// The private view handed to the row surface. A source snapshot is applied in place, as
/// removals, insertions and replacements matched by section 5 identity, so a row that did not
/// change keeps its container and the list animates only what moved. The collection exposes no
/// <c>Move</c> operation. Never <c>Clear</c>: see <see cref="ClearItems"/>. The view also answers
/// the list's selection questions from the table's selection, so the list keeps none of its own:
/// see <see cref="IsSelected"/>.
/// </summary>
internal sealed class View : Collection<object>, IObservableVector<object>, ISelectionInfo
{
    private readonly Func<object, bool> _isSelected;
    private readonly Func<IReadOnlyCollection<int>> _selectedIndices;
    private IIdentity _identity = null!;
    private PositionIndex _candidate = null!;
    private bool _reconciling;
    private long _inserts;
    private long _removals;
    // The producer's version of the order the view shows, or null when none names it.
    private object? _shown;
    private readonly List<int> _rebound = new();

    /// <summary>
    /// How the view differs from an order: view indices that leave, ascending; order indices
    /// that arrive, ascending; and (view index, order index) of rows that stay with a new
    /// instance. A row named as moved is both a leaving and an arriving row; an unnamed reorder
    /// names no rows and is left to the run script.
    /// </summary>
    internal sealed record Difference(IReadOnlyList<int> Leaving, IReadOnlyList<int> Arriving,
        IReadOnlyList<(int Before, int After)> Replacing, RowMoves Moves = RowMoves.None)
    {
        internal static readonly Difference None = new([], [], []);
        internal bool IsEmpty =>
            Moves == RowMoves.None && Leaving.Count + Arriving.Count + Replacing.Count == 0;
    }

    /// <summary>
    /// What the producer of an order knows about it: which version of its own order it is, and how
    /// it differs from the version before.
    /// </summary>
    /// <param name="Version">
    /// Names this order. Null when the order is not the producer's own, as when pinned rows stand
    /// in it, so no later difference can start from it.
    /// </param>
    /// <param name="Predecessor">The version <paramref name="Difference"/> starts from.</param>
    /// <param name="Difference">
    /// How this order differs from its predecessor, or null when the producer does not know.
    /// </param>
    internal readonly record struct Origin(object? Version, object? Predecessor = null,
        Difference? Difference = null)
    {
        /// <summary>
        /// Whether <see cref="Difference"/> starts from the view as it stands rather than from
        /// <see cref="Predecessor"/>.
        /// </summary>
        internal bool StartsFromView { get; private init; }

        /// <summary>
        /// An order the producer made from the view as it stands, which differs from it by
        /// <paramref name="difference"/>. It names no version.
        /// </summary>
        internal static Origin FromView(Difference difference) =>
            new(null, null, difference) { StartsFromView = true };
    }

    /// <summary>
    /// The view positions where the last <see cref="Reconcile"/> put a new instance of a row
    /// whose realized container still shows the old one.
    /// </summary>
    internal IReadOnlyList<int> Rebound => _rebound;

    /// <remarks>The table installs the identity before first use (<c>SetIdentity</c>).</remarks>
    internal View(Func<object, bool> isSelected, Func<IReadOnlyCollection<int>> selectedIndices)
        : base(new GapBuffer())
    {
        _isSelected = isSelected;
        _selectedIndices = selectedIndices;
    }

    internal PositionIndex Positions { get; private set; } = null!;
    internal long Version { get; private set; }

    internal int IndexOfIdentity(object? item)
    {
        if (item is null)
        {
            return -1;
        }
        if (!_reconciling)
        {
            return Positions.IndexOf(item);
        }
        // Native container preparation can query identity between the run script's individual
        // vector changes, while rows stand neither where they stood nor where they will.
        int index = Positions.IndexOf(item);
        if (Matches(index))
        {
            return index;
        }
        index = _candidate.IndexOf(item);
        if (Matches(index))
        {
            return index;
        }
        for (index = 0; index < Count; index++)
        {
            if (Matches(index))
            {
                return index;
            }
        }
        return -1;

        bool Matches(int at) => at >= 0 && at < Count && _identity.IsSame(this[at], item);
    }

    internal void SetIdentity(IIdentity identity)
    {
        _identity = identity;
        _shown = null;
        Positions = new(identity, () => new(_inserts, _removals));
        _candidate = new(identity, () => new(_inserts, _removals));
        Positions.Build(this);
    }

    // Native vector events avoid marshaling a .NET collection event and its item lists per row.
    public event VectorChangedEventHandler<object>? VectorChanged;

    protected override void InsertItem(int index, object item)
    {
        base.InsertItem(index, item);
        _inserts++;
        Version++;
        VectorChanged?.Invoke(this, new VectorChange(CollectionChange.ItemInserted, index));
    }

    protected override void RemoveItem(int index)
    {
        base.RemoveItem(index);
        _removals++;
        Version++;
        VectorChanged?.Invoke(this, new VectorChange(CollectionChange.ItemRemoved, index));
    }

    protected override void SetItem(int index, object item)
    {
        base.SetItem(index, item);
        Version++;
        VectorChanged?.Invoke(this, new VectorChange(CollectionChange.ItemChanged, index));
    }

    private sealed class VectorChange(CollectionChange change, int index) : IVectorChangedEventArgs
    {
        public CollectionChange CollectionChange => change;
        public uint Index => (uint)index;
    }

    // ------------------------------------------------------------------ selection

    /// <summary>
    /// The list asks this when it builds a container and after every range call, so a container is
    /// right as built and nothing is re-applied after a reconcile.
    /// </summary>
    /// <remarks>
    /// A list that keeps a selection of its own drops a row from it on the row's removal, and a
    /// reorder is a removal, so the table had to clear the list's selection and apply its own
    /// again after every reconcile. That re-apply made the list scroll to the first selected row
    /// with a forced layout in the reconcile's own tick, and the rows that scroll built took the
    /// insert animation: blank for its first 300 ms.
    /// </remarks>
    public bool IsSelected(int index) => index >= 0 && index < Count && _isSelected(this[index]);

    /// <summary>
    /// Called by the list for its own gestures, a press, a tap, Space or Ctrl+A on a container,
    /// and by the table's own range calls. The table arbitrates every gesture itself and has
    /// applied its own changes before it calls, so nothing changes here; the list then re-reads
    /// every realized row through <see cref="IsSelected"/>.
    /// </summary>
    public void SelectRange(ItemIndexRange itemIndexRange) { }

    /// <inheritdoc cref="SelectRange"/>
    public void DeselectRange(ItemIndexRange itemIndexRange) { }

    public IReadOnlyList<ItemIndexRange> GetSelectedRanges()
    {
        List<ItemIndexRange> ranges = new();
        int start = -1;
        int last = -1;
        foreach (int index in _selectedIndices())
        {
            if (start < 0)
            {
                start = index;
            }
            else if (index != last + 1)
            {
                ranges.Add(new ItemIndexRange(start, (uint)(last - start + 1)));
                start = index;
            }
            last = index;
        }
        if (start >= 0)
        {
            ranges.Add(new ItemIndexRange(start, (uint)(last - start + 1)));
        }
        return ranges;
    }

    /// <summary>How the view differs from <paramref name="order"/>.</summary>
    /// <remarks>
    /// The producer's difference is taken when the view shows the version it starts from, so a
    /// publish costs only its own rows. Otherwise the view walks itself and the order side by
    /// side: it compares references and looks up the view index only where they differ, so an
    /// unchanged view costs one comparison per row. A row the order has and the view does not
    /// arrives; view rows passed over to reach the next shared row leave. A shared row found
    /// before the walk's place in the view means shared rows changed their relative order.
    /// </remarks>
    internal Difference Compare(IReadOnlyList<object> order, Origin origin)
    {
        if (origin.Difference is { } stated && (origin.StartsFromView
            || origin.Predecessor is not null && ReferenceEquals(origin.Predecessor, _shown)))
        {
            return stated;
        }
        List<int> leaving = new();
        List<int> arriving = new();
        List<(int, int)> replacing = new();
        int at = 0;
        for (int next = 0; next < order.Count; next++)
        {
            object row = order[next];
            if (at < Count && ReferenceEquals(this[at], row))
            {
                at++;
                continue;
            }
            int index = Positions.IndexOf(row);
            if (index < 0)
            {
                arriving.Add(next);
            }
            else if (index < at)
            {
                return new([], [], [], RowMoves.Unnamed);
            }
            else
            {
                for (; at < index; at++)
                {
                    leaving.Add(at);
                }
                if (!ReferenceEquals(this[index], row))
                {
                    replacing.Add((index, next));
                }
                at++;
            }
        }
        for (; at < Count; at++)
        {
            leaving.Add(at);
        }
        return new(leaving, arriving, replacing);
    }

    /// <summary>
    /// Record that the view shows the producer's <paramref name="version"/>, because
    /// <see cref="Compare"/> found no difference from it.
    /// </summary>
    internal void RecordShown(object? version) => _shown = version;

    /// <summary>
    /// Bring the view to <paramref name="order"/>, which differs from it by
    /// <paramref name="difference"/>.
    /// </summary>
    /// <remarks>
    /// Per-row notifications preserve the list's native transitions and unchanged containers;
    /// a reset drops every container. When the difference names every row it changes, by no
    /// more rows than <paramref name="realized"/> holds, the leaving rows are removed highest
    /// index first and the arriving rows inserted lowest first. Every row that keeps its place
    /// keeps its container, and a row the difference names as moved is removed and inserted
    /// again. Otherwise the run script below runs.
    /// <para>
    /// A new instance of a row that stays takes its place without a notification, in both
    /// paths, because the list answers a replaced item by generating a new container, which
    /// fades in as if the row had arrived. The positions whose container showed the old
    /// instance are listed in <see cref="Rebound"/> for the table to rebind.
    /// </para>
    /// <para>
    /// The table suppresses the list's transitions for a disjoint visible-window replacement,
    /// whose native delete and add delay would otherwise leave the panel empty.
    /// </para>
    /// <para>
    /// The run script: rows leaving realized runs go first, highest index first. Then every
    /// position outside those runs, set without a notification. Last, what each run is missing,
    /// ascending. Removing before placing and placing before inserting is what keeps a row from
    /// being in the view twice at any moment, which the list is never asked to make sense of. A
    /// growth is inserted after the last run, so it cannot shift a run's containers. A shrink
    /// trims the tail, so a run row that stays past the new count is removed and inserted again.
    /// </para>
    /// <para>
    /// The list realizes only the rows on screen, a small cache around them, and whatever it has
    /// pinned. For every other position it holds no container and no row — only a count — and it
    /// reads the row when it realizes the position. So it is told about a reorder only where it has
    /// a container, which is 20 to 60 positions against 2,002 rows here. Telling it about the rest
    /// was the waste: a sort once raised about 4,000 notifications, and the original host's
    /// diagnostics section N priced one at between 56 and 113 microseconds including its layout.
    /// Both counts came from that host's own diagnostics harness, which has been deleted, so
    /// neither can be reproduced as written; what survives is the shape of the waste this reconcile
    /// removes.
    /// </para>
    /// <para>
    /// A failure part-way leaves the view as it stands, indexed again, so the next publish compares
    /// against the view as it really is.
    /// </para>
    /// </remarks>
    /// <param name="shown">
    /// The producer's version of <paramref name="order"/>, if any, which the view shows once this
    /// succeeds.
    /// </param>
    /// <param name="pinned">
    /// Rows that stand at the same index in the view and the order and must keep their
    /// containers, so they do not animate out and back in under the pointer.
    /// </param>
    internal void Reconcile(IReadOnlyList<object> order, Difference difference, object? shown,
        IReadOnlyList<int> realized, IdentitySet pinned)
    {
        _rebound.Clear();
        _shown = null;
        try
        {
            if (difference.Moves == RowMoves.Unnamed
                || difference.Leaving.Count + difference.Arriving.Count > realized.Count)
            {
                RunScript(order, realized, pinned);
            }
            else
            {
                ApplyRows(order, difference, realized);
            }
            Version++;
            _shown = shown;
        }
        catch
        {
            _reconciling = false;
            Positions.Build(this);
            Version++;
            throw;
        }
    }

    private void ApplyRows(IReadOnlyList<object> order, Difference difference, IReadOnlyList<int> realized)
    {
        for (int index = difference.Leaving.Count - 1; index >= 0; index--)
        {
            int at = difference.Leaving[index];
            Positions.Remove(this[at]);
            RemoveAt(at);
        }
        foreach (int at in difference.Arriving)
        {
            Positions.Insert(order[at], at);
            Insert(at, order[at]);
        }
        foreach (var (before, after) in difference.Replacing)
        {
            Positions.Insert(order[after], after);
            Items[after] = order[after];
            if (realized.Contains(before))
            {
                _rebound.Add(after);
            }
        }
    }

    private void RunScript(IReadOnlyList<object> order, IReadOnlyList<int> realized, IdentitySet pinned)
    {
        _candidate.Build(order);
        _reconciling = true;
        List<int> held = new(realized);
        held.Sort();

        List<(int Start, int End)> runs = Runs(held);
        int Target(object item) => _candidate.IndexOf(item);
        List<int> removed = RemoveFromRuns(Target, runs, pinned);
        runs = runs.Where(run => run.Start < order.Count)
            .Select(run => (run.Start, Math.Min(run.End, order.Count))).ToList();
        PlaceQuietly(order, runs, removed);
        InsertIntoRuns(order, Target, runs);

        (Positions, _candidate) = (_candidate, Positions);
        Positions.Bind(this);
        _reconciling = false;
    }

    /// <summary>The realized indices as the runs of consecutive positions they form.</summary>
    private static List<(int Start, int End)> Runs(List<int> held)
    {
        List<(int Start, int End)> runs = new();
        int start = -1;
        int previous = -2;

        foreach (int index in held)
        {
            if (index < 0)
            {
                continue;
            }

            if (index != previous + 1)
            {
                if (start >= 0)
                {
                    runs.Add((start, previous + 1));
                }

                start = index;
            }

            previous = index;
        }

        if (start >= 0)
        {
            runs.Add((start, previous + 1));
        }

        return runs;
    }

    /// <summary>
    /// Every row of every run that is not staying, highest index first and highest run first, so a
    /// run's removals never move the run below it. Reports the indices it removed, in order.
    /// </summary>
    /// <remarks>
    /// Removing from all the runs before anything is placed or inserted is what keeps today's
    /// property that no row is in the view twice at any moment. Taking one run at a time, whole,
    /// would hold a duplicate of every row arriving in a run from off screen until its old home was
    /// overwritten, and a focused row pinned far from the viewport makes a second run ordinary.
    /// </remarks>
    private List<int> RemoveFromRuns(
        Func<object, int> target,
        List<(int Start, int End)> runs,
        IdentitySet pinned
    )
    {
        List<int> removed = new();

        for (int r = runs.Count - 1; r >= 0; r--)
        {
            (int start, int end) = runs[r];
            bool[] stays = RunSurvivors(target, start, end, pinned);

            for (int i = end - 1; i >= start; i--)
            {
                if (!stays[i - start])
                {
                    RemoveAt(i);
                    removed.Add(i);
                }
            }
        }

        removed.Sort();
        return removed;
    }

    /// <summary>
    /// Every position outside the runs, set without a notification. The list holds no container for
    /// these and keeps nothing for them but the count, so it has nothing to be told; it reads the
    /// row if it ever scrolls there.
    /// </summary>
    private void PlaceQuietly(
        IReadOnlyList<object> order,
        List<(int Start, int End)> runs,
        List<int> removed
    )
    {
        int missing = removed.Count(index => index < order.Count);
        int count = order.Count - missing;
        while (Count > count)
        {
            RemoveAt(Count - 1);
        }
        int gone = 0;
        int next = 0;
        int run = 0;

        for (int j = 0; j < order.Count; j++)
        {
            while (next < removed.Count && removed[next] < j)
            {
                gone++;
                next++;
            }

            while (run < runs.Count && runs[run].End <= j)
            {
                run++;
            }

            if (run < runs.Count && j >= runs[run].Start)
            {
                continue;
            }

            // Its place right now is its final one less the removals the runs made below it.
            int at = j - gone;
            if (at < Count)
            {
                Items[at] = order[j];
            }
            else
            {
                Insert(at, order[j]);
            }
        }
    }

    /// <summary>
    /// What each run is missing, ascending across all of them. Every position below the one being
    /// filled is already final by the time it is reached, so the row standing there can be read
    /// directly.
    /// </summary>
    private void InsertIntoRuns(IReadOnlyList<object> order, Func<object, int> target,
        List<(int Start, int End)> runs)
    {
        foreach ((int start, int end) in runs)
        {
            for (int i = start; i < end; i++)
            {
                object next = order[i];

                if (i < Count && target(this[i]) == i)
                {
                    if (!ReferenceEquals(this[i], next))
                    {
                        Items[i] = next;
                        _rebound.Add(i);
                    }
                }
                else
                {
                    Insert(i, next);
                }
            }
        }
    }

    /// <summary>
    /// Which rows of the run at [<paramref name="start"/>, <paramref name="end"/>) already stand in
    /// the order's sequence and can stay where they are: a longest run of rows whose places in the
    /// order increase. A row whose place in the order is outside this run cannot stay in it at
    /// all, and every other row that is not in that longest run has to move.
    /// </summary>
    /// <remarks>
    /// Per run, never over the whole view. With one survivor set for the whole view, the single row
    /// a reversal keeps could fall outside every run, and the run scripts would then neither remove
    /// it nor account for it: the list would be left holding a row at an index the view disagrees
    /// with. A surviving equal-sized run takes its removals back as insertions, so runs below it
    /// keep their indices. A shrinking source clips or drops tail runs; net-count changes then
    /// account for those positions after the last surviving run.
    /// <para>
    /// A row that would have to cross a pinned row moves instead of it. Every row left can then be
    /// joined by the pinned rows in an increasing run, so the longest run contains them all and a
    /// pinned row is never removed.
    /// </para>
    /// </remarks>
    private bool[] RunSurvivors(
        Func<object, int> targetIndex,
        int start,
        int end,
        IdentitySet pinned
    )
    {
        int count = end - start;
        int[] target = new int[count];
        for (int row = 0; row < count; row++)
        {
            int to = targetIndex(this[start + row]);
            target[row] = to >= start && to < end ? to : -1;
        }

        if (pinned.Count > 0)
        {
            int above = -1;
            for (int row = 0; row < count; row++)
            {
                if (target[row] < 0)
                {
                    continue;
                }

                if (pinned.Contains(this[start + row]))
                {
                    above = target[row];
                }
                else if (target[row] < above)
                {
                    target[row] = -1;
                }
            }

            int below = int.MaxValue;
            for (int row = count - 1; row >= 0; row--)
            {
                if (target[row] < 0)
                {
                    continue;
                }

                if (pinned.Contains(this[start + row]))
                {
                    below = target[row];
                }
                else if (target[row] > below)
                {
                    target[row] = -1;
                }
            }
        }

        // Patience sorting. runEnd[k] is the row ending the increasing run of length k + 1 whose
        // last target is smallest; before[row] is the row that precedes it in its run.
        int[] runEnd = new int[count];
        int[] before = new int[count];
        int longest = 0;

        for (int row = 0; row < count; row++)
        {
            if (target[row] < 0)
            {
                // Its place in the order is outside this run, so it leaves whatever else happens.
                before[row] = -1;
                continue;
            }

            int low = 0;
            int high = longest;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (target[runEnd[middle]] < target[row])
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            before[row] = low > 0 ? runEnd[low - 1] : -1;
            runEnd[low] = row;
            if (low == longest)
            {
                longest++;
            }
        }

        bool[] stays = new bool[count];
        for (int row = longest > 0 ? runEnd[longest - 1] : -1; row >= 0; row = before[row])
        {
            stays[row] = true;
        }

        return stays;
    }

    /// <summary>
    /// Refused. A reset makes the list drop every container: the rows that leave vanish instead of
    /// animating out, and the new rows appear in one step, which the owner reads as a flash. Every
    /// change goes through <see cref="Reconcile"/>.
    /// </summary>
    protected override void ClearItems() =>
        throw new NotSupportedException(
            "Never Clear a displayed collection: the list drops every container and nothing "
                + "animates. Reconcile the snapshot in place instead."
        );
}
