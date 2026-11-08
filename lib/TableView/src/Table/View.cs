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

    /// <summary>
    /// Show in each realized container the instance the view now holds at its position, where the
    /// reconcile put a new instance of the same row without a notification. The container keeps
    /// its place and look, and its row element rebinds through its <c>DataContext</c>.
    /// </summary>
    /// <remarks>
    /// The list still records the old instance for that container, and its
    /// <c>ItemFromContainer</c> and <c>ContainerFromItem</c> answer from that record, so the
    /// table finds rows and containers by view position instead: see <see cref="RowItem"/> and
    /// <see cref="RowContainer"/>.
    /// </remarks>
    private void RebindRows()
    {
        foreach (int index in RealizedIndices())
        {
            if (
                _surface?.ContainerFromIndex(index) is ContentControl container
                && !ReferenceEquals(container.Content, _view[index])
            )
            {
                container.Content = _view[index];
            }
        }
    }

    /// <summary>The current instance of the row <paramref name="container"/> shows; see <see cref="RebindRows"/>.</summary>
    private object? RowItem(DependencyObject container)
    {
        int index = _surface?.IndexFromContainer(container) ?? -1;
        return index >= 0 && index < _view.Count ? _view[index] : null;
    }

    /// <summary>The realized container of <paramref name="item"/>'s row, found by identity; see <see cref="RebindRows"/>.</summary>
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
        if (item is null)
        {
            return -1;
        }

        if (_positions is not null && !_reconcilingView)
        {
            int index = _positions.TryGetValue(KeyOf(item), out var position) ? position.Index : -1;
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

    private bool _preparingView;
    private bool _sortChangedPending;
    private long _viewVersion;
    private TaskCompletionSource? _viewReady;
    private IComparer<object>? _preparedSort;
    private IReadOnlyList<object> _displayed = [];
    private HashSet<object> _sourceKeys = [];

    /// <summary>
    /// After the first Loaded, completes when the latest requested source and sort have reached
    /// the displayed view. Native layout may still be pending.
    /// </summary>
    public Task ViewReady => _viewReady?.Task ?? Task.CompletedTask;

    private void RebuildView(
        IReadOnlyList<object>? snapshot = null, bool sortChanged = false, IComparer<object>? preparedSort = null)
    {
        if (snapshot is not null)
        {
            _source.Accept(snapshot);
            _hierarchy?.Invalidate();
        }
        if (!_schemaCaptured)
            return;
        _viewVersion++;
        _sortChangedPending |= sortChanged;
        _preparedSort = preparedSort;
        var input = _preparingView || _detached ? null : CaptureProjection();
        if (_viewReady?.Task.IsCompleted != false)
            _viewReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (input is not null)
            PrepareView(input);
    }

    private async void PrepareView(Body.Projection.Input input)
    {
        _preparingView = true;
        try
        {
            while (!_detached)
            {
                var version = _viewVersion;
                var preparation = Task.Run(() => new Body.Projection(input).Prepare());
                await ((Task)preparation).ConfigureAwait(
                    ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
                if (_detached)
                    break;
                if (version != _viewVersion || !input.Realized.SequenceEqual(RealizedIndices()))
                {
                    input = CaptureProjection();
                    continue;
                }
                var prepared = await preparation;
                _sortChangedPending = false;
                ApplyProjection(prepared, input);
                if (version != _viewVersion)
                {
                    input = CaptureProjection();
                    continue;
                }
                _viewReady!.TrySetResult();
                return;
            }
            _viewReady?.TrySetCanceled();
        }
        catch (Exception error)
        {
            _viewReady?.TrySetException(error);
            throw;
        }
        finally
        {
            _preparingView = false;
            if (_fillPending && !_detached)
            {
                _fillPending = false;
                QueueFill();
                InvalidateMeasure();
            }
            if (!_detached)
            {
                ApplyPendingScroll();
                if (!_layoutPending)
                    Reveal();
            }
        }
    }

    private Body.Projection.Input CaptureProjection()
    {
        var source = _source.Snapshot;
        var displayed = _displayed;
        var hierarchy = _hierarchy is null ? null : CaptureHierarchy(source);
        var previous = _hierarchy?.Captured;
        var expanded = hierarchy?.Nodes.Values.Where(node => _hierarchy!.IsExpanded(node.Item))
            .Select(node => node.Item).ToHashSet(ReferenceEqualityComparer.Instance)
            ?? new HashSet<object>(ReferenceEqualityComparer.Instance);
        return new Body.Projection.Input(
            new Body.Projection.Rows
            {
                Source = source,
                Displayed = displayed,
                Identity = _identity,
                RowType = _rowType,
                Hierarchy = hierarchy,
                PreviousHierarchy = previous,
                Expanded = expanded,
                Expandable = hierarchy?.Nodes.Values.Where(node => _hierarchy!.IsExpandable(node))
                    .Select(node => node.Item).ToHashSet(ReferenceEqualityComparer.Instance)
                    ?? new HashSet<object>(ReferenceEqualityComparer.Instance),
            },
            new Body.Projection.Ordering
            {
                Comparer = _preparedSort,
                Capture = SortsLocally ? _sortColumn!.Column.CaptureSort : null,
                Direction = _sortDirection,
                Local = SortsLocally,
                Reorders = Reorders,
                Changed = _sortChangedPending,
                Interval = UsesInterval ? _sortInterval : TimeSpan.Zero,
                SettledAt = _orderSettledAt,
                Showing = _settleShowing,
            },
            new Body.Projection.Holding
            {
                Pointed = IsPointed,
                PointedRow = _pointedRow,
                MenuRow = _menuRow,
                Released = _released,
                Lifetime = ReleaseLifetime,
                Releases = _releases.ToArray(),
            },
            RealizedIndices());
    }

    private object KeyOf(object item) => ItemKey?.Invoke(item) ?? item;

    private void ApplyProjection(Body.Projection.Prepared prepared, Body.Projection.Input input)
    {
        var order = prepared.Order;
        _displayed = order;
        var settles = prepared.Settles;
        _positions = prepared.Positions;
        _held = prepared.Held;
        _sourceKeys = prepared.SourceKeys;
        _releases.RemoveRange(0, input.Holding.Releases.Count);
        _released = prepared.Released;
        if (prepared.StructureChanged)
            _orderSettledAt = null;
        if (_hierarchy is not null && input.Rows.Hierarchy is { } hierarchy)
        {
            _hierarchy.Accept(hierarchy);
            _hierarchy.HasChildren = prepared.HasChildren;
        }
        if (prepared.SettleAfter is { } due)
            ScheduleSettle(due);
        if (prepared.Changes.Edits.Count == 0)
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
            _surface?.Focus(FocusState.Pointer);

        bool viewMoved;
        _reconcilingView = true;
        try
        {
            _view.Apply(prepared.Changes);
            viewMoved = prepared.Changes.Changed;
        }
        finally
        {
            _reconcilingView = false;
        }
        RebindRows();

        ReconcileDrag();
        QueueRealized();
        // A publish that only swapped row instances changed nothing on screen, so it starts no
        // interval.
        if (settles && viewMoved)
        {
            ShowSettle();
        }

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
            : ResolveItem(focusedItem)
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

}
