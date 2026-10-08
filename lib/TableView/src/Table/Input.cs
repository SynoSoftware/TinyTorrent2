using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Syno.TableView;

/// <summary>
/// The one pointer-gesture arbiter of sections 13 to 16, and the keyboard set of section 13.
/// </summary>
/// <remarks>
/// Keyboard handling is gated on the focused element, not on <c>Handled</c>: a single-line
/// <c>TextBox</c> in a cell leaves the arrow keys unhandled, and gating on that would move the
/// current row while the caret is in the editor.
/// </remarks>
public sealed partial class Table
{
    /// <summary>Movement below this is a click, not a drag or a marquee.</summary>
    private const double DragThresholdDips = 4;

    /// <summary>Groups the drag's destination announcements, so only the latest one is spoken.</summary>
    private const string RowDragActivityId = "TableViewRowDrag";

    private readonly Body.Marquee _marquee = new();
    private readonly Body.Drag _rowDrag = new();

    private RowGesture _gesture;
    private uint _gesturePointerId;

    /// <summary>Set only while the table holds a capture of its own, so it releases nothing else.</summary>
    private Pointer? _gestureCapture;

    /// <summary>The press position, relative to the row surface: the space the marquee measures in.</summary>
    private Point _gestureOrigin;

    private object? _gestureItem;
    private bool _gestureCtrl;
    private bool _gestureShift;

    /// <summary>Set when the click's selection change waits for release, so a drag keeps its packet.</summary>
    private bool _gestureDeferred;

    /// <summary>
    /// Whether the table would have dragged the pressed row, answered at the press. The threshold
    /// asks again, and a drag needs both answers: a row that became draggable after a press that
    /// had already selected it must not then move a packet the press just changed.
    /// </summary>
    private bool _gestureCouldDrag;

    /// <summary>The selection as it stood at press, restored if a committed gesture is cancelled.</summary>
    private SelectionState.Checkpoint? _gestureSelection;

    /// <summary>
    /// Section 16's moving packet, resolved once when the drag begins. Nothing can move the
    /// selection under a live drag, so the drop reports the packet the gesture started with.
    /// </summary>
    private IReadOnlyList<object> _movingPacket = Array.Empty<object>();

    /// <summary>The boundary the live drag last announced. Negative is no live destination.</summary>
    private int _dragBoundary = -1;

    private ScrollViewer? _innerScrollViewer;

    /// <summary>
    /// On by default. A marquee is a selection gesture, so it is meaningful in every table, and
    /// design decision 16 rules that a press nothing else competes for must not be a dead press.
    /// </summary>
    public static readonly DependencyProperty IsMarqueeEnabledProperty =
        DependencyProperty.Register(
            nameof(IsMarqueeEnabled),
            typeof(bool),
            typeof(Table),
            new PropertyMetadata(true, OnMarqueeChanged)
        );

    /// <summary>
    /// Off by default, and the asymmetry with the marquee is sayable: a reorder is a domain request
    /// and means something only where the host owns an order, which most tables do not.
    /// </summary>
    public static readonly DependencyProperty CanReorderProperty = DependencyProperty.Register(
        nameof(CanReorder),
        typeof(bool),
        typeof(Table),
        new PropertyMetadata(false, OnReorderChanged)
    );

