using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Syno.TableView;

public sealed partial class Table
{
    /// <summary>Set while a snapshot is being applied to the private view.</summary>
    private bool _reconcilingView;

    /// <summary>
    /// The list has not laid out this turn's view change in a frame yet. A reveal waits for the
    /// frame after that layout; see <see cref="ScrollIntoView(object)"/>.
    /// </summary>
    /// <remarks>
    /// The list counts the rows inserted and removed per layout tick, and in a tick with an insert
    /// it plays the insert animation on every container it builds: invisible for 300 ms, then a
    /// fade. A scroll in that tick to a row that has no container yet makes the panel recycle every
    /// row and build them all again, and a scroll to a row off screen builds the rows it brings in,
    /// so the rows built that way go blank for those 300 ms. The tick ends with the frame.
    /// <c>CompositionTarget.Rendering</c> fires only inside a frame, after that frame's layout, so it
    /// clears this. <c>LayoutUpdated</c> also fires from a synchronous <c>UpdateLayout</c> in the
    /// same tick, which would clear it too early.
    /// </remarks>
    private bool _layoutPending;
    private Dictionary<object, (int Index, int Sibling)>? _positions;

    /// <summary>The private view, in current visual row order.</summary>
    internal IReadOnlyList<object> View => _view;

    /// <summary>
    /// The view positions the list is holding a container for. Everywhere else it keeps nothing but
    /// the count and reads the row when it realizes the position, so those are the only positions a
    /// reorder has to be announced at.
    /// </summary>
    /// <remarks>
    /// Read from the panel's children rather than from <c>FirstCacheIndex</c> to
    /// <c>LastCacheIndex</c>. A pinned container — the focused row's, above all — lives outside that
    /// range, and so does one the panel has not recycled yet: the comment in
    /// <see cref="Body.Marquee"/> records a container still answering for row 0 after the range had
    /// moved to 51..73. Changing a row quietly under a container that still answers for its index is
    /// the one failure this must not have, so the test is deliberately generous: a container counts
    /// if it names an index and the list hands that index back to it.
    /// </remarks>
    private IReadOnlyList<int> RealizedIndices()
    {
        if (_surface?.ItemsPanelRoot is not Panel panel)
        {
            return Array.Empty<int>();
        }

        List<int> indices = new(panel.Children.Count);
        foreach (UIElement child in panel.Children)
        {
            int index = _surface.IndexFromContainer(child);
            if (index >= 0 && ReferenceEquals(_surface.ContainerFromIndex(index), child))
            {
                indices.Add(index);
            }
        }

        indices.Sort();
        return indices;
    }

