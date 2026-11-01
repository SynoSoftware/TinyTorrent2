using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Syno.TableView;

public sealed partial class Table
{
    /// <summary>
    /// The list has not laid out this turn's view change in a frame yet. A reveal waits for the
    /// frame after that layout; see <see cref="ScrollIntoView(object)"/>.
    /// </summary>
    /// <remarks>
    /// With native transitions enabled, the list counts inserts and removals per layout tick
    /// and can delay newly built containers before fading them in. A scroll in that tick to a
    /// row that has no container yet makes the panel recycle every
    /// row and build them all again, and a scroll to a row off screen builds the rows it brings in,
    /// so the rows built that way can go blank during the arrival delay. The tick ends with the frame.
    /// <c>CompositionTarget.Rendering</c> fires only inside a frame, after that frame's layout, so it
    /// clears this. <c>LayoutUpdated</c> also fires from a synchronous <c>UpdateLayout</c> in the
    /// same tick, which would clear it too early.
    /// </remarks>
    private bool _layoutPending;
    private bool _frameRequested;
    private PositionIndex _candidatePositions;
    private long _publishVersion;
    private SuppressedTransitions? _suppressedTransitions;

    private sealed record SuppressedTransitions(Body.Surface Surface, object LocalValue);

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

    /// <summary>Set while a refresh of <see cref="RealizedItems"/> waits on the queue.</summary>
    private bool _realizing;

    /// <summary>
    /// The items whose rows the table realizes, in view order: the visible rows, about a quarter
    /// viewport above and below them, and the row that holds keyboard focus. A host that loads
    /// data only for rows on screen reads these. Empty while the table is out of the tree.
    /// </summary>
    public IReadOnlyList<object> RealizedItems { get; private set; } = Array.Empty<object>();

    /// <summary>
    /// Raised with the new <see cref="RealizedItems"/> after a scroll, a size change, a view
    /// change or a focus move changed them, once for all the rows one layout pass realized.
    /// </summary>
    public event EventHandler<IReadOnlyList<object>>? RealizedItemsChanged;

