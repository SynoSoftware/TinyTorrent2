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
    private long _publishVersion;
    private SuppressedTransitions? _suppressedTransitions;

    /// <summary>
    /// What the publishes since the last frame did to the rows that frame laid out, until the
    /// next frame lays the view out. Null while no publish has changed the view since that frame,
    /// and after the host scrolled the view.
    /// </summary>
    private LaidOutRows? _laidOut;

    private sealed record SuppressedTransitions(Body.Surface Surface, object LocalValue);

    /// <summary>
    /// The rows a frame laid out, followed through each later publish by the rows its difference
    /// names, so each publish can tell whether the view still has any of them without a copy of
    /// the view.
    /// </summary>
    /// <param name="count">
    /// How many rows the frame laid out, or 0 when the list was not scrolled, so no offset can
    /// go back to the top.
    /// </param>
    /// <param name="offset">The offset the frame laid the rows out at.</param>
    private sealed class LaidOutRows(int count, double offset, IIdentity identity)
    {
        // Rows the frame laid out that the view no longer has.
        private readonly IdentitySet _gone = new(identity);
        // Rows the view has that the frame did not lay out.
        private readonly IdentitySet _added = new(identity);

        internal int Count => count;
        internal double Offset => offset;

        /// <summary>Whether a publish since the frame set the offset to 0 for a different list.</summary>
        internal bool AtTop { get; set; }

        /// <summary>Whether the frame laid out rows and the view has none of them now.</summary>
        internal bool NoneRemain => count > 0 && _gone.Count == count;

        /// <summary>Record a row that leaves the view. Call it before the row arrives again.</summary>
        internal void Leave(object row)
        {
            if (!_added.Remove(row))
            {
                _gone.Add(row);
            }
        }

        internal void Arrive(object row)
        {
            if (!_gone.Remove(row))
            {
                _added.Add(row);
            }
        }
    }

    /// <summary>
    /// The whole order a new sort gives, with the change it was prepared from: the source's for
    /// a flat table, the hierarchy's for a hierarchical one.
    /// </summary>
    private sealed record PreparedSort(IReadOnlyList<object> Order, Body.Source.Preparation? Source,
        Hierarchy.Preparation? Hierarchy);

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

    /// <summary>
    /// Show each new instance <see cref="Body.View.Rebound"/> lists in the container that showed
    /// the old one. The container keeps its place and look, and its row element rebinds through
    /// its <c>DataContext</c>.
    /// </summary>
    /// <remarks>
    /// The list still records the old instance for that container, and its
    /// <c>ItemFromContainer</c> and <c>ContainerFromItem</c> answer from that record, so the
    /// table finds rows and containers by view position instead: see <see cref="RowItem"/> and
    /// <see cref="RowContainer"/>.
    /// </remarks>
    private void RebindRows()
    {
        foreach (int index in _view.Rebound)
        {
            if (_surface?.ContainerFromIndex(index) is ContentControl container)
            {
                container.Content = _view[index];
            }
        }
    }

    /// <summary>
    /// The current instance of the row <paramref name="container"/> shows; see <see
    /// cref="RebindRows"/>.
    /// </summary>
    private object? RowItem(DependencyObject container)
    {
        int index = _surface?.IndexFromContainer(container) ?? -1;
        return index >= 0 && index < _view.Count ? _view[index] : null;
    }

    /// <summary>
    /// The realized container of <paramref name="item"/>'s row, found by identity; see <see
    /// cref="RebindRows"/>.
    /// </summary>
    private DependencyObject? RowContainer(object item)
    {
        int index = IndexInView(item);
        return index < 0 ? null : _surface?.ContainerFromIndex(index);
    }

    /// <summary>
    /// The instance the list generated <paramref name="item"/>'s realized container for, which an
    /// automation peer must name for the list to find that container; see <see cref="RebindRows"/>.
    /// </summary>
    internal object ContainerItem(object item) =>
        RowContainer(item) is { } container && _surface!.ItemFromContainer(container) is { } generated
            ? generated
            : item;

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

    private void OnRowsRealizing(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        QueueRealized();
        if (!args.InRecycleQueue)
        {
            // One delegate for every container, so each registration reuses one native wrapper.
            args.RegisterUpdateCallback(_showCurrent ??= ShowCurrent);
        }
    }

    private Windows.Foundation.TypedEventHandler<ListViewBase, ContainerContentChangingEventArgs>? _showCurrent;

    /// <summary>
    /// Put the view's instance into a container the list filled with an older instance of the
    /// same row. The list fills a container from the instance it recorded when it prepared it:
    /// after the preparation event, or, for a placeholder shown while scrolling, in a later pass.
    /// A publish in between gave the row a new instance (<see cref="RebindRows"/>), which the
    /// fill then replaces.
    /// </summary>
    /// <remarks>
    /// The list calls this after it has filled and measured the container, and measures it again
    /// afterwards, so the row element takes the new instance as its data context in the same
    /// pass. Setting the content from the row element's own <c>DataContextChanged</c> does not
    /// work: the list raises that while it applies the data context, and drops a change made
    /// there.
    /// </remarks>
    private void ShowCurrent(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is ContentControl { Content: { } shown } container
            && ResolveItem(shown) is { } current && !ReferenceEquals(current, shown))
        {
            container.Content = current;
        }
    }

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

    private bool IsInView(object row) => _view.Positions.IndexOf(row) >= 0;

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
    /// Unavailable identities are ignored. Only the named rows' keys are read, unless a withheld
    /// change is due, in which case every key is.
    /// </summary>
    public void RefreshView(IEnumerable<object> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        RebuildView(change: new Change.Values(changed));
    }

    // ------------------------------------------------------------------ view reconciliation

    /// <summary>
    /// Apply a source change, under the current sort, to the private view and then to the
    /// table-owned selection. Specification 5.3 leaves the mechanism open — "the implementation may
    /// update a private view incrementally or rebuild it, but the observable result MUST be the
    /// same" — and the observable result is settled below, not by the collection notifications:
    /// selection is re-applied from the table's own model, and at most one event is raised.
    /// </summary>
    /// <remarks>
    /// Every step takes the change as the source classified it and applies one rule to the rows
    /// it affects. The order's producer states how its order differs from its previous one when
    /// it knows; the view takes that difference when it shows the previous order, and otherwise
    /// compares itself with the order.
    /// </remarks>
    /// <param name="sort">The whole order a new sort gave, and what it was prepared from.</param>
    private void RebuildView(Change? change = null, PreparedSort? sort = null)
    {
        _publishVersion++;
        bool completed = false;
        bool rowVisualsChanged = true;
        SelectionChange? selectionChange = null;
        Hierarchy.Preparation? hierarchy = null;
        try
        {
            change ??= new Change.Whole(_source.Current);
            Body.Source.Capture capture = change switch
            {
                Change.Whole whole => whole.Capture,
                Change.Collection collection => collection.Capture,
                _ => _source.Current,
            };
            if (!_schemaCaptured)
            {
                _source.Store(capture, validated: false);
                return;
            }
            bool hostScrolled = _scrolledAssignment?.Assignment == capture.AssignmentVersion;
            IReadOnlyList<object> order;
            IdentitySet pinned;
            Body.Source.Preparation? affected = null;
            Body.View.Origin origin;
            bool keepsView = false;
            if (_hierarchy is null)
            {
                Body.Source.Preparation source = sort?.Source ?? _source.Prepare(change, ValidateRows);
                (order, pinned, affected, origin) = sort is null ? ViewOrder(source) : SortedView(source, sort.Order);
                _source.Accept(source);
            }
            else
            {
                hierarchy = sort?.Hierarchy ?? _hierarchy.Prepare(change, capture, ValidateRows);
                // A list under a collapsed or hidden parent changes no shown row.
                keepsView = sort is null && hierarchy.ChangesNoShownRow(IsInView);
                (order, pinned, affected, origin) =
                    keepsView ? (_view, _held, null, Body.View.Origin.FromView(Body.View.Difference.None))
                    : sort is null ? ViewOrder(hierarchy)
                    : SortedView(null, sort.Order);
                _source.Store(capture, validated: true);
            }
            Body.View.Difference difference = _view.Compare(order, origin);
            bool changed = !difference.IsEmpty;
            if (changed)
            {
                CancelPage();
            }
            if (keepsView)
            {
                rowVisualsChanged = hierarchy!.AcceptHidden(IsInView);
            }
            else if (hierarchy is not null)
            {
                hierarchy.Accept();
                _hierarchy!.UpdatePresentation(order, changed);
            }
            if (_detached || !changed)
            {
                if (changed)
                {
                    _view.Reconcile(order, difference, origin.Version, Array.Empty<int>(), pinned);
                }
                else
                {
                    _view.RecordShown(origin.Version);
                    ReconcileDrag();
                    UpdatePlaceholder();
                }
                selectionChange = ReconcileSelection(FocusState.Unfocused, preparation: affected);
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
                    ? RowItem(focusedRow)
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
            {
                long publish = _publishVersion;
                _surface?.Focus(FocusState.Pointer);
                if (_publishVersion != publish)
                {
                    // A focus handler changed the source, and its publish already showed this
                    // change with its own, so this order and difference are stale.
                    return;
                }
            }

            IReadOnlyList<int> realized = RealizedIndices();
            object[]? reordered = FollowLaidOutRows(order, difference, hostScrolled);
            PrepareTransitions(order);
            _view.Reconcile(order, difference, origin.Version, realized, pinned);
            RebindRows();
            if (reordered is not null)
            {
                foreach (object row in reordered)
                {
                    if (!IsInView(row))
                    {
                        _laidOut!.Leave(row);
                    }
                }
            }
            StartDifferentList();

            ReconcileDrag();
            QueueRealized();

            // The panel has to be told to look again. Rows placed without a notification are
            // invisible to it, so a reorder gives it no reason to re-examine which rows it should
            // be holding, and it goes on holding the ones it had: the owner watched a sort leave
            // the foot of the viewport blank until something else happened to poke it seconds
            // later. This is the one thing the list must be told when it has been told nothing
            // else, and it says only "look", not what changed.
            _surface?.ItemsPanelRoot?.InvalidateMeasure();
            _layoutPending = true;
            RequestFrame();

            UpdatePlaceholder();
            object? retained = focusedItem is null
                ? null
                : ResolveItem(focusedItem) ?? CollapsedAncestor(focusedItem);
            if (collapsing.Item is not null && rowFocus != FocusState.Unfocused)
            {
                ScrollIntoView(collapsing.Item);
            }
            selectionChange = ReconcileSelection(rowFocus, retained, affected);
            completed = true;
        }
        finally
        {
            hierarchy?.Complete();
            _source.Complete();
            _sortedOrder?.Complete();
            if (completed && ShowsSource)
            {
                // The publish showed the list the host scrolled for, so its ScrollTo is spent.
                if (_scrolledAssignment is { Offset: double offset } scrolled
                    && scrolled.Assignment == _source.Current.AssignmentVersion)
                {
                    _pendingScroll = offset;
                    ApplyPendingScroll();
                }
                _scrolledAssignment = null;
            }
            if (completed && !_detached && rowVisualsChanged)
            {
                RowVisualsChanged?.Invoke(this, EventArgs.Empty);
            }
            if (completed)
            {
                PublishSelection(selectionChange);
            }
        }
    }

    /// <summary>
    /// Follow the rows the last frame laid out through this publish, before it reconciles: the
    /// first publish since that frame takes the view as the frame laid it out. Nothing is
    /// followed when there is no offset to take back: the frame laid out no rows or the list at
    /// the top, or this publish shows the list the host scrolled for.
    /// </summary>
    /// <returns>
    /// For a reorder that names no rows, the view's rows before it, which leave unless the
    /// reconciled view still has them; otherwise null.
    /// </returns>
    private object[]? FollowLaidOutRows(IReadOnlyList<object> order, Body.View.Difference difference,
        bool hostScrolled)
    {
        if (hostScrolled || !IsLoaded)
        {
            _laidOut = null;
            return null;
        }
        if (_laidOut is null)
        {
            double offset = InnerScrollViewer()?.VerticalOffset ?? 0;
            _laidOut = new(offset > 0 ? _view.Count : 0, offset, _identity);
        }
        LaidOutRows laidOut = _laidOut;
        if (laidOut.Count == 0)
        {
            return null;
        }
        if (difference.Moves == RowMoves.Unnamed)
        {
            // The reconcile walks every row anyway.
            foreach (object row in order)
            {
                if (!IsInView(row))
                {
                    laidOut.Arrive(row);
                }
            }
            return _view.ToArray();
        }
        foreach (int at in difference.Leaving)
        {
            laidOut.Leave(_view[at]);
        }
        foreach (int at in difference.Arriving)
        {
            laidOut.Arrive(order[at]);
        }
        return null;
    }

    /// <summary>
    /// A different list starts at its first row (specification 5.3): when the view has rows and
    /// none of the rows the last frame laid out, a scrolled list goes to the top. Each publish
    /// sets the offset for the view it leaves, so the frame lays the list out once, from the
    /// right place. A turn can show one list in several publishes, as a Clear and then one Add
    /// per row does, so a later publish that brings a laid-out row back returns the offset the
    /// frame laid out; the frame lays out only what the last of them left.
    /// </summary>
    private void StartDifferentList()
    {
        if (_laidOut is not { } laidOut)
        {
            return;
        }
        bool different = laidOut.NoneRemain && _view.Count > 0;
        if (different != laidOut.AtTop && InnerScrollViewer() is { } scroller)
        {
            scroller.ChangeView(null, different ? 0 : laidOut.Offset, null, disableAnimation: true);
            laidOut.AtTop = different;
        }
    }

    private void PrepareTransitions(IReadOnlyList<object> order)
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
            int previous = _view.Positions.IndexOf(order[index]);
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
        // The rows this frame lays out are the ones the next publish compares with.
        _laidOut = null;
        ShowSettle();
        QueuedSort? queued = _queuedSort;
        PageNavigation? page = _pendingPage is { Stage: not PageStage.Scrolling } ready
            ? ready : null;
        PageStage? stage = page?.Stage;
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
                {
                    return;
                }
                if (version == _view.Version)
                {
                    _layoutPending = false;
                    RestoreTransitions();
                    Reveal();
                    if (page is not null && ReferenceEquals(_pendingPage, page) && page.Stage == stage)
                    {
                        CompletePage(page);
                    }
                }
                else
                {
                    RequestFrame();
                }
                if (queued is not null)
                {
                    ConsumeSort(queued);
                }
            }
            catch (Exception error)
            {
                // Raw dispatcher exceptions bypass Application.UnhandledException
                // (microsoft/microsoft-ui-xaml#10447); retain the host's error policy.
                WinRT.ExceptionHelpers.ReportUnhandledError(error);
            }
        }))
        {
            CancelPage();
            RestoreTransitions();
        }
    }

    /// <summary>The whole order a new sort gives, prepared but not accepted.</summary>
    private PreparedSort PrepareSort(SortedOrder? workspace, SortDirection direction, SortWork? work = null)
    {
        Body.Source.Capture capture = _source.Current;
        if (_hierarchy is null)
        {
            Body.Source.Preparation source = _source.Prepare(new Change.Whole(capture), ValidateRows);
            IReadOnlyList<object> order = work is not null
                ? work.Sorting.Prepare(capture.Rows, work.PublishVersion != _publishVersion)
                : workspace is null ? capture.Rows
                : workspace.Prepare(SortChange.Whole(capture.Rows, capture.Rows), direction, allowMoves: true);
            return new(order, source, null);
        }

        Hierarchy.Preparation prepared = _hierarchy.Prepare(new Change.Whole(capture), capture, ValidateRows);
        try
        {
            IReadOnlyList<object> order = work?.Hierarchy is { } groups
                ? _hierarchy.Project(prepared, groups, work.Sorting, work.PublishVersion != _publishVersion)
                : _hierarchy.Project(prepared, workspace, direction, SiblingSort.All).Order;
            return new(order, null, prepared);
        }
        catch
        {
            prepared.Complete();
            throw;
        }
    }

    private SortWork CaptureSort(SortedOrder workspace, SortDirection direction)
    {
        Body.Source.Capture capture = _source.Current;
        if (_hierarchy is null)
        {
            try
            {
                _source.Prepare(new Change.Whole(capture), ValidateRows);
                return new(workspace, workspace.CaptureWork(new[] { capture.Rows }, direction), null,
                    _publishVersion);
            }
            finally
            {
                _source.Complete();
            }
        }
        Hierarchy.Preparation prepared = _hierarchy.Prepare(new Change.Whole(capture), capture, ValidateRows);
        try
        {
            var groups = _hierarchy.Groups(prepared, out Hierarchy.Work hierarchy);
            return new(workspace, workspace.CaptureWork(groups, direction), hierarchy, _publishVersion);
        }
        finally
        {
            prepared.Complete();
        }
    }

    private void RequestFrame()
    {
        if (_frameRequested || !IsLoaded)
        {
            return;
        }
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
