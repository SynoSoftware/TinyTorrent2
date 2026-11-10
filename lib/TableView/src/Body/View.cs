using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Interop;
using Windows.Foundation.Collections;
using WinRT;

namespace Syno.TableView.Body;

/// <summary>
/// The live private view handed to the row surface. It applies the worker's prepared
/// removals, insertions and replacements, so a row that did not
/// change keeps its container and the list animates only what moved. The collection exposes no
/// <c>Move</c> operation. Never <c>Clear</c>: see <see cref="ClearItems"/>. The view also answers
/// the list's selection questions from the table's selection, so the list keeps none of its own:
/// see <see cref="IsSelected"/>.
/// </summary>
/// <remarks>
/// The view has two faces. <see cref="Rows"/> is the table's: every row of the projection, in
/// order. The collection itself is the list's: as many positions as the notices have told it of.
/// The two differ only at the end, while the list is still owed a change in count
/// (<see cref="CatchUp(long)"/>): rows past the list's count wait to arrive, and positions past
/// the last row are vacant and read as null, the platform's placeholder item.
/// </remarks>
internal sealed class View : Collection<object?>, IBindableObservableVector, ISelectionInfo
{
    private readonly List<object> _rows;
    private readonly Positions _positions;
    private readonly Func<object, bool> _isSelected;
    private readonly Func<IEnumerable<int>> _selected;
    private Change _change = new();

    /// <param name="isSelected">Whether the table's selection holds a row.</param>
    /// <param name="selected">The positions of the selected rows in <see cref="Rows"/>, in any order.</param>
    internal View(Func<object, bool> isSelected, Func<IEnumerable<int>> selected)
        : this(new Positions(new List<object>()), isSelected, selected) { }

    private View(Positions positions, Func<object, bool> isSelected, Func<IEnumerable<int>> selected)
        : base(positions)
    {
        _positions = positions;
        _rows = positions.Rows;
        _isSelected = isSelected;
        _selected = selected;
    }

    /// <summary>The table's face of the view: every row it shows, in order.</summary>
    internal IReadOnlyList<object> Rows => _rows;

    /// <summary>The list is still owed a change in count: its count is not the view's.</summary>
    internal bool Behind => Count != _rows.Count;

    /// <summary>Rows wait to arrive at the end, rather than vacant positions to leave.</summary>
    internal bool Arriving => Count < _rows.Count;

    // Native vector events avoid marshaling a .NET collection event and its item lists per row.
    public event BindableVectorChangedEventHandler? VectorChanged;

    protected override void InsertItem(int index, object? item)
    {
        base.InsertItem(index, item);
        Raise(CollectionChange.ItemInserted, index);
    }

    protected override void RemoveItem(int index)
    {
        base.RemoveItem(index);
        Raise(CollectionChange.ItemRemoved, index);
    }

    protected override void SetItem(int index, object? item)
    {
        base.SetItem(index, item);
        Raise(CollectionChange.ItemChanged, index);
    }

    /// <summary>
    /// Raise one notice with the one arguments object the view reuses, as WinUI's own vectors do
    /// (<c>DXamlCore::GetVectorChangedEventArgsFromPool</c>). A new object per notice gave every
    /// notice its own COM wrapper, GC handle and finalizable releaser. The list reads the
    /// arguments inside the notice; a listener that still holds them afterwards keeps them, and
    /// the next notice takes a new object, which is WinUI's own rule for its pool.
    /// </summary>
    private void Raise(CollectionChange kind, int index)
    {
        if (VectorChanged is not { } handler)
        {
            return;
        }

        Change change = _change;
        change.Kind = kind;
        change.At = index;
        handler(this, change);
        IntPtr held = MarshalInspectable<object>.FromManaged(change);
        if (Marshal.Release(held) > 0)
        {
            _change = new Change();
        }
    }

    private sealed class Change : IVectorChangedEventArgs
    {
        internal CollectionChange Kind;
        internal int At;

        public CollectionChange CollectionChange => Kind;
        public uint Index => (uint)At;
    }

    // ------------------------------------------------------------------ the change in count

    /// <summary>
    /// Tell the list the change in count it is still owed, one notice per position at the end of
    /// the view, until it knows the count or <paramref name="deadline"/>, a
    /// <see cref="Stopwatch"/> timestamp, has passed. Returns whether it knows the count.
    /// </summary>
    /// <remarks>
    /// The list keeps nothing but the count for a position it holds no container for, so the
    /// notices can trail the update that changed the count: contract section 5.3 says what a
    /// person sees meanwhile.
    /// </remarks>
    internal bool CatchUp(long deadline)
    {
        while (Behind && Stopwatch.GetTimestamp() < deadline)
        {
            if (Arriving)
            {
                Arrive();
            }
            else
            {
                _positions.Count--;
                Raise(CollectionChange.ItemRemoved, _positions.Count);
            }
        }

        return !Behind;
    }

    /// <summary>Tell the list of the rows up to <paramref name="index"/>, so it can scroll to one.</summary>
    internal void CatchUpTo(int index)
    {
        while (Arriving && Count <= index)
        {
            Arrive();
        }
    }

