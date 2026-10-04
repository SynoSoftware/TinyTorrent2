using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Syno.TableView;

/// <summary>
/// A generic table surface: one vertical scrolling owner, one effective column layout shared by
/// the header strip and every realized row, and a table-owned horizontal axis.
/// </summary>
public sealed partial class Table : Control
{
    private const string HeaderStripPartName = "PART_HeaderStrip";
    private const string ItemsViewPartName = "PART_ItemsView";
    private const string StateLayerPartName = "PART_StateLayer";
    private const string HorizontalScrollBarPartName = "PART_HorizontalScrollBar";
    private const string MarqueeOverlayPartName = "PART_MarqueeOverlay";
    private const string RowInsertionMarkerPartName = "PART_RowInsertionMarker";

    private const double WheelStepDips = 48;
    private const double WheelNotch = 120;

    private readonly Body.Source _source;
    private readonly Body.View _view;
    private readonly List<ResolvedColumn> _resolved = new();
    private readonly SelectionState _selection;

    private Header.Strip? _headerStrip;
    private ListView? _itemsView;
    private ContentPresenter? _stateLayer;
    private ScrollBar? _horizontalScrollBar;
    private FrameworkElement? _marqueeOverlay;
    private FrameworkElement? _rowInsertionMarker;

    private bool _schemaCaptured;
    private ColumnLayout? _pendingLayout;

    private UIElement? _shippedPlaceholder;
    private Placeholder _shippedPlaceholderKind;