    /// <summary>
    /// Section 7's escape hatch for a custom interactive control the table cannot recognize. Set
    /// to false on the control's root or an ancestor inside a cell template.
    /// </summary>
    public static readonly DependencyProperty IsRowGestureEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsRowGestureEnabled",
            typeof(bool),
            typeof(Table),
            new PropertyMetadata(true)
        );

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

    /// <summary>Section 5.3: withdrawing the marquee mid-gesture cancels it before the flag applies.</summary>
    private static void OnMarqueeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
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
    private static void OnReorderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
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

    public static void SetIsRowGestureEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsRowGestureEnabledProperty, value);

    public static bool GetIsRowGestureEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsRowGestureEnabledProperty);

    private void AttachInput()
    {
        PreviewKeyDown += OnTablePreviewKeyDown;

        if (_surface is null)
        {
            return;
        }

        // The container handles pointer input first and marks it handled, so every one of these
        // must be registered with handledEventsToo.
        _surface.AddHandler(
            UIElement.PointerEnteredEvent,
            new PointerEventHandler(OnRowsPointerEntered),
            true
        );
        _surface.AddHandler(
            UIElement.PointerExitedEvent,
            new PointerEventHandler(OnRowsPointerExited),
            true
        );
        _surface.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnRowsPointerPressed),
            true
        );
        _surface.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(OnRowsPointerMoved),
            true
        );
        _surface.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnRowsPointerReleased),
            true
        );
        _surface.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnRowsPointerCanceled),
            true
        );
        _surface.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnRowsPointerCaptureLost),
            true
        );
        _surface.AddHandler(
            UIElement.DoubleTappedEvent,
            new DoubleTappedEventHandler(OnRowsDoubleTapped),
            true
        );
        _surface.AddHandler(UIElement.TappedEvent, new TappedEventHandler(OnRowsTapped), true);

        // Deliberately not handledEventsToo: a cell control that shows its own context flyout marks
        // this handled, and section 15 leaves that control its own menu.
        _surface.ContextRequested += OnRowsContextRequested;
        _surface.SelectionChanged += OnSurfaceSelectionChanged;
    }

    private void DetachInput()
    {
        PreviewKeyDown -= OnTablePreviewKeyDown;
        CancelGesture();

        if (_surface is null)
        {
            return;
        }

        _surface.RemoveHandler(
            UIElement.PointerEnteredEvent,
            new PointerEventHandler(OnRowsPointerEntered)
        );
        _surface.RemoveHandler(
            UIElement.PointerExitedEvent,
            new PointerEventHandler(OnRowsPointerExited)
        );
        _surface.RemoveHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnRowsPointerPressed)
        );
        _surface.RemoveHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(OnRowsPointerMoved)
        );
        _surface.RemoveHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnRowsPointerReleased)
        );
        _surface.RemoveHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnRowsPointerCanceled)
        );
        _surface.RemoveHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnRowsPointerCaptureLost)
        );
        _surface.RemoveHandler(
            UIElement.DoubleTappedEvent,
            new DoubleTappedEventHandler(OnRowsDoubleTapped)
        );
        _surface.RemoveHandler(UIElement.TappedEvent, new TappedEventHandler(OnRowsTapped));
        _surface.ContextRequested -= OnRowsContextRequested;
        _surface.SelectionChanged -= OnSurfaceSelectionChanged;

        _innerScrollViewer = null;
    }

    // ------------------------------------------------------------------ pointer arbiter

    private void OnRowsPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        CancelGesture();

        if (_surface is null)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(_surface);
        bool mouseOrPen =
            e.Pointer.PointerDeviceType is PointerDeviceType.Mouse or PointerDeviceType.Pen;
        if (!mouseOrPen || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        HitTarget hit = HitTest(e.OriginalSource as DependencyObject, out object? item);
        if (hit == HitTarget.Suppressed)
        {
            return;
        }

        SyncSelectionPolicy();

        _gesture = RowGesture.Pressed;
        _gesturePointerId = e.Pointer.PointerId;
        _gestureOrigin = point.Position;
        _gestureItem = item;
        _gestureCtrl = IsDown(VirtualKey.Control);
        _gestureShift = IsDown(VirtualKey.Shift);
        _gestureCouldDrag = item is not null && CanBeginRowDrag(item);

        ApplyPress();
    }

    private void OnRowsPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Under a captured gesture the pointed row stays the one the gesture began on, so the row
        // being dragged keeps its place as the pointed row does.
        if (_gestureCapture is null)
        {
            PointAt(RowFrom(e.OriginalSource as DependencyObject));
        }

        if (_gesture == RowGesture.None || e.Pointer.PointerId != _gesturePointerId)
        {
            return;
        }

        Point now = e.GetCurrentPoint(_surface).Position;

        if (_gesture == RowGesture.Marquee)
        {
            _marquee.Track(now);
            return;
        }

        if (_gesture == RowGesture.RowDrag)
        {
            UpdateDragDestination(_rowDrag.Track(now.Y));
            return;
        }

        if (
            Math.Abs(now.X - _gestureOrigin.X) < DragThresholdDips
            && Math.Abs(now.Y - _gestureOrigin.Y) < DragThresholdDips
        )
        {
            return;
        }

        CommitGesture(e.Pointer);
    }

    /// <summary>
    /// The threshold is crossed. Which gesture that becomes is decided once, here, and no selection
    /// change is committed on the way. A press that can become neither gesture stays a press, so
    /// its release still applies the click the press deferred.
    /// </summary>
    private void CommitGesture(Pointer pointer)
    {
        RowGesture gesture = GestureAtThreshold();
        if (_surface is not { } rows || gesture == RowGesture.None)
        {
            return;
        }

        // A committed gesture has to keep reporting once the pointer leaves the list, and has to
        // get the release that ends it wherever that happens. Without the capture there is no
        // such release: a gesture begun anyway would outlive the button, overlay drawn and the
        // marquee's auto-scroll running, until the next press cancelled it. So no capture, no
        // gesture; the press stays a press and its release, if it arrives, is the click.
        if (!rows.CapturePointer(pointer))
        {
            return;
        }

        _gestureCapture = pointer;

        if (gesture == RowGesture.RowDrag)
        {
            BeginRowDrag(rows, _gestureItem!);
        }
        else
        {
            BeginMarquee(rows);
        }
    }

    /// <summary>
    /// Which gesture the press becomes once the threshold is crossed: a press on a row the table
    /// would drag becomes section 16's drag, and any other press draws section 14's rectangle,
    /// from empty row surface or from a row the table withholds the drag from. Nothing guesses
    /// from the direction of the first movement: a user selecting rows sweeps downward and a user
    /// reordering drags downward, so any threshold between the two is a guess, and the wrong guess
    /// changes queue positions. The reference implementation draws the same line between its rows
    /// and the canvas beside them; this table also lets a row it would not drag start the
    /// rectangle, because nothing competes for the gesture there, and the pointer has said so
    /// already: the arrow, where a draggable row shows the move cursor. The drag needs the press's
    /// answer as well as this one: a press that found no drag to defer for has applied its click,
    /// and a row that became draggable since must not move a packet that click just made.
    /// </summary>
    /// <remarks>
    /// What went wrong before was not this rule but what the user could see of it. A row container
    /// is only as wide as its columns, so the list beside the columns is empty surface: in a
    /// 2,538-wide list with 1,120-wide rows, measured once on the original host, 56% of every row
    /// band drew a rectangle where the owner expected a drag, and the boundary moved with every
    /// fit, resize and hidden column while nothing on screen showed where it was. That host's
    /// diagnostics harness has been deleted, so specification 14 records the figure and nothing
    /// here reproduces it; what the rule turns on is the surface being wide enough to be pressed
    /// by mistake, not the fraction. The surface stays, because a full table has nowhere else to
    /// start a rectangle; the pointer now shows the line, with <see cref="Body.Row"/>
    /// setting the move cursor over a row that can be dragged.
    /// </remarks>
    private RowGesture GestureAtThreshold()
    {
        SyncSelectionPolicy();

        if (_gestureItem is object item && _gestureCouldDrag && CanBeginRowDrag(item))
        {
            return RowGesture.RowDrag;
        }

        return IsMarqueeEnabled && _selection.AllowsMultiple ? RowGesture.Marquee : RowGesture.None;
    }

    /// <summary>Sections 5, 14 and 16: whether the press can become any gesture at all.</summary>
    private bool CanCommitGesture() => GestureAtThreshold() != RowGesture.None;

    private void OnRowsPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        bool ours = e.Pointer.PointerId == _gesturePointerId;

        if (ours && _gesture == RowGesture.RowDrag)
        {
            // The drop ends the gesture itself, before the request reaches the host.
            CompleteRowDrag(e.GetCurrentPoint(_surface).Position.Y);
            return;
        }

        if (ours && _gesture == RowGesture.Pressed)
        {
            DispatchClick();
        }

        CancelGesture();
    }

    /// <summary>
    /// A cancelled press keeps whatever click it applied, as any press the system cancels does. A
    /// cancelled marquee goes back to the selection as it stood before the press, that click
    /// included, because a rectangle begun on a row is one gesture with its press. Section 16
    /// leaves the selection alone until the host acts on the request, so a cancelled drag has
    /// nothing to put back.
    /// </summary>
    private void OnRowsPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture == RowGesture.Marquee && e.Pointer.PointerId == _gesturePointerId)
        {
            RestoreSelectionBeforeMarquee();
            CommitSelection();
        }

        CancelGesture();
    }

    /// <summary>
    /// Losing the capture the table took ends the gesture where it stands, and leaves a marquee's
    /// result alone: the user has watched it apply row by row, and taking that back would be the
    /// surprise. The test is which element lost the capture, never when the event arrived: the
    /// container takes a capture of its own on the press and loses it the moment the table captures
    /// for the gesture, and that loss bubbles through here too. Guarding on timing instead would
    /// end every drag on its first move the day the framework raises it a tick later.
    /// </summary>
    private void OnRowsPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (
            _gestureCapture is not null
            && ReferenceEquals(e.OriginalSource, _surface)
            && e.Pointer.PointerId == _gesturePointerId
        )
        {
            CancelGesture();
        }
    }

    /// <summary>
    /// What the press decides and what it does about it, over the gesture fields already set.
    /// </summary>
    /// <remarks>
    /// A plain press waits for the release whenever the drag it might become must not have changed
    /// the selection first: an empty-surface marquee, a drag of the selected packet, and section
    /// 16's drag of an unselected row, which leaves the existing selection standing.
    /// <para>
    /// This wait was once removed, to answer a plain click at the press rather than at the release,
    /// and the owner rejected that outright: it is a behaviour change, and the button hold is not
    /// what he calls sluggish. What he does is a sort header that shows nothing for more than
    /// 100 ms after the click, and a column resize that draws no guide line while the frame rate
    /// falls so far that the pointer itself stops tracking. Neither is paid for here. Do not trade
    /// this rule for latency again without a measurement that names this code.
    /// </para>
    /// <para>
    /// One method rather than a rule the caller applies, because the proof harness cannot build a
    /// <see cref="PointerRoutedEventArgs"/> and so cannot enter through the real handler. Given the
    /// rule inline, it kept its own copy, and a harness that can agree with itself while
    /// disagreeing with the control is a suite that stays green through a regression.
    /// </para>
    /// </remarks>
    private void ApplyPress()
    {
        _gestureSelection = _selection.Capture();
        _gestureDeferred =
            _gestureItem is null
            || (
                !_gestureCtrl
                && !_gestureShift
                && (_selection.IsSelected(_gestureItem) || _gestureCouldDrag)
            );

        if (_gestureDeferred)
        {
            // Undo the container's own toggle inside the same input event.
            ApplySelectionToContainers();
            return;
        }

        ApplyPressSelection();
    }

    /// <summary>Release with no gesture running: the click a press held back, if it held one.</summary>
    private void DispatchClick()
    {
        if (_gestureDeferred)
        {
            ApplyPressSelection();
        }
    }

    /// <summary>
    /// The selection change the press stands for: section 13's rules over a row, and the clear for
    /// the surface beside the rows. One implementation, whether the press applies it or the release
    /// does, so the two can never disagree about what a click means.
    /// </summary>
    private void ApplyPressSelection()
    {
        if (_gestureItem is object item)
        {
            SelectItem(item, _gestureCtrl, _gestureShift);
            return;
        }

        SyncSelectionPolicy();
        _selection.Clear();
        CommitSelection();
    }

    /// <summary>Section 13's click, tap, and Space selection, applied through one model operation.</summary>
    private void SelectItem(object item, bool ctrl, bool shift)
    {
        if (ResolveItem(item) is not { } current)
            return;
        SyncSelectionPolicy();
        _selection.Select(current, ctrl, shift, View);
        CommitSelection();
    }

    private void OnRowsTapped(object sender, TappedRoutedEventArgs e)
    {
        if (
            e.PointerDeviceType == PointerDeviceType.Touch
            && SelectFromTap(
                e.OriginalSource as DependencyObject,
                IsDown(VirtualKey.Control),
                IsDown(VirtualKey.Shift)
            )
        )
        {
            e.Handled = true;
        }
    }

    private bool SelectFromTap(DependencyObject? source, bool ctrl, bool shift)
    {
        HitTarget hit = HitTest(source, out object? item);
        if (hit == HitTarget.Suppressed)
        {
            return false;
        }

        if (item is not null)
        {
            SelectItem(item, ctrl, shift);
        }
        else
        {
            SyncSelectionPolicy();
            _selection.Clear();
            CommitSelection();
        }

        return true;
    }

    /// <summary>
    /// Section 14's rectangle from the press position: empty row surface, or a row the table would
    /// not drag.
    /// </summary>
    private void BeginMarquee(ListView rows)
    {
        _gesture = RowGesture.Marquee;
        _marquee.Begin(
            rows,
            InnerScrollViewer(),
            _marqueeOverlay,
            _gestureOrigin,
            ApplyMarqueeCoverage
        );
    }

    /// <summary>
    /// Section 16. A selected row moves the whole selected packet and any other row moves alone,
    /// resolved here because nothing can move the selection under a live drag. The marker shows the
    /// boundary from the first moment, and nothing else about the table changes until the host acts
    /// on the request this drag ends with.
    /// </summary>
    private void BeginRowDrag(ListView rows, object item)
    {
        _gesture = RowGesture.RowDrag;
        _movingPacket = _selection.IsSelected(item) ? SelectedItems : new[] { item };

        _rowDrag.Begin(rows, _rowInsertionMarker);
        UpdateDragDestination(_rowDrag.Track(_gestureOrigin.Y));
        RowVisualsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelGesture()
    {
        bool wasDragging = _gesture == RowGesture.RowDrag;

        _gesture = RowGesture.None;
        _marquee.End();
        _rowDrag.End();
        UpdateDragDestination(-1);
        ReleaseGesturePointer();
        _gestureItem = null;
        _gestureDeferred = false;
        _gestureCouldDrag = false;
        _gestureSelection = null;
        _movingPacket = Array.Empty<object>();

        if (wasDragging)
        {
            RowVisualsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ReleaseGesturePointer()
    {
        if (_gestureCapture is Pointer pointer)
        {
            _gestureCapture = null;
            _surface?.ReleasePointerCapture(pointer);
        }
    }

    // ------------------------------------------------------------------ marquee

    /// <summary>
    /// Section 14's three compositions. The gesture's modifiers are the ones read at press, so a
    /// key pressed or released mid-drag does not change the rule the user started under.
    /// </summary>
    private void ApplyMarqueeCoverage()
    {
        _selection.SetMarqueeSelection(MarqueeItems(), View);
        CommitSelection();
    }

    /// <summary>
    /// What the rectangle selects, under the modifier the gesture started with. Read on every
    /// pointer move and again whenever a source update reorders the rows beneath it, because the
    /// answer is a question about the current view and not about the one the gesture began on.
    /// </summary>
    private List<object> MarqueeItems() =>
        _gestureShift ? MarqueeExtendedFromAnchor()
        : _gestureCtrl ? MarqueeToggledAgainstStart()
        : MarqueeCovered();

    private List<object> MarqueeCovered()
    {
        List<object> items = new();
        foreach (int index in _marquee.CoveredIndices)
        {
            object item = View[index];
            if (_selection.IsInteractive(item))
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>Ctrl: each covered row flips against the selection as it stood at press.</summary>
    private List<object> MarqueeToggledAgainstStart()
    {
        HashSet<object> started = new(_gestureSelection!.Items, _identity);
        HashSet<object> covered = new(MarqueeCovered(), _identity);

        List<object> items = new();
        foreach (object item in View)
        {
            if (_selection.IsInteractive(item) && started.Contains(item) != covered.Contains(item))
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>Shift: the inclusive run from the anchor out to the far edge of what is covered.</summary>
    private List<object> MarqueeExtendedFromAnchor()
    {
        int anchor = IndexInView(_selection.Anchor);
        if (anchor < 0 || _marquee.CoveredIndices.Count == 0)
        {
            return new List<object>(_gestureSelection!.Items);
        }

        int low = Math.Min(anchor, _marquee.LowestCovered);
        int high = Math.Max(anchor, _marquee.HighestCovered);

        List<object> items = new();
        for (int i = low; i <= high; i++)
        {
            if (_selection.IsInteractive(View[i]))
            {
                items.Add(View[i]);
            }
        }

        return items;
    }

    /// <summary>
    /// Section 14's Escape, and a host withdrawing the gesture mid-drag: both end the marquee by putting
    /// back the selection the gesture started from. The caller publishes the restored selection.
    /// </summary>
    private void RestoreSelectionBeforeMarquee()
    {
        if (_gesture != RowGesture.Marquee)
        {
            return;
        }

        SelectionState.Checkpoint restored = _gestureSelection!;
        CancelGesture();
        SyncSelectionPolicy();
        _selection.Restore(restored, View);
    }

    // ------------------------------------------------------------------ row drag

    /// <summary>
    /// Sections 5 and 16: the gesture is offered only when the host enabled it, the view shows the
    /// row order, so that the boundary a drop names is a place in that order, and the row is one
    /// the user may act on. The same answer decides whether the press defers its selection change,
    /// so a row that cannot be dragged still selects on press, and what a drag from it does
    /// instead: section 14's rectangle.
    /// </summary>
    /// <remarks>
    /// Having a <see cref="ReorderRequested"/> handler used to be part of this, which made
    /// subscribing to an event load-bearing behaviour that nothing in the API announced — two
    /// owners of one capability. <see cref="CanReorder"/> is now the only one, and it
    /// is off by default, so a host that has no order to change offers no dead drag either.
    /// </remarks>
    internal bool CanBeginRowDrag(object item)
    {
        // The rows ask this for their cursor as soon as they load, which can be before any press
        // or reconcile has copied the host's interaction predicate into the model.
        SyncSelectionPolicy();
        return _hierarchy is null
            && CanReorder
            && ShowsRowOrder
            && _selection.IsInteractive(item)
            && (
                CanReorderItem is null
                || (
                    CanReorderItem(item)
                    && (!_selection.IsSelected(item) || SelectedItems.All(CanReorderItem))
                )
            );
    }

    /// <summary>
    /// Section 16's drop. The gesture ends before the request is raised, so a handler that updates
    /// its source inside the event finds the table already idle.
    /// </summary>
    private void CompleteRowDrag(double y)
    {
        int boundary = _rowDrag.Track(y);
        IReadOnlyList<object> moving = _movingPacket;

        CancelGesture();
        RequestReorder(moving, boundary);
    }

    /// <summary>
    /// Section 16's rejections, all silent: nothing to move, no realized boundary to move it to,
    /// and a placement that leaves the order as it stands. A drop inside a packet that is already
    /// one block is that last one, because the anchor resolves past the packet to the row it
    /// already sits beside. A scattered packet is gathered at the boundary instead, which does
    /// change the order. A view that no longer shows the row order is refused here too, as a
    /// backstop: the sort that took it away has already cancelled the drag.
    /// </summary>
    /// <remarks>
    /// Section 5.1 speaks in row order. Under the row-order column sorted downward the view runs
    /// the other way, so the request is read against the view in the opposite direction: the
    /// packet reversed, and its anchor the first row above the boundary that is not moving, with
    /// null at the top of the view meaning the end of the row order. A host that applies the
    /// request in row order then puts the packet exactly where it was dropped. Reversing the
    /// packet is right only because it arrives in view order: a selected packet is built by
    /// walking the view, and a lone row is a packet of one.
    /// </remarks>
    private void RequestReorder(IReadOnlyList<object> moving, int boundary)
    {
        if (_hierarchy is not null || moving.Count == 0 || boundary < 0 || !ShowsRowOrder)
        {
            return;
        }

        bool reversed = RowOrderIsReversed;
        object? target = reversed
            ? InsertTargetAbove(boundary, moving)
            : InsertTarget(boundary, moving);
        if (KeepsOrder(moving, target, reversed))
        {
            return;
        }

        IReadOnlyList<object> packet = reversed ? moving.Reverse().ToList() : moving;
        _reorderRequested?.Invoke(this, new ReorderRequestedEventArgs(packet, target));
    }

    /// <summary>Section 16: the moving rows carry the platform's own dragged-item treatment.</summary>
    internal bool IsRowDragging(object? item)
    {
        if (item is null || _gesture != RowGesture.RowDrag)
        {
            return false;
        }

        foreach (object moving in _movingPacket)
        {
            if (_selection.IsSame(moving, item))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Section 16: the destination, exposed as the table's automation status for as long as the
    /// drag is live and announced whenever it moves. The control claims no UI Automation drag/drop
    /// pattern — that would promise a drop-target tree it does not implement — so this status is
    /// the whole accessible account of a drag. A negative boundary means there is none.
    /// </summary>
    private void UpdateDragDestination(int boundary)
    {
        if (boundary == _dragBoundary)
        {
            return;
        }

        _dragBoundary = boundary;
        RefreshDragText();
    }

    private void RefreshDragText()
    {
        string status = DragDestination(_dragBoundary);
        AutomationProperties.SetItemStatus(this, status);

        if (status.Length > 0)
        {
            FrameworkElementAutomationPeer
                .FromElement(this)
                ?.RaiseNotificationEvent(
                    AutomationNotificationKind.Other,
                    AutomationNotificationProcessing.MostRecent,
                    status,
                    RowDragActivityId
                );
        }
    }

    /// <summary>
    /// The destination as a position rather than the row's own name: a row's name is every cell it
    /// holds, which is too long to repeat on each boundary the pointer crosses.
    /// </summary>
    private string DragDestination(int boundary)
    {
        if (boundary < 0)
        {
            return string.Empty;
        }

        object? target = InsertTarget(boundary, _movingPacket);
        return target is null
            ? Strings.DropAtEnd
            : string.Format(
                CultureInfo.CurrentCulture,
                Strings.DropBeforeRow,
                IndexInView(target) + 1,
                View.Count
            );
    }

    /// <summary>
    /// Section 5.1's insertion anchor: the first row at or after the boundary that is not moving,
    /// or null for the end of the view. Naming the row rather than an index is what makes the
    /// request immune to the packet's own removal — once the host has taken the packet out, that
    /// row is still exactly the one the packet goes before, wherever it has ended up. A held row is
    /// never the anchor, because the host no longer has it.
    /// </summary>
    private object? InsertTarget(int boundary, IReadOnlyList<object> moving)
    {
        HashSet<object> packet = new(moving, _identity);
        for (int i = boundary; i < View.Count; i++)
        {
            if (!packet.Contains(View[i]) && !IsHeld(View[i]))
            {
                return View[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The anchor read the other way, for the row-order column sorted downward: the first row
    /// above the boundary that is not moving, or null when nothing stands above it, which is the
    /// end of the row order.
    /// </summary>
    private object? InsertTargetAbove(int boundary, IReadOnlyList<object> moving)
    {
        HashSet<object> packet = new(moving, _identity);
        for (int i = Math.Min(boundary, View.Count) - 1; i >= 0; i--)
        {
            if (!packet.Contains(View[i]) && !IsHeld(View[i]))
            {
                return View[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the placement leaves the view exactly as it is: the packet is already one block, and
    /// the row beside that block, next in row order, is the one it would be inserted before — the
    /// row below the block, or the row above it when the view runs opposite to the row order. A
    /// scattered packet always moves, because the drop gathers it. A packet that is the whole view
    /// is the same test — its block ends the view and its target is null, which section 5.1
    /// requires to raise nothing.
    /// </summary>
    private bool KeepsOrder(IReadOnlyList<object> moving, object? target, bool reversed)
    {
        // The row order the request speaks of is the host's, which has no held rows.
        IReadOnlyList<object> rows =
            _held.Count == 0 ? View : View.Where(row => !IsHeld(row)).ToList();
        int start = -1;
        for (int i = 0; i < rows.Count && start < 0; i++)
        {
            if (_selection.IsSame(rows[i], moving[0]))
            {
                start = i;
            }
        }

        if (start < 0 || start + moving.Count > rows.Count)
        {
            return false;
        }

        for (int i = 1; i < moving.Count; i++)
        {
            if (!_selection.IsSame(rows[start + i], moving[i]))
            {
                return false;
            }
        }

        int beside = reversed ? start - 1 : start + moving.Count;
        object? neighbour = beside >= 0 && beside < rows.Count ? rows[beside] : null;
        return _selection.IsSame(neighbour, target);
    }

    /// <summary>
    /// Section 5.3: a source update, <see cref="RefreshView"/>, a sort change, or the host
    /// withdrawing <see cref="CanReorder"/> all end a live drag, with no request.
    /// </summary>
    private void CancelRowDrag()
    {
        if (_gesture == RowGesture.RowDrag)
        {
            CancelGesture();
        }
    }

    private void OnRowsDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (
            HitTest(e.OriginalSource as DependencyObject, out object? item) != HitTarget.Row
            || item is null
        )
        {
            return;
        }

        SyncSelectionPolicy();
        if (!_selection.IsInteractive(item))
        {
            return;
        }

        // A mouse raises this on the second press, which has already armed a press. Left live, a
        // movement before its release would start a row drag or a marquee after the invocation.
        CancelGesture();
        ItemInvoked?.Invoke(this, new ItemInvokedEventArgs(item, SelectedItems));
        e.Handled = true;
    }

    // ------------------------------------------------------------------ context requests

    /// <summary>
    /// Section 15's context invocation. The platform raises this for right-click, touch
    /// press-and-hold, Menu, and Shift+F10 alike, so the table needs no long-press timer of its own
    /// and no second keyboard path.
    /// </summary>
    private void OnRowsContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (
            _surface is null
            || HitTest(e.OriginalSource as DependencyObject, out object? item)
                == HitTarget.Suppressed
        )
        {
            return;
        }

        // Menu and Shift+F10 address the current row wherever row focus physically sits. A pointer
        // request that landed on no row is empty surface, and asks for nothing.
        if (item is null)
        {
            if (e.TryGetPosition(_surface, out _))
            {
                return;
            }

            item = _selection.Current;
        }

        if (item is null || _surface.ContainerFromItem(item) is not FrameworkElement row)
        {
            return;
        }

        Point? relativePoint = e.TryGetPosition(row, out Point position) ? position : null;
        e.Handled = RequestRowContext(item, row, relativePoint);
    }

    /// <summary>Section 15's mechanics, completed before the request reaches the host.</summary>
    private bool RequestRowContext(
        object item,
        FrameworkElement placementTarget,
        Point? relativePoint
    )
    {
        SyncSelectionPolicy();
        if (!_selection.IsInteractive(item))
        {
            return false;
        }

        // A right button pressed during a left-button press would otherwise leave the arbiter armed,
        // and its release would then re-select over this request's selection.
        CancelGesture();
        SelectForContext(item);
        Selection packet = CommitSelection();

        if (
            _detached
            || !_selection.IsInteractive(item)
            || !SameSelection(packet, Selection)
            || !View.Any(row => ReferenceEquals(row, item))
            || _surface is null
            || !ReferenceEquals(_surface.ItemFromContainer(placementTarget), item)
        )
            return true;

        IReadOnlyList<Popup> open = OpenPopups();
        ItemContextRequested?.Invoke(
            this,
            new ItemContextRequestedEventArgs(item, packet.Items, placementTarget, relativePoint)
        );
        HoldForMenu(item, open);
        return true;
    }

    /// <summary>
    /// Walk from the input target up to the row surface. An interactive descendant, an explicit
    /// <c>IsRowGestureEnabled="False"</c> subtree, and the scroll bars all stop a table gesture; anything
    /// else that reaches the list without passing a container is empty row surface.
    /// </summary>
    private HitTarget HitTest(DependencyObject? source, out object? item)
    {
        item = null;
        if (_surface is null)
        {
            return HitTarget.Suppressed;
        }

        DependencyObject? node = source;
        while (node is not null && !ReferenceEquals(node, _surface))
        {
            if (node is ListViewItem container)
            {
                item = _surface.ItemFromContainer(container);
                return item is null ? HitTarget.Suppressed : HitTarget.Row;
            }

            if (node is ScrollBar || !GetIsRowGestureEnabled(node))
            {
                return HitTarget.Suppressed;
            }

            // A tab stop inside a cell is an editor, button, or selector and owns its own input.
            if (node is Control { IsTabStop: true })
            {
                return HitTarget.Suppressed;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return node is null ? HitTarget.Suppressed : HitTarget.EmptySurface;
    }

    // ------------------------------------------------------------------ keyboard

    private void OnTablePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Escape belongs to the live gesture, not to the row surface: a committed gesture is driven
        // by a captured pointer and does not move focus, so the focus gate below would refuse it.
        if (e.Key == VirtualKey.Escape && CancelCommittedGesture())
        {
            e.Handled = true;
            return;
        }

        if (e.Handled || _surface is null || RowSurfaceFocusState() == FocusState.Unfocused)
        {
            return;
        }

        if (
            IsDown(VirtualKey.Menu)
            || IsDown(VirtualKey.LeftWindows)
            || IsDown(VirtualKey.RightWindows)
        )
        {
            return;
        }

        bool ctrl = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);

        e.Handled = e.Key switch
        {
            VirtualKey.Left or VirtualKey.Right => NavigateHierarchy(e.Key, shift, ctrl),
            VirtualKey.Up => MoveCurrentBy(-1, shift, ctrl),
            VirtualKey.Down => MoveCurrentBy(1, shift, ctrl),
            VirtualKey.PageUp => MoveCurrentBy(-RowsPerPage(), shift, ctrl),
            VirtualKey.PageDown => MoveCurrentBy(RowsPerPage(), shift, ctrl),
            VirtualKey.Home => MoveCurrentToEdge(first: true, shift, ctrl),
            VirtualKey.End => MoveCurrentToEdge(first: false, shift, ctrl),
            VirtualKey.A when ctrl => SelectAllFromKeyboard(),
            VirtualKey.Enter => InvokeCurrentItem(),
            VirtualKey.Space => SelectFocusedItem(ctrl, shift),
            _ => false,
        };
    }

    /// <summary>
    /// Section 14 and section 16's Escape. The marquee has already changed the selection and puts it
    /// back; a row drag has changed nothing and simply stops, with no request.
    /// </summary>
    private bool CancelCommittedGesture()
    {
        if (_gesture == RowGesture.Marquee)
        {
            RestoreSelectionBeforeMarquee();
            CommitSelection();
            return true;
        }

        if (_gesture == RowGesture.RowDrag)
        {
            CancelGesture();
            return true;
        }

        return false;
    }

    /// <summary>
    /// The table's keys apply only from the passive row surface. A focused cell editor, button, or
    /// any other interactive descendant is not one of these elements, so it keeps its own keys.
    /// </summary>
    /// <summary>
    /// How the row surface holds focus, or <see cref="FocusState.Unfocused"/> when it does not.
    /// </summary>
    /// <remarks>
    /// The state matters as much as the fact. The platform draws its focus visual for
    /// <see cref="FocusState.Keyboard"/> and <see cref="FocusState.Programmatic"/> but not for
    /// <see cref="FocusState.Pointer"/>, which is why clicking anything in Windows leaves no focus
    /// ring while tabbing to it does. Restoring focus after a snapshot has to restore the state the
    /// row already had; asking for Programmatic instead turns every pointer click, and every tick
    /// of a live source, into a keyboard-style ring around the row.
    /// </remarks>
    private FocusState RowSurfaceFocusState()
    {
        if (_surface is null || XamlRoot is null)
        {
            return FocusState.Unfocused;
        }

        if (FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
        {
            return FocusState.Unfocused;
        }

        if (ReferenceEquals(focused, this) || ReferenceEquals(focused, _surface))
        {
            return ((Control)focused).FocusState;
        }

        return focused is ListViewItem row && IsInsideRows(focused)
            ? row.FocusState
            : FocusState.Unfocused;
    }

    private bool IsInsideRows(DependencyObject node)
    {
        DependencyObject? current = node;
        while (current is not null)
        {
            if (ReferenceEquals(current, _surface))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool MoveCurrentBy(int delta, bool extend, bool ctrl)
    {
        List<object> interactive = InteractiveItems();
        if (interactive.Count == 0)
        {
            return false;
        }

        int index = IndexOfCurrent(interactive);
        int target =
            index < 0
                ? (delta > 0 ? 0 : interactive.Count - 1)
                : Math.Clamp(index + delta, 0, interactive.Count - 1);

        return MoveCurrentTo(interactive[target], extend, ctrl);
    }

    private bool MoveCurrentToEdge(bool first, bool extend, bool ctrl)
    {
        List<object> interactive = InteractiveItems();
        if (interactive.Count == 0)
        {
            return false;
        }

        return MoveCurrentTo(first ? interactive[0] : interactive[^1], extend, ctrl);
    }

    private bool MoveCurrentTo(object item, bool extend, bool ctrl)
    {
        _selection.Navigate(item, ctrl, extend, View);
        CommitSelection();
        if (_detached || !ReferenceEquals(_selection.Current, item))
            return true;

        ScrollIntoView(item);

        // Keyboard navigation is the one path that should show the platform's focus ring: this is
        // reached from the arrow, Home, End and page keys, and section 19 requires a visible focus
        // cue for exactly this case.
        if (!_detached && ReferenceEquals(_selection.Current, item))
            FocusRow(item, FocusState.Keyboard);
        return true;
    }

    private bool SelectFocusedItem(bool ctrl, bool shift)
    {
        if (_surface is null || RowSurfaceFocusState() == FocusState.Unfocused)
        {
            return false;
        }

        object? item = FocusManager.GetFocusedElement(XamlRoot) is ListViewItem row
            ? _surface.ItemFromContainer(row)
            : _selection.Current;
        SyncSelectionPolicy();
        if (item is null || !_selection.IsInteractive(item))
        {
            return false;
        }

        SelectItem(item, ctrl, shift);
        return true;
    }

    private bool SelectAllFromKeyboard()
    {
        SyncSelectionPolicy();
        if (!_selection.AllowsMultiple)
        {
            return false;
        }

        _selection.SelectAll(View);
        CommitSelection();
        return true;
    }

    private bool InvokeCurrentItem()
    {
        SyncSelectionPolicy();
        if (_selection.Current is not object item || !_selection.IsInteractive(item))
        {
            return false;
        }

        ItemInvoked?.Invoke(this, new ItemInvokedEventArgs(item, SelectedItems));
        return true;
    }

    private List<object> InteractiveItems()
    {
        SyncSelectionPolicy();

        List<object> interactive = new();
        foreach (object item in View)
        {
            if (_selection.IsInteractive(item))
            {
                interactive.Add(item);
            }
        }

        return interactive;
    }

    private int IndexOfCurrent(List<object> interactive)
    {
        for (int i = 0; i < interactive.Count; i++)
        {
            if (_selection.IsSame(interactive[i], _selection.Current))
            {
                return i;
            }
        }

        return -1;
    }

    private int RowsPerPage()
    {
        double viewport = InnerScrollViewer()?.ViewportHeight ?? 0;
        double rowHeight = FirstVisibleRowHeight();
        return viewport > 0 && rowHeight > 0 ? Math.Max(1, (int)(viewport / rowHeight)) : 1;
    }

    /// <summary>The height of the first row in the viewport, or 0 while no row is realized.</summary>
    private double FirstVisibleRowHeight()
    {
        if (_surface?.ItemsPanelRoot is not ItemsStackPanel rows || rows.FirstVisibleIndex < 0)
        {
            return 0;
        }

        return _surface.ContainerFromIndex(rows.FirstVisibleIndex) is FrameworkElement container
            ? container.ActualHeight
            : 0;
    }

    /// <summary>
    /// Reveal an item in the current view without changing selection or keyboard focus.
    /// </summary>
    public void ScrollIntoView(object item)
    {
        // ItemsStackPanel's first request can land one row short while estimating its extent.
        _surface?.ScrollIntoView(item);
        _surface?.ScrollIntoView(item);
    }

    /// <summary>
    /// Put row focus back where it was before the view changed, in the state it was in.
    /// </summary>
    /// <remarks>
    /// The state has to be captured before the change, not read back after it. When the framework
    /// removes the container holding focus it rescues focus itself, and it hands the leaving
    /// element's own focus state to whatever it lands on. That target is not scoped to this
    /// control, so focus can leave the table entirely: measured here, a sort with a focused row
    /// left focus on the host page's own Clear button. Restore the retained physical row when
    /// available, otherwise the selection's logical focus.
    /// </remarks>
    private void RestoreRowFocus(FocusState state, object? row = null)
    {
        if (
            state == FocusState.Unfocused
            || (row ?? _selection.Focus) is not object item
            || _surface is null
        )
        {
            return;
        }

        if (_surface.ContainerFromItem(item) is Control container)
        {
            container.Focus(state);
            return;
        }

        object? handoff = row is null ? null : FocusManager.GetFocusedElement(XamlRoot);
        object? focus = _selection.Focus;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (
                    (
                        row is null
                            ? _selection.IsSame(item, _selection.Focus)
                            : ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), handoff)
                                && ReferenceEquals(_selection.Focus, focus)
                                && View.Contains(item)
                    ) && _surface?.ContainerFromItem(item) is Control realized
                )
                {
                    realized.Focus(state);
                }
            }
        );
    }

    private void FocusRow(object item, FocusState state)
    {
        if (_surface is null)
        {
            return;
        }

        // A realized row takes focus as it is. The forced layout pass is only for a row that
        // ScrollIntoView has just asked for and the list has not built yet.
        Control? container = _surface.ContainerFromItem(item) as Control;
        if (container is null)
        {
            _surface.UpdateLayout();
            container = _surface.ContainerFromItem(item) as Control;
        }

        container?.Focus(state);
    }

    private ScrollViewer? InnerScrollViewer()
    {
        if (_innerScrollViewer is not null || _surface is null)
        {
            return _innerScrollViewer;
        }

        _innerScrollViewer = FindDescendant<ScrollViewer>(_surface);
        return _innerScrollViewer;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is T deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
