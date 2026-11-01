using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace Syno.TableView;

/// <summary>
/// The table-owned selection surface of specification sections 5, 5.3 and 13. The table is the
/// single interactive selection owner: the row surface reads each row's selected state from the
/// private view, which answers from the model here, and keeps no selection of its own.
/// </summary>
public sealed partial class Table
{
    private Selection _publishedSelection = Selection.Empty;
    private Selection? _pendingSelection;
    private ListViewSelectionMode _selectionMode = ListViewSelectionMode.Extended;
    private Action? _cancelPreparation;
    private long _selectionItemsVersion = -1;

    private sealed record SelectionChange(Selection Packet, object[] Added, object[] Removed);

    /// <summary>
    /// Runs selection-dependent preparation on a worker thread. A newer selection,
    /// another preparation, or cancellation ends the wait and cancels this work.
    /// Call on the UI thread; marshal presentation back to it and check the token
    /// before applying results. The returned task reports completion or failure.
    /// </summary>
    public async Task PrepareSelection(
        Func<Selection, CancellationToken, Task> prepare,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        CancelPreparation();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var token = source.Token;
        Task? cancelling = null;
        Action cancel = () => cancelling ??= source.CancelAsync();
        _cancelPreparation = cancel;
        var selection = Selection;
        try
        {
            await Task.Run(() => prepare(selection, token), token).WaitAsync(token);
        }
        finally
        {
            if (_cancelPreparation == cancel)
                _cancelPreparation = null;
            cancel();
            await cancelling!;
        }
    }

    private void CancelPreparation()
    {
        _cancelPreparation?.Invoke();
    }

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
            if (SameSelection(_publishedSelection, value))
                return;
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
    private void SelectForContext(object item) => _selection.Context(item);

    /// <summary>
    /// Bring the table-owned state to the view a snapshot left, and put row focus back where it
    /// was: the framework moves it when the focused container leaves.
    /// </summary>
    private SelectionChange? ReconcileSelection(FocusState rowFocus, object? focusedItem = null,
        Body.Source.Preparation? preparation = null)
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
                CollapsedAncestor(_selection.Focus),
                preparation
            );
        }
        if (_gesture == RowGesture.Marquee)
        {
            _marquee.Refresh();
            _selection.RebaseMarquee(_marquee.CoveredIndices, _marquee.Bounds);
        }

        if (!_detached)
            RestoreRowFocus(rowFocus, focusedItem);
        SelectionChange? change = CommitPacket();
        if (_hierarchy is not null && _surface is { } surface)
            surface.RefreshHierarchy();
        return change;
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
        SelectionChange? change = CommitPacket();
        Selection packet = _publishedSelection;
        if (change is not null && !_detached)
        {
            RowVisualsChanged?.Invoke(this, EventArgs.Empty);
        }
        PublishSelection(change);
        return packet;
    }

    private SelectionChange? CommitPacket()
    {
        Selection previous = _publishedSelection;
        Selection next = _selectionItemsVersion != _selection.ItemsVersion
            ? new(_selection.BuildPacket(), _selection.Current)
            : ReferenceEquals(previous.Current, _selection.Current)
                ? previous : new(previous, _selection.Current);
        _selectionItemsVersion = _selection.ItemsVersion;
        if (!ReferenceEquals(previous.Current, next.Current))
        {
            CancelPage();
        }
        bool changed = !SameSelection(previous, next);
        if (!changed)
        {
            next = previous;
        }
        _publishedSelection = next;
        object[] added = changed && Body.Surface.SelectionListens ? _selection.Added : Array.Empty<object>();
        object[] removed = changed && Body.Surface.SelectionListens ? _selection.Removed : Array.Empty<object>();
        _selection.AcceptChanges();
        if (changed)
            CancelPreparation();
        if (!_detached)
        {
            RefreshContainers();
        }
        return changed ? new(next, added, removed) : null;
    }

    private void PublishSelection(SelectionChange? change)
    {
        if (change is null)
        {
            return;
        }
        if (!_detached && _surface is not null && Body.Surface.SelectionListens)
        {
            _surface.RaiseSelectionEvents(change.Added, change.Removed, change.Packet.Items.Count);
        }
        SelectionChanged?.Invoke(this, change.Packet);
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

    /// <summary>
    /// One range call makes the list refresh every cached and pinned container from ISelectionInfo.
    /// Its range does not limit that refresh, and the view does not change selection for the call.
    /// </summary>
    private void RefreshContainers()
    {
        if (_detached || _surface is null || View.Count == 0)
        {
            return;
        }

        foreach (int index in RealizedIndices())
        {
            if (_surface.ContainerFromIndex(index) is ListViewItem container
                && index < View.Count && container.IsSelected != _selection.IsSelected(View[index]))
            {
                ApplyRange(index, 1, _selection.IsSelected(View[index]));
                return;
            }
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
