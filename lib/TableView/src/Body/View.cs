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
    private PositionIndex? _reconcilingOrder;
    private long _inserts;
    private long _removals;
    private IIdentityKeys? _changingKeys;

    internal View(IIdentity identity, Func<object, bool> isSelected, Func<IReadOnlyCollection<int>> selectedIndices)
        : base(new GapBuffer())
    {
        _isSelected = isSelected;
        _selectedIndices = selectedIndices;
        Positions = new(identity);
        Positions.Bind(this, () => new(_inserts, _removals));
    }

    internal PositionIndex Positions { get; private set; }
    internal long Version { get; private set; }

    internal IIdentityKeys? KeysFor(object item) =>
        _reconcilingOrder?.ContainsReference(item) == true ? _reconcilingOrder
            : _changingKeys?.ContainsReference(item) == true ? _changingKeys
            : Positions.ContainsReference(item) ? Positions : null;

    internal int IndexOfIdentity(object? item)
    {
        if (item is null)
        {
            return -1;
        }
        if (_reconcilingOrder is not { } next)
        {
            if (_changingKeys is Source.Preparation source)
            {
                IIdentityKeys? available = KeysFor(item);
                object? previous = available is null ? Positions.Map.Resolve(item) : Positions.Map.Resolve(item, available);
                available ??= previous is not null ? Positions : null;
                object? current = available is null ? source.Membership.Resolve(item) : source.Resolve(item, available);
                if (previous is null && current is null)
                {
                    return -1;
                }
                int stored = previous is not null && Positions.Map.TryGetValue(previous, Positions, out int known) ? known : -1;
                if (ContainsAt(stored))
                {
                    return stored;
                }
                for (int at = 0; at < Count; at++)
                {
                    if (ContainsAt(at))
                    {
                        return at;
                    }
                }
                return -1;

                bool ContainsAt(int at) => at >= 0 && at < Count
                    && (ReferenceEquals(this[at], previous) || ReferenceEquals(this[at], current));
            }
            return KeysFor(item) is { } captured ? Positions.IndexOf(item, captured) : Positions.IndexOf(item);
        }

        IIdentityKeys? keys = KeysFor(item);
        object? before = keys is null ? Positions.Map.Resolve(item) : Positions.Map.Resolve(item, keys);
        keys ??= before is not null ? Positions : null;
        object? after = keys is null ? next.Map.Resolve(item) : next.Map.Resolve(item, keys);
        if (before is null && after is null)
        {
            return -1;
        }
        int index = before is null ? -1 : Positions.IndexOf(before, Positions);
        if (Matches(index))
        {
            return index;
        }
        index = after is null ? -1 : next.IndexOf(after, next);
        if (Matches(index))
        {
            return index;
        }
        // Native container preparation can query identity between individual vector changes.
        for (index = 0; index < Count; index++)
        {
            if (Matches(index))
            {
                return index;
            }
        }
        return -1;

        bool Matches(int at) => at >= 0 && at < Count
            && (ReferenceEquals(this[at], before) || ReferenceEquals(this[at], after));
    }

    internal void SetIdentity(IIdentity identity)
    {
        Positions = new(identity);
        Positions.Bind(this, () => new(_inserts, _removals));
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

    /// <summary>Bring the view to <paramref name="snapshot"/>.</summary>
    /// <remarks>
    /// Per-row notifications preserve the list's native transitions and unchanged containers;
    /// a reset drops every container. Reorders use removals and insertions, a sort included;
    /// same-position replacements update the item. Table's frame owner suppresses transitions
    /// for a disjoint visible-window replacement, whose native delete/add delay would otherwise
    /// leave the panel empty.
    /// <para>
    /// A snapshot that is already the view, the same instances in the same order, costs one
    /// comparison per row and nothing else. The host re-projects after ticks that changed nothing
    /// visible, and this is what keeps that free.
    /// </para>
    /// <para>
    /// Rows leaving realized runs go first, highest index first. Then every position
    /// outside those runs, set without a notification. Last, what each run is missing, ascending.
    /// Removing before placing and placing before inserting is what keeps a row from being in the
    /// view twice at any moment, which the list is never asked to make sense of. Net count
    /// changes follow the last surviving run, so they cannot shift its containers.
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
    /// </remarks>
    /// <param name="pinned">
    /// Rows that stand at the same index in the view and the snapshot and must keep their
    /// containers, so they do not animate out and back in under the pointer.
    /// </param>
    internal bool Reconcile(
        IReadOnlyList<object> snapshot,
        PositionIndex place,
        IReadOnlyList<int> realized,
        IdentitySet pinned
    )
    {
        _reconcilingOrder = place;
        Positions.Bind(this);
        try
        {
            List<int> held = new(realized);
            held.Sort();

            List<(int Start, int End)> runs = Runs(held);
            int Target(object item) => place.IndexOfKnown(item, Positions);
            List<int> removed = RemoveFromRuns(Target, runs, pinned);
            runs = runs.Where(run => run.Start < snapshot.Count)
                .Select(run => (run.Start, Math.Min(run.End, snapshot.Count))).ToList();
            PlaceQuietly(snapshot, runs, removed);
            InsertIntoRuns(snapshot, Target, runs);

            place.Bind(this, () => new(_inserts, _removals));
            Positions = place;
            Version++;

            return true;
        }
        finally
        {
            _reconcilingOrder = null;
        }
    }

    internal bool Reconcile(IReadOnlyList<object> snapshot, IReadOnlyList<Change.Edit> changes,
        Source.Preparation source, IReadOnlyList<int> realized, IdentitySet pinned)
    {
        List<(Change.Edit Edit, int Before)> edits = new(changes.Count);
        foreach (Change.Edit edit in changes)
        {
            edits.Add((edit, edit.Before is null ? -1 : IndexOfIdentity(edit.Before)));
        }
        if (changes.Count == 1 && (changes[0].Before is null || changes[0].After is null
                || edits[0].Before == changes[0].Index)
            || ReferenceEquals(snapshot, this))
        {
            return ApplyEdits(edits, source, realized);
        }

        int[] removedIndices = edits.Where(entry => entry.Before >= 0).Select(entry => entry.Before).Order().ToArray();
        int[] insertedIndices = edits.Where(entry => entry.Edit.After is not null).Select(entry => entry.Edit.Index).Order().ToArray();
        Dictionary<object, int> targets = new(ReferenceEqualityComparer.Instance);
        foreach (int index in realized)
        {
            object item = this[index];
            int edited = edits.FindIndex(entry => entry.Before == index);
            if (edited >= 0)
            {
                Change.Edit edit = edits[edited].Edit;
                targets[item] = edit.After is not null ? edit.Index : -1;
                continue;
            }
            int target = index - removedIndices.Count(removed => removed < index);
            foreach (int inserted in insertedIndices)
            {
                if (inserted <= target)
                {
                    target++;
                }
            }
            targets[item] = target;
        }
        int Target(object item) => targets.GetValueOrDefault(item, -1);
        _changingKeys = source;
        try
        {
            List<(int Start, int End)> runs = Runs(realized.Order().ToList());
            List<int> removed = RemoveFromRuns(Target, runs, pinned);
            runs = runs.Where(run => run.Start < snapshot.Count)
                .Select(run => (run.Start, Math.Min(run.End, snapshot.Count))).ToList();
            PlaceQuietly(snapshot, runs, removed);
            InsertIntoRuns(snapshot, Target, runs);
            foreach (var entry in edits)
            {
                if (entry.Edit.Before is not null)
                {
                    Positions.Remove(entry.Edit.Before, source);
                }
            }
            foreach (var entry in edits)
            {
                if (entry.Edit.After is not null)
                {
                    Positions.Insert(entry.Edit.After, entry.Edit.Index, source);
                }
                if (entry.Before != entry.Edit.Index)
                {
                    _removals += entry.Before >= 0 ? 1 : 0;
                    _inserts += entry.Edit.After is not null ? 1 : 0;
                }
            }
            Version++;
            return changes.Count > 0;
        }
        finally
        {
            _changingKeys = null;
        }
    }

    private bool ApplyEdits(List<(Change.Edit Edit, int Before)> changes, Source.Preparation source,
        IReadOnlyList<int> realized)
    {
        _changingKeys = source;
        try
        {
            List<(Change.Edit Edit, int Before)> edits = new(changes.Count);
            bool changed = false;
            foreach (var entry in changes)
            {
                Change.Edit edit = entry.Edit;
                int before = entry.Before;
                if (before == edit.Index && edit.After is not null)
                {
                    if (!ReferenceEquals(this[before], edit.After))
                    {
                        Positions.Insert(edit.After, before, source);
                        if (realized.Contains(before))
                        {
                            this[before] = edit.After;
                        }
                        else
                        {
                            Items[before] = edit.After;
                            Version++;
                        }
                        changed = true;
                    }
                    continue;
                }
                edits.Add((edit, before));
            }
            edits.Sort((left, right) => right.Before.CompareTo(left.Before));
            foreach (var entry in edits)
            {
                if (entry.Before < 0)
                {
                    continue;
                }
                Positions.Remove(entry.Edit.Before!, source);
                RemoveAt(entry.Before);
                changed = true;
            }
            edits.Sort((left, right) => left.Edit.Index.CompareTo(right.Edit.Index));
            foreach (var entry in edits)
            {
                if (entry.Edit.After is not { } item)
                {
                    continue;
                }
                Positions.Insert(item, entry.Edit.Index, source);
                Insert(entry.Edit.Index, item);
                changed = true;
            }
            if (changed)
            {
                Version++;
            }
            return changed;
        }
        finally
        {
            _changingKeys = null;
        }
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
        IReadOnlyList<object> snapshot,
        List<(int Start, int End)> runs,
        List<int> removed
    )
    {
        int missing = removed.Count(index => index < snapshot.Count);
        int count = snapshot.Count - missing;
        while (Count > count)
        {
            RemoveAt(Count - 1);
        }
        int gone = 0;
        int next = 0;
        int run = 0;

        for (int j = 0; j < snapshot.Count; j++)
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
                Items[at] = snapshot[j];
            }
            else
            {
                Insert(at, snapshot[j]);
            }
        }
    }

    /// <summary>
    /// What each run is missing, ascending across all of them. Every position below the one being
    /// filled is already final by the time it is reached, so the row standing there can be read
    /// directly.
    /// </summary>
    private void InsertIntoRuns(IReadOnlyList<object> snapshot, Func<object, int> target, List<(int Start, int End)> runs)
    {
        foreach ((int start, int end) in runs)
        {
            for (int i = start; i < end; i++)
            {
                object next = snapshot[i];

                if (i < Count && target(this[i]) == i)
                {
                    // Same row, new instance: the host abandoned the object this container is bound
                    // to, so nothing would update that container again.
                    if (!ReferenceEquals(this[i], next))
                    {
                        this[i] = next;
                    }
                }
                else
                {
                    Insert(i, next);
                }
            }
        }
    }

    /// <summary>The snapshot is the view as it stands: the same instances in the same order.</summary>
    internal bool IsAlready(IReadOnlyList<object> snapshot)
    {
        if (snapshot.Count != Count)
        {
            return false;
        }

        for (int i = 0; i < Count; i++)
        {
            if (!ReferenceEquals(this[i], snapshot[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Which rows of the run at [<paramref name="start"/>, <paramref name="end"/>) already stand in
    /// the snapshot's order and can stay where they are: a longest run of rows whose places in the
    /// snapshot increase. A row whose place in the snapshot is outside this run cannot stay in it at
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

                if (pinned.Contains(this[start + row], KeysFor(this[start + row])!))
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

                if (pinned.Contains(this[start + row], KeysFor(this[start + row])!))
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
                // Its place in the snapshot is outside this run, so it leaves whatever else happens.
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
