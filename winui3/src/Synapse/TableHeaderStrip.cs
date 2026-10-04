using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Synapse;

/// <summary>
/// The header region. It is one composite tab stop: <c>TabNavigation="Once"</c> puts a single Tab
/// landing inside it, and Left, Right, Home and End move the active header from there. It is also
/// the one pointer arbiter of the header: the resize separator of section 10, the column drag of
/// section 11 and the menu of section 12 are the same press, threshold and dispatch.
/// </summary>
public sealed partial class TableHeaderStrip : Control
{
    private const string ClipPartName = "PART_Clip";
    private const string PanelPartName = "PART_HeaderPanel";
    private const string InsertionMarkerPartName = "PART_ColumnInsertionMarker";
    private const string FitAllPartName = "PART_FitAllColumns";
    private const string ResizeGuidePartName = "PART_ResizeGuide";

    /// <summary>Half of the separator hit width, so the grab zone is centred on the boundary.</summary>
    private const double SeparatorReachDips = 4;

    /// <summary>Horizontal movement below this is a click on the header, not a column drag.</summary>
    private const double DragThresholdDips = 4;

    private static readonly InputCursor ResizeCursor =
        InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

    private readonly KeyEventHandler _cancelOnEscape;
    private readonly TranslateTransform _markerOffset = new();

    private FrameworkElement? _clip;
    private TableCellsPanel? _panel;
    private FrameworkElement? _marker;
    private Popup? _resizeGuide;
    private Button? _fitAll;
    private TableView? _owner;
    private int _activeIndex = -1;

    private HeaderGesture _gesture;
    private uint _pointerId;
    private double _originX;
    private ResolvedColumn? _column;
    private double _startWidth;

    /// <summary>The width the release would apply. No width changes until then.</summary>
    private double _previewWidth;

    /// <summary>Where the resized edge stood when the gesture began, in the strip's coordinates.</summary>
    private double _resizeEdgeOrigin;
    private TableHeaderCell? _cell;
    private UIElement? _escapeRoot;
    private bool _showingResizeCursor;

    public TableHeaderStrip()
    {
        DefaultStyleKey = typeof(TableHeaderStrip);
        AutomationProperties.SetName(this, TableResources.HeaderStripAccessibleName);
        GotFocus += OnHeaderGotFocus;
        ContextRequested += OnContextRequested;
        _cancelOnEscape = OnRootKeyDown;
        Unloaded += (_, _) => CancelGesture();
    }

    private enum HeaderGesture
    {
        /// <summary>No button is down on the header.</summary>
        None,

        /// <summary>A resize separator is captured and tracking the pointer.</summary>
        Resizing,

        /// <summary>A header is pressed, still inside the drag threshold. Release here is a click.</summary>
        Pressed,

        /// <summary>The threshold was crossed and the pressed header is being reordered.</summary>
        Dragging,
    }

    /// <summary>The panel holding one realized header cell per visible column.</summary>
    internal TableCellsPanel? Panel => _panel;

    internal void Attach(TableView owner)
    {
        _owner = owner;
        ApplyTemplate();
        _panel?.AttachAsHeader(owner);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_clip is not null)
        {
            _clip.SizeChanged -= OnClipSizeChanged;
        }

        if (_fitAll is not null)
        {
            _fitAll.Click -= OnFitAllClick;
        }

        _clip = GetTemplateChild(ClipPartName) as FrameworkElement;
        _panel = GetTemplateChild(PanelPartName) as TableCellsPanel;
        _marker = GetTemplateChild(InsertionMarkerPartName) as FrameworkElement;
        _fitAll = GetTemplateChild(FitAllPartName) as Button;
        _resizeGuide = GetTemplateChild(ResizeGuidePartName) as Popup;

        if (_clip is not null)
        {
            _clip.SizeChanged += OnClipSizeChanged;
        }

        if (_fitAll is not null)
        {
            _fitAll.Click += OnFitAllClick;
            // The all-columns glyph, because that is the command this button is: it fits every visible
        // column, not the one nearest to it.
        _fitAll.Content = TableIcons.FitVisibleColumns();
            AutomationProperties.SetName(_fitAll, TableResources.FitVisibleColumns);
            ToolTipService.SetToolTip(_fitAll, TableResources.FitVisibleColumns);
        }

        if (_marker is not null)
        {
            _marker.RenderTransform = _markerOffset;
        }

