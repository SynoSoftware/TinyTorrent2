using System.Buffers;

namespace Syno.TableView.Body;

/// <summary>Plans collection edits without touching the live view or its native observers.</summary>
/// <remarks>
/// Rows are named by their slot (<see cref="Slots"/>), so where a row stands in either order is
/// read from an array instead of found by its key. The plan works on a copy of the view taken
/// from shared pools and given back when the plan is made; the edits stay in a pooled buffer until
/// the view has applied them and the table calls <see cref="Return"/>.
/// </remarks>
internal sealed class Changes
{
    private Edit[] _edits = [];
    private int _count;
    private int[] _slots = [];
    private object?[] _rows = [];
    private int _length;
    private Func<int, object> _row = _ => throw new InvalidOperationException();

    /// <summary>
    /// One edit to the view, in the order the view applies them. A removal and a resize carry no
    /// row; a resize's index is the view's new length.
    /// </summary>
    internal readonly record struct Edit(EditKind Kind, int Index, object? Row);

    /// <summary>What the list holds of the view while an update is prepared.</summary>
    /// <param name="Realized">The view positions the list holds a container for.</param>
    /// <param name="Reach">
    /// How many leading positions its cache reaches: the visible rows and the cache around them,
    /// without a pinned container far from them.
    /// </param>
    internal readonly record struct Screen(IReadOnlyList<int> Realized, int Reach);

    internal ReadOnlySpan<Edit> Edits => _edits.AsSpan(0, _count);
    internal bool Changed { get; private set; }

    /// <summary>The order shares no row with the view, and <see cref="Flip"/> swapped it in.</summary>
    internal bool IsDifferentList { get; private set; }

    /// <param name="view">The rows the view shows, in order.</param>
    /// <param name="shown">The slot of each row of <paramref name="view"/>.</param>
    /// <param name="order">The slots to show, in order.</param>
    /// <param name="targets">Each slot's index in <paramref name="order"/>, or -1.</param>
    /// <param name="row">The current row of a slot in <paramref name="order"/>.</param>
    /// <param name="screen">What the list holds of the view.</param>
    /// <param name="pinned">
    /// Rows that stand at the same index in the view and the order and must keep their
    /// containers, so they do not animate out and back in under the pointer.
    /// </param>
    internal void Prepare(
        IReadOnlyList<object> view,
        ArraySegment<int> shown,
        ArraySegment<int> order,
        int[] targets,
        Func<int, object> row,
        Screen screen,
        IReadOnlySet<int> pinned
    )
    {
        // Membership announces arrivals before the view shrinks to the order's length.
        int length = view.Count + order.Count;
        _slots = ArrayPool<int>.Shared.Rent(length);
        _rows = ArrayPool<object?>.Shared.Rent(length);
        _row = row;
        shown.AsSpan().CopyTo(_slots);
        for (int i = 0; i < view.Count; i++)
        {
            _rows[i] = view[i];
        }

        _length = view.Count;
        try
        {
            Changed = Reconcile(order, targets, screen, pinned);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(_slots);
            ArrayPool<object?>.Shared.Return(_rows, clearArray: true);
            _slots = [];
            _rows = [];
        }
    }

    /// <summary>Give the edit buffer back to its pool, once the view has applied the edits.</summary>
    internal void Return()
    {
        if (_edits.Length > 0)
        {
            ArrayPool<Edit>.Shared.Return(_edits, clearArray: true);
        }

        _edits = [];
        _count = 0;
    }

    private void Add(EditKind kind, int index, object? row)
    {
        if (_count == _edits.Length)
        {
            Edit[] larger = ArrayPool<Edit>.Shared.Rent(Math.Max(16, _count * 2));
            Array.Copy(_edits, larger, _count);
            if (_edits.Length > 0)
            {
                ArrayPool<Edit>.Shared.Return(_edits, clearArray: true);
            }

            _edits = larger;
        }

        _edits[_count++] = new(kind, index, row);
    }

    private void Insert(int index, int slot)
    {
        object row = _row(slot);
        Array.Copy(_slots, index, _slots, index + 1, _length - index);
        Array.Copy(_rows, index, _rows, index + 1, _length - index);
        _slots[index] = slot;
        _rows[index] = row;
        _length++;
        Add(EditKind.Insert, index, row);
    }

