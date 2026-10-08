using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace Syno.TableView;

/// <summary>
/// The table-owned selection surface of specification sections 5, 5.3 and 13. The table is the
/// single interactive selection owner: the row surface's own <c>SelectionChanged</c> is never
/// treated as truth, and the authoritative state is re-applied to it after every change.
/// </summary>
public sealed partial class Table
{
    private Selection _publishedSelection = Selection.Empty;
    private Selection? _pendingSelection;
    private ListViewSelectionMode _selectionMode = ListViewSelectionMode.Extended;

    /// <summary>Set while the table is writing the row surface's selection, to stop re-entry.</summary>
    private bool _syncingContainers;

    /// <summary>
    /// Raised once after any completed selection or current-item change, including the ones caused
    /// by assignment and source reconciliation. Replacement instances and packet order changes
    /// also notify, so observers always receive the exposed rows.
    /// </summary>
    public event EventHandler<Selection>? SelectionChanged;

    /// <summary>Row visuals re-read the table's selected and current state when this fires.</summary>
    internal event EventHandler? RowVisualsChanged;

    /// <summary>Setup-only. Default <see cref="ListViewSelectionMode.Extended"/>.</summary>
    public ListViewSelectionMode SelectionMode
    {
        get => _selectionMode;
        set
        {
            RequireSetup();
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            _selectionMode = value;
        }
    }