        if (_owner is not null && _panel is not null)
        {
            _panel.AttachAsHeader(_owner);
        }
    }

    /// <summary>Cells scrolled past the viewport must not paint outside the strip.</summary>
    private void OnClipSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_clip is null)
        {
            return;
        }

        _clip.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };

        UpdateFitAllVisibility();
    }

    private void OnFitAllClick(object sender, RoutedEventArgs e) => _owner?.AutoFitVisibleColumns();

    /// <summary>
    /// Show the fit-all command only while the strip's trailing space is genuinely free.
    /// </summary>
    /// <remarks>
    /// The threshold is the button's own width rather than a number chosen here: if the columns
    /// reach far enough right that the button would sit over one, there is nothing to offer and it
    /// goes. That covers the two cases without a second rule — columns wider than the viewport
    /// scroll horizontally and leave no trailing space at all, and a narrow set of columns leaves
    /// plenty.
    /// </remarks>
    internal void UpdateFitAllVisibility()
    {
        if (_fitAll is null || _clip is null)
        {
            return;
        }

        bool offered = _owner is { IsFitAllButtonEnabled: true, Geometry: { } layout }
            && _clip.ActualWidth - (layout.TotalWidth - layout.HorizontalOffset)
                >= _fitAll.ActualWidth;

        _fitAll.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHeaderGotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        TableHeaderCell? cell = FindCell(source, out _);
        if (cell is null || _panel is null)
        {
            return;
        }

        _activeIndex = _panel.Children.IndexOf(cell);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        FindCell(e.OriginalSource as DependencyObject, out bool passive);
        if (e.Handled || !passive)
        {
            base.OnKeyDown(e);
            return;
        }

        // Section 9 and 12: primary activation on the active passive header runs the sort cycle.
        // A key pressed inside a control the host put in a header template is that control's.
        if (e.Key is VirtualKey.Enter or VirtualKey.Space)
        {
            if (ActivateSortFrom(e.OriginalSource as DependencyObject))
            {
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
            return;
        }

        int count = _panel?.Children.Count ?? 0;
        bool navigationKey = e.Key is VirtualKey.Left or VirtualKey.Right
            or VirtualKey.Home or VirtualKey.End;

        if (count == 0 || !navigationKey)
        {
            base.OnKeyDown(e);
            return;
        }

        int current = _activeIndex < 0 ? 0 : Math.Min(_activeIndex, count - 1);
        int target = e.Key switch
        {
            VirtualKey.Left => current - 1,
            VirtualKey.Right => current + 1,
            VirtualKey.Home => 0,
            _ => count - 1,
        };

        target = Math.Clamp(target, 0, count - 1);
        if (_panel!.Children[target] is Control cell && cell.Focus(FocusState.Keyboard))
        {
            _activeIndex = target;
        }

        e.Handled = true;
    }

    // ------------------------------------------------------------------ pointer arbiter

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);

        // A press with a gesture still live is a second button, or a release that never arrived
        // because an uncaptured press left the strip. Either way the live one is over.
        if (_gesture != HeaderGesture.None)
        {
            CancelGesture();
        }

        PointerPoint point = e.GetCurrentPoint(this);
        if (_owner is null
            || !IsMouseOrPen(e.Pointer.PointerDeviceType)
            || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (SeparatorNear(point.Position.X) is not null)
        {
            // Without the capture the resize would stop as soon as the pointer left the strip.
            if (CapturePointer(e.Pointer))
            {
                BeginResizeAt(point.Position.X, e.Pointer.PointerId);
                e.Handled = true;
            }

            return;
        }

        // Section 11 arms a drag from the passive header only, and leaves the click itself
        // unhandled so a press that never crosses the threshold still reaches the header's sort.
        TableHeaderCell? cell = FindCell(e.OriginalSource as DependencyObject, out bool passive);
        if (!passive || ColumnOf(cell) is not ResolvedColumn column)
        {
            return;
        }

        Begin(HeaderGesture.Pressed, column, e.Pointer.PointerId, point.Position.X);
        _cell = cell;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_owner is null)
        {
            return;
        }

        double x = e.GetCurrentPoint(this).Position.X;

        if (_gesture == HeaderGesture.None)
        {
            ShowResizeCursor(SeparatorNear(x) is not null);
            return;
        }

        if (e.Pointer.PointerId != _pointerId)
        {
            return;
        }

        switch (_gesture)
        {
            case HeaderGesture.Resizing:
                TrackResize(x);
                break;

            case HeaderGesture.Pressed when Math.Abs(x - _originX) >= DragThresholdDips:
                if (!BeginColumnDrag(e.Pointer))
                {
                    return;
                }

                MoveInsertionMarker(x);
                break;

            case HeaderGesture.Dragging:
                MoveInsertionMarker(x);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_gesture == HeaderGesture.None || e.Pointer.PointerId != _pointerId)
        {
            return;
        }

        switch (_gesture)
        {
            case HeaderGesture.Resizing:
                CompleteResize();
                ReleasePointerCapture(e.Pointer);
                break;

            case HeaderGesture.Dragging:
                CompleteMove(e.GetCurrentPoint(this).Position.X, e.Pointer);
                break;

            default:
                // Pressed: a click. It changed no layout, and it belongs to the header underneath.
                EndGesture();
                return;
        }

        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        if (_gesture != HeaderGesture.None && e.Pointer.PointerId == _pointerId)
        {
            CancelGesture();
        }
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);

        if (_gesture == HeaderGesture.None)
        {
            ShowResizeCursor(false);
        }
    }

    /// <summary>
    /// Section 9's pointer path. A mouse or pen click that never became a resize or a column drag
    /// arrives here, and so does a touch tap, which arms neither gesture. A tap on a mouse/pen
    /// resize separator belongs to section 10's fit, not to the header underneath it.
    /// </summary>
    protected override void OnTapped(TappedRoutedEventArgs e)
    {
        base.OnTapped(e);

        if (IsMouseOrPen(e.PointerDeviceType) && SeparatorNear(e.GetPosition(this).X) is not null)
        {
            return;
        }

        if (ActivateSortFrom(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
        }
    }

    protected override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        base.OnDoubleTapped(e);

        if (!IsMouseOrPen(e.PointerDeviceType)
            || SeparatorNear(e.GetPosition(this).X) is not ResolvedColumn column)
        {
            return;
        }

        _owner!.AutoFitColumn(column);
        e.Handled = true;
    }

    private void Begin(HeaderGesture gesture, ResolvedColumn column, uint pointerId, double x)
    {
        _gesture = gesture;
        _column = column;
        _pointerId = pointerId;
        _originX = x;
        WatchForEscape();
    }

    /// <summary>
    /// Escape and a lost capture both end the gesture with the layout as it was. A resize has
    /// nothing to put back: it applies its width on release and nowhere else, so an abandoned one
    /// has changed nothing to undo.
    /// </summary>
    private void CancelGesture()
    {
        EndGesture();
        ReleasePointerCaptures();
    }

    private void EndGesture()
    {
        _gesture = HeaderGesture.None;
        _column = null;
        _cell?.SetDragging(false);
        _cell = null;
        HideInsertionMarker();
        HideResizeGuide();
        _escapeRoot?.RemoveHandler(KeyDownEvent, _cancelOnEscape);
        _escapeRoot = null;
    }

    /// <summary>
    /// Capturing the pointer does not move keyboard focus, so Escape arrives at whatever was
    /// focused before the gesture. The handler sits on the window's content until it ends.
    /// </summary>
    private void WatchForEscape()
    {
        _escapeRoot = XamlRoot?.Content as UIElement;
        _escapeRoot?.AddHandler(KeyDownEvent, _cancelOnEscape, handledEventsToo: true);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_gesture == HeaderGesture.None || e.Key != VirtualKey.Escape)
        {
            return;
        }

        CancelGesture();
        e.Handled = true;
    }

    // ------------------------------------------------------------------ resize separator

    /// <summary>
    /// Start a resize on the separator at this header-strip x. The three resize steps are the
    /// strip's own state machine in plain coordinates, so the pointer overrides only translate
    /// events into them and the gesture can be exercised without a pointer.
    /// </summary>
    /// <returns>False when no resizable separator is there, which starts no gesture.</returns>
    private bool BeginResizeAt(double x, uint pointerId)
    {
        if (SeparatorNear(x) is not ResolvedColumn separator)
        {
            return false;
        }

        Begin(HeaderGesture.Resizing, separator, pointerId, x);
        _startWidth = separator.Width;
        _previewWidth = separator.Width;
        _resizeEdgeOrigin = TrailingEdgeOf(separator);
        ShowResizeGuide(_resizeEdgeOrigin);
        return true;
    }

    /// <summary>
    /// Take the preview to this header-strip x. Section 10 clamps to the column's own limits and
    /// nothing else, so the guide stops exactly where the width would; and a movement now changes
    /// no width at all, which is a stronger form of never being a persistence event.
    /// </summary>
    private void TrackResize(double x)
    {
        _previewWidth = ResolvedColumn.Clamp(
            _startWidth + (x - _originX), _column!.Column.MinWidth, _column.Column.MaxWidth);

        ShowResizeGuide(_resizeEdgeOrigin + (_previewWidth - _startWidth));
    }

    /// <summary>
    /// Finish the resize: the one width this gesture applies, and one notification if it moved.
    /// The gesture ends before the width lands, so a host that re-enters the table from its own
    /// layout handler finds the strip already idle.
    /// </summary>
    private void CompleteResize()
    {
        ResolvedColumn column = _column!;
        double width = _previewWidth;

        EndGesture();

        if (_owner!.SetColumnWidth(column, width))
        {
            _owner.RaiseLayoutChanged(TableLayoutChangeKind.ColumnResize);
        }
    }

    /// <summary>This column's trailing edge in the strip's own coordinates.</summary>
    private double TrailingEdgeOf(ResolvedColumn column)
    {
        int index = _owner!.Geometry.IndexOfVisible(column);
        return index < 0
            ? _originX
            : _owner.Geometry.VisibleColumns[index].Offset + column.Width
                - _owner.Geometry.HorizontalOffset;
    }

    /// <summary>
    /// Put the guide at this header-strip x, centred on the boundary rather than led by it. Its
    /// height is the table's, so one line runs from the top of the header to the foot of the rows
    /// and says the same thing about every one of them.
    /// </summary>
    private void ShowResizeGuide(double x)
    {
        if (_resizeGuide is null || _owner is null)
        {
            return;
        }

        if (_resizeGuide.Child is FrameworkElement body)
        {
            body.Height = _owner.ActualHeight;
        }

        _resizeGuide.HorizontalOffset = x - 1;
        _resizeGuide.IsOpen = true;
    }

    private void HideResizeGuide()
    {
        if (_resizeGuide is not null)
        {
            _resizeGuide.IsOpen = false;
        }
    }

    /// <summary>The resizable column this header-strip x would resize, or null.</summary>
    private ResolvedColumn? SeparatorNear(double x)
    {
        if (_owner is null)
        {
            return null;
        }

        int index = _owner.Geometry.TrailingEdgeNear(x, SeparatorReachDips);
        if (index < 0)
        {
            return null;
        }

        ResolvedColumn column = _owner.Geometry.VisibleColumns[index].Column;
        return column.Column.CanResize ? column : null;
    }

    private void ShowResizeCursor(bool onSeparator)
    {
        if (onSeparator == _showingResizeCursor)
        {
            return;
        }

        _showingResizeCursor = onSeparator;
        ProtectedCursor = onSeparator ? ResizeCursor : null;
    }

    // ------------------------------------------------------------------ column drag

    private bool BeginColumnDrag(Pointer pointer)
    {
        // Without the capture the drop would be lost as soon as the pointer left the strip.
        if (!CapturePointer(pointer))
        {
            EndGesture();
            return false;
        }

        _gesture = HeaderGesture.Dragging;
        _cell?.SetDragging(true);
        return true;
    }

    private void CompleteMove(double x, Pointer pointer)
    {
        ResolvedColumn column = _column!;
        int boundary = DropBoundary(x, column);

        EndGesture();
        ReleasePointerCapture(pointer);

        // Section 11 returns focus to the moved header. Pointer, because the move came from a
        // pointer: the platform then leaves no focus ring, which is what it does for every other
        // control a mouse moves or presses.
        _owner!.MoveColumnTo(column, boundary, FocusState.Pointer);
    }

    /// <summary>
    /// The boundary this drop asks for, counted the way <see cref="TableView.MoveColumnTo"/> counts:
    /// among the visible columns with the dragged one taken out.
    /// </summary>
    private int DropBoundary(double x, ResolvedColumn dragged)
    {
        int boundary = BoundaryAt(x);
        int index = _owner!.Geometry.IndexOfVisible(dragged);
        return boundary > index ? boundary - 1 : boundary;
    }

    /// <summary>
    /// The visible boundary a pointer at this header-strip x lands on: the nearer edge of the
    /// column it is over, and past either end, that end. Every legal destination is reachable,
    /// including before the first column and after the last.
    /// </summary>
    private int BoundaryAt(double x)
    {
        IReadOnlyList<VisibleColumn> visible = _owner!.Geometry.VisibleColumns;
        double contentX = x + _owner.Geometry.HorizontalOffset;

        for (int i = 0; i < visible.Count; i++)
        {
            if (contentX < visible[i].Offset + (visible[i].Width / 2))
            {
                return i;
            }
        }

        return visible.Count;
    }

    private void MoveInsertionMarker(double x)
    {
        if (_marker is null)
        {
            return;
        }

        IReadOnlyList<VisibleColumn> visible = _owner!.Geometry.VisibleColumns;
        int boundary = BoundaryAt(x);
        double contentX = boundary < visible.Count ? visible[boundary].Offset : _owner.Geometry.TotalWidth;

        // Held inside the strip so the first and last boundaries do not show half a marker.
        double centred = contentX - _owner.Geometry.HorizontalOffset - (_marker.Width / 2);
        _markerOffset.X = Math.Clamp(centred, 0, Math.Max(0, ActualWidth - _marker.Width));
        _marker.Visibility = Visibility.Visible;
    }

    private void HideInsertionMarker()
    {
        if (_marker is not null)
        {
            _marker.Visibility = Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------ header menu

    /// <summary>
    /// Section 12. The platform raises this for right-click, touch press-and-hold, the Menu key and
    /// Shift+F10 alike, so the header needs no press-and-hold timer and no second keyboard path.
    /// </summary>
    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        TableHeaderCell? cell = FindCell(e.OriginalSource as DependencyObject, out _);

        e.Handled = ShowMenu(cell, e.TryGetPosition(this, out Point position) ? position : null);
    }

    /// <summary>
    /// Section 12's menu, over the header the request came from, or over unused header space when
    /// there is no active column. The invoking header takes focus first: a flyout returns focus to
    /// whatever held it when the flyout opened, which is section 12's restore without a second focus
    /// path.
    /// </summary>
    private bool ShowMenu(TableHeaderCell? cell, Point? position)
    {
        if (_owner is null)
        {
            return false;
        }

        // Focus with the state the request arrived in, not Programmatic. The platform draws its
        // focus visual for Keyboard and Programmatic but not for Pointer, so asking for
        // Programmatic here puts a focus ring on the header after a right-click or a press-and-hold.
        // A pointer-raised context request carries a position; the Menu key and Shift+F10 do not.
        FocusState state = position is null ? FocusState.Keyboard : FocusState.Pointer;
        cell?.Focus(state);

        MenuFlyout menu = TableHeaderMenu.Create(_owner, ColumnOf(cell));

        // A flyout returns focus to the element it was shown from when it closes, and the state it
        // returns is not the state the header was focused with: a menu opened by right-click and
        // dismissed by invoking a command left a keyboard focus ring around the header, which is
        // the ring the owner saw around Size after Fit visible columns. Focusing again with the
        // state the request actually arrived in is what takes it off.
        if (cell is not null)
        {
            menu.Closed += (_, _) => cell.Focus(state);
        }

        // Shown from the strip and never from the header cell, although the cell is what the menu
        // is about. The menu's first command hides that column, and hiding a column takes its cell
        // out of the panel. A flyout does not outlive the element it was shown from, so the menu
        // refused the close its own invocation asked for and was then closed anyway, by the
        // platform, when the cell unloaded. The strip is present in every column state, so an
        // anchor on the strip is too. A keyboard request carries no point, so the invoking header's
        // own bottom-left stands in for one.
        menu.ShowAt(this, new FlyoutShowOptions { Position = position ?? CornerOf(cell) });

        return true;
    }

    /// <summary>The bottom-left corner of a header cell, in the strip's own coordinates.</summary>
    private Point? CornerOf(TableHeaderCell? cell) => cell is null
        ? null
        : cell.TransformToVisual(this).TransformPoint(new Point(0, cell.ActualHeight));

    /// <summary>
    /// Run section 9's sort cycle for the passive header this input came from.
    /// </summary>
    /// <returns>False when the input was not on a passive header of a sortable column.</returns>
    private bool ActivateSortFrom(DependencyObject? source)
    {
        TableHeaderCell? cell = FindCell(source, out bool passive);
        if (_owner is null || !passive || ColumnOf(cell) is not ResolvedColumn column
            || !column.Column.CanSort)
        {
            return false;
        }

        _owner.ActivateSort(column);
        return true;
    }

    private static bool IsMouseOrPen(PointerDeviceType type) =>
        type is PointerDeviceType.Mouse or PointerDeviceType.Pen;

    /// <summary>The resolved column this header cell shows, or null when it shows none.</summary>
    private ResolvedColumn? ColumnOf(TableHeaderCell? cell) =>
        cell?.Column is TableColumn declared ? _owner?.Geometry.Find(declared) : null;

    /// <summary>
    /// The header cell this element sits in, and whether the path to it crossed a control the host
    /// put in a header template. Section 11 starts a drag from the passive surface only; focus
    /// tracking wants the cell either way.
    /// </summary>
    private static TableHeaderCell? FindCell(DependencyObject? node, out bool passive)
    {
        passive = true;
        DependencyObject? current = node;

        while (current is not null)
        {
            if (current is TableHeaderCell cell)
            {
                return cell;
            }

            if (current is Control { IsTabStop: true })
            {
                passive = false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
