using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace Syno.TableView;

/// <summary>
/// The table-owned selection surface of specification sections 5, 5.3 and 13. The table is the
/// single interactive selection owner: the hosted list's own <c>SelectionChanged</c> is never
/// treated as truth, and the authoritative state is re-applied to it after every change.
/// </summary>
public sealed partial class Table
{
    /// <summary>
    /// On by default. A marquee is a selection gesture, so it is meaningful in every table, and
    /// design decision 16 rules that a press nothing else competes for must not be a dead press.
    /// </summary>
    public static readonly DependencyProperty IsMarqueeEnabledProperty =
        DependencyProperty.Register(
            nameof(IsMarqueeEnabled),
            typeof(bool),
            typeof(Table),
            new PropertyMetadata(true, OnMarqueeChanged));

    /// <summary>
    /// Off by default, and the asymmetry with the marquee is sayable: a reorder is a domain request
    /// and means something only where the host owns an order, which most tables do not.
    /// </summary>
    public static readonly DependencyProperty CanReorderProperty =
        DependencyProperty.Register(
            nameof(CanReorder),
            typeof(bool),
            typeof(Table),
            new PropertyMetadata(false, OnReorderChanged));

    /// <summary>
    /// Section 7's escape hatch for a custom interactive control the table cannot recognize. Set
    /// to false on the control's root or an ancestor inside a cell template.
    /// </summary>
    public static readonly DependencyProperty IsRowGestureEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsRowGestureEnabled",
            typeof(bool),
            typeof(Table),
            new PropertyMetadata(true));

    private readonly ItemIdentity _identity = new();

    private Selection _publishedSelection = Selection.Empty;
    private Selection? _pendingSelection;
    private object? _schema;
    private Type? _rowType;
    private ListViewSelectionMode _selectionMode = ListViewSelectionMode.Extended;

    /// <summary>Set while the table is writing the hosted list's selection, to stop re-entry.</summary>
    private bool _syncingContainers;

    /// <summary>Set while a snapshot is being applied to the private view.</summary>
    private bool _reconcilingView;

    /// <summary>
    /// Raised once after any completed selection or current-item change, including the ones caused
    /// by assignment and source reconciliation. Replacement instances and packet order changes
    /// also notify, so observers always receive the exposed rows.
    /// </summary>
    public event EventHandler<Selection>? SelectionChanged;

    /// <summary>Raised after the table has processed the input that invoked a row.</summary>
    public event EventHandler<ItemInvokedEventArgs>? ItemInvoked;

    /// <summary>
    /// Raised after the table has applied section 15's context mechanics, so a handler that reads
    /// <see cref="SelectedItems"/> sees the packet the menu will act on.
    /// </summary>
    public event EventHandler<ItemContextRequestedEventArgs>? ItemContextRequested;

    private EventHandler<ReorderRequestedEventArgs>? _reorderRequested;

    /// <summary>
    /// Raised once for a completed row drag that asks for a new order. The table has changed
    /// nothing: it never mutates the source, and it does not infer that the host accepted the
    /// request.
    /// </summary>
    public event EventHandler<ReorderRequestedEventArgs>? ReorderRequested
    {
        add => _reorderRequested += value;
        remove => _reorderRequested -= value;
    }

    /// <summary>Row visuals re-read the table's selected and current state when this fires.</summary>
    internal event EventHandler? RowVisualsChanged;

    /// <summary>Setup-only. Default <see cref="ListViewSelectionMode.Extended"/>.</summary>
    public ListViewSelectionMode SelectionMode
    {
        get => _selectionMode;
        set
        {
            RequireSetup();
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _selectionMode = value;
        }
    }

    /// <summary>
    /// State the row type once, and hand over the identity selector, the interaction predicate and
    /// every column's sort key with it. Setup-only, like <see cref="Columns"/>: the table captures
    /// the schema at its first <c>Loaded</c> and asking for one afterwards is a configuration error.
    /// </summary>
    public Schema<TRow> Schema<TRow>() where TRow : class
    {
        RequireSetup();
        if (_schema is Schema<TRow> existing) return existing;
        if (_schema is not null) throw ConfigurationError("A table has one schema row type.");
        _rowType = typeof(TRow);
        Schema<TRow> schema = new(this);
        _schema = schema;
        return schema;
    }

    internal void RequireSetup()
    {
        if (_schemaCaptured)
            throw ConfigurationError("The schema is fixed at first Loaded.");
    }

    /// <summary>
    /// A stable key per item, from <see cref="Schema{TRow}"/>. Without one identity is
    /// object reference.
    /// </summary>
    internal Func<object, object>? ItemKey { get; set; }

    internal IEqualityComparer<object> KeyComparer { get; set; } = EqualityComparer<object>.Default;

    /// <summary>
    /// Which items the user may act on, from <see cref="Schema{TRow}"/>. Null means all of them.
    /// The predicate is fixed; what it answers for an item need not be, and the table does not
    /// watch for that. Section 5.3's rule covers it: after a change to anything the predicate
    /// reads, the host calls <see cref="RefreshView"/> once, and the rows re-read their
    /// eligibility and the cursor that shows it there.
    /// </summary>
    internal Func<object, bool>? CanInteract { get; set; }

    /// <summary>
    /// The selected packet and the current row. Reading gives the state that stands; assigning is
    /// an idempotent request for a different one, resolved against the eligible rows of the current
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

    public bool IsMarqueeEnabled
    {
        get => (bool)GetValue(IsMarqueeEnabledProperty);
        set => SetValue(IsMarqueeEnabledProperty, value);
    }

    public bool CanReorder
    {
        get => (bool)GetValue(CanReorderProperty);
        set => SetValue(CanReorderProperty, value);
    }

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
        if (_itemsView?.ItemsPanelRoot is not Panel panel)
        {
            return Array.Empty<int>();
        }

        List<int> indices = new(panel.Children.Count);
        foreach (UIElement child in panel.Children)
        {
            int index = _itemsView.IndexFromContainer(child);
            if (index >= 0 && ReferenceEquals(_itemsView.ContainerFromIndex(index), child))
            {
                indices.Add(index);
            }
        }

        indices.Sort();
        return indices;
    }

    internal bool IsRowSelected(object? item) => item is not null && _selection.IsSelected(item);

    internal bool IsRowCurrent(object? item) =>
        item is not null && _selection.IsSame(item, _selection.Current);

    /// <summary>Section 5.3: withdrawing the marquee mid-gesture cancels it before the flag applies.</summary>
    private static void OnMarqueeChanged(
        DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Table table && !(bool)e.NewValue && table._gesture == RowGesture.Marquee)
        {
            table.RestoreSelectionBeforeMarquee();
            table.CommitSelection();
        }
    }

    /// <summary>
    /// Section 5.3: withdrawing reordering mid-drag cancels it, and raises no request. Either way
    /// the rows re-read whether they can be dragged, because the cursor they show says so.
    /// </summary>
    private static void OnReorderChanged(
        DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Table table)
        {
            return;
        }

        if (!(bool)e.NewValue)
        {
            table.CancelRowDrag();
        }

        table.RowVisualsChanged?.Invoke(table, EventArgs.Empty);
    }

    private int IndexInView(object? item)
    {
        if (item is null)
        {
            return -1;
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

    public static void SetIsRowGestureEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsRowGestureEnabledProperty, value);

    public static bool GetIsRowGestureEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsRowGestureEnabledProperty);

    /// <summary>
    /// Section 15: an already-selected row keeps the whole selected packet, and any other row
    /// becomes the selection. Both make the row current, focused, and the next range anchor, so a
    /// context request on an already-selected row still reports the moved current item.
    /// </summary>
    private void SelectForContext(object item) => _selection.SetSelection(
        _selection.IsSelected(item) ? SelectedItems : new[] { item }, item, View);

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
    private void RebuildView(IReadOnlyList<object>? snapshot = null, IReadOnlyList<object>? preparedOrder = null)
    {
        snapshot ??= _source.Snapshot;
        if (!_schemaCaptured)
        {
            _source.Accept(snapshot);
            return;
        }
        IReadOnlyList<object> order;
        if (preparedOrder is null)
        {
            ValidateRows(snapshot);
            order = ViewOrder(snapshot);
        }
        else
        {
            order = preparedOrder;
            _orderSettledAt = DateTimeOffset.UtcNow;
        }
        _source.Accept(snapshot);
        if (_detached)
        {
            _view.Reconcile(order, Array.Empty<int>());
            ReconcileSelection(FocusState.Unfocused);
            return;
        }
        // Section 5.3 cancels a live gesture for a view-changing update. The test is whether the
        // view actually changed, not whether the source published: the original host published about once
        // a second and cancelling on each one made both gestures unusable, a marquee dying on the
        // next publish and a row drag dying under the pointer. A marquee is never
        // cancelled here at all — the rectangle has not moved, so what it covers is re-derived
        // over the new view below. A row drag is, but only once the order beneath it moved, which
        // is the moment its destination stopped meaning what the user aimed at.

        // Capture how the rows hold focus, not merely that they do, and capture it before the view
        // changes. Once the focused row's container is gone the framework has already rescued
        // focus, carrying that container's state to whatever it landed on, which is not the row's.
        FocusState rowFocus = RowSurfaceFocusState();
        // Remove keyboard focus before containers leave so native focus rescue cannot
        // carry a row's keyboard cue onto an unrelated control.
        if (rowFocus != FocusState.Unfocused) _itemsView?.Focus(FocusState.Pointer);

        // The hosted list keeps a selection of its own and revises it on every single removal and
        // insertion. A re-sort of 2,002 rows once sent it about 3,800 of those, under the reconcile
        // that announced every row it moved; that reconcile is gone, and the harness the figure came
        // from has been deleted, so the 3,800 cannot be reproduced. The current reconcile raises a
        // notification only for a realized position, 20 to 60 of them here, and none of that
        // bookkeeping survives regardless: the table's own selection is re-applied a few lines below,
        // over whatever the list decided. Taking the list out of selection for the duration removes
        // that work per notification rather than once.
        ListViewSelectionMode hosted = _itemsView?.SelectionMode ?? ListViewSelectionMode.None;

        bool viewMoved;
        _reconcilingView = true;
        try
        {
            if (_itemsView is not null)
            {
                _itemsView.SelectionMode = ListViewSelectionMode.None;
            }

            viewMoved = _view.Reconcile(order, RealizedIndices());
        }
        finally
        {
            if (_itemsView is not null)
            {
                _itemsView.SelectionMode = hosted;
            }

            _reconcilingView = false;
        }

        CancelRowDrag();

        if (viewMoved)
        {
            // The panel has to be told to look again. Rows placed without a notification are
            // invisible to it, so a reorder gives it no reason to re-examine which rows it should
            // be holding, and it goes on holding the ones it had: the owner watched a sort leave
            // the foot of the viewport blank until something else happened to poke it seconds
            // later. This is the one thing the list must be told when it has been told nothing
            // else, and it says only "look", not what changed.
            _itemsView?.ItemsPanelRoot?.InvalidateMeasure();
        }

        UpdateStateLayer();
        ReconcileSelection(rowFocus);
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

    /// <summary>
    /// Re-apply the table-owned state after a snapshot. The hosted list drops a row from its own
    /// selection as soon as that row is removed, and a reorder is a removal, so its selection and
    /// its focus are restored from the model here rather than trusted.
    /// </summary>
    private void ReconcileSelection(FocusState rowFocus)
    {
        SyncSelectionPolicy();

        if (_pendingSelection is Selection pending)
        {
            _pendingSelection = null;
            _selection.SetSelection(pending.Items, pending.Current, View);
        }
        else
        {
            _selection.Reconcile(View);
        }
        if (_gesture == RowGesture.Marquee)
        {
            _marquee.Refresh();
            _selection.SetMarqueeSelection(MarqueeItems(), View);
        }

        if (!_detached) RestoreRowFocus(rowFocus);
        CommitSelection();
    }

    // ------------------------------------------------------------------ commit

    /// <summary>The setup-only policy the model needs. Read live; changing it late is a config error.</summary>
    private void SyncSelectionPolicy()
    {
        _selection.Mode = SelectionMode;
        _selection.Eligible = CanInteract;
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
        if (changed) SelectionChanged?.Invoke(this, next);
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
    /// with the table's own state. Measured: without this the hosted list and the table disagree
    /// after a real click.
    /// </summary>
    private void OnHostedSelectionChanged(object sender, SelectionChangedEventArgs e)
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
    /// Bring the hosted list's selection to the table's. Only the rows that differ are touched, and
    /// they are touched as index ranges: the list's <c>SelectedItems</c> is a vector, so adding rows
    /// one at a time is a container update per row and a linear search per check, while
    /// <c>SelectRange</c> and <c>DeselectRange</c> are one call per run of rows.
    /// </summary>
    private void ApplySelectionToContainers()
    {
        if (_detached || _itemsView is null || _syncingContainers)
        {
            return;
        }

        int count = View.Count;
        if (count == 0)
        {
            return;
        }

        bool[] listed = new bool[count];
        foreach (ItemIndexRange range in _itemsView.SelectedRanges)
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
            _itemsView!.SelectRange(range);
        }
        else
        {
            _itemsView!.DeselectRange(range);
        }
    }
}