    public Table()
    {
        DefaultStyleKey = typeof(Table);
        _view = new Body.View(_identity);
        _selection = new SelectionState(_identity);
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

    /// <summary>The single geometry source read by the header panel and every realized row panel.</summary>
    internal ResolvedLayout Geometry { get; } = new();

    /// <summary>
    /// The effective column layout, as data the host can store. Reading gives an independent
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
            List<string> order = new();
            Dictionary<string, bool> visibility = new(StringComparer.Ordinal);
            Dictionary<string, double> widths = new(StringComparer.Ordinal);

            if (_schemaCaptured)
            {
                foreach (ResolvedColumn column in Geometry.Order)
                {
                    if (column.Id is not string id)
                    {
                        continue;
                    }

                    order.Add(id);

                    if (column.VisibilityOverride is bool visible && visible != column.BaselineVisibility)
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

            return new ColumnLayout(order, visibility, widths, _sortColumn?.Id, _sortDirection);
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (!_schemaCaptured)
            {
                _pendingLayout = value;
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

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        DetachTemplateParts();

        _headerStrip = GetTemplateChild(HeaderStripPartName) as Header.Strip;
        _itemsView = GetTemplateChild(ItemsViewPartName) as ListView;
        _stateLayer = GetTemplateChild(StateLayerPartName) as ContentPresenter;
        _horizontalScrollBar = GetTemplateChild(HorizontalScrollBarPartName) as ScrollBar;
        _marqueeOverlay = GetTemplateChild(MarqueeOverlayPartName) as FrameworkElement;
        _rowInsertionMarker = GetTemplateChild(RowInsertionMarkerPartName) as FrameworkElement;

        _headerStrip?.Attach(this);

        if (_itemsView is not null)
        {
            _itemsView.ItemsSource = _view;
            _itemsView.SizeChanged += OnBodySizeChanged;
            _itemsView.AddHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(OnBodyPointerWheelChanged),
                handledEventsToo: true);
        }

        if (_horizontalScrollBar is not null)
        {
            _horizontalScrollBar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
            _horizontalScrollBar.ValueChanged += OnHorizontalScrollBarValueChanged;
        }

        Geometry.Invalidated += OnLayoutInvalidated;

        AttachInput();

        UpdateStateLayer();
        UpdateHorizontalRange();
        ApplySelectionToContainers();
    }

    private void DetachTemplateParts()
    {
        DetachInput();

        if (_itemsView is not null)
        {
            _itemsView.SizeChanged -= OnBodySizeChanged;
            _itemsView.RemoveHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(OnBodyPointerWheelChanged));
        }

        if (_horizontalScrollBar is not null)
        {
            _horizontalScrollBar.ValueChanged -= OnHorizontalScrollBarValueChanged;
        }

        // A settle waiting to fire would rebuild a view whose template parts have just been taken
        // away, and would hold this table alive to do it.
        _settleDue?.Stop();

        Geometry.Invalidated -= OnLayoutInvalidated;
    }

    /// <summary>
    /// Leaving the tree, which for the last table in an application is the window closing. Until
    /// this existed the settle timer was stopped only when the control was re-templated, so a table
    /// whose sort was still settling went on ticking into a torn-down XAML core and the tick failed
    /// inside the hosted list. Reloading is ordinary — a tab or a navigation frame does it — so
    /// this only pauses the settle; <see cref="OnLoaded"/> lets it run again.
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _detached = true;
        _settleDue?.Stop();
        if (!CancelCommittedGesture())
        {
            CancelGesture();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _detached = false;

        if (_schemaCaptured)
        {
            RebuildView();
            return;
        }

        CaptureSchema();
    }

    // ---------------------------------------------------------------- schema

    /// <summary>
    /// Capture the setup-only schema exactly once, validate it, and resolve the first effective
    /// layout — including a layout state the host applied before load.
    /// </summary>
    private void CaptureSchema()
    {
        ValidateColumns();

        _resolved.Clear();
        foreach (Column column in Columns)
        {
            _resolved.Add(new ResolvedColumn(column));
        }

        _schemaCaptured = true;
        Columns.CollectionChanged += OnColumnsMutatedAfterCapture;

        // Identity is setup-only. Anything selected before this point was bucketed by reference.
        _identity.KeySelector = ItemKey;
        _selection.RehashIdentity();

        Geometry.SetOrder(_resolved);

        if (_pendingLayout is not null)
        {
            ColumnLayout pending = _pendingLayout;
            _pendingLayout = null;
            ApplyLayoutCore(pending);
        }

        UpdateHorizontalRange();
        RebuildView();
    }

    private void ValidateColumns()
    {
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
                throw ConfigurationError($"Column '{Describe(column)}' needs a non-empty DisplayName.");
            }

            if (!double.IsFinite(column.Width) || column.Width <= 0)
            {
                throw ConfigurationError(
                    $"Column '{Describe(column)}' needs a finite Width greater than zero.");
            }

            if (!double.IsFinite(column.MinWidth) || column.MinWidth < 0)
            {
                throw ConfigurationError(
                    $"Column '{Describe(column)}' needs a finite, non-negative MinWidth.");
            }

            bool maxWidthValid = double.IsPositiveInfinity(column.MaxWidth)
                || (double.IsFinite(column.MaxWidth) && column.MaxWidth > 0);
            if (!maxWidthValid)
            {
                throw ConfigurationError(
                    $"Column '{Describe(column)}' needs a finite positive MaxWidth or positive infinity.");
            }

            if (column.MinWidth > column.MaxWidth)
            {
                throw ConfigurationError(
                    $"Column '{Describe(column)}' has MinWidth greater than MaxWidth.");
            }

            // Sortability is no longer two properties that had to agree: a column carries a sort
            // key from the schema or it does not, so there is nothing left here to contradict.
            if (column.IsVisible)
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
            "Columns is setup-only. Adding, removing, or replacing a column after the first " +
            "Loaded is a configuration error.");

    private static InvalidOperationException ConfigurationError(string message) => new(message);

    /// <summary>How to name a column in a configuration error, now that its Id may be absent.</summary>
    private static string Describe(Column column) => column.Id ?? column.DisplayName;

    // ------------------------------------------------------- layout persistence