    private void RemoveAt(int index)
    {
        _length--;
        Array.Copy(_slots, index + 1, _slots, index, _length - index);
        Array.Copy(_rows, index + 1, _rows, index, _length - index);
        _rows[_length] = null;
        Add(EditKind.Remove, index, null);
    }

    private void Put(int index, int slot)
    {
        object row = _row(slot);
        if (_slots[index] == slot && ReferenceEquals(_rows[index], row))
        {
            return;
        }

        _slots[index] = slot;
        _rows[index] = row;
        Add(EditKind.Replace, index, row);
    }

    /// <summary>Bring the view to <paramref name="order"/>.</summary>
    /// <remarks>
    /// The owner's requirement: when the view is repopulated, the rows that leave animate out and
    /// the rows that arrive animate in, and the table is never empty in between. Only per-row
    /// notifications give the list that; a reset drops every container and shows the new rows in
    /// one step. So every change comes through here as removals and insertions, a sort included.
    /// <para>
    /// An order that is already the view, the same rows in the same order, costs one comparison
    /// per row and raises nothing; a new instance of a row takes its place quietly. The host
    /// re-projects after ticks that changed nothing visible, and this is what keeps that free. An
    /// order that shares no row with the view is a different list, and <see cref="Flip"/>
    /// replaces it in one step.
    /// </para>
    /// <para>
    /// Otherwise five passes, and the order of them is the point. Membership first, raised where
    /// it happens within the list's cache. Then the view takes the order's length without a
    /// notification, and the view announces that change in count at its end, after the update
    /// (<see cref="View.CatchUp(long)"/>). Then every row leaving a run of realized positions,
    /// highest first. Then every position outside those runs, set without a notification. Last,
    /// what each run is missing, ascending. Removing before placing and placing before inserting is what
    /// keeps a row from being in the view twice at any moment, which the list is never asked to
    /// make sense of.
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
    private bool Reconcile(
        ReadOnlySpan<int> order,
        int[] targets,
        Screen screen,
        IReadOnlySet<int> pinned
    )
    {
        if (IsAlready(order))
        {
            return false;
        }

        if (_length > 0 && order.Length > 0 && !SharesRow(targets))
        {
            Flip(order);
            IsDifferentList = true;
            return true;
        }

        // A container on a vacant position shows no row and takes no part.
        List<int> held = screen.Realized.Where(index => index < _length).ToList();
        held.Sort();

        Membership(order, targets, held, Math.Min(screen.Reach, _length));
        Resize(order.Length);
        for (int i = 0; i < held.Count; i++)
        {
            if (held[i] >= _length)
            {
                held[i] = -1;
            }
        }

        List<(int Start, int End)> runs = Runs(held);
        List<int> removed = RemoveFromRuns(targets, runs, pinned);
        PlaceQuietly(order, runs, removed);
        InsertIntoRuns(order, runs);

        return true;
    }