    /// <summary>
    /// Tell the list of the next row waiting at the end, as the platform's placeholder, null. The
    /// list reads the inserted item inside the notice and wraps it for COM, so announcing the row
    /// itself gave every row the table ever inserted a wrapper, a GC handle and a finalizable
    /// releaser, on screen or not: 165,559 wrappers for 159,810 peers. Inside a notice the panel
    /// only records the insertion (<c>ModernCollectionBasePanel::OnItemAdded</c>) and builds
    /// containers in its next layout, which reads the row standing there by then.
    /// </summary>
    private void Arrive()
    {
        int at = Count;
        _positions.Placeholder = at;
        _positions.Count++;
        try
        {
            Raise(CollectionChange.ItemInserted, at);
        }
        finally
        {
            _positions.Placeholder = -1;
        }
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
    public bool IsSelected(int index) =>
        index >= 0 && index < Count && index < _rows.Count && _isSelected(_rows[index]);

    /// <summary>
    /// Called by the list for its own gestures, a press, a tap, Space or Ctrl+A on a container,
    /// and by the table's own range calls. The table arbitrates every gesture itself and has
    /// applied its own changes before it calls, so nothing changes here; the list then re-reads
    /// every realized row through <see cref="IsSelected"/>.
    /// </summary>
    public void SelectRange(ItemIndexRange itemIndexRange) { }

    /// <inheritdoc cref="SelectRange"/>
    public void DeselectRange(ItemIndexRange itemIndexRange) { }

    /// <summary>The selected positions the list knows of, as runs, read from the selection.</summary>
    public IReadOnlyList<ItemIndexRange> GetSelectedRanges()
    {
        int end = Math.Min(Count, _rows.Count);
        List<int> positions = _selected().Where(index => index >= 0 && index < end).ToList();
        positions.Sort();

        List<ItemIndexRange> ranges = new();
        int start = -1;
        int last = -2;
        foreach (int index in positions)
        {
            if (index != last + 1)
            {
                if (start >= 0)
                {
                    ranges.Add(new ItemIndexRange(start, (uint)(last - start + 1)));
                }

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

    internal void Apply(Changes changes)
    {
        foreach (var edit in changes.Edits)
        {
            switch (edit.Kind)
            {
                case EditKind.Remove:
                    RemoveAt(edit.Index);
                    break;
                case EditKind.Insert:
                    Insert(edit.Index, edit.Row!);
                    break;
                case EditKind.Replace:
                    _rows[edit.Index] = edit.Row!;
                    break;
                case EditKind.Resize:
                    // A longer view's new positions are null only until the replacements that
                    // follow place every one of them (Changes.Resize).
                    CollectionsMarshal.SetCount(_rows, edit.Index);
                    break;
            }
        }
    }

    /// <summary>
    /// Refused. A reset makes the list drop every container: the rows that leave vanish instead of
    /// animating out, and the new rows appear in one step, which the owner reads as a flash. Every
    /// change applies the worker's <see cref="Changes"/> in place.
    /// </summary>
    protected override void ClearItems() =>
        throw new NotSupportedException(
            "Never Clear a displayed collection: the list drops every container and nothing "
                + "animates. Reconcile the snapshot in place instead."
        );

    /// <summary>
    /// The list's face of the rows: the positions it has been told of. Past the last row a position
    /// is vacant and reads as null; a row past <see cref="Count"/> has not arrived.
    /// </summary>
    private sealed class Positions(List<object> rows) : IList<object?>
    {
        internal List<object> Rows { get; } = rows;

        /// <summary>The position an insertion notice announces as a placeholder, or -1.</summary>
        internal int Placeholder { get; set; } = -1;

        /// <summary>How many positions the list has been told of.</summary>
        public int Count { get; set; }

        public bool IsReadOnly => false;

        public object? this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
                return index < Rows.Count && index != Placeholder ? Rows[index] : null;
            }
            set => Rows[index] = value ?? throw new ArgumentNullException(nameof(value));
        }

        public int IndexOf(object? item) =>
            item is null ? -1 : Rows.IndexOf(item, 0, Math.Min(Count, Rows.Count));

        public void Insert(int index, object? item)
        {
            Rows.Insert(index, item ?? throw new ArgumentNullException(nameof(item)));
            Count++;
        }

        public void RemoveAt(int index)
        {
            Rows.RemoveAt(index);
            Count--;
        }

        public void Add(object? item) => Insert(Count, item);

        public void Clear() => throw new NotSupportedException();

        public bool Contains(object? item) => IndexOf(item) >= 0;

        public bool Remove(object? item)
        {
            int index = IndexOf(item);
            if (index < 0)
            {
                return false;
            }

            RemoveAt(index);
            return true;
        }

        public void CopyTo(object?[] array, int arrayIndex)
        {
            for (int i = 0; i < Count; i++)
            {
                array[arrayIndex + i] = this[i];
            }
        }

        public IEnumerator<object?> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