    private void OnRowsRealizing(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        QueueRealized();

    private void OnRowsResized(object sender, SizeChangedEventArgs args) => QueueRealized();

    private void OnRowsFocus(object sender, RoutedEventArgs args) => QueueRealized();

    private void QueueRealized()
    {
        if (_realizing)
        {
            return;
        }

        _realizing = true;
        // Low priority runs after the layout pass that moves the panel's cache range.
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                _realizing = false;
                IReadOnlyList<object> items = ReadRealized();
                if (items.SequenceEqual(RealizedItems, ReferenceEqualityComparer.Instance))
                {
                    return;
                }

                RealizedItems = items;
                RealizedItemsChanged?.Invoke(this, items);
            }
        );
    }

    /// <summary>
    /// Read from the panel's cache range, not its children as <see cref="RealizedIndices"/> is:
    /// a container left from before a scroll keeps its old row without showing it. The list pins
    /// the focused row's container outside that range, so that row is added.
    /// </summary>
    private IReadOnlyList<object> ReadRealized()
    {
        if (_detached || _surface?.ItemsPanelRoot is not ItemsStackPanel rows)
        {
            return Array.Empty<object>();
        }

        int first = rows.FirstCacheIndex;
        int last = Math.Min(rows.LastCacheIndex, View.Count - 1);
        List<object> items = new();
        for (int index = Math.Max(first, 0); index <= last; index++)
        {
            items.Add(View[index]);
        }

        if (
            RowSurfaceFocusState() != FocusState.Unfocused
            && FocusManager.GetFocusedElement(XamlRoot) is ListViewItem focused
        )
        {
            int pinned = _surface.IndexFromContainer(focused);
            if (pinned >= 0 && pinned < View.Count && (pinned < first || pinned > last))
            {
                items.Insert(pinned < first ? 0 : items.Count, View[pinned]);
            }
        }

        return items;
    }

    private int IndexInView(object? item)
    {
        return _view.IndexOfIdentity(item);
    }

    internal object? ResolveItem(object item)
    {
        var index = IndexInView(item);
        return index < 0 ? null : View[index];
    }

    /// <summary>
    /// Re-evaluate the source as last assigned or notified after a batch changed values the active
    /// sort or <see cref="CanInteract"/> depends on. It re-sorts and reconciles, and does not
    /// enumerate a non-indexable source again.
    /// </summary>
    public void RefreshView() => RebuildView();

    /// <summary>
    /// Re-evaluate the named items after their sort or interaction values changed. Include every
    /// changed item; omitted items can retain their previous order or interaction state.
    /// Unavailable identities are ignored. A valid flat delta reads only named keys and policy;
    /// due global settling, held/released membership recovery, stale source validation or a
    /// required comparer/global fallback prepares the whole order. Hierarchies project siblings.
    /// </summary>
    public void RefreshView(IEnumerable<object> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        RebuildView(change: new Change.Values(changed));
    }

    // ------------------------------------------------------------------ view reconciliation

    /// <summary>
    /// Apply the current snapshot, under the current sort, to the private view and then to the
    /// table-owned selection. Specification 5.3 leaves the mechanism open — "the implementation may
    /// update a private view incrementally or rebuild it, but the observable result MUST be the
    /// same" — and the observable result is settled below, not by the collection notifications:
    /// selection is re-applied from the table's own model, and at most one event is raised.
    /// </summary>
    private void RebuildView(
        Body.Source.Capture? capture = null,
        IReadOnlyList<object>? preparedOrder = null,
        IdentityMap<object>? preparedMembership = null,
        Change? change = null
    )
    {
        _publishVersion++;
        bool completed = false;
        SelectionChange? selectionChange = null;
        try
        {
            capture ??= change switch
            {
                Change.Whole whole => whole.Capture,
                Change.Collection collection => collection.Capture,
                _ => _source.Current,
            };
            IReadOnlyList<object> snapshot = capture.Rows;
            if (!_schemaCaptured)
            {
                _source.Accept(capture, validated: false);
                return;
            }
            Body.Source.Preparation? source = _hierarchy is null && preparedMembership is null
                ? _source.Prepare(change ?? new Change.Whole(capture), ValidateRows) : null;
            IdentityMap<object>? membership = _hierarchy is null
                ? preparedMembership ?? source!.Membership : null;
            IReadOnlyList<object> order;
            IdentitySet pinned;
            IReadOnlyList<Change.Edit>? edits = null;
            if (preparedOrder is null)
            {
                if (_hierarchy is null)
                {
                    (order, pinned, edits) = ViewOrder(snapshot, membership!, source);
                }
                else
                {
                    DateTimeOffset settled = _orderSettledAt;
                    try
                    {
                        Hierarchy.Projection projected = PrepareHierarchy(capture);
                        if (change is Change.Values values)
                        {
                            foreach (object item in values.Rows)
                            {
                                _ = _preparedHierarchy!.Nodes.Resolve(item);
                            }
                        }
                        (order, pinned, edits) = ViewOrder(projected);
                    }
                    catch
                    {
                        _orderSettledAt = settled;
                        throw;
                    }
                }
            }
            else
            {
                // A sort the person or host asked for moves every row, so nothing is held through it.
                order = preparedOrder;
                var (_, held, released) = Hold(snapshot, membership ?? _source.Membership, sortChanged: true);
                AcceptHold(held, released);
                pinned = held;
                _orderSettledAt = DateTimeOffset.UtcNow;
            }
            IIdentityKeys keys = source is not null ? source : membership is not null ? membership
                : _preparedHierarchy is { } preparedHierarchy ? preparedHierarchy : _hierarchy!.Captured!.Nodes;
            bool changed = edits is not null ? edits.Count > 0 : !_view.IsAlready(order);
            if (changed && edits is null)
            {
                _candidatePositions.Build(order, keys, _view.Positions);
            }
            if (source is null)
            {
                _source.Accept(capture, validated: true, membership);
            }
            else
            {
                _source.Accept(source);
            }
            AcceptHierarchy(order, changed);
            if (_detached)
            {
                if (changed)
                {
                    ReconcileRows(order, Array.Empty<int>(), pinned, edits, source);
                }
                selectionChange = ReconcileSelection(FocusState.Unfocused, preparation: source);
                completed = true;
                return;
            }
            if (!changed)
            {
                ReconcileDrag();
                UpdatePlaceholder();
                selectionChange = ReconcileSelection(FocusState.Unfocused, preparation: source);
                completed = true;
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
            var collapsing = CollapsingFocus(_candidatePositions);
            if (collapsing.Item is not null)
            {
                focusedItem = collapsing.Item;
                rowFocus = collapsing.State;
            }
            // Remove keyboard focus before containers leave so native focus rescue cannot
            // carry a row's keyboard cue onto an unrelated control.
            if (rowFocus != FocusState.Unfocused)
            {
                _surface?.Focus(FocusState.Pointer);
            }

            PrepareTransitions(order, keys);
            bool viewMoved = ReconcileRows(order, RealizedIndices(), pinned, edits, source);

            ReconcileDrag();
            QueueRealized();

            if (viewMoved)
            {
                // The panel has to be told to look again. Rows placed without a notification are
                // invisible to it, so a reorder gives it no reason to re-examine which rows it should
                // be holding, and it goes on holding the ones it had: the owner watched a sort leave
                // the foot of the viewport blank until something else happened to poke it seconds
                // later. This is the one thing the list must be told when it has been told nothing
                // else, and it says only "look", not what changed.
                _surface?.ItemsPanelRoot?.InvalidateMeasure();
                _layoutPending = true;
                RequestFrame();
            }

            UpdatePlaceholder();
            object? retained = focusedItem is null
                ? null
                : ResolveItem(focusedItem) ?? CollapsedAncestor(focusedItem);
            if (collapsing.Item is not null && rowFocus != FocusState.Unfocused)
            {
                ScrollIntoView(collapsing.Item);
            }
            selectionChange = ReconcileSelection(rowFocus, retained, source);
            completed = true;
        }
        finally
        {
            _preparedHierarchy?.Complete();
            _preparedHierarchy = null;
            _source.Complete();
            _sortedOrder?.Complete();
            if (completed && !_detached)
            {
                RowVisualsChanged?.Invoke(this, EventArgs.Empty);
            }
            if (completed)
            {
                PublishSelection(selectionChange);
            }
        }
    }

    private bool ReconcileRows(IReadOnlyList<object> order, IReadOnlyList<int> realized, IdentitySet pinned,
        IReadOnlyList<Change.Edit>? edits = null, Body.Source.Preparation? source = null)
    {
        if (edits is not null)
        {
            return _view.Reconcile(order, edits, source!, realized, pinned);
        }
        PositionIndex previous = _view.Positions;
        bool changed = _view.Reconcile(order, _candidatePositions, realized, pinned);
        _candidatePositions = previous;
        return changed;
    }

    private void PrepareTransitions(IReadOnlyList<object> order, IIdentityKeys keys)
    {
        if (_suppressedTransitions is not null || !IsLoaded || order.Count == 0
            || _surface?.ItemsPanelRoot is not ItemsStackPanel panel)
        {
            return;
        }
        int first = Math.Max(0, panel.FirstVisibleIndex);
        int last = Math.Min(panel.LastVisibleIndex, _view.Count - 1);
        if (first > last)
        {
            return;
        }
        for (int index = first; index <= Math.Min(last, order.Count - 1); index++)
        {
            object item = order[index];
            IIdentityKeys captured = keys.ContainsReference(item) ? keys : _view.Positions;
            int previous = _view.Positions.IndexOf(item, captured);
            if (previous >= first && previous <= last)
            {
                return;
            }
        }

        // Mixed native transitions remove the old rows before their 600 ms arrival delay.
        // Keep a disjoint visible replacement immediate until its layout tick has ended.
        _suppressedTransitions = new(_surface, _surface.ReadLocalValue(ItemsControl.ItemContainerTransitionsProperty));
        _surface.ItemContainerTransitions = new TransitionCollection();
        RequestFrame();
    }

    private void RestoreTransitions()
    {
        if (_suppressedTransitions is not { } saved)
        {
            return;
        }
        _suppressedTransitions = null;
        if (ReferenceEquals(saved.LocalValue, DependencyProperty.UnsetValue))
        {
            saved.Surface.ClearValue(ItemsControl.ItemContainerTransitionsProperty);
        }
        else
        {
            saved.Surface.SetValue(ItemsControl.ItemContainerTransitionsProperty, saved.LocalValue);
        }
    }

    private void OnLaidOut(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnLaidOut;
        _frameRequested = false;
        QueuedSort? queued = _queuedSort;
        long version = _view.Version;
        long lifetime = _lifetimeRevision;
        // The tick ends when the frame returns, so the note clears and the reveal runs past it.
        // A view change made inside a frame, from a Loaded or a SizeChanged handler, is laid out
        // after this event in the same frame, which is why the note cannot clear here.
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_detached || lifetime != _lifetimeRevision || !IsLoaded)
                    return;
                if (version == _view.Version)
                {
                    _layoutPending = false;
                    RestoreTransitions();
                    Reveal();
                }
                else
                    RequestFrame();
                if (queued is not null)
                    ConsumeSort(queued);
            }
            catch (Exception error)
            {
                // Raw dispatcher exceptions bypass Application.UnhandledException
                // (microsoft/microsoft-ui-xaml#10447); retain the host's error policy.
                WinRT.ExceptionHelpers.ReportUnhandledError(error);
            }
        }))
        {
            RestoreTransitions();
        }
    }

    private IReadOnlyList<object> PrepareSort(Body.Source.Capture capture, SortedOrder? workspace,
        SortDirection direction, out IdentityMap<object>? membership, SortWork? work = null)
    {
        membership = null;
        if (_hierarchy is null)
        {
            membership = _source.Prepare(capture, ValidateRows);
            if (work is not null)
            {
                return work.Sorting.Prepare(capture.Rows, membership, work.PublishVersion != _publishVersion);
            }
            return workspace is null ? capture.Rows
                : workspace.Prepare(capture.Rows, membership, direction, allowMoves: true).Rows;
        }

        DateTimeOffset settled = _orderSettledAt;
        try
        {
            Hierarchy.Projection projection = PrepareHierarchy(capture);
            return work?.Hierarchy is { } groups
                ? projection.Order(groups, work.Sorting, work.PublishVersion != _publishVersion)
                : projection.Order(workspace, direction);
        }
        catch
        {
            _orderSettledAt = settled;
            _preparedHierarchy?.Complete();
            _preparedHierarchy = null;
            throw;
        }
    }

    private SortWork CaptureSort(SortedOrder workspace, SortDirection direction)
    {
        Body.Source.Capture capture = _source.Current;
        if (_hierarchy is null)
        {
            IdentityMap<object> membership = _source.Prepare(capture, ValidateRows);
            return new(workspace, workspace.CaptureWork(new[] { capture.Rows }, membership, direction), null,
                _publishVersion);
        }
        DateTimeOffset settled = _orderSettledAt;
        try
        {
            Hierarchy.Projection projection = PrepareHierarchy(capture);
            SortedOrder.Work sorting = projection.CaptureWork(workspace, direction, out Hierarchy.Work hierarchy);
            return new(workspace, sorting, hierarchy, _publishVersion);
        }
        finally
        {
            _preparedHierarchy?.Complete();
            _preparedHierarchy = null;
            _orderSettledAt = settled;
        }
    }

    private void RequestFrame()
    {
        if (_frameRequested || !IsLoaded)
            return;
        _frameRequested = true;
        CompositionTarget.Rendering += OnLaidOut;
    }

    private void ValidateRows(object item)
    {
        if (item is null)
        {
            throw ConfigurationError(_hierarchy is null ? "ItemsSource contains a null row." : "The hierarchy contains a null row.");
        }
        if (_rowType is not null && !_rowType.IsInstanceOfType(item))
        {
            throw ConfigurationError($"The source contains a row outside {_rowType}.");
        }
    }
}
