using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Controls;

public sealed partial class Week : UserControl
{
    private static readonly InputSystemCursor MoveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private static readonly InputSystemCursor ResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private static readonly InputSystemCursor CreateCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    private readonly UISettings _settings = new();
    private double Gutter => 64 * _settings.TextScaleFactor;
    private double Ruler => 24 * _settings.TextScaleFactor;
    private double RowHeight => 16 + 32 * _settings.TextScaleFactor;
    private readonly Preferences _model;
    private Drag? _drag;
    private int _day;
    private int _minute;
    private int? _hoverDay;
    private double TrackWidth => Math.Max(1, ActualWidth - Gutter);

    internal Week(Preferences model)
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
            _model.PropertyChanged += OnModel;
            _model.WeekChanged += OnWeek;
            _model.TextChanged += OnText;
            _settings.TextScaleFactorChanged += OnScaling;
            DrawGrid();
            Draw();
        };
        Unloaded += (_, _) =>
        {
            CancelDrag();
            _model.PropertyChanged -= OnModel;
            _model.WeekChanged -= OnWeek;
            _model.TextChanged -= OnText;
            _settings.TextScaleFactorChanged -= OnScaling;
        };
        SizeChanged += (_, _) => { CancelDrag(); DrawGrid(); Draw(); };
        ActualThemeChanged += (_, _) => Draw();
    }

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Preferences.HasDraft)) return;
        if (_drag is { } drag && (!_model.CanSchedule || drag.Draft != _model.Draft ||
            (drag.Draft is null && drag.Action != PeriodAction.Create && !_model.Periods.Contains(drag.Period)))) CancelDrag();
        if (_drag is null) Draw();
    }

    private void OnWeek(object? sender, EventArgs args) => OnModel(sender, new(string.Empty));
    private void OnText(object? sender, EventArgs args) { DrawGrid(); Draw(); }
    private void OnScaling(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() => { CancelDrag(); DrawGrid(); Draw(); });
    private double X(int minute) => Gutter + minute / 1440.0 * TrackWidth;
    private double Y(int day) => Ruler + day * RowHeight;
    private int Minute(double x) => Math.Clamp(PeriodSpan.Snap((x - Gutter) / TrackWidth * 1440), 0, 1425);

    private void Place(FrameworkElement element, double x, double y)
    {
        element.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(element, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Position(element, x, y);
        Backdrop.Children.Add(element);
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
        element.Style = (Style)Resources[style];
        return element;
    }

    private static void Trim(Canvas canvas, int count)
    {
        while (canvas.Children.Count > count) canvas.Children.RemoveAt(canvas.Children.Count - 1);
    }

    private TextBlock Text(string text, string style = "WeekTextStyle") => new()
    {
        Text = text, Style = (Style)Resources[style], TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis
    };

    private void DrawGrid()
    {
        Backdrop.Children.Clear();
        Height = Ruler + 7 * RowHeight;
        if (ActualWidth <= Gutter + 24) return;
        var step = TrackWidth >= 384 * _settings.TextScaleFactor ? 3 : TrackWidth >= 192 * _settings.TextScaleFactor ? 6 : 12;
        for (var hour = 0; hour <= 24; hour += step)
        {
            var label = Text(hour.ToString("00"), "WeekSecondaryStyle");
            label.Width = 28 * _settings.TextScaleFactor;
            label.TextAlignment = hour == 0 ? TextAlignment.Left : hour == 24 ? TextAlignment.Right : TextAlignment.Center;
            Place(label, hour == 0 ? Gutter : hour == 24 ? ActualWidth - label.Width : X(hour * 60) - label.Width / 2, 0);
        }
        for (var day = 0; day < 7; day++)
        {
            var label = Text(_model.ShortDay(day));
            label.Width = Gutter - 8;
            Place(label, 0, Y(day) + (RowHeight - 16 * _settings.TextScaleFactor) / 2);
            Place(new Border
            {
                Width = TrackWidth, Height = RowHeight - 4, Style = (Style)Resources["WeekTrackStyle"], BorderThickness = new(1)
            }, Gutter, Y(day));
            for (var hour = step; hour < 24; hour += step)
                Place(new Border { Width = 1, Height = RowHeight - 4, Style = (Style)Resources["WeekLineStyle"] }, X(hour * 60), Y(day));
        }
    }

    private void Draw()
    {
        Tip.Visibility = Visibility.Collapsed;
        if (ActualWidth <= Gutter + 24) { Trim(Blocks, 0); Trim(Outlines, 0); return; }
        var preview = _drag is { HasMoved: true } drag ? drag.Period.WithSpan(drag.Span) : _model.Preview;
        var original = _model.IsEditing ? _model.Selection : _drag is { Action: not PeriodAction.Create } moving ? moving.Period : null;
        var selected = preview ?? _model.Selection;
        var blocks = 0;
        var outlines = 0;
        for (var day = 0; day < 7; day++)
        {
            foreach (var range in _model.Ranges(day, original, preview))
            {
                if (range.Mode == ScheduleMode.Normal) continue;
                var paused = range.Mode == ScheduleMode.Paused;
                var style = paused ? "WeekTextStyle" : "WeekAccentStyle";
                var block = GetBorder(Blocks, blocks++, paused ? "WeekPausedStyle" : "WeekAlternativeStyle");
                if (block.Child is not StackPanel)
                    block.Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Text(string.Empty), Text(string.Empty) } };
                var caption = (StackPanel)block.Child;
                var mode = (TextBlock)caption.Children[0];
                mode.Style = (Style)Resources[style];
                mode.Text = _model.FormatMode(range.Mode);
                mode.TextAlignment = TextAlignment.Center;
                var times = (TextBlock)caption.Children[1];
                times.Style = (Style)Resources[style];
                times.Text = range.Start == 0 && range.End == 1440 ? _model.Text.Get("preferences", "time_all_day") :
                    _model.Text.Format("preferences", "time_range", Preferences.Time(range.Start), Preferences.Time(range.End));
                times.TextAlignment = TextAlignment.Center;
                block.Width = Math.Max(0, X(range.End) - X(range.Start));
                block.Height = RowHeight - 12;
                block.Padding = new(4, 0, 4, 0);
                block.BorderThickness = new(1);
                Position(block, X(range.Start), Y(day) + 4);
            }
            if (selected is null) continue;
            foreach (var span in selected.Occurrences(day))
            {
                var left = X(Math.Max(0, span.Start));
                var right = X(Math.Min(1440, span.End));
                var outline = GetBorder(Outlines, outlines++, "WeekOutlineStyle");
                outline.Width = right - left;
                outline.Height = RowHeight - 8;
                outline.BorderThickness = new(1);
                outline.CornerRadius = new(0);
                Position(outline, left, Y(day) + 2);
                if (day != (_drag is null ? _hoverDay : _day)) continue;
                if (span.Start >= 0) Handle(outlines++, Math.Min(right - 3, left + 4), day, selected.Mode);
                if (span.End <= 1440) Handle(outlines++, Math.Max(left, right - 7), day, selected.Mode);
            }
        }
        if (FocusState == FocusState.Keyboard && _model.Selection is null && !_model.IsEditing)
        {
            var cursor = GetBorder(Outlines, outlines++, "WeekOutlineStyle");
            cursor.Width = 2;
            cursor.Height = RowHeight - 4;
            cursor.BorderThickness = new(1);
            cursor.CornerRadius = new(0);
            Position(cursor, X(_minute), Y(_day));
            ShowTip(_model.Text.Format("preferences", "day_schedule", _model.Day(_day), Preferences.Time(_minute)), X(_minute));
        }
        var description = selected?.Description ?? _model.Text.Format("preferences", "day_schedule", _model.Day(_day), Preferences.Time(_minute));
        AutomationProperties.SetName(this, _model.Text.Format("preferences", "timeline_name", description));
        if (_drag is { HasMoved: true } active && preview is not null)
        {
            ShowTip(preview.TimeLabel, active.Current.X);
        }
        Trim(Blocks, blocks);
        Trim(Outlines, outlines);
    }

    private void ShowTip(string text, double x)
    {
        TipText.Text = text;
        Tip.MaxWidth = Math.Min(360, ActualWidth);
        Tip.Visibility = Visibility.Visible;
        Tip.Measure(new Size(Tip.MaxWidth, double.PositiveInfinity));
        Position(Tip, Math.Clamp(x + 12, 0, Math.Max(0, ActualWidth - Tip.DesiredSize.Width)),
            Math.Max(0, Y(_day) - Tip.DesiredSize.Height - 2));
    }

    private void Handle(int index, double x, int day, ScheduleMode mode)
    {
        var handle = GetBorder(Outlines, index, mode == ScheduleMode.Paused ? "WeekPausedHandleStyle" : "WeekHandleStyle");
        handle.Width = 3;
        handle.Height = 14;
        handle.CornerRadius = new(1);
        handle.BorderThickness = new(0);
        Position(handle, Math.Clamp(x, Gutter, ActualWidth - 3), Y(day) + (RowHeight - 4 - handle.Height) / 2);
    }

    private (PeriodAction Action, SchedulePeriod? Period) Hit(Point point, int day)
    {
        if ((_model.IsEditing ? _model.Preview : _model.Selection) is { } selected)
        {
            var spans = selected.Occurrences(day).ToArray();
            var start = spans.Any(span => span.Start >= 0 && Math.Abs(point.X - X(span.Start)) <= 8);
            var end = spans.Any(span => span.End <= 1440 && Math.Abs(point.X - X(span.End)) <= 8);
            if (start && (!end || point.Y - Y(day) < (RowHeight - 4) / 2)) return (PeriodAction.Start, selected);
            if (end) return (PeriodAction.End, selected);
            foreach (var span in spans)
            {
                if (point.X >= X(Math.Max(0, span.Start)) && point.X <= X(Math.Min(1440, span.End)) &&
                    (_model.IsEditing || point.Y - Y(day) < 5 || point.Y - Y(day) > RowHeight - 12)) return (PeriodAction.Move, selected);
            }
        }
        if (_model.IsEditing) return (PeriodAction.Create, null);
        var minute = Math.Clamp((point.X - Gutter) / TrackWidth * 1440, 0, 1439);
        var period = _model.Ranges(day).First(range => minute >= range.Start && minute < range.End).Period;
        return (period is null ? PeriodAction.Create : PeriodAction.Move, period);
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs args)
    {
        base.OnPointerPressed(args);
        if (!_model.CanSchedule || _drag is not null) return;
        var pointer = args.GetCurrentPoint(this);
        var point = pointer.Position;
        if (!pointer.Properties.IsLeftButtonPressed || point.X < Gutter || point.Y < Ruler || point.Y >= Height) return;
        _day = Math.Clamp((int)((point.Y - Ruler) / RowHeight), 0, 6);
        _hoverDay = _day;
        _minute = Minute(point.X);
        var hit = Hit(point, _day);
        if (_model.IsEditing && hit.Period is null) return;
        Focus(FocusState.Pointer);
        _model.Select(hit.Period);
        if (!CapturePointer(args.Pointer)) return;
        HoverTip.IsOpen = false;
        ToolTipService.SetToolTip(this, null);
        var period = hit.Period ?? new SchedulePeriod(_model, [_day], new(_minute, 15), ScheduleMode.Alternative);
        _drag = new(hit.Action, period, point, args.Pointer.PointerId) { Draft = _model.Draft };
        args.Handled = true;
        Draw();
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs args)
    {
        base.OnPointerMoved(args);
        var point = args.GetCurrentPoint(this).Position;
        if (_drag is { } drag)
        {
            if (args.Pointer.PointerId != drag.Pointer) return;
            if (UpdateDrag(point)) Draw();
            args.Handled = true;
        }
        int? day = point.X >= Gutter && point.Y >= Ruler && point.Y < Height
            ? Math.Clamp((int)((point.Y - Ruler) / RowHeight), 0, 6) : null;
        if (_hoverDay != day)
        {
            _hoverDay = day;
            if (_drag is null) Draw();
        }
        var action = _drag?.Action;
        SchedulePeriod? hovered = null;
        if (action is null && day is { } current)
        {
            var hit = Hit(point, current);
            action = hit.Action;
            hovered = hit.Period;
        }
        var tooltip = _drag is null ? hovered?.Description : null;
        if (!Equals(HoverTip.Content, tooltip)) { HoverTip.IsOpen = false; HoverTip.Content = tooltip; }
        var tip = tooltip is null ? null : HoverTip;
        if (!ReferenceEquals(ToolTipService.GetToolTip(this), tip)) ToolTipService.SetToolTip(this, tip);
        ProtectedCursor = !_model.CanSchedule || (_model.IsEditing && hovered is null && _drag is null) ? null : action switch
        {
            PeriodAction.Start or PeriodAction.End => ResizeCursor,
            PeriodAction.Move => MoveCursor,
            PeriodAction.Create => CreateCursor,
            _ => null
        };
    }

    protected override async void OnPointerReleased(PointerRoutedEventArgs args)
    {
        base.OnPointerReleased(args);
        if (_drag is not { } drag || args.Pointer.PointerId != drag.Pointer) return;
        UpdateDrag(args.GetCurrentPoint(this).Position);
        _drag = null;
        ReleasePointerCaptures();
        args.Handled = true;
        if (drag.HasMoved && drag.Draft is null)
        {
            if (drag.Action == PeriodAction.Create) _model.CreatePeriod(_day, drag.Span);
            else await _model.Reschedule(drag.Period, drag.Span);
        }
        Draw();
    }

    private bool UpdateDrag(Point point)
    {
        if (_drag is not { } drag) return false;
        drag.Current = point;
        var moved = drag.HasMoved;
        if (Math.Abs(point.X - drag.Origin.X) >= 4) drag.HasMoved = true;
        if (!drag.HasMoved) return false;
        var span = drag.Period.Span.Adjust(drag.Action, (int)Math.Round((point.X - drag.Origin.X) / TrackWidth * 1440));
        if (span == drag.Span) return !moved;
        drag.Span = span;
        drag.Draft?.SetSpan(span);
        return true;
    }

    protected override void OnPointerExited(PointerRoutedEventArgs args)
    {
        base.OnPointerExited(args);
        _hoverDay = null;
        HoverTip.IsOpen = false;
        if (_drag is null) Draw();
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs args) { base.OnPointerCaptureLost(args); CancelDrag(); }
    protected override void OnPointerCanceled(PointerRoutedEventArgs args) { base.OnPointerCanceled(args); CancelDrag(); }
    protected override void OnLostFocus(RoutedEventArgs args) { base.OnLostFocus(args); CancelDrag(); Draw(); }
    protected override void OnGotFocus(RoutedEventArgs args) { base.OnGotFocus(args); Draw(); }

    private void CancelDrag()
    {
        if (_drag is not { } drag) return;
        _drag = null;
        ReleasePointerCaptures();
        if (drag.Draft is not null && drag.Draft == _model.Draft) drag.Draft.SetSpan(drag.Period.Span);
        Draw();
    }

    protected override void OnKeyDown(KeyRoutedEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.Key == VirtualKey.Escape && (_drag is not null || !_model.IsEditing))
        {
            if (_drag is not null) CancelDrag();
            else _model.Select(null);
            args.Handled = true;
            return;
        }
        if (_drag is not null || !_model.CanSchedule || _model.IsEditing) return;
        switch (args.Key)
        {
            case VirtualKey.Up: _day = Math.Max(0, _day - 1); _model.Select(null); break;
            case VirtualKey.Down: _day = Math.Min(6, _day + 1); _model.Select(null); break;
            case VirtualKey.Left: _minute = Math.Max(0, _minute - 15); _model.Select(null); break;
            case VirtualKey.Right: _minute = Math.Min(1425, _minute + 15); _model.Select(null); break;
            case VirtualKey.Home: _minute = 0; _model.Select(null); break;
            case VirtualKey.End: _minute = 1425; _model.Select(null); break;
            case VirtualKey.Space:
                _model.Select(_model.Ranges(_day).First(range => _minute >= range.Start && _minute < range.End).Period);
                break;
            case VirtualKey.Enter:
                if (_model.Selection is { } selected) _model.Edit(selected);
                else _model.CreatePeriod(_day, new(_minute, Math.Min(60, 1440 - _minute)));
                break;
            default: return;
        }
        args.Handled = true;
        Draw();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private sealed class Drag(PeriodAction action, SchedulePeriod period, Point origin, uint pointer)
    {
        internal PeriodAction Action { get; } = action;
        internal SchedulePeriod Period { get; } = period;
        internal PeriodDraft? Draft { get; init; }
        internal Point Origin { get; } = origin;
        internal uint Pointer { get; } = pointer;
        internal Point Current { get; set; } = origin;
        internal PeriodSpan Span { get; set; } = period.Span;
        internal bool HasMoved { get; set; }
    }
}