    /// <summary>
    /// The selected packet and the current row. Reading gives the state that stands; assigning is
    /// an idempotent request for a different one, resolved against the interactive rows of the current
    /// view. An equal logical request raises no event, so a host can project the selection out to
    /// another surface and push it back without a suppression flag.
    /// </summary>
    /// <remarks>
    /// Deliberately not a dependency property. Section 5.2 forbids a two-way selected-items
    /// binding because it would create a competing selection owner; leaving this un-bindable makes
    /// that structural instead of a rule somebody has to have read.
    /// </remarks>
    public Selection Selection
    {
        get => _pendingSelection ?? _publishedSelection;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (!_schemaCaptured)
            {
                _pendingSelection = value;
                return;
            }
            SyncSelectionPolicy();
            CancelGesture();
            _selection.SetSelection(value.Items, value.Current, View);
            CommitSelection();
        }
    }

    /// <summary>The selected packet in current visual row order.</summary>
    internal IReadOnlyList<object> SelectedItems => Selection.Items;

    internal bool IsRowSelected(object? item) => item is not null && _selection.IsSelected(item);

    internal bool IsRowCurrent(object? item) =>
        item is not null && _selection.IsSame(item, _selection.Current);

    /// <summary>
    /// Section 15: an already-selected row keeps the whole selected packet, and any other row
    /// becomes the selection. Both make the row current, focused, and the next range anchor, so a
    /// context request on an already-selected row still reports the moved current item.
    /// </summary>
    private void SelectForContext(object item) =>
        _selection.SetSelection(
            _selection.IsSelected(item) ? SelectedItems : new[] { item },
            item,
            View
        );

    /// <summary>
    /// Re-apply the table-owned state after a snapshot. The row surface drops a row from its own
    /// selection as soon as that row is removed, and a reorder is a removal, so its selection and
    /// its focus are restored from the model here rather than trusted.
    /// </summary>
    private void ReconcileSelection(FocusState rowFocus, object? focusedItem = null)
    {
        SyncSelectionPolicy();

        if (_pendingSelection is Selection pending)
        {
            _pendingSelection = null;
            _selection.SetSelection(pending.Items, pending.Current, View);
        }
        else
        {
            _selection.Reconcile(
                View,
                CollapsedAncestor(_selection.Current),
                CollapsedAncestor(_selection.Focus)
            );
        }
        if (_gesture == RowGesture.Marquee)
        {
            _marquee.Refresh();
            _selection.SetMarqueeSelection(MarqueeItems(), View);
        }

        if (!_detached)
            RestoreRowFocus(rowFocus, focusedItem);
        CommitSelection();
        if (_hierarchy is not null && _surface is { } surface)
            surface.RefreshHierarchy();
    }

    // ------------------------------------------------------------------ commit

    /// <summary>
    /// The setup-only policy the model needs. Read live; changing it late is a config error. The
    /// model's interaction predicate is <see cref="IsInteractive"/>, given once at construction.
    /// </summary>
    private void SyncSelectionPolicy()
    {
        _selection.Mode = SelectionMode;
    }

    /// <summary>
    /// Publish after compound model updates, so observers see only the final packet
    /// and current item.
    /// </summary>
    private Selection CommitSelection()
    {
        Selection next = new(BuildSelectedPacket(), _selection.Current);
        bool changed = !SameSelection(_publishedSelection, next);
        _publishedSelection = next;
        if (!_detached)
        {
            ApplySelectionToContainers();
            RowVisualsChanged?.Invoke(this, EventArgs.Empty);
        }
        if (changed)
            SelectionChanged?.Invoke(this, next);
        return next;
    }

    private static bool SameSelection(Selection left, Selection right)
    {
        if (left.Items.Count != right.Items.Count || !ReferenceEquals(left.Current, right.Current))
        {
            return false;
        }
        for (int index = 0; index < left.Items.Count; index++)
        {
            if (!ReferenceEquals(left.Items[index], right.Items[index]))
            {
                return false;
            }
        }
        return true;
    }

    private IReadOnlyList<object> BuildSelectedPacket()
    {
        List<object> packet = new();
        foreach (object item in View)
        {
            if (_selection.IsSelected(item))
            {
                packet.Add(item);
            }
        }

        return packet;
    }

    /// <summary>
    /// The container selects itself on a press, a tap, and Space, and removing a row drops it from
    /// the list's selection. Every one of those arrives here, and every one of them is overwritten
    /// with the table's own state. Measured: without this the row surface and the table disagree
    /// after a real click.
    /// </summary>
    private void OnSurfaceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A reconcile raises this synchronously on each removal, with a row the table has not
        // finished moving already gone. Reading the list there would record a selection that was
        // never real; RebuildView re-applies the state itself once the whole snapshot is applied.
        if (!_syncingContainers && !_reconcilingView)
        {
            ApplySelectionToContainers();
        }
    }

    /// <summary>
    /// Bring the row surface's selection to the table's. Only the rows that differ are touched, and
    /// they are touched as index ranges: the list's <c>SelectedItems</c> is a vector, so adding rows
    /// one at a time is a container update per row and a linear search per check, while
    /// <c>SelectRange</c> and <c>DeselectRange</c> are one call per run of rows.
    /// </summary>
    private void ApplySelectionToContainers()
    {
        if (_detached || _surface is null || _syncingContainers)
        {
            return;
        }

        int count = View.Count;
        if (count == 0)
        {
            return;
        }

        bool[] listed = new bool[count];
        foreach (ItemIndexRange range in _surface.SelectedRanges)
        {
            int last = Math.Min(range.LastIndex, count - 1);
            for (int i = Math.Max(range.FirstIndex, 0); i <= last; i++)
            {
                listed[i] = true;
            }
        }

        _syncingContainers = true;
        try
        {
            int start = -1;
            bool selecting = false;

            for (int i = 0; i <= count; i++)
            {
                bool wanted = i < count && _selection.IsSelected(View[i]);
                bool differs = i < count && wanted != listed[i];

                if (start >= 0 && (!differs || wanted != selecting))
                {
                    ApplyRange(start, i - start, selecting);
                    start = -1;
                }

                if (differs && start < 0)
                {
                    start = i;
                    selecting = wanted;
                }
            }
        }
        finally
        {
            _syncingContainers = false;
        }
    }

    private void ApplyRange(int first, int length, bool select)
    {
        ItemIndexRange range = new(first, (uint)length);
        if (select)
        {
            _surface!.SelectRange(range);
        }
        else
        {
            _surface!.DeselectRange(range);
        }
    }
}
