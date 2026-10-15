using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
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

    private readonly Body.Marquee _marquee = new();

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
            table.CancelDrag();
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

        _innerScrollViewer = null;
    }

    // ------------------------------------------------------------------ pointer arbiter

    private void OnRowsPointerPressed(object sender, PointerRoutedEventArgs e)
    {
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

        CancelGesture();
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
        _gestureCouldDrag = item is not null && CanStartDrag(item);

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
            SetDestination(_wheel!.Track(ToRoot(now)));
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
        if (_gesture == RowGesture.Marquee)
            _marquee.Track(now);
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
            BeginDrag(_gestureItem!);
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

        if (_gestureItem is object item && _gestureCouldDrag && CanStartDrag(item))
        {
            return RowGesture.RowDrag;
        }

        return IsMarqueeEnabled && _selection.AllowsMultiple ? RowGesture.Marquee : RowGesture.None;
    }

    /// <summary>Sections 5, 14 and 16: whether the press can become any gesture at all.</summary>
    private bool CanCommitGesture() => GestureAtThreshold() != RowGesture.None;

    private void OnRowsPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _gesturePointerId)
            return;

        if (_gesture == RowGesture.RowDrag)
        {
            // The drop ends the gesture itself, before the request reaches the host.
            CompleteDrag(ToRoot(e.GetCurrentPoint(_surface).Position));
            return;
        }

        if (_gesture == RowGesture.Pressed)
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
        if (e.Pointer.PointerId != _gesturePointerId)
            return;
        if (_gesture == RowGesture.Marquee)
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
            // The container's own toggle undoes itself: it ends in a range call on the view, and
            // the list re-reads the state the table kept.
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

        CancelGesture();
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

    private void CancelGesture()
    {
        bool wasDragging = _gesture == RowGesture.RowDrag;

        _gesture = RowGesture.None;
        _marquee.End();
        EndDrag();
        ReleaseGesturePointer();
        _gestureItem = null;
        _gestureDeferred = false;
        _gestureCouldDrag = false;
        _gestureSelection = null;

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
        HashSet<object> items = new(_gestureSelection!.Items, _identity);
        items.SymmetricExceptWith(MarqueeCovered());
        return items.ToList();
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
        SyncSelectionPolicy();
        int index = IndexInView(_selection.Current);
        if (index < 0)
            return MoveCurrentToEdge(delta > 0, extend, ctrl);
        int target = index;
        int step = Math.Sign(delta);
        int remaining = Math.Abs(delta);
        while (remaining > 0 && index + step >= 0 && index + step < View.Count)
        {
            index += step;
            if (!_selection.IsInteractive(View[index]))
                continue;
            target = index;
            remaining--;
        }
        return MoveCurrentTo(View[target], extend, ctrl);
    }

    private bool MoveCurrentToEdge(bool first, bool extend, bool ctrl)
    {
        SyncSelectionPolicy();
        for (int index = first ? 0 : View.Count - 1; index >= 0 && index < View.Count; index += first ? 1 : -1)
        {
            if (_selection.IsInteractive(View[index]))
                return MoveCurrentTo(View[index], extend, ctrl);
        }
        return false;
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
    /// The reveal waiting for the list to lay out the view change, with the focus that goes with
    /// it. A newer reveal replaces it; see <see cref="_layoutPending"/>.
    /// </summary>
    private (object Item, FocusState Focus)? _pendingReveal;

    /// <summary>
    /// Reveal an item without changing selection or keyboard focus. A source arrival waiting
    /// under the pointer is released before scrolling. After a view change in the same turn the
    /// scroll runs a frame later, once the list has laid the change out.
    /// </summary>
    public void ScrollIntoView(object item)
    {
        if (IndexInView(item) < 0 && _source.Snapshot.Contains(item, _identity))
            Release([item]);

        if (_layoutPending)
        {
            _pendingReveal = (item, FocusState.Unfocused);
            return;
        }
        _pendingReveal = null;
        _surface?.ScrollIntoView(item);
    }

    /// <summary>Scroll to the waiting reveal, and focus it if keyboard navigation asked.</summary>
    private void Reveal()
    {
        if (_pendingReveal is not { } reveal)
            return;
        _pendingReveal = null;
        ScrollIntoView(reveal.Item);
        if (reveal.Focus != FocusState.Unfocused)
            FocusRow(reveal.Item, reveal.Focus);
    }

    /// <summary>
    /// Reveal a batch's waiting arrivals together, then scroll to its target without changing
    /// selection or focus. Already displayed items do not release a future membership change.
    /// </summary>
    public void ScrollIntoView(object item, IEnumerable<object> arrivals)
    {
        ArgumentNullException.ThrowIfNull(arrivals);
        if (_hierarchy is null)
        {
            HashSet<object> waiting = new(arrivals, _identity);
            waiting.ExceptWith(View);
            waiting.IntersectWith(_source.Snapshot);
            if (waiting.Count > 0)
                Release(waiting);
        }
        ScrollIntoView(item);
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

        if (container is null && ReferenceEquals(_pendingReveal?.Item, item))
        {
            // The reveal that builds it is waiting for the frame; the focus goes with it.
            _pendingReveal = (item, state);
            return;
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