    /// <returns>True when the restored sort is not the one that was already in force.</returns>
    private bool ApplyLayoutCore(ColumnLayout state)
    {
        Dictionary<string, ResolvedColumn> byId = new(StringComparer.Ordinal);
        foreach (ResolvedColumn column in _resolved)
        {
            if (column.Id is string id)
            {
                byId[id] = column;
            }
        }

        // Order: known IDs first, duplicates dropped after their first valid occurrence, then every
        // column the snapshot did not name — a new one, or one with no Id — in definition order.
        List<ResolvedColumn> ordered = new();
        HashSet<ResolvedColumn> placed = new();

        if (state.Order is not null)
        {
            foreach (string id in state.Order)
            {
                if (id is null || !byId.TryGetValue(id, out ResolvedColumn? column) || !placed.Add(column))
                {
                    continue;
                }

                ordered.Add(column);
            }
        }

        foreach (ResolvedColumn column in _resolved)
        {
            if (placed.Add(column))
            {
                ordered.Add(column);
            }
        }

        // Both maps are complete override maps: an omitted ID clears any earlier override.
        foreach (ResolvedColumn column in ordered)
        {
            column.WidthOverride = null;
            column.VisibilityOverride = null;
        }

        if (state.Widths is not null)
        {
            foreach (KeyValuePair<string, double> entry in state.Widths)
            {
                if (entry.Key is null || !byId.TryGetValue(entry.Key, out ResolvedColumn? column))
                {
                    continue;
                }

                if (!column.Column.CanResize || !double.IsFinite(entry.Value) || entry.Value <= 0)
                {
                    continue;
                }

                column.WidthOverride = ResolvedColumn.Clamp(
                    entry.Value, column.Column.MinWidth, column.Column.MaxWidth);
            }
        }

        if (state.Visibility is not null)
        {
            foreach (KeyValuePair<string, bool> entry in state.Visibility)
            {
                if (entry.Key is null || !byId.TryGetValue(entry.Key, out ResolvedColumn? column))
                {
                    continue;
                }

                // A required column saved as hidden is restored.
                if (!entry.Value && !column.Column.CanHide)
                {
                    continue;
                }

                column.VisibilityOverride = entry.Value;
            }
        }

        EnsureOneVisibleColumn(ordered);

        bool sortChanged = RestoreSort(state, byId);

        // SetOrder republishes the geometry, which re-applies each header cell's sort indicator.
        Geometry.SetOrder(ordered);
        UpdateHorizontalRange();
        return sortChanged;
    }

    private static void EnsureOneVisibleColumn(List<ResolvedColumn> ordered)
    {
        if (ordered.Count == 0)
        {
            return;
        }

        foreach (ResolvedColumn column in ordered)
        {
            if (column.IsVisible)
            {
                return;
            }
        }

        ResolvedColumn fallback = ordered[0];
        foreach (ResolvedColumn column in ordered)
        {
            if (column.BaselineVisibility)
            {
                fallback = column;
                break;
            }
        }

        fallback.VisibilityOverride = true;
    }

    // ---------------------------------------------------------------- source

    private void SetItemsSource(IEnumerable? source) => _source.SetSource(source);

    private void OnSnapshotChanged(object? sender, EventArgs e) => RebuildView();

    // ---------------------------------------------------- horizontal offset

