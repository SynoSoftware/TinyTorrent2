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
    /// The rows a scrolled view showed when a publish emptied it, until the next frame lays the
    /// empty view out. Until then they are the rows the person last saw, so a publish that fills
    /// the view again compares its rows with them.
    /// </summary>
    private object[]? _emptiedRows;

    private sealed record SuppressedTransitions(Body.Surface Surface, object LocalValue);

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
        StopSlide(args.ItemContainer);
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
            bool hostScrolled = _scrolledAssignment == capture.AssignmentVersion;
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
                keepsView = sort is null && hierarchy.LeavesShownRows(IsInView);
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
                _surface?.Focus(FocusState.Pointer);
            }

            IReadOnlyList<int> realized = RealizedIndices();
            int shownCount = _view.Count;
            if (order.Count == 0 && _emptiedRows is null && IsLoaded
                && InnerScrollViewer()?.VerticalOffset > 0)
            {
                _emptiedRows = _view.ToArray();
            }
            PrepareTransitions(order, TryCaptureSlides(order, difference, realized));
            _view.Reconcile(order, difference, origin.Version, realized, pinned);
            RebindRows();
            // A different list starts at its first row (specification 5.3), in the frame that
            // first shows it, unless the host scrolled after assigning it.
            if (!hostScrolled && IsDifferentList(difference, shownCount)
                && InnerScrollViewer() is { VerticalOffset: > 0 } scroller)
            {
                scroller.ChangeView(null, 0, null, disableAnimation: true);
            }

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
    /// Whether the reconciled view shows a different list: it has rows, and none of them was
    /// among the rows the person last saw. Those are the view before this publish, or the rows
    /// it showed before a publish emptied it since the last layout. A view the person saw empty
    /// shows no list to differ from.
    /// </summary>
    /// <param name="shownCount">The view's count before this publish.</param>
    private bool IsDifferentList(Body.View.Difference difference, int shownCount)
    {
        if (_view.Count == 0)
        {
            return false;
        }
        if (shownCount > 0)
        {
            // Every row left and every row arrived, and the difference names no row as moved.
            return difference.Moves == RowMoves.None && difference.Leaving.Count == shownCount
                && difference.Arriving.Count == _view.Count;
        }
        if (_emptiedRows is not { } seen)
        {
            return false;
        }
        foreach (object row in seen)
        {
            if (_view.Positions.IndexOf(row) >= 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <param name="sliding">
    /// The table slides the reordered rows itself; see <see cref="TryCaptureSlides"/>.
    /// </param>
    private void PrepareTransitions(IReadOnlyList<object> order, bool sliding)
    {
        if (_suppressedTransitions is not null || !IsLoaded || order.Count == 0
            || _surface?.ItemsPanelRoot is not ItemsStackPanel panel)
        {
            return;
        }
        if (sliding)
        {
            SuppressTransitions(_surface);
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
        SuppressTransitions(_surface);
    }

    private void SuppressTransitions(Body.Surface surface)
    {
        _suppressedTransitions = new(surface, surface.ReadLocalValue(ItemsControl.ItemContainerTransitionsProperty));
        surface.ItemContainerTransitions = new TransitionCollection();
        RequestFrame();
    }

    private void RestoreTransitions()
    {
        // The rows a capture recorded slide only inside the episode that captured them.
        _slideFrom = null;
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
        _emptiedRows = null;
        EndSuppression();
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
        }
    }

    /// <summary>
    /// End the layout episode whose transitions <see cref="SuppressTransitions"/> turned off: lay
    /// out what it changed, slide the rows it reordered, and give the list its transitions back,
    /// so a later publish animates as the list does.
    /// </summary>
    /// <remarks>
    /// The list decides a fade-out when it is told of a removal and a fade-in or move when it
    /// arranges, so the episode runs from the first notification to the end of this layout.
    /// </remarks>
    private void EndSuppression()
    {
        if (_suppressedTransitions is null)
        {
            return;
        }
        // The frame has normally laid the episode out already; a view change made inside the
        // frame is laid out here, so the containers stand where they are going.
        UpdateLayout();
        StartSlides();
        RestoreTransitions();
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