    private bool SharesRow(int[] targets)
    {
        for (int i = 0; i < _length; i++)
        {
            if (targets[_slots[i]] >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// An order that shares no row with the view: a different list, such as another torrent's
    /// peers. Every position takes its new row without a notification, and only the change in
    /// count is raised, at the end, by the view. The containers the list holds stay and show the
    /// new rows (<c>Table.RebindRows</c>), so a switch builds no row and plays no animation;
    /// contract section 5.3 records why.
    /// </summary>
    private void Flip(ReadOnlySpan<int> order)
    {
        Resize(order.Length);
        for (int i = 0; i < _length; i++)
        {
            Put(i, order[i]);
        }
    }

    /// <summary>
    /// Rows joining or leaving within the first <paramref name="head"/> positions, raised where
    /// they happen, which leaves the panel's scroll anchoring around the rows it holds as it was.
    /// Past the head the list holds no container and keeps nothing but the count, so a row that
    /// joins or leaves there changes only the count: <see cref="Resize"/> takes it, and the view
    /// announces it at its end.
    /// </summary>
    /// <param name="head">
    /// The positions the list's cache reaches. It moves with each change raised inside it, so it
    /// always ends at the same row.
    /// </param>
    private void Membership(ReadOnlySpan<int> order, int[] targets, List<int> held, int head)
    {
        for (int i = head - 1; i >= 0; i--)
        {
            if (targets[_slots[i]] < 0)
            {
                RemoveAt(i);
                Shift(held, i, -1);
                head--;
            }
        }

        bool[] present = ArrayPool<bool>.Shared.Rent(order.Length);
        try
        {
            Array.Clear(present, 0, order.Length);
            for (int i = 0; i < _length; i++)
            {
                // Rows past the head that leave are still here, and are in no place of the order.
                if (targets[_slots[i]] >= 0)
                {
                    present[targets[_slots[i]]] = true;
                }
            }

            for (int i = 0; i < order.Length; i++)
            {
                if (present[i])
                {
                    continue;
                }

                // The order's index, clamped: the view is still in its old order here, so this is
                // where the row belongs at the end rather than where it belongs now. The passes
                // below put every row in its place; this one only has to get the membership right
                // inside the head, and to leave the index it inserted at valid.
                int at = Math.Min(i, _length);
                if (at >= head)
                {
                    break;
                }

                Insert(at, order[i]);
                Shift(held, at, 1);
                head++;
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(present);
        }
    }

    /// <summary>
    /// Make the view as long as the order without a notification: past the last position the
    /// list holds a container for, a change in count is all the list needs to learn, and the view
    /// tells it after the update. A new position is placed by the passes that follow.
    /// </summary>
    private void Resize(int length)
    {
        if (length == _length)
        {
            return;
        }

        for (int i = Math.Min(length, _length); i < Math.Max(length, _length); i++)
        {
            _slots[i] = -1;
            _rows[i] = null;
        }

        _length = length;
        Add(EditKind.Resize, length, null);
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
        int[] targets,
        List<(int Start, int End)> runs,
        IReadOnlySet<int> pinned
    )
    {
        List<int> removed = new();

        for (int r = runs.Count - 1; r >= 0; r--)
        {
            (int start, int end) = runs[r];
            bool[] stays = RunSurvivors(targets, start, end, pinned);

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
        ReadOnlySpan<int> order,
        List<(int Start, int End)> runs,
        List<int> removed
    )
    {
        int gone = 0;
        int next = 0;
        int run = 0;

        for (int j = 0; j < order.Length; j++)
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
            Put(j - gone, order[j]);
        }
    }

    /// <summary>
    /// What each run is missing, ascending across all of them. Every position below the one being
    /// filled is already final by the time it is reached, so the row standing there can be read
    /// directly.
    /// </summary>
    private void InsertIntoRuns(ReadOnlySpan<int> order, List<(int Start, int End)> runs)
    {
        foreach ((int start, int end) in runs)
        {
            for (int i = start; i < end; i++)
            {
                if (i < _length && _slots[i] == order[i])
                {
                    // Same row, perhaps a new instance. A notification would make the list build a
                    // new container and fade it in, so the row would blink although it stays; the
                    // table rebinds the existing container instead.
                    Put(i, order[i]);
                }
                else
                {
                    Insert(i, order[i]);
                }
            }
        }
    }

    /// <summary>
    /// The order is the view as it stands: the same rows in the same order. A new instance of a
    /// row takes its place without a notification, also when a later row differs, because the
    /// passes after this one compare rows by slot and carry the new instance where they move.
    /// </summary>
    private bool IsAlready(ReadOnlySpan<int> order)
    {
        if (order.Length != _length)
        {
            return false;
        }

        for (int i = 0; i < _length; i++)
        {
            if (_slots[i] != order[i])
            {
                return false;
            }

            Put(i, order[i]);
        }

        return true;
    }

    /// <summary>
    /// Which rows of the run at [<paramref name="start"/>, <paramref name="end"/>) already stand in
    /// the order and can stay where they are: a longest run of rows whose places in the order
    /// increase. A row whose place in the order is outside this run cannot stay in it at all, and
    /// every other row that is not in that longest run has to move.
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
    private bool[] RunSurvivors(int[] targets, int start, int end, IReadOnlySet<int> pinned)
    {
        int count = end - start;
        int[] target = new int[count];
        for (int row = 0; row < count; row++)
        {
            int to = targets[_slots[start + row]];
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

                if (pinned.Contains(_slots[start + row]))
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

                if (pinned.Contains(_slots[start + row]))
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
}
