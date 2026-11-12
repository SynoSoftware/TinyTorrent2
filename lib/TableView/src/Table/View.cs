using System.Diagnostics;
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

    /// <summary>
    /// The <see cref="Stopwatch"/> timestamp of the frame that laid out the last view change, where
    /// the rows it inserted start their <see cref="ArrivalDelay"/>.
    /// </summary>
    private long _laidOutAt;

    /// <summary>
    /// How long a row the list inserts stays invisible before it fades in. The platform does not
    /// publish its insert transition's timing, so this is the measured value in the remarks on
    /// <see cref="_layoutPending"/>.
    /// </summary>
    private static readonly TimeSpan ArrivalDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>The rows' slots and what preparations keep by slot; created with the schema's identity.</summary>
    private Body.Slots? _slots;

    /// <summary>The private view, in current visual row order.</summary>
    internal IReadOnlyList<object> View => _view.Rows;

    /// <summary>
    /// <see cref="RealizedIndices"/> as last read, or null once a container's row, a view change or
    /// the template may have changed them.
    /// </summary>
    private IReadOnlyList<int>? _realized;

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
    /// <para>
    /// The answer is kept until it can have changed: the list raises
    /// <c>ContainerContentChanging</c> for every container it fills or recycles, and the view's own
    /// notices move indices. A walk of the panel costs two native calls per container, and every
    /// preparation asked twice.
    /// </para>
    /// </remarks>
    private IReadOnlyList<int> RealizedIndices()
    {
        if (_realized is not null)
        {
            return _realized;
        }

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
        _realized = indices;
        return indices;
    }

    /// <summary>
    /// How many leading positions the list's cache reaches: the visible rows and the cache around
    /// them, without a pinned container far from them.
    /// </summary>
    /// <remarks>
    /// Never more than the positions the list knows. The panel reports its last layout, and an
    /// update that removed rows inside the cache can be followed by the next preparation before
    /// the panel lays out again; a reach past the list's count would announce a change at a
    /// position the list has not been told of, which the view refuses with an exception.
    /// </remarks>
    private int Reach() =>
        _surface?.ItemsPanelRoot is ItemsStackPanel rows && rows.LastCacheIndex >= 0
            ? Math.Min(rows.LastCacheIndex + 1, _view.Count)
            : 0;

    /// <summary>Set while a refresh of <see cref="RealizedItems"/> waits on the queue.</summary>
    private bool _realizing;

    private readonly List<object> _realizedRead = [];

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
    /// Show in each realized container the row the view now holds at its position, where the
    /// reconcile put a new instance of the same row, or a different list's row, without a
    /// notification. The container keeps its place, and its row element rebinds through its
    /// <c>DataContext</c>.
    /// </summary>
    /// <remarks>
    /// The list still records the old instance for that container, and its
    /// <c>ItemFromContainer</c> and <c>ContainerFromItem</c> answer from that record, so the
    /// table finds rows and containers by view position instead: see <see cref="RowItem"/> and
    /// <see cref="RowContainer"/>. A container on a vacant position shows no row.
    /// </remarks>
    /// <param name="below">
    /// Positions from here on are left for <see cref="QueueRebind"/>; all are rebound when null.
    /// </param>
    private void RebindRows(int? below = null)
    {
        bool left = false;
        foreach (int index in RealizedIndices())
        {
            if (index >= below)
            {
                left = true;
                continue;
            }

            if (_surface?.ContainerFromIndex(index) is ContentControl container)
            {
                object? row = index < View.Count ? View[index] : null;
                if (!ReferenceEquals(container.Content, row))
                {
                    container.Content = row;
                }
            }
        }

        if (left)
        {
            QueueRebind();
        }
    }

    /// <summary>Set while held containers below the screen wait for <see cref="RebindRows"/>.</summary>
    private bool _rebinding;

    /// <summary>
    /// Show the rows of a different list in the held containers below the screen at low priority,
    /// after the layout that shows the screen, or at once when the rows start to scroll
    /// (<see cref="OnRowsScrolling"/>).
    /// </summary>
    /// <remarks>
    /// The list holds half a viewport of rows past each edge of the screen, and a different list
    /// starts at the top, so about a third of the containers it holds after a switch are below the
    /// screen. Showing a row costs every cell its content and a measure: in the 10 November trace
    /// cell layout and fill took about 490 ms of a 3,159 ms click phase. Rows nobody sees wait,
    /// and a click that switches again first drops them.
    /// </remarks>
    private void QueueRebind()
    {
        if (_rebinding)
        {
            return;
        }

        WatchScrolling();
        _rebinding = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            FlushRebind
        );
    }

    private void FlushRebind()
    {
        if (!_rebinding)
        {
            return;
        }

        _rebinding = false;
        RebindRows();
    }

    /// <summary>The current instance of the row <paramref name="container"/> shows; see <see cref="RebindRows"/>.</summary>
    private object? RowItem(DependencyObject container)
    {
        int index = _surface?.IndexFromContainer(container) ?? -1;
        return index >= 0 && index < View.Count ? View[index] : null;
    }

    /// <summary>The realized container of <paramref name="item"/>'s row, found by identity; see <see cref="RebindRows"/>.</summary>
    private DependencyObject? RowContainer(object item)
    {
        int index = IndexInView(item);
        return index < 0 ? null : _surface?.ContainerFromIndex(index);
    }

    /// <summary>
    /// The row the container the list generated for <paramref name="generated"/> shows, or, where
    /// the list holds no container for it, that row's current instance; see <see cref="RebindRows"/>.
    /// </summary>
    internal object? ShownRow(object generated) =>
        _surface?.ContainerFromItem(generated) is { } container
            ? RowItem(container)
            : ResolveItem(generated);

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
        _realized = null;
        QueueRealized();
        if (!args.InRecycleQueue)
        {
            // One delegate for every container, so each registration reuses one native wrapper.
            args.RegisterUpdateCallback(_showCurrent ??= ShowCurrent);
        }
    }

    private Windows.Foundation.TypedEventHandler<ListViewBase, ContainerContentChangingEventArgs>? _showCurrent;

    /// <summary>
    /// Put the view's row into a container the list filled with the row it recorded. The list
    /// fills a container from the instance it recorded when it prepared it: after the preparation
    /// event, or, for a placeholder shown while scrolling, in a later pass. A publish in between
    /// gave the position a new instance or a different list's row (<see cref="RebindRows"/>), which
    /// the fill then replaces. So is an empty fill, should the list have recorded the placeholder
    /// an arriving row was announced as (<see cref="Body.View.CatchUp(long)"/>).
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
        if (args.ItemContainer is ContentControl container
            && RowItem(container) is { } current && !ReferenceEquals(current, container.Content))
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
                List<object> items = ReadRealized();
                if (items.SequenceEqual(RealizedItems, ReferenceEqualityComparer.Instance))
                {
                    return;
                }

                RealizedItems = items.ToArray();
                RealizedItemsChanged?.Invoke(this, RealizedItems);
            }
        );
    }

    /// <summary>
    /// Read from the panel's cache range, not its children as <see cref="RealizedIndices"/> is:
    /// a container left from before a scroll keeps its old row without showing it. The list pins
    /// the focused row's container outside that range, so that row is added.
    /// </summary>
    /// <returns>
    /// A list this method reuses on its next call, because most reads find the rows unchanged.
    /// </returns>
    private List<object> ReadRealized()
    {
        List<object> items = _realizedRead;
        items.Clear();
        if (_detached || _surface?.ItemsPanelRoot is not ItemsStackPanel rows)
        {
            return items;
        }

        int first = rows.FirstCacheIndex;
        int last = Math.Min(rows.LastCacheIndex, View.Count - 1);
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

        if (_slots is not null && !_reconcilingView)
        {
            return _slots.IndexOf(item);
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
    /// <see cref="CanInteract"/> depends on, in rows the host does not name. Every row's sort key
    /// is read again. It re-sorts and reconciles, and does not re-enumerate the source.
    /// </summary>
    public void RefreshView()
    {
        _allChanged = true;
        _changed.Clear();
        _refreshed = true;
        RebuildView();
    }

    /// <summary>
    /// Re-evaluate the current source snapshot after a batch changed values of
    /// <paramref name="changed"/> that the active sort or <see cref="CanInteract"/> depends on.
    /// Only those rows' sort keys are read again, so the work follows the rows named. The table
    /// keeps the names until the rows take their sorted places, so a refresh the host skips
    /// loses none. A row the source does not have is ignored. An <see cref="ItemsSource"/> set
    /// in the same turn reaches the view in the same update.
    /// </summary>
    public void RefreshView(IEnumerable<object> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        _refreshed = true;
        if (!_allChanged)
        {
            _changed.AddRange(changed);

            // A table that cannot prepare, such as one out of the tree, would otherwise collect
            // names without limit, and more names than rows cost more than reading every row.
            if (_changed.Count > _source.Snapshot.Count)
            {
                RefreshView();
                return;
            }
        }

        RebuildView();
    }

    // ------------------------------------------------------------------ view reconciliation

    private bool _preparingView;
    private bool _sortChangedPending;
    private long _viewVersion;
    private TaskCompletionSource? _viewReady;

    /// <summary>The keys a sort or layout request read on the UI thread, until a preparation keeps them.</summary>
    private (Column Column, Body.SortKeys Keys)? _readKeys;

    /// <summary>Rows <see cref="RefreshView(IEnumerable{object})"/> named since a preparation last took them.</summary>
    private List<object> _changed = [];

    /// <summary><see cref="RefreshView()"/> ran since a preparation last took the names.</summary>
    private bool _allChanged;

    /// <summary>
    /// A refresh ran since the view last applied an update, so whether a row is interactive may
    /// have changed, and the rows read it again (<see cref="ApplyProjection"/>).
    /// </summary>
    private bool _refreshed;

    /// <summary>Cancels the preparation running on the worker; see <see cref="Supersede"/>.</summary>
    private CancellationTokenSource? _preparation;

    /// <summary>
    /// After the first Loaded, completes when the latest requested source and sort have reached
    /// the displayed view. Native layout may still be pending.
    /// </summary>
    public Task ViewReady => _viewReady?.Task ?? Task.CompletedTask;

    private void RebuildView(
        IReadOnlyList<object>? snapshot = null, bool sortChanged = false, Body.SortKeys? readKeys = null)
    {
        if (snapshot is not null)
        {
            _source.Accept(snapshot);
            _hierarchy?.Invalidate();
        }
        if (!_schemaCaptured)
            return;
        Supersede();
        _sortChangedPending |= sortChanged;
        if (sortChanged)
            _readKeys = readKeys is null ? null : (_sortColumn!.Column, readKeys);
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
            // Every request made in this turn reaches the view in one preparation. A host that
            // sets a new list and names the rows whose values changed makes two calls, and
            // starting on the first would prepare the update twice.
            var version = _viewVersion;
            await Task.Yield();
            bool realizedMoved = false;
            while (!_detached)
            {
                if (realizedMoved || version != _viewVersion)
                {
                    version = _viewVersion;
                    input = CaptureProjection();
                }
                var run = _allChanged || _changed.Count > 0 ? input with { Refresh = TakeRefresh() } : input;
                var slots = _slots!;
                using var cancellation = new CancellationTokenSource();
                _preparation = cancellation;
                var token = cancellation.Token;
                var preparation = Task.Run(() => new Body.Projection(run, slots).Prepare(token), token);
                await ((Task)preparation).ConfigureAwait(
                    ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
                _preparation = null;
                // A cancelled preparation may have stopped before it marked the rows the host
                // named, and the host names a row only once.
                if (preparation.IsCanceled)
                    RestoreRefresh(run.Refresh);
                // The preparation kept these keys in the slots, discarded or not; a cancelled one
                // may have stopped before it did.
                if (!preparation.IsCanceled && input.Ordering.Read is { } read && ReferenceEquals(_readKeys?.Keys, read))
                    _readKeys = null;
                if (_detached)
                    break;
                realizedMoved = !input.Screen.Realized.SequenceEqual(RealizedIndices());
                if (realizedMoved || version != _viewVersion)
                    continue;
                var prepared = await preparation;
                _sortChangedPending = false;
                ApplyProjection(prepared, input);

                // Section 10: a sort command's first order and the first rows after a fill that
                // found none replace what the screen shows, so a fill then moves nothing the
                // person did not already change. While this preparation runs the fill waits, and
                // the finally block below queues it for the layout that shows these rows. A
                // different list replaces the screen too, but fills only once the person stays
                // on it: section 10 gives the cost of a fill at every list.
                if (input.Ordering.Changed || (_filledEmpty && View.Count > 0))
                {
                    _filledEmpty = false;
                    FillViewport();
                }
                else if (prepared.Changes.IsDifferentList)
                {
                    DelayFill();
                }

                if (version != _viewVersion)
                    continue;
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
                QueueCatchUp();
            }
        }
    }

    /// <summary>
    /// A newer request makes the preparation in flight obsolete: its result cannot publish, and
    /// its work on the worker stops at the next point it checks.
    /// </summary>
    private void Supersede()
    {
        _viewVersion++;
        _preparation?.Cancel();
    }

    /// <summary>Hand the rows RefreshView named to a preparation, which marks their keys stale in the slots.</summary>
    private Body.Projection.Refresh TakeRefresh()
    {
        var refresh = new Body.Projection.Refresh(_changed, _allChanged);
        _changed = [];
        _allChanged = false;
        return refresh;
    }

    /// <summary>Give back what <see cref="TakeRefresh"/> handed to a preparation that was cancelled.</summary>
    private void RestoreRefresh(Body.Projection.Refresh refresh)
    {
        if (refresh.All)
        {
            _allChanged = true;
            _changed.Clear();
        }
        else if (!_allChanged)
        {
            _changed.AddRange(refresh.Rows);
        }
    }

    private Body.Projection.Input CaptureProjection()
    {
        var source = _source.Snapshot;
        var hierarchy = _hierarchy is null ? null : CaptureHierarchy(source);
        var previous = _hierarchy?.Captured;
        return new Body.Projection.Input(
            new Body.Projection.Rows
            {
                Source = source,
                Identity = _identity,
                RowType = _rowType,
                Hierarchy = hierarchy,
                PreviousHierarchy = previous,
                Expanded = hierarchy?.Nodes.Values.Where(node => _hierarchy!.IsExpanded(node.Item))
                    .Select(node => node.Item).ToHashSet(ReferenceEqualityComparer.Instance)
                    ?? Body.Projection.Rows.None,
                Expandable = hierarchy?.Nodes.Values.Where(node => _hierarchy!.IsExpandable(node))
                    .Select(node => node.Item).ToHashSet(ReferenceEqualityComparer.Instance)
                    ?? Body.Projection.Rows.None,
            },
            new Body.Projection.Ordering
            {
                Keys = SortsLocally ? _sortColumn!.Column.SortKeys : null,
                Read = SortsLocally && _readKeys is { } read && read.Column == _sortColumn!.Column
                    ? read.Keys
                    : null,
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
                Held = _held,
                Released = _released,
                Lifetime = ReleaseLifetime,
                Releases = _releases.ToArray(),
                Caused = _personChange == PersonChange.Pending,
            },
            new Body.Changes.Screen(RealizedIndices(), Reach()));
    }

    private object KeyOf(object item) => ItemKey?.Invoke(item) ?? item;

    private void ApplyProjection(Body.Projection.Prepared prepared, Body.Projection.Input input)
    {
        var settles = prepared.Settles;
        // The rows read their held look, whether they can be acted on or dragged, and their
        // hierarchy cells again only when the update may have changed one of them. Whether a row
        // can be dragged follows the order's rows, which any update can change.
        bool visuals = _refreshed || _hierarchy is not null || _order is not null
            || (!ReferenceEquals(_held, prepared.Held) && !_held.SetEquals(prepared.Held));
        _refreshed = false;
        _held = prepared.Held;
        _releases.RemoveRange(0, input.Holding.Releases.Count);
        _released = prepared.Released;
        _shownSource = input.Rows.Source;
        if (input.Holding.Caused)
        {
            _personChange = PersonChange.None;
        }
        if (prepared.StructureChanged)
            _orderSettledAt = null;
        if (_hierarchy is not null && input.Rows.Hierarchy is { } hierarchy)
        {
            _hierarchy.Accept(hierarchy);
            _hierarchy.HasChildren = prepared.HasChildren;
        }
        if (prepared.SettleAfter is { } due)
            ScheduleSettle(due);
        if (prepared.Changes.Edits.Length == 0)
        {
            _slots!.Commit();
            ReconcileDrag();
            UpdatePlaceholder();
            // The same rows in the same instances leave the selection as it stands.
            if (visuals || _pendingSelection is not null)
            {
                ReconcileSelection(FocusState.Unfocused, null, visuals);
            }
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
            _slots!.Commit();
            _view.Apply(prepared.Changes);
            viewMoved = prepared.Changes.Changed;
        }
        finally
        {
            _reconcilingView = false;
        }
        prepared.Changes.Return();
        _realized = null;
        // The first slice of a change in count goes with the update; QueueCatchUp tells the rest.
        // A position past the last row that the list builds a container for shows the platform's
        // placeholder. A different list keeps every container where it is unless the table
        // scrolls it to the top, and any other update can bring such a position into the cache, so
        // when the rows end inside the cache the update tells the whole change in count.
        bool inPlace = prepared.Changes.IsDifferentList
            && InnerScrollViewer() is not { VerticalOffset: > 0 };
        bool whole = View.Count < input.Screen.Reach && !inPlace;
        CatchUp(whole ? long.MaxValue : Stopwatch.GetTimestamp() + SliceTicks);
        bool tops = prepared.Changes.IsDifferentList && _pendingScroll is null;
        RebindRows(tops ? ScreenRows() : null);

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
            WaitForLayout();
        }

        UpdatePlaceholder();
        object? retained = focusedItem is null
            ? null
            : ResolveItem(focusedItem)
                ?? CollapsedAncestor(focusedItem);
        if (collapsing.Item is not null && rowFocus != FocusState.Unfocused)
            ScrollIntoView(collapsing.Item);
        bool showedSelection = _publishedSelection.Items.Count != 0;
        ReconcileSelection(rowFocus, retained, visuals);
        if (prepared.Changes.IsDifferentList)
            ShowDifferentList(showedSelection);
    }

    /// <summary>
    /// How many rows the screen shows, read from the last layout: the positions a different list
    /// shows once it starts at the top. Null before the list has laid out a row.
    /// </summary>
    private int? ScreenRows() =>
        _surface?.ItemsPanelRoot is ItemsStackPanel rows && rows.FirstVisibleIndex >= 0
            ? rows.LastVisibleIndex - rows.FirstVisibleIndex + 1
            : null;

    /// <summary>
    /// Show a different list, which <see cref="RebindRows"/> put into the old list's containers,
    /// from its first row (contract section 5.3). A host's <see cref="ScrollTo"/> waiting for this
    /// view keeps the host's offset.
    /// </summary>
    /// <param name="showedSelection">
    /// The old list had selected rows. A container that showed one now shows a row of the new
    /// list, and the table names only rows the view still has to the list, so the list reads every
    /// container's selection again.
    /// </param>
    private void ShowDifferentList(bool showedSelection)
    {
        if (_detached || _surface is null)
        {
            return;
        }

        if (_pendingScroll is null && InnerScrollViewer() is { VerticalOffset: > 0 } scroller)
        {
            WatchScrolling();
            _ownScroll = scroller.ChangeView(null, 0, null, disableAnimation: true);
        }

        IReadOnlyList<int> realized = RealizedIndices();
        if (showedSelection && realized.Count > 0)
        {
            ApplyRange(realized[0], realized[^1] - realized[0] + 1, select: false);
        }
    }

    // ------------------------------------------------------------------ the change in count

    /// <summary>
    /// How long one slice of a change in count may run: a few milliseconds, so input waits at most
    /// that long however many rows the count changes by.
    /// </summary>
    private static readonly long SliceTicks = Stopwatch.Frequency * 4 / 1000;

    /// <summary>A slice of the change in count waits on the queue.</summary>
    private bool _catchingUp;

    /// <summary>The rows are scrolling, so rows that arrive wait; see <see cref="CatchUpSlice"/>.</summary>
    private bool _scrolling;

    /// <summary>The scroll viewer whose view changes <see cref="_scrolling"/> follows.</summary>
    private ScrollViewer? _watched;

    /// <summary>
    /// The table's own scroll to a different list's first row is under way. It recycles the
    /// containers the list holds at the old offset, which then show the new list's rows anyway,
    /// so the rows below the screen wait for <see cref="QueueRebind"/> instead of the scroll.
    /// </summary>
    private bool _ownScroll;

    /// <summary>Tell the list the change in count it is owed, until <paramref name="deadline"/>.</summary>
    private void CatchUp(long deadline)
    {
        if (_view.Behind)
        {
            _view.CatchUp(deadline);
            _realized = null;
        }
    }

    /// <summary>
    /// Tell the list the rest of a change in count in slices below input and below normal posted
    /// work (contract section 5.3). A newer update replaces what is left, because each slice tells
    /// the list what the view owes it now.
    /// </summary>
    private void QueueCatchUp()
    {
        if (_catchingUp || !_view.Behind || _detached)
        {
            return;
        }

        WatchScrolling();
        _catchingUp = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            CatchUpSlice
        );
    }

    /// <remarks>
    /// Slices pause while a preparation runs, because its update can undo what they would tell,
    /// and the update queues the next slice. Rows wait to arrive while the rows scroll, because
    /// the list plays the insert animation on every container it builds in a layout tick with an
    /// insert (<see cref="_layoutPending"/>), and the rows a scroll brings in would stay blank for
    /// 300 ms; the scroll's end queues the next slice.
    /// </remarks>
    private void CatchUpSlice()
    {
        _catchingUp = false;
        if (_detached || _preparingView || (_scrolling && _view.Arriving))
        {
            return;
        }

        CatchUp(Stopwatch.GetTimestamp() + SliceTicks);
        QueueCatchUp();
    }

    /// <summary>
    /// Tell the list of the rows up to <paramref name="item"/>'s at once, so it can scroll to that
    /// row. The scroll then waits for the frame, because it would build its rows in the tick of
    /// these insertions (<see cref="_layoutPending"/>).
    /// </summary>
    private void CatchUpTo(object item)
    {
        int index = IndexInView(item);
        if (!_view.Arriving || index < _view.Count)
        {
            return;
        }

        _view.CatchUpTo(index);
        _realized = null;
        WaitForLayout();
    }

    /// <summary>Note that the list has a view change to lay out; see <see cref="_layoutPending"/>.</summary>
    private void WaitForLayout()
    {
        if (!_layoutPending)
        {
            _layoutPending = true;
            CompositionTarget.Rendering += OnLaidOut;
        }
    }

    private void WatchScrolling()
    {
        if (InnerScrollViewer() is not ScrollViewer scroller || ReferenceEquals(scroller, _watched))
        {
            return;
        }

        if (_watched is not null)
        {
            _watched.ViewChanging -= OnRowsScrolling;
            _watched.ViewChanged -= OnRowsScrolled;
        }

        _watched = scroller;
        _scrolling = false;
        scroller.ViewChanging += OnRowsScrolling;
        scroller.ViewChanged += OnRowsScrolled;
    }

    /// <summary>The rows start or keep scrolling: held rows below the screen show their rows first.</summary>
    private void OnRowsScrolling(object? sender, ScrollViewerViewChangingEventArgs args)
    {
        _scrolling = true;
        if (!_ownScroll)
        {
            FlushRebind();
        }
    }

    private void OnRowsScrolled(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!args.IsIntermediate)
        {
            _scrolling = false;
            _ownScroll = false;
            QueueCatchUp();
        }
    }

    private void OnLaidOut(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnLaidOut;
        _laidOutAt = Stopwatch.GetTimestamp();
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
