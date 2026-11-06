using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Interop;
using Windows.Foundation.Collections;

namespace Syno.TableView.Body;

/// <summary>
/// The live private view handed to the row surface. It applies the worker's prepared
/// removals, insertions and replacements, so a row that did not
/// change keeps its container and the list animates only what moved. The collection exposes no
/// <c>Move</c> operation. Never <c>Clear</c>: see <see cref="ClearItems"/>. The view also answers
/// the list's selection questions from the table's selection, so the list keeps none of its own:
/// see <see cref="IsSelected"/>.
/// </summary>
internal sealed class View : Collection<object>, IBindableObservableVector, ISelectionInfo
{
    private readonly Func<object, bool> _isSelected;

    internal View(Func<object, bool> isSelected)
    {
        _isSelected = isSelected;
    }

    // Native vector events avoid marshaling a .NET collection event and its item lists per row.
    public event BindableVectorChangedEventHandler? VectorChanged;

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
        for (int index = 0; index <= Count; index++)
        {
            bool selected = index < Count && _isSelected(this[index]);
            if (selected && start < 0)
            {
                start = index;
            }
            else if (!selected && start >= 0)
            {
                ranges.Add(new ItemIndexRange(start, (uint)(index - start)));
                start = -1;
            }
        }
        return ranges;
    }

    internal void Apply(Changes changes)
    {
        foreach (var edit in changes.Edits)
        {
            switch (edit)
            {
                case Changes.Removal removal:
                    RemoveAt(removal.Index);
                    break;
                case Changes.Insertion insertion:
                    Insert(insertion.Index, insertion.Item);
                    break;
                case Changes.Replacement replacement:
                    Items[replacement.Index] = replacement.Item;
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
}
