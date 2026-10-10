using System.Collections.ObjectModel;
using Windows.Foundation.Collections;

namespace Syno.TableView.Body;

/// <summary>
/// The private view handed to the row surface. A source snapshot is applied in place, as
/// removals, insertions and replacements matched by section 5 identity, so a row that did not
/// change keeps its container and the list animates only what moved. The collection exposes no
/// <c>Move</c> operation. Never <c>Clear</c>: see <see cref="ClearItems"/>.
/// </summary>
internal sealed class View : Collection<object>, IObservableVector<object>
{
    private readonly ItemIdentity _identity;

    internal View(ItemIdentity identity) => _identity = identity;

    // Native vector events avoid marshaling a .NET collection event and its item lists per row.
    public event VectorChangedEventHandler<object>? VectorChanged;

    protected override void InsertItem(int index, object item)
    {
        base.InsertItem(index, item);
        VectorChanged?.Invoke(this, new Change(CollectionChange.ItemInserted, index));
    }

    protected override void RemoveItem(int index)
    {
        base.RemoveItem(index);
        VectorChanged?.Invoke(this, new Change(CollectionChange.ItemRemoved, index));
    }

    protected override void SetItem(int index, object item)
    {
        base.SetItem(index, item);
        VectorChanged?.Invoke(this, new Change(CollectionChange.ItemChanged, index));
    }

    private sealed class Change(CollectionChange change, int index) : IVectorChangedEventArgs
    {
        public CollectionChange CollectionChange => change;
        public uint Index => (uint)index;
    }

    /// <summary>Bring the view to <paramref name="snapshot"/>.</summary>
    /// <remarks>
    /// The owner's requirement: when the view is repopulated, the rows that leave animate out and
    /// the rows that arrive animate in, and the table is never empty in between. Only per-row
    /// notifications give the list that; a reset drops every container and shows the new rows in
    /// one step. So every change comes through here as removals and insertions, a sort included.
    /// <para>
    /// A snapshot that is already the view, the same instances in the same order, costs one
    /// comparison per row and nothing else. The host re-projects after ticks that changed nothing
    /// visible, and this is what keeps that free.
    /// </para>
    /// <para>
    /// Otherwise four passes, and the order of them is the point. Membership first, raised where it
    /// happens, because the count is the one thing the list keeps for a row it has not realized.
    /// Then every row leaving a run of realized positions, highest first. Then every position
    /// outside those runs, set without a notification. Last, what each run is missing, ascending.
    /// Removing before placing and placing before inserting is what keeps a row from being in the
    /// view twice at any moment, which the list is never asked to make sense of.
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
        IReadOnlyList<int> realized,
        IReadOnlySet<object> pinned
    )
    {
        if (IsAlready(snapshot))
        {
            return false;
        }

        Dictionary<object, int> place = new(snapshot.Count, _identity);
        for (int i = 0; i < snapshot.Count; i++)
        {
            place[snapshot[i]] = i;
        }

        List<int> held = new(realized);
        held.Sort();

        Membership(snapshot, place, held);

        List<(int Start, int End)> runs = Runs(held);
        List<int> removed = RemoveFromRuns(place, runs, pinned);
        PlaceQuietly(snapshot, runs, removed);
        InsertIntoRuns(snapshot, runs);

        return true;
    }

    /// <summary>
    /// Rows joining or leaving, raised where they happen. The count is the one thing the list keeps
    /// for a row it has not realized, so this pass is announced in full however far off screen it
    /// is, and raising it at the true index leaves the panel's scroll anchoring as it is today.
    /// </summary>
    private void Membership(
        IReadOnlyList<object> snapshot,
        Dictionary<object, int> place,
        List<int> held
    )
    {
        for (int i = Count - 1; i >= 0; i--)
        {
            if (!place.ContainsKey(this[i]))
            {
                RemoveAt(i);
                Shift(held, i, -1);
            }
        }

        bool[] present = new bool[snapshot.Count];
        for (int i = 0; i < Count; i++)
        {
            present[place[this[i]]] = true;
        }

        for (int i = 0; i < snapshot.Count; i++)
        {
            if (present[i])
            {
                continue;
            }

            // The snapshot's index, clamped: the view is still in its old order here, so this is
            // where the row belongs at the end rather than where it belongs now. The passes below
            // put every row in its place; this one only has to get the membership and the count
            // right, and to leave the index it inserted at valid.
            int at = Math.Min(i, Count);
            Insert(at, snapshot[i]);
            Shift(held, at, 1);
        }
    }

    /// <summary>
    /// The realized indices after a raised change at <paramref name="at"/>. An index the removal
    /// took out is marked gone rather than moved.
    /// </summary>
    private static void Shift(List<int> held, int at, int delta)
    {
        for (int i = 0; i < held.Count; i++)
        {
            if (held[i] < 0)
            {
                continue;
            }

            if (delta < 0 && held[i] == at)
            {
                held[i] = -1;
            }
            else if (held[i] >= at)
            {
                held[i] += delta;
            }
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
        Dictionary<object, int> place,
        List<(int Start, int End)> runs,
        IReadOnlySet<object> pinned
    )
    {
        List<int> removed = new();

        for (int r = runs.Count - 1; r >= 0; r--)
        {
            (int start, int end) = runs[r];
            bool[] stays = RunSurvivors(place, start, end, pinned);

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
            Items[j - gone] = snapshot[j];
        }
    }

    /// <summary>
    /// What each run is missing, ascending across all of them. Every position below the one being
    /// filled is already final by the time it is reached, so the row standing there can be read
    /// directly.
    /// </summary>
    private void InsertIntoRuns(IReadOnlyList<object> snapshot, List<(int Start, int End)> runs)
    {
        foreach ((int start, int end) in runs)
        {
            for (int i = start; i < end; i++)
            {
                object next = snapshot[i];

                if (i < Count && _identity.Equals(this[i], next))
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
    private bool IsAlready(IReadOnlyList<object> snapshot)
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
    /// with. Restricted to the run, the arithmetic also stays neutral — a run holds the same number
    /// of positions in both orders, so what it removes here it takes back in insertions, and the
    /// runs below it never move.
    /// <para>
    /// A row that would have to cross a pinned row moves instead of it. Every row left can then be
    /// joined by the pinned rows in an increasing run, so the longest run contains them all and a
    /// pinned row is never removed.
    /// </para>
    /// </remarks>
    private bool[] RunSurvivors(
        Dictionary<object, int> place,
        int start,
        int end,
        IReadOnlySet<object> pinned
    )
    {
        int count = end - start;
        int[] target = new int[count];
        for (int row = 0; row < count; row++)
        {
            int to = place[this[start + row]];
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