    private int IndexInView(object? item)
    {
        if (item is null)
        {
            return -1;
        }

        if (_positions is not null && !_reconcilingView)
        {
            int index = _positions.TryGetValue(item, out var position) ? position.Index : -1;
            if (index < 0)
                return -1;
            if (index >= 0 && index < View.Count && _selection.IsSame(View[index], item))
                return index;
        }

        for (int i = 0; i < View.Count; i++)
        {
            if (_selection.IsSame(View[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    internal object? ResolveItem(object item)
    {
        var index = IndexInView(item);
        return index < 0 ? null : View[index];
    }

    /// <summary>
    /// Re-evaluate the current source snapshot after a batch changed values the active sort or
    /// <see cref="CanInteract"/> depends on. It re-sorts and reconciles, and does not
    /// re-enumerate the source.
    /// </summary>
    public void RefreshView() => RebuildView();

    // ------------------------------------------------------------------ view reconciliation

    /// <summary>
    /// Apply the current snapshot, under the current sort, to the private view and then to the
    /// table-owned selection. Specification 5.3 leaves the mechanism open — "the implementation may
    /// update a private view incrementally or rebuild it, but the observable result MUST be the
    /// same" — and the observable result is settled below, not by the collection notifications:
    /// selection is re-applied from the table's own model, and at most one event is raised.
    /// </summary>
    private void RebuildView(
        IReadOnlyList<object>? snapshot = null,
        IReadOnlyList<object>? preparedOrder = null
    )
    {
        bool capture = snapshot is not null;
        snapshot ??= _source.Snapshot;
        if (!_schemaCaptured)
        {
            _source.Accept(snapshot);
            return;
        }
        IReadOnlyList<object> order;
        IReadOnlySet<object> pinned;
        if (preparedOrder is null)
        {
            if (_hierarchy is null)
            {
                ValidateRows(snapshot);
                (order, pinned) = ViewOrder(snapshot);
            }
            else
            {
                DateTimeOffset settled = _orderSettledAt;
                try
                {
                    (order, pinned) = ViewOrder(PrepareHierarchy(snapshot, capture));
                }
                catch
                {
                    _orderSettledAt = settled;
                    _preparedHierarchy = null;
                    throw;
                }
            }
        }
        else
        {
            // A sort the person or host asked for moves every row, so nothing is held through it.
            order = preparedOrder;
            var (_, held, released) = Hold(snapshot, sortChanged: true);
            AcceptHold(held, released);
            pinned = held;
            _orderSettledAt = DateTimeOffset.UtcNow;
        }
        _source.Accept(snapshot);
        AcceptOrder(order);
        if (_detached)
        {
            _view.Reconcile(order, Array.Empty<int>(), pinned);
            ReconcileSelection(FocusState.Unfocused);
            return;
        }
        if (order.SequenceEqual(View, ReferenceEqualityComparer.Instance))
        {
            ReconcileDrag();
            UpdatePlaceholder();
            ReconcileSelection(FocusState.Unfocused);
            return;
        }

        // Capture how the rows hold focus, not merely that they do, and capture it before the view
        // changes. Once the focused row's container is gone the framework has already rescued
        // focus, carrying that container's state to whatever it landed on, which is not the row's.
        FocusState rowFocus = RowSurfaceFocusState();
        object? focusedItem =
            rowFocus != FocusState.Unfocused
            && FocusManager.GetFocusedElement(XamlRoot) is ListViewItem focusedRow
                ? _surface?.ItemFromContainer(focusedRow)
                : null;
        var collapsing = CollapsingFocus();
        if (collapsing.Item is not null)
        {
            focusedItem = collapsing.Item;
            rowFocus = collapsing.State;
        }
        // Remove keyboard focus before containers leave so native focus rescue cannot
        // carry a row's keyboard cue onto an unrelated control.
        if (rowFocus != FocusState.Unfocused)
            _surface?.Focus(FocusState.Pointer);

        bool viewMoved;
        _reconcilingView = true;
        try
        {
            viewMoved = _view.Reconcile(order, RealizedIndices(), pinned);
        }
        finally
        {
            _reconcilingView = false;
        }

        ReconcileDrag();

        if (viewMoved)
        {
            // The panel has to be told to look again. Rows placed without a notification are
            // invisible to it, so a reorder gives it no reason to re-examine which rows it should
            // be holding, and it goes on holding the ones it had: the owner watched a sort leave
            // the foot of the viewport blank until something else happened to poke it seconds
            // later. This is the one thing the list must be told when it has been told nothing
            // else, and it says only "look", not what changed.
            _surface?.ItemsPanelRoot?.InvalidateMeasure();
            if (!_layoutPending)
            {
                _layoutPending = true;
                CompositionTarget.Rendering += OnLaidOut;
            }
        }

        UpdatePlaceholder();
        object? retained = focusedItem is null
            ? null
            : View.FirstOrDefault(item => _selection.IsSame(item, focusedItem))
                ?? CollapsedAncestor(focusedItem);
        if (collapsing.Item is not null && rowFocus != FocusState.Unfocused)
            ScrollIntoView(collapsing.Item);
        ReconcileSelection(rowFocus, retained);
    }

    private void OnLaidOut(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnLaidOut;
        // The tick ends when the frame returns, so the note clears and the reveal runs past it.
        // A view change made inside a frame, from a Loaded or a SizeChanged handler, is laid out
        // after this event in the same frame, which is why the note cannot clear here.
        DispatcherQueue.TryEnqueue(() =>
        {
            _layoutPending = false;
            Reveal();
        });
    }

    private void ValidateRows(IReadOnlyList<object> snapshot)
    {
        HashSet<object> seen = new(_identity);
        foreach (object item in snapshot)
        {
            if (_rowType is not null && !_rowType.IsInstanceOfType(item))
                throw ConfigurationError($"The source contains a row outside {_rowType}.");
            if (ItemKey is not null && ItemKey(item) is null)
                throw ConfigurationError("The schema key selector returned null.");
            if (!seen.Add(item))
                throw ConfigurationError("The source contains duplicate row identities.");
        }
    }
}
