using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Controls;

public sealed partial class Week : UserControl
{
    internal event EventHandler<DateTime>? TimeChanged;
    private static readonly InputSystemCursor MoveCursor = InputSystemCursor.Create(
        InputSystemCursorShape.SizeAll
    );
    private static readonly InputSystemCursor ResizeCursor = InputSystemCursor.Create(
        InputSystemCursorShape.SizeWestEast
    );
    private static readonly InputSystemCursor CreateCursor = InputSystemCursor.Create(
        InputSystemCursorShape.Cross
    );
    private readonly UISettings _settings = new();
    private double Gutter => 64 * _settings.TextScaleFactor;
    private double Ruler => 24 * _settings.TextScaleFactor;
    private double RowHeight => 20 + 24 * _settings.TextScaleFactor;
    private readonly Schedule _model;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _invalid;
    private Drag? _drag;
    private int _day;
    private int _minute;
    private int? _hoverDay;
    private int? _gridStep;
    private double TrackWidth => Math.Max(1, ActualWidth - Gutter);

    internal Week(Schedule model)
    {
        _model = model;
        InitializeComponent();
        Height = Ruler + 7 * RowHeight;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        FlowDirection = FlowDirection.LeftToRight;
        ManipulationMode = ManipulationModes.None;
        AutomationProperties.SetAutomationId(this, "ScheduleTimeline");
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnWeek;
            _model.TextChanged += OnText;
            _settings.TextScaleFactorChanged += OnScaling;
            _clock.Start();
            DrawGrid(rebuild: true);
            Invalidate();
        };
        Unloaded += (_, _) =>
        {
            CancelDrag();
            _model.PropertyChanged -= OnWeek;
            _model.TextChanged -= OnText;
            _settings.TextScaleFactorChanged -= OnScaling;
            _clock.Stop();
            CompositionTarget.Rendering -= OnRendering;
            _invalid = false;
        };
        _clock.Tick += (_, _) => PlaceNow();
        SizeChanged += (_, _) =>
        {
            CancelDrag();
            DrawGrid();
            Invalidate();
        };
        ActualThemeChanged += (_, _) => Invalidate();
        ContextRequested += OnContextRequested;
    }

    // Requests wait for the next frame and draw once, so a fast pointer
    // redraws at the display's rate rather than on every input event.
    private void Invalidate()
    {
        if (_invalid)
            return;
        _invalid = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, object args)
    {
        CompositionTarget.Rendering -= OnRendering;
        _invalid = false;
        Draw();
    }

    private void OnWeek(object? sender, PropertyChangedEventArgs args)
    {
        if (
            _drag is { } drag
            && (
                !_model.CanSchedule
                || drag.Action != PeriodAction.Create && !_model.Periods.Contains(drag.Period)
            )
        )
            CancelDrag();
        if (_drag is null)
            Invalidate();
    }

    private void OnText(object? sender, EventArgs args)
    {
        DrawGrid(rebuild: true);
        Invalidate();
    }

    private void OnScaling(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            CancelDrag();
            DrawGrid(rebuild: true);
            Invalidate();
        });

    private double X(int minute) => Gutter + minute / 1440.0 * TrackWidth;

    private double Y(int day) => Ruler + day * RowHeight;

    private int Minute(double x) =>
        Math.Clamp(PeriodSpan.Snap((x - Gutter) / TrackWidth * 1440), 0, 1425);

    private int Day(double y) => Math.Clamp((int)((y - Ruler) / RowHeight), 0, 6);

    private void Place(FrameworkElement element, double x, double y)
    {
        if (element.Parent is null)
        {
            element.IsHitTestVisible = false;
            AutomationProperties.SetAccessibilityView(element, AccessibilityView.Raw);
            Backdrop.Children.Add(element);
        }
        Position(element, x, y);
    }

    private static void Position(FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
    }

    private Border GetBorder(Canvas canvas, int index, string style)
    {
        if (index == canvas.Children.Count)
        {
            var border = new Border();
            AutomationProperties.SetAccessibilityView(border, AccessibilityView.Raw);
            canvas.Children.Add(border);
        }
        var element = (Border)canvas.Children[index];
        Restyle(element, style);
        return element;
    }

    // A reused element keeps its style when that is unchanged, because a new
    // style makes it measure again.
    private void Restyle(FrameworkElement element, string style)
    {
        var chosen = (Style)Resources[style];
        if (!ReferenceEquals(element.Style, chosen))
            element.Style = chosen;
    }

    private static void Trim(Canvas canvas, int count)
    {
        while (canvas.Children.Count > count)
            canvas.Children.RemoveAt(canvas.Children.Count - 1);
    }

    private TextBlock Text(string text, string style = "WeekTextStyle") =>
        new()
        {
            Text = text,
            Style = (Style)Resources[style],
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

    private void DrawGrid(bool rebuild = false)
    {
        Height = Ruler + 7 * RowHeight;
        if (ActualWidth <= Gutter + 24)
        {
            Backdrop.Children.Clear();
            _gridStep = null;
            return;
        }
        var step = TrackWidth >= 192 * _settings.TextScaleFactor ? 6 : 12;
        rebuild |= _gridStep != step;
        if (rebuild)
            Backdrop.Children.Clear();
        _gridStep = step;
        var index = 0;
        for (var hour = 0; hour <= 24; hour += step)
        {
            var label = rebuild
                ? Text(hour.ToString("00"), "WeekSecondaryStyle")
                : (TextBlock)Backdrop.Children[index];
            index++;
            label.Width = 28 * _settings.TextScaleFactor;
            label.TextAlignment =
                hour == 0 ? TextAlignment.Left
                : hour == 24 ? TextAlignment.Right
                : TextAlignment.Center;
            Place(
                label,
                hour == 0 ? Gutter
                    : hour == 24 ? ActualWidth - label.Width
                    : X(hour * 60) - label.Width / 2,
                0
            );
        }
        for (var day = 0; day < 7; day++)
        {
            var label = rebuild ? Text(_model.ShortDay(day)) : (TextBlock)Backdrop.Children[index];
            index++;
            label.Width = Gutter - 8;
            if (rebuild)
                label.Measure(new Size(label.Width, double.PositiveInfinity));
            Place(label, 0, Y(day) + (RowHeight - label.DesiredSize.Height) / 2);
            var track = rebuild
                ? new Border
                {
                    Style = (Style)Resources["WeekTrackStyle"],
                    BorderThickness = new(1),
                }
                : (Border)Backdrop.Children[index];
            index++;
            track.Width = TrackWidth;
            track.Height = RowHeight - 4;
            Place(track, Gutter, Y(day));
            for (var hour = step; hour < 24; hour += step)
            {
                var line = rebuild
                    ? new Border
                    {
                        Width = 1,
                        Height = RowHeight - 4,
                        Style = (Style)Resources["WeekLineStyle"],
                    }
                    : (Border)Backdrop.Children[index];
                index++;
                Place(line, X(hour * 60), Y(day));
            }
        }
    }

    private void PlaceNow()
    {
        var now = DateTime.Now;
        NowMarker.Visibility =
            ActualWidth > Gutter + 24 ? Visibility.Visible : Visibility.Collapsed;
        NowLine.Height = RowHeight - 4;
        var x = X(now.Hour * 60 + now.Minute);
        var y = Y(Schedule.Weekday(now));
        Position(NowLine, x - 1, y);
        Position(NowDot, x - 4, y - 4);
        TimeChanged?.Invoke(this, now);
    }

    private void Draw()
    {
        Tip.Visibility = Visibility.Collapsed;
        PlaceNow();
        IsHitTestVisible = IsTabStop = _model.FollowsSchedule;
        if (ActualWidth <= Gutter + 24)
        {
            Trim(Blocks, 0);
            Trim(Outlines, 0);
            return;
        }
        // A fixed choice applies all week, so the week shows it in place of the
        // saved periods, which apply again once the schedule is chosen.
        if (_model.FixedMode is { } mode)
        {
            var limits = _model.FormatFixedLimits(mode);
            for (var day = 0; day < 7; day++)
                Block(day, day, new ScheduleRange(0, 1440, mode, null), limits);
            Trim(Blocks, 7);
            Trim(Outlines, 0);
            var allDay = _model.Text.Get("settings", "time_all_day");
            AutomationProperties.SetName(
                this,
                _model.Text.Format("settings", "day_schedule", limits, allDay)
            );
            AutomationProperties.SetHelpText(this, string.Empty);
            return;
        }
        var preview =
            _drag is not { HasMoved: true } drag ? null
            : drag.Action == PeriodAction.Create ? _model.NewPeriod(drag.Days, drag.Span)
            : drag.Period.WithSpan(drag.Span);
        var original = _drag is { Action: not PeriodAction.Create } moving ? moving.Period : null;
        var selected = preview ?? _model.OpenPeriod;
        var blocks = 0;
        var outlines = 0;
        for (var day = 0; day < 7; day++)
        {
            foreach (var range in _model.Ranges(day, original, preview))
            {
                // Short standard time stays unlabelled, so the periods
                // around it keep their room.
                var label =
                    range.Mode != ScheduleMode.Normal || range.End - range.Start >= 240
                        ? _model.FormatMode(range.Mode)
                        : null;
                Block(blocks++, day, range, label);
            }
            if (selected is null)
                continue;
            foreach (var span in selected.Occurrences(day))
            {
                var left = X(Math.Max(0, span.Start));
                var right = X(Math.Min(1440, span.End));
                var outline = GetBorder(Outlines, outlines++, "WeekOutlineStyle");
                outline.Width = right - left;
                outline.Height = RowHeight - 8;
                outline.BorderThickness = new(2);
                outline.CornerRadius = new(0);
                Position(outline, left, Y(day) + 2);
                if (day != (_drag is null ? _hoverDay : _day))
                    continue;
                if (span.Start >= 0)
                    Handle(outlines++, Math.Min(right - 3, left + 4), day);
                if (span.End <= 1440)
                    Handle(outlines++, Math.Max(left, right - 7), day);
            }
        }
        if (FocusState == FocusState.Keyboard && _drag is null)
        {
            var cursor = GetBorder(Outlines, outlines++, "WeekOutlineStyle");
            cursor.Width = 2;
            cursor.Height = RowHeight - 4;
            cursor.BorderThickness = new(1);
            cursor.CornerRadius = new(0);
            Position(cursor, X(_minute), Y(_day));
            ShowTip(
                _model.Text.Format(
                    "settings",
                    "day_schedule",
                    _model.Day(_day),
                    Schedule.Time(_minute)
                ),
                X(_minute),
                _day
            );
        }
        var position = _model.Text.Format(
            "settings",
            "day_schedule",
            _model.Day(_day),
            Schedule.Time(_minute)
        );
        var description = selected?.Description;
        // The name always carries the cursor, which the arrow keys move while
        // an opened period keeps its description.
        AutomationProperties.SetName(
            this,
            _model.Text.Format(
                "settings",
                "timeline_name",
                description is null ? position : description + ". " + position
            )
        );
        AutomationProperties.SetHelpText(this, Hint);
        UpdateTip(description);
        if (_drag is { HasMoved: true } active && preview is not null)
        {
            // A new period's tip sits above its first day, clear of the
            // days the drag covers.
            ShowTip(
                preview.TimeLabel,
                active.Current.X,
                active.Action == PeriodAction.Create ? active.Days[0] : _day
            );
        }
        Trim(Blocks, blocks);
        Trim(Outlines, outlines);
    }

    // A null label leaves short standard gaps blank.
    private void Block(int index, int day, ScheduleRange range, string? label)
    {
        var block = GetBorder(
            Blocks,
            index,
            range.Mode switch
            {
                ScheduleMode.Paused => "WeekPausedStyle",
                ScheduleMode.Alternative => "WeekAlternativeStyle",
                _ => "WeekStandardStyle",
            }
        );
        if (block.Child is not TextBlock)
            block.Child = Text(string.Empty);
        var style = range.Mode == ScheduleMode.Normal ? "WeekSecondaryStyle" : "WeekTextStyle";
        var caption = (TextBlock)block.Child;
        Restyle(caption, style);
        caption.VerticalAlignment = VerticalAlignment.Center;
        caption.TextAlignment = TextAlignment.Center;
        var times =
            range.Start == 0 && range.End == 1440
                ? _model.Text.Get("settings", "time_all_day")
                : _model.Text.Format(
                    "settings",
                    "time_range",
                    Schedule.Time(range.Start),
                    Schedule.Time(range.End)
                );
        caption.Text = label is null ? string.Empty : label + " · " + times;
        block.Width = Math.Max(0, X(range.End) - X(range.Start));
        block.Height = RowHeight - 12;
        block.Padding = new(4, 0, 4, 0);
        Position(block, X(range.Start), Y(day) + 4);
    }

    private void ShowTip(string text, double x, int day)
    {
        TipText.Text = text;
        Tip.MaxWidth = Math.Min(360, ActualWidth);
        Tip.Visibility = Visibility.Visible;
        Tip.Measure(new Size(Tip.MaxWidth, double.PositiveInfinity));
        Position(
            Tip,
            Math.Clamp(x + 12, 0, Math.Max(0, ActualWidth - Tip.DesiredSize.Width)),
            Math.Max(0, Y(day) - Tip.DesiredSize.Height - 2)
        );
    }

    private void Handle(int index, double x, int day)
    {
        var handle = GetBorder(Outlines, index, "WeekHandleStyle");
        handle.Width = 3;
        handle.Height = 14;
        handle.CornerRadius = new(1);
        handle.BorderThickness = new(0);
        Position(
            handle,
            Math.Clamp(x, Gutter, ActualWidth - 3),
            Y(day) + (RowHeight - 4 - handle.Height) / 2
        );
    }

    private (PeriodAction Action, SchedulePeriod? Period) Hit(Point point, int day)
    {
        if (_model.OpenPeriod is { } selected)
        {
            var spans = selected.Occurrences(day).ToArray();
            var start = spans.Any(span =>
                span.Start >= 0 && Math.Abs(point.X - X(span.Start)) <= 8
            );
            var end = spans.Any(span => span.End <= 1440 && Math.Abs(point.X - X(span.End)) <= 8);
            if (start && (!end || point.Y - Y(day) < (RowHeight - 4) / 2))
                return (PeriodAction.Start, selected);
            if (end)
                return (PeriodAction.End, selected);
            foreach (var span in spans)
            {
                if (point.X >= X(Math.Max(0, span.Start)) && point.X <= X(Math.Min(1440, span.End)))
                    return (PeriodAction.Move, selected);
            }
        }
        var minute = Math.Clamp((point.X - Gutter) / TrackWidth * 1440, 0, 1439);
        var period = _model
            .Ranges(day)
            .First(range => minute >= range.Start && minute < range.End)
            .Period;
        return (period is null ? PeriodAction.Create : PeriodAction.Move, period);
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs args)
    {
        base.OnPointerPressed(args);
        if (!_model.CanSchedule || _drag is not null)
            return;
        var pointer = args.GetCurrentPoint(this);
        var point = pointer.Position;
        if (
            !pointer.Properties.IsLeftButtonPressed
            || point.X < Gutter
            || point.Y < Ruler
            || point.Y >= Height
        )
            return;
        _day = Day(point.Y);
        _hoverDay = _day;
        _minute = Minute(point.X);
        var hit = Hit(point, _day);
        Focus(FocusState.Pointer);
        if (!CapturePointer(args.Pointer))
            return;
        HoverTip.IsOpen = false;
        ToolTipService.SetToolTip(this, null);
        var period = hit.Period ?? _model.NewPeriod([_day], new(_minute, 15));
        _drag = new(hit.Action, period, point, args.Pointer.PointerId);
        args.Handled = true;
        Invalidate();
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs args)
    {
        base.OnPointerMoved(args);
        var pointer = args.GetCurrentPoint(this);
        var point = pointer.Position;
        if (_drag is { } drag)
        {
            if (args.Pointer.PointerId != drag.Pointer)
                return;
            if (pointer.Properties.IsLeftButtonPressed && UpdateDrag(point))
                Invalidate();
            args.Handled = true;
        }
        int? day = point.X >= Gutter && point.Y >= Ruler && point.Y < Height ? Day(point.Y) : null;
        if (_hoverDay != day)
        {
            _hoverDay = day;
            if (_drag is null)
                Invalidate();
        }
        SchedulePeriod? hovered = null;
        if (_drag is null && day is { } current)
            hovered = Hit(point, current).Period;
        UpdateTip(hovered?.Description);
        ProtectedCursor = _drag is { HasMoved: true } active
            ? active.Action switch
            {
                PeriodAction.Start or PeriodAction.End => ResizeCursor,
                PeriodAction.Move => MoveCursor,
                PeriodAction.Create => CreateCursor,
                _ => null,
            }
            : null;
    }

    private string Hint => _model.Text.Get("settings", "timeline_hint");

    private void UpdateTip(string? description)
    {
        var tooltip = _drag is null ? description : null;
        if (!Equals(HoverTip.Content, tooltip))
        {
            HoverTip.IsOpen = false;
            HoverTip.Content = tooltip;
        }
        var tip = tooltip is null ? null : HoverTip;
        if (!ReferenceEquals(ToolTipService.GetToolTip(this), tip))
            ToolTipService.SetToolTip(this, tip);
    }

    protected override async void OnPointerReleased(PointerRoutedEventArgs args)
    {
        base.OnPointerReleased(args);
        if (_drag is not { } drag || args.Pointer.PointerId != drag.Pointer)
            return;
        if (drag.HasMoved)
            UpdateDrag(args.GetCurrentPoint(this).Position);
        _drag = null;
        ProtectedCursor = null;
        ReleasePointerCaptures();
        args.Handled = true;
        if (drag.HasMoved)
        {
            if (drag.Action == PeriodAction.Create)
                await _model.CreatePeriod(drag.Days, drag.Span);
            else
                await _model.Reschedule(drag.Period, drag.Span);
        }
        else if (drag.Action == PeriodAction.Create)
            await _model.Close();
        else
            await _model.Open(drag.Period);
        Invalidate();
    }

    private bool UpdateDrag(Point point)
    {
        if (_drag is not { } drag)
            return false;
        drag.Current = point;
        var moved = drag.HasMoved;
        var span = drag.Period.Span.Adjust(
            drag.Action,
            (int)Math.Round((point.X - drag.Origin.X) / TrackWidth * 1440)
        );
        // A drag over empty time counts only while it covers an hour, so a
        // short accidental drag stays a click and creates nothing.
        drag.HasMoved =
            drag.Action == PeriodAction.Create
                ? span.Duration >= 60
                : moved || Math.Abs(point.X - drag.Origin.X) >= 4;
        if (!drag.HasMoved)
            return moved;
        var days = drag.Days;
        // Dragging up or down extends a new period to every day between the
        // pressed row and the pointer's row.
        if (drag.Action == PeriodAction.Create)
        {
            var row = Day(point.Y);
            var first = Math.Min(_day, row);
            var last = Math.Max(_day, row);
            if (first != days[0] || last != days[^1])
                days = Enumerable.Range(first, last - first + 1).ToArray();
        }
        if (span == drag.Span && ReferenceEquals(days, drag.Days))
            return !moved;
        drag.Span = span;
        drag.Days = days;
        return true;
    }

    protected override void OnPointerExited(PointerRoutedEventArgs args)
    {
        base.OnPointerExited(args);
        _hoverDay = null;
        HoverTip.IsOpen = false;
        if (_drag is null)
            Invalidate();
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs args)
    {
        base.OnPointerCaptureLost(args);
        CancelDrag();
    }

    protected override void OnPointerCanceled(PointerRoutedEventArgs args)
    {
        base.OnPointerCanceled(args);
        CancelDrag();
    }

    protected override void OnLostFocus(RoutedEventArgs args)
    {
        base.OnLostFocus(args);
        CancelDrag();
        Invalidate();
    }

    protected override void OnGotFocus(RoutedEventArgs args)
    {
        base.OnGotFocus(args);
        Invalidate();
    }

    private void CancelDrag()
    {
        if (_drag is not { } drag)
            return;
        _drag = null;
        ProtectedCursor = null;
        ReleasePointerCaptures();
        Invalidate();
    }

    // A pointer finds the period under it; the menu key and Shift+F10 use the
    // keyboard cursor.
    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (!_model.CanSchedule || _drag is not null)
            return;
        SchedulePeriod? period;
        if (args.TryGetPosition(this, out var position))
        {
            if (position.X < Gutter || position.Y < Ruler || position.Y >= Height)
                return;
            period = Hit(position, Day(position.Y)).Period;
        }
        else
        {
            period = _model
                .Ranges(_day)
                .First(range => _minute >= range.Start && _minute < range.End)
                .Period;
            position = new Point(X(_minute), Y(_day) + RowHeight - 4);
        }
        if (period is null)
            return;
        var menu = MenuItems.Flyout(
            () =>
                [
                    new MenuCommand(
                        _model.Text.Get("settings", "delete"),
                        Lucide.Trash2,
                        new RelayCommand(() => _model.Delete(period), () => true)
                    )
                    {
                        Name = period.DeleteName,
                    },
                ],
            () => _model.Text
        );
        HoverTip.IsOpen = false;
        args.Handled = true;
        menu.ShowAt(this, position);
    }

    protected override async void OnKeyDown(KeyRoutedEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.Key == VirtualKey.Escape && (_drag is not null || _model.IsOpen))
        {
            args.Handled = true;
            if (_drag is not null)
                CancelDrag();
            else
                await _model.Close();
            return;
        }
        if (_drag is not null || !_model.CanSchedule)
            return;
        var period = _model
            .Ranges(_day)
            .First(range => _minute >= range.Start && _minute < range.End)
            .Period;
        switch (args.Key)
        {
            case VirtualKey.Up:
                _day = Math.Max(0, _day - 1);
                break;
            case VirtualKey.Down:
                _day = Math.Min(6, _day + 1);
                break;
            case VirtualKey.Left:
                _minute = Math.Max(0, _minute - 15);
                break;
            case VirtualKey.Right:
                _minute = Math.Min(1425, _minute + 15);
                break;
            case VirtualKey.Home:
                _minute = 0;
                break;
            case VirtualKey.End:
                _minute = 1425;
                break;
            case VirtualKey.Space or VirtualKey.Enter when period is not null:
                await _model.Open(period);
                break;
            case VirtualKey.Space:
                await _model.Close();
                break;
            case VirtualKey.Enter:
                _ = _model.CreatePeriod([_day], new(_minute, Math.Min(60, 1440 - _minute)));
                break;
            default:
                return;
        }
        args.Handled = true;
        Invalidate();
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new FrameworkElementAutomationPeer(this);

    private sealed class Drag(
        PeriodAction action,
        SchedulePeriod period,
        Point origin,
        uint pointer
    )
    {
        internal PeriodAction Action { get; } = action;
        internal SchedulePeriod Period { get; } = period;
        internal Point Origin { get; } = origin;
        internal uint Pointer { get; } = pointer;
        internal Point Current { get; set; } = origin;
        internal PeriodSpan Span { get; set; } = period.Span;
        internal IReadOnlyList<int> Days { get; set; } = period.Days;
        internal bool HasMoved { get; set; }
    }
}
