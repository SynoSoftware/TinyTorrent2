using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TableView;

/// <summary>
/// A generic table surface: one vertical scrolling owner and one effective column layout shared
/// by the header strip and every realized row. Columns past the right edge are cut off; the table
/// never scrolls sideways.
/// </summary>
public sealed partial class Table : Control
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new FrameworkElementAutomationPeer(this);

    private const string HeaderStripPartName = "PART_HeaderStrip";
    private const string SurfacePartName = "PART_Surface";
    private const string PlaceholderPartName = "PART_Placeholder";
    private const string MarqueeOverlayPartName = "PART_MarqueeOverlay";
    private const string RowInsertionMarkerPartName = "PART_RowInsertionMarker";

    private readonly ItemIdentity _identity = new();

    private readonly Body.Source _source;
    private readonly Body.View _view;
    private readonly List<EffectiveColumn> _baselineOrder = new();
    private readonly SelectionState _selection;

    private Header.Strip? _headerStrip;
    private Body.Surface? _surface;
    private ContentPresenter? _placeholderPresenter;
    private FrameworkElement? _marqueeOverlay;
    private FrameworkElement? _rowInsertionMarker;

    private bool _schemaCaptured;
    private ColumnLayout? _pendingLayout;
    private double? _pendingScroll;

    public Table()
    {
        DefaultStyleKey = typeof(Table);
        _view = new Body.View(_identity);
        _held = new HashSet<object>(_identity);
        _released = new Dictionary<object, bool>(_identity);
        _selection = new SelectionState(_identity) { CanInteract = IsInteractive };
        _source = new Body.Source(DispatcherQueue);
        _source.SnapshotChanged += OnSnapshotChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Set while the table is out of the tree, so a settle that is already on the queue does not
    /// rebuild against template parts that have gone. Stopping the timer is not enough on its own:
    /// stopping it does not recall a tick the dispatcher has already picked up.
    /// </summary>
    private bool _detached;

    /// <summary>
    /// Raised once after each completed effective sort, column move, resize, fit, visibility, or
    /// reset, carrying which of those it was. Never raised by initial setup or by restoring
    /// <see cref="Layout"/>. A host that wants the snapshot reads <see cref="Layout"/>, which is
    /// the same value the event used to carry.
    /// </summary>
    public event EventHandler<LayoutChange>? LayoutChanged;

    /// <summary>The effective layout read by the header panel and every realized row panel.</summary>
    internal EffectiveLayout EffectiveLayout { get; } = new();

    /// <summary>
    /// The layout snapshot, as data the host can store. Reading gives an independent
    /// snapshot of the overrides only; assigning restores one defensively — unknown, stale, or
    /// impossible entries are ordinary compatibility input, recovered as section 18 defines, not a
    /// configuration error.
    /// </summary>
    /// <remarks>
    /// The setter is silent: it never raises <see cref="LayoutChanged"/>, because the host that
    /// applied it is the host that would be told. Assigned before the first <c>Loaded</c> it is
    /// held and resolved immediately after schema capture, so a saved layout can be restored at
    /// construction.
    /// <para>
    /// A column with no <see cref="Column.Id"/> is not persisted: it appears in neither the
    /// order nor the override maps, and a restore leaves it in the declared order after every
    /// column the snapshot did name.
    /// </para>
    /// </remarks>
    public ColumnLayout Layout
    {
        get
        {
            if (!_schemaCaptured && _pendingLayout is not null)
            {
                ColumnLayout pending = CopyLayout(_pendingLayout);
                return _hasPendingSort
                    ? pending with
                    {
                        SortColumnId = _pendingSort?.Column.Id,
                        SortDirection = _pendingSort?.Direction ?? SortDirection.Ascending,
                    }
                    : pending;
            }
            List<string> order = new();
            Dictionary<string, bool> visibility = new(StringComparer.Ordinal);
            Dictionary<string, double> widths = new(StringComparer.Ordinal);

            if (_schemaCaptured)
            {
                foreach (EffectiveColumn column in EffectiveLayout.Order)
                {
                    if (column.Id is not string id)
                    {
                        continue;
                    }

                    order.Add(id);

                    if (
                        column.VisibilityOverride is bool visible
                        && visible != column.IsBaselineVisible
                    )
                    {
                        visibility[id] = visible;
                    }

                    if (column.WidthOverride is double width && width != column.BaselineWidth)
                    {
                        widths[id] = width;
                    }
                }
            }
            else
            {
                foreach (Column column in Columns)
                {
                    if (column.Id is string id)
                    {
                        order.Add(id);
                    }
                }
            }

            Sort? sort = Sort;
            return new ColumnLayout(
                order,
                visibility,
                widths,
                sort?.Column.Id,
                sort?.Direction ?? SortDirection.Ascending
            );
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (!_schemaCaptured)
            {
                _pendingLayout = CopyLayout(value);
                _pendingSort = null;
                _hasPendingSort = false;
                return;
            }

            // A restored sort changes the private view. Schema capture rebuilds it itself, so only
            // the post-load path needs this, and only when the effective sort actually moved.
            if (ApplyLayoutCore(value))
            {
                RebuildView();
            }
        }
    }

    private static ColumnLayout CopyLayout(ColumnLayout value) =>
        new(
            value.Order?.ToArray() ?? Array.Empty<string>(),
            value.VisibilityOverrides is null
                ? new Dictionary<string, bool>()
                : new Dictionary<string, bool>(value.VisibilityOverrides),
            value.WidthOverrides is null
                ? new Dictionary<string, double>()
                : new Dictionary<string, double>(value.WidthOverrides),
            value.SortColumnId,
            value.SortDirection
        );

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        DetachTemplateParts();

        _headerStrip = GetTemplateChild(HeaderStripPartName) as Header.Strip;
        _surface = GetTemplateChild(SurfacePartName) as Body.Surface;
        _placeholderPresenter = GetTemplateChild(PlaceholderPartName) as ContentPresenter;
        _marqueeOverlay = GetTemplateChild(MarqueeOverlayPartName) as FrameworkElement;
        _rowInsertionMarker = GetTemplateChild(RowInsertionMarkerPartName) as FrameworkElement;

        if (_headerStrip is not null)
        {
            _headerStrip.Attach(this);
            if (!_filledOnce)
            {
                _headerStrip.SizeChanged += OnHeaderSizeChanged;
            }
        }

        if (_surface is not null)
        {
            _surface.ItemsSource = _view;
        }

        AttachInput();

        UpdatePlaceholder();
        ApplySelectionToContainers();
    }

    private void DetachTemplateParts()
    {
        DetachInput();

        if (_headerStrip is not null)
        {
            _headerStrip.SizeChanged -= OnHeaderSizeChanged;
        }

        // A settle waiting to fire would rebuild a view whose template parts have just been taken
        // away, and would hold this table alive to do it.
        _settleDue?.Stop();
    }

    /// <summary>Suspend external notifications and visual work while retaining logical state.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // WinUI queues Unloaded and can deliver it while the table is still in
        // the tree, as ContentDialog does to its content
        // (microsoft/microsoft-ui-xaml#8402). A table still in the tree keeps
        // its rows.
        if (IsLoaded)
        {
            return;
        }

        _detached = true;

        // A table taken out of the tree sees no exit, and nothing points at rows it no longer shows.
        _pointerOver = false;
        _pointedRow = null;
        _menuRow = null;
        _source.Suspend();
        if (_surface is not null)
            _surface.ItemsSource = null;
        foreach (Column column in Columns)
            column.TextChanged -= OnColumnTextChanged;
        _settleDue?.Stop();
        if (!CancelCommittedGesture())
        {
            CancelGesture();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _source.Resume();
        _detached = false;
        if (_surface is not null)
            _surface.ItemsSource = _view;
        foreach (Column column in Columns)
        {
            column.TextChanged -= OnColumnTextChanged;
            column.TextChanged += OnColumnTextChanged;
        }
        RefreshText();

        if (_schemaCaptured)
        {
            RebuildView();
            return;
        }

        CaptureSchema();
    }

    // ---------------------------------------------------------------- schema

    private object? _schema;
    private Type? _rowType;

    /// <summary>
    /// State the row type once, and hand over the identity selector, the interaction predicate and
    /// every column's sort key with it. Setup-only, like <see cref="Columns"/>: the table captures
    /// the schema at its first <c>Loaded</c> and asking for one afterwards is a configuration error.
    /// </summary>
    public Schema<TRow> Schema<TRow>()
        where TRow : class
    {
        RequireSetup();
        if (_schema is Schema<TRow> existing)
            return existing;
        if (_schema is not null)
            throw ConfigurationError("A table has one schema row type.");
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
    /// interactivity and the cursor that shows it there.
    /// </summary>
    internal Func<object, bool>? CanInteract { get; set; }
    internal Func<object, bool>? CanReorderItem { get; set; }

    /// <summary>
    /// Capture the setup-only schema exactly once, validate it, and resolve the first effective
    /// layout — including a layout state the host applied before load.
    /// </summary>
    private void CaptureSchema()
    {
        ValidateColumns();

        _baselineOrder.Clear();
        foreach (Column column in Columns)
        {
            _baselineOrder.Add(new EffectiveColumn(column, IsHierarchyColumn(column)));
        }

        _schemaCaptured = true;
        Columns.CollectionChanged += OnColumnsMutatedAfterCapture;

        // Install identity before resolving the pending initial selection.
        _identity.KeySelector = ItemKey;
        _identity.KeyComparer = KeyComparer;
        _selection.RehashIdentity();

        EffectiveLayout.SetOrder(_baselineOrder);
        if (_hierarchy is not null)
        {
            _baselineOrder.Clear();
            _baselineOrder.AddRange(EffectiveLayout.Order);
        }

        if (_pendingLayout is not null)
        {
            ColumnLayout pending = _pendingLayout;
            _pendingLayout = null;
            ApplyLayoutCore(pending);
        }

        FillOnce();

        if (_hasPendingSort)
        {
            if (_pendingSort is Sort requested && !Enum.IsDefined(requested.Direction))
                throw ConfigurationError("The initial sort direction is invalid.");
            _sortColumn = _pendingSort is Sort sort ? RequireSortable(sort.Column) : null;
            _sortDirection = _pendingSort?.Direction ?? SortDirection.Ascending;
            _pendingSort = null;
            _hasPendingSort = false;
        }
        RebuildView();
        ApplyPendingScroll();
    }

    private void ValidateColumns()
    {
        if (_hierarchy is not null && !Columns.Contains(_hierarchy.Column))
            throw ConfigurationError("The hierarchy column must belong to this table.");
        HashSet<string> ids = new(StringComparer.Ordinal);
        bool anyVisible = false;

        foreach (Column column in Columns)
        {
            if (column is null)
            {
                throw ConfigurationError("Columns contains a null entry.");
            }

            // Id is optional: a table whose layout is never saved needs no persistence keys. What
            // is not optional is that a supplied one identifies exactly one column.
            if (column.Id is string id)
            {
                if (id.Length == 0)
                {
                    throw ConfigurationError("A column Id must be non-empty, or absent.");
                }

                if (!ids.Add(id))
                {
                    throw ConfigurationError($"Duplicate column Id '{id}'.");
                }
            }

            if (string.IsNullOrEmpty(column.DisplayName))
            {
                throw ConfigurationError(
                    $"Column '{Describe(column)}' needs a non-empty DisplayName."
                );
            }

            if (!double.IsFinite(column.Width) || column.Width <= 0)
            {
                throw ConfigurationError(
                    $"Column '{Describe(column)}' needs a finite Width greater than zero."
                );
            }

            if (!double.IsFinite(column.MinWidth) || column.MinWidth < 0)
            {
                throw ConfigurationError(
                    $"Column '{Describe(column)}' needs a finite, non-negative MinWidth."
                );
            }

            // Sortability is no longer two properties that had to agree: a column carries a sort
            // key from the schema or it does not, so there is nothing left here to contradict.
            if (column.IsVisible || IsHierarchyColumn(column))
            {
                anyVisible = true;
            }
        }

        // A table declaring no columns has no visible column either, so it belongs here rather than
        // in an exemption: nothing can add one afterwards, because the schema is captured now.
        if (!anyVisible)
        {
            throw ConfigurationError("At least one column must be visible by default.");
        }

        // Section 6.1: one row order. Under a sort by either of two claimants, a drop would name a
        // place in an order the other column contradicts.
        if (Columns.Count(column => column.DefinesRowOrder) > 1)
        {
            throw ConfigurationError("At most one column may set DefinesRowOrder.");
        }
    }

    private void OnColumnsMutatedAfterCapture(object? sender, NotifyCollectionChangedEventArgs e) =>
        throw ConfigurationError(
            "Columns is setup-only. Adding, removing, or replacing a column after the first "
                + "Loaded is a configuration error."
        );

    private static InvalidOperationException ConfigurationError(string message) => new(message);

    /// <summary>How to name a column in a configuration error, now that its Id may be absent.</summary>
    private static string Describe(Column column) => column.Id ?? column.DisplayName;

    // ------------------------------------------------------- layout persistence

    /// <returns>True when the restored sort is not the one that was already in force.</returns>
    private bool ApplyLayoutCore(ColumnLayout state)
    {
        Dictionary<string, EffectiveColumn> byId = new(StringComparer.Ordinal);
        foreach (EffectiveColumn column in _baselineOrder)
        {
            if (column.Id is string id)
            {
                byId[id] = column;
            }
        }

        // Order: known IDs first, duplicates dropped after their first valid occurrence, then every
        // column the snapshot did not name — a new one, or one with no Id — in definition order.
        List<EffectiveColumn> ordered = new();
        HashSet<EffectiveColumn> placed = new();

        if (state.Order is not null)
        {
            foreach (string id in state.Order)
            {
                if (
                    id is null
                    || !byId.TryGetValue(id, out EffectiveColumn? column)
                    || !placed.Add(column)
                )
                {
                    continue;
                }

                ordered.Add(column);
            }
        }

        foreach (EffectiveColumn column in _baselineOrder)
        {
            if (placed.Add(column))
            {
                ordered.Add(column);
            }
        }

        // Both maps are complete override maps: an omitted ID clears any earlier override.
        foreach (EffectiveColumn column in ordered)
        {
            column.WidthOverride = null;
            column.VisibilityOverride = null;
        }

        if (state.WidthOverrides is not null)
        {
            foreach (KeyValuePair<string, double> entry in state.WidthOverrides)
            {
                if (entry.Key is null || !byId.TryGetValue(entry.Key, out EffectiveColumn? column))
                {
                    continue;
                }

                if (!column.Column.CanResize || !double.IsFinite(entry.Value) || entry.Value <= 0)
                {
                    continue;
                }

                column.WidthOverride = column.Clamp(entry.Value);
            }
        }

        if (state.VisibilityOverrides is not null)
        {
            foreach (KeyValuePair<string, bool> entry in state.VisibilityOverrides)
            {
                if (entry.Key is null || !byId.TryGetValue(entry.Key, out EffectiveColumn? column))
                {
                    continue;
                }

                // A required column saved as hidden is restored.
                if (!entry.Value && !column.CanHide)
                {
                    continue;
                }

                column.VisibilityOverride = entry.Value;
            }
        }

        EnsureOneVisibleColumn(ordered);

        bool sortChanged = RestoreSort(state, byId);

        // SetOrder republishes the layout, which re-applies each header cell's sort indicator.
        EffectiveLayout.SetOrder(ordered);
        return sortChanged;
    }

    private static void EnsureOneVisibleColumn(List<EffectiveColumn> ordered)
    {
        if (ordered.Count == 0)
        {
            return;
        }

        foreach (EffectiveColumn column in ordered)
        {
            if (column.IsVisible)
            {
                return;
            }
        }

        EffectiveColumn fallback = ordered[0];
        foreach (EffectiveColumn column in ordered)
        {
            if (column.IsBaselineVisible)
            {
                fallback = column;
                break;
            }
        }

        fallback.VisibilityOverride = true;
    }

    // ---------------------------------------------------------------- source

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable),
        typeof(Table),
        new PropertyMetadata(null, OnItemsSourceChanged)
    );

    /// <summary>The host's already filtered projection. The table never filters it further.</summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    private static void OnItemsSourceChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e
    ) => ((Table)d).SetItemsSource(e.NewValue as IEnumerable);

    private void SetItemsSource(IEnumerable? source) => _source.Set(source);

    private void OnSnapshotChanged(object? sender, IReadOnlyList<object> snapshot) =>
        RebuildView(snapshot);

    // -------------------------------------------------------- scroll offset

    /// <summary>How far the rows are scrolled, in DIPs.</summary>
    public double VerticalOffset => _pendingScroll ?? InnerScrollViewer()?.VerticalOffset ?? 0;

    /// <summary>
    /// Scroll the rows to the given offset in DIPs, clamped to the scrollable range, without
    /// changing selection or keyboard focus. A non-finite offset makes the call do nothing.
    /// </summary>
    /// <remarks>
    /// The rows are laid out first, so an offset into rows the host has only just supplied is not
    /// clamped to the shorter extent of the rows before them. Called before the table has loaded,
    /// the offset is held and applied when it loads.
    /// </remarks>
    public void ScrollTo(double verticalOffset)
    {
        if (!double.IsFinite(verticalOffset))
        {
            return;
        }

        _pendingScroll = verticalOffset;
        ApplyPendingScroll();
    }

    private void ApplyPendingScroll()
    {
        if (_pendingScroll is not double offset || !_schemaCaptured || _surface is null)
        {
            return;
        }

        _surface.UpdateLayout();
        if (InnerScrollViewer() is not ScrollViewer scroller)
        {
            return;
        }

        _pendingScroll = null;
        scroller.ChangeView(null, offset, null, disableAnimation: true);
    }
}