    private void OnLayoutInvalidated(object? sender, LayoutInvalidationReason reason)
    {
        // Anything but the offset can have moved the total width, a resize included.
        if (reason != LayoutInvalidationReason.Offset)
        {
            UpdateHorizontalRange();
        }
    }

    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e) => UpdateHorizontalRange();

    private void UpdateHorizontalRange()
    {
        double viewport = _itemsView?.ActualWidth ?? 0;
        double maximum = Math.Max(0, Geometry.TotalWidth - viewport);

        if (Geometry.HorizontalOffset > maximum)
        {
            Geometry.HorizontalOffset = maximum;
        }

        if (_horizontalScrollBar is null)
        {
            return;
        }

        _horizontalScrollBar.Minimum = 0;
        _horizontalScrollBar.Maximum = maximum;
        _horizontalScrollBar.ViewportSize = viewport;
        _horizontalScrollBar.LargeChange = Math.Max(1, viewport);
        _horizontalScrollBar.SmallChange = WheelStepDips;
        _horizontalScrollBar.Value = Geometry.HorizontalOffset;
        _horizontalScrollBar.Visibility = maximum > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Every way the scroll bar's value can change moves the table: thumb drag, track click,
    /// arrow button, keyboard, and the RangeValue automation pattern. <c>Scroll</c> alone covers
    /// only the pointer paths, which leaves the bar and the content disagreeing.
    /// </summary>
    private void OnHorizontalScrollBarValueChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        SetHorizontalOffset(e.NewValue);

    /// <summary>
    /// Shift+wheel and horizontal wheel drive the table's own offset. The inner ScrollViewer's
    /// horizontal axis is disabled, so the event is marked handled to stop it scrolling.
    /// </summary>
    private void OnBodyPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;

        bool horizontalWheel = properties.IsHorizontalMouseWheel;
        bool shiftDown = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (!horizontalWheel && !shiftDown)
        {
            return;
        }

        double notches = properties.MouseWheelDelta / WheelNotch;
        double delta = horizontalWheel ? notches : -notches;
        SetHorizontalOffset(Geometry.HorizontalOffset + (delta * WheelStepDips));
        e.Handled = true;
    }

    private void SetHorizontalOffset(double value)
    {
        double viewport = _itemsView?.ActualWidth ?? 0;
        double maximum = Math.Max(0, Geometry.TotalWidth - viewport);
        double clamped = Math.Clamp(value, 0, maximum);

        Geometry.HorizontalOffset = clamped;

        if (_horizontalScrollBar is not null)
        {
            _horizontalScrollBar.Value = clamped;
        }
    }

    // ------------------------------------------------- loading / empty states

    /// <summary>
    /// Section 17. It reads the resolved view, not <see cref="Placeholder"/>, so existing rows stay
    /// visible during a refresh; the placeholder says only which presentation an empty view gets.
    /// </summary>
    private void UpdateStateLayer()
    {
        if (_stateLayer is null)
        {
            return;
        }

        if (_view.Count > 0)
        {
            _stateLayer.Content = null;
            _stateLayer.ContentTemplate = null;
            _stateLayer.Visibility = Visibility.Collapsed;
            return;
        }

        Placeholder kind = Placeholder;
        (object? content, DataTemplate? template) = kind switch
        {
            Placeholder.Loading => (LoadingContent, LoadingContentTemplate),
            Placeholder.NoResults => (NoResultsContent, NoResultsContentTemplate),
            _ => (EmptyContent, EmptyContentTemplate),
        };

        _stateLayer.Content = content ?? (template is null ? ShippedPlaceholder(kind) : null);
        _stateLayer.ContentTemplate = template;
        _stateLayer.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// What an empty table shows when the host has configured nothing. Kept while the kind holds,
    /// so a table that rebuilds an empty view does not restart the ring it is showing.
    /// </summary>
    /// <remarks>
    /// Deliberately the least the platform can say: the ring at its own size, the two messages in
    /// the inherited foreground. No spacing, no colour and no font size is chosen here, because
    /// none of them is derivable and a host that wants more sets <see cref="LoadingContent"/>,
    /// <see cref="EmptyContent"/>, or <see cref="NoResultsContent"/>. What this replaces is a blank
    /// rectangle, which is what a table with no rows and nothing configured used to render.
    /// </remarks>
    private UIElement ShippedPlaceholder(Placeholder kind)
    {
        if (_shippedPlaceholder is not null && _shippedPlaceholderKind == kind)
        {
            return _shippedPlaceholder;
        }

        _shippedPlaceholderKind = kind;
        _shippedPlaceholder = kind == Placeholder.Loading
            ? Centred(new ProgressRing { IsActive = true }, Strings.Loading)
            : Centred(
                new TextBlock
                {
                    Text = kind == Placeholder.NoResults
                        ? Strings.NoResults
                        : Strings.Empty,
                },
                null);

        return _shippedPlaceholder;
    }

    private static FrameworkElement Centred(FrameworkElement element, string? accessibleName)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.VerticalAlignment = VerticalAlignment.Center;

        if (accessibleName is not null)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, accessibleName);
        }

        return element;
    }
}
