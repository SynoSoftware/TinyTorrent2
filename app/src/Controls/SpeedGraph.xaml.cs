using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Controls;

// Draws the Speed view as docs/interface.md defines it: averages, not samples.
public sealed partial class SpeedGraph : UserControl
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples),
        typeof(IReadOnlyList<SpeedSample>), typeof(SpeedGraph), new PropertyMetadata(Array.Empty<SpeedSample>(), (graph, _) => ((SpeedGraph)graph).OnSamples()));

    // Zoom levels, in seconds. Each view draws 60 averages.
    private static readonly long[] Lengths = [300, 900, 1800, 3600, 21600, 86400];
    // Older history has one sample a minute, so a view shorter than this shows
    // only the present, where per-second samples exist.
    private const long PastMinimum = 1800;
    // Samples are at most a minute apart, so a longer pause is unknown time,
    // such as sleep or an engine restart, and the line breaks there.
    private const long Gap = 90;
    // One notch of a mouse wheel. A touchpad sends smaller deltas that add up to
    // it, so one swipe does not race through every zoom level.
    private const int Notch = 120;
    // The + and - keys of the main keyboard; VirtualKey names only the keypad ones.
    private const VirtualKey Plus = (VirtualKey)0xBB;
    private const VirtualKey Minus = (VirtualKey)0xBD;
    private Strings? _text;
    private long _length = 300;
    // Where a past view ends; null while the view follows the newest sample.
    private long? _end;
    private double? _marker;
    // Where the pointer rests on the plot, so new data keeps the marker under it.
    private double? _pointer;
    private Drag? _drag;
    private string _value = string.Empty;
    private int _wheel;

    public SpeedGraph()
    {
        InitializeComponent();
        FiveMinutes.Tag = 300L;
        Hour.Tag = 3600L;
        SixHours.Tag = 21600L;
        Day.Tag = 86400L;
    }

    public IReadOnlyList<SpeedSample> Samples { get => (IReadOnlyList<SpeedSample>)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }

    private long Newest => Samples.Count == 0 ? DateTimeOffset.Now.ToUnixTimeSeconds() : Samples[^1].Time;
    private long End => _end ?? Newest;
    private long Start => End - _length;

    internal void RefreshText(Strings text)
    {
        _text = text;
        AutomationProperties.SetName(this, text.Get("speed", "chart"));
        Title.Text = text.Get("speed", "all");
        Live.Content = text.Get("speed", "now");
        AutomationProperties.SetName(Ranges, text.Get("speed", "range"));
        FiveMinutes.Text = text.Get("speed", "five_minutes");
        Hour.Text = text.Get("speed", "hour");
        SixHours.Text = text.Get("speed", "six_hours");
        Day.Text = text.Get("speed", "day");
        foreach (var item in Ranges.Items)
        {
            AutomationProperties.SetName(item, item.Text);
        }
        DownloadLabel.Text = text.Get("speed", "download");
        UploadLabel.Text = text.Get("speed", "upload");
        NoHistory.Text = text.Get("speed", "empty");
        Update();
    }

    // A view stays inside the retained history, and a view that reaches the
    // newest sample follows it again.
    private void SetView(long length, double end)
    {
        var newest = Newest;
        var oldest = Samples.Count == 0 ? newest : Samples[0].Time;
        var clamped = (long)Math.Max(end, oldest + length);
        long? past = length >= PastMinimum && clamped < newest ? clamped : null;
        if (length == _length && past == _end)
        {
            return;
        }
        _length = length;
        _end = past;
        Update();
    }

    // Keeps the anchor time at the same place on the plot.
    private void Zoom(int direction, double anchor)
    {
        var length = Lengths[Math.Clamp(Array.IndexOf(Lengths, _length) + direction, 0, Lengths.Length - 1)];
        if (_end is not null)
        {
            length = Math.Max(length, PastMinimum);
        }
        var after = (End - anchor) / _length;
        SetView(length, anchor + after * length);
    }

    private void Pan(double seconds) => SetView(_length, End + seconds);

    private void OnSize(object sender, SizeChangedEventArgs args) => Update();

    private void OnSamples()
    {
        if (_pointer is { } x)
        {
            _marker = Nearest(Averages(), TimeAt(x))?.Time;
        }
        Update();
    }

    private void Update()
    {
        if (_text is not { } text)
        {
            return;
        }
        Ranges.SelectedItem = _end is null ? Ranges.Items.FirstOrDefault(item => (long)item.Tag == _length) : null;
        Live.Visibility = _end is null ? Visibility.Collapsed : Visibility.Visible;
        // Averages shorter than a minute need seconds to tell them apart.
        var format = _length < 3600 ? "T" : "t";
        var runs = Runs();
        var averages = runs.SelectMany(run => run).ToArray();
        var marked = Nearest(averages, _marker);
        var shown = marked ?? averages.LastOrDefault();
        var downloadPeak = averages.Select(average => average.Download).DefaultIfEmpty().Max();
        var uploadPeak = averages.Select(average => average.Upload).DefaultIfEmpty().Max();
        var top = Ceiling(Math.Max(downloadPeak, uploadPeak));
        // The legend names the time of any value that is not the present one.
        var at = shown is not null && (marked is not null || _end is not null) ? text.Format("speed", "at", Time(shown.Time, format)) : null;
        DownloadValue.Text = Rate(text, shown?.Download);
        UploadValue.Text = Rate(text, shown?.Upload);
        DownloadDetail.Text = at ?? text.Format("speed", "peak", Rate(text, downloadPeak));
        UploadDetail.Text = at ?? text.Format("speed", "peak", Rate(text, uploadPeak));
        Maximum.Text = averages.Length == 0 ? string.Empty : Rate(text, top);
        NoHistory.Visibility = averages.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _value = marked is null ? string.Empty : text.Format("speed", "point", Time(marked.Time, format),
            Rate(text, marked.Download), Rate(text, marked.Upload));
        var extent = new Extent(Plot.ActualWidth, Plot.ActualHeight, Start, _length, top);
        if (extent.Width <= 0 || extent.Height <= 0)
        {
            return;
        }
        Draw(runs, extent);
        DrawTicks(extent);
        DrawMarker(extent, marked);
    }

    private static string Rate(Strings text, double? value) => value is { } rate ? text.Format("units", "rate", text.Bytes(rate)) : "—";

    private static string Time(double time, string format) =>
        DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(time)).ToLocalTime().ToString(format, CultureInfo.CurrentCulture);

    private static Average? Nearest(Average[] averages, double? time) =>
        time is { } target && averages.Length > 0 ? averages.MinBy(average => Math.Abs(average.Time - target)) : null;

    private List<List<Average>> Runs() => Bucket(Samples, Start, End, _length / 60);

    private Average[] Averages() => Runs().SelectMany(run => run).ToArray();

    // Splits the samples at unknown time, then averages each run in buckets
    // aligned to clock time, so a new sample changes only the newest bucket.
    private static List<List<Average>> Bucket(IReadOnlyList<SpeedSample> samples, long start, long end, long width)
    {
        var runs = new List<List<Average>>();
        var bucket = new List<SpeedSample>();
        SpeedSample? previous = null;
        foreach (var sample in samples)
        {
            if (sample.Time < start || sample.Time > end)
            {
                continue;
            }
            if (previous is null || sample.Time - previous.Time > Gap)
            {
                Close();
                runs.Add([]);
            }
            else if (sample.Time / width != previous.Time / width)
            {
                Close();
            }
            bucket.Add(sample);
            previous = sample;
        }
        Close();
        return runs;

        void Close()
        {
            if (bucket.Count == 0)
            {
                return;
            }
            var time = bucket.Average(sample => (double)sample.Time);
            runs[^1].Add(new Average(time, bucket.Average(sample => sample.DownloadRate), bucket.Average(sample => sample.UploadRate)));
            bucket.Clear();
        }
    }

    // Rounds the peak up to a step times a power of ten in the unit that
    // Strings.Bytes shows, so the line fills at least two thirds of the height.
    // A step of 1,000 becomes 1,024, which Bytes shows as 1 of the next unit.
    // The scale never goes below 1 KiB/s, so a few bytes of idle traffic stay
    // a flat line instead of filling the height.
    private static double Ceiling(double peak)
    {
        var unit = 1024.0;
        while (peak >= unit * 1024)
        {
            unit *= 1024;
        }
        var value = Math.Max(peak / unit, 1);
        var power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        ReadOnlySpan<double> steps = [1, 1.5, 2, 3, 4, 5, 6, 8, 10];
        foreach (var step in steps)
        {
            var top = step * power;
            if (top >= value)
            {
                return (top < 1000 ? top : 1024) * unit;
            }
        }
        return 1024 * unit;
    }

    private void Draw(List<List<Average>> runs, Extent extent)
    {
        var area = new PathGeometry();
        var download = new PathGeometry();
        var upload = new PathGeometry();
        foreach (var run in runs)
        {
            var downloads = run.Select(average => extent.At(average.Time, average.Download)).ToArray();
            var uploads = run.Select(average => extent.At(average.Time, average.Upload)).ToArray();
            var fill = new PathFigure { StartPoint = new Point(downloads[0].X, extent.Height), IsClosed = true };
            fill.Segments.Add(new LineSegment { Point = downloads[0] });
            fill.Segments.Add(Curve(downloads));
            fill.Segments.Add(new LineSegment { Point = new Point(downloads[^1].X, extent.Height) });
            area.Figures.Add(fill);
            download.Figures.Add(Line(downloads));
            upload.Figures.Add(Line(uploads));
        }
        DownloadArea.Data = area;
        DownloadLine.Data = download;
        UploadLine.Data = upload;
    }

    // Marks round local clock times, at most about six per view. A label that
    // would touch the previous one is left out; its line stays.
    private void DrawTicks(Extent extent)
    {
        ReadOnlySpan<long> intervals = [60, 300, 600, 1800, 3600, 10800, 21600];
        var interval = intervals[^1];
        foreach (var candidate in intervals)
        {
            if (candidate * 6 >= extent.Length)
            {
                interval = candidate;
                break;
            }
        }
        var ticks = new GeometryGroup();
        TickLabels.Children.Clear();
        var offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(extent.Start)).TotalSeconds;
        var first = (extent.Start + offset + interval - 1) / interval * interval - offset;
        var right = double.NegativeInfinity;
        for (var time = first; time <= extent.Start + extent.Length; time += interval)
        {
            var x = extent.At(time, 0).X;
            ticks.Children.Add(new LineGeometry { StartPoint = new Point(x, 0), EndPoint = new Point(x, extent.Height) });
            var label = new TextBlock { Text = Time(time, "t"), Style = (Style)Resources["SpeedGraphTickStyle"], HorizontalAlignment = HorizontalAlignment.Left };
            TickLabels.Children.Add(label);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var width = label.DesiredSize.Width;
            var left = Math.Clamp(x - width / 2, 0, Math.Max(0, extent.Width - width));
            if (left < right + 8)
            {
                TickLabels.Children.Remove(label);
                continue;
            }
            label.Margin = new Thickness(left, 0, 0, 0);
            right = left + width;
        }
        Ticks.Data = ticks;
    }

    private void DrawMarker(Extent extent, Average? marked)
    {
        if (marked is null)
        {
            Marker.Visibility = Visibility.Collapsed;
            return;
        }
        var download = extent.At(marked.Time, marked.Download);
        var upload = extent.At(marked.Time, marked.Upload);
        MarkerLine.X1 = download.X;
        MarkerLine.X2 = download.X;
        MarkerLine.Y2 = extent.Height;
        Canvas.SetLeft(DownloadDot, download.X - DownloadDot.Width / 2);
        Canvas.SetTop(DownloadDot, download.Y - DownloadDot.Height / 2);
        Canvas.SetLeft(UploadDot, upload.X - UploadDot.Width / 2);
        Canvas.SetTop(UploadDot, upload.Y - UploadDot.Height / 2);
        Marker.Visibility = Visibility.Visible;
    }

    private static PathFigure Line(Point[] points)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = false, IsFilled = false };
        figure.Segments.Add(Curve(points));
        return figure;
    }

    // A monotone cubic curve: each section stays between its two averages, so
    // the curve shows no peak or dip that the averages do not have.
    private static PolyBezierSegment Curve(Point[] points)
    {
        var slopes = Slopes(points);
        var segment = new PolyBezierSegment();
        for (var index = 0; index < points.Length - 1; index++)
        {
            var third = (points[index + 1].X - points[index].X) / 3;
            segment.Points.Add(new Point(points[index].X + third, points[index].Y + slopes[index] * third));
            segment.Points.Add(new Point(points[index + 1].X - third, points[index + 1].Y - slopes[index + 1] * third));
            segment.Points.Add(points[index + 1]);
        }
        return segment;
    }

    // Fritsch-Butland tangents: zero where the line turns, and otherwise a
    // weighted harmonic mean of the neighbouring slopes.
    private static double[] Slopes(Point[] points)
    {
        var slopes = new double[points.Length];
        if (points.Length < 2)
        {
            return slopes;
        }
        var secants = new double[points.Length - 1];
        for (var index = 0; index < secants.Length; index++)
        {
            secants[index] = (points[index + 1].Y - points[index].Y) / (points[index + 1].X - points[index].X);
        }
        slopes[0] = secants[0];
        slopes[^1] = secants[^1];
        for (var index = 1; index < points.Length - 1; index++)
        {
            var before = secants[index - 1];
            var after = secants[index];
            if (before * after <= 0)
            {
                continue;
            }
            var left = points[index].X - points[index - 1].X;
            var right = points[index + 1].X - points[index].X;
            slopes[index] = 3 * (left + right) / ((2 * right + left) / before + (right + 2 * left) / after);
        }
        return slopes;
    }

    // Moves the marker to the nearest average and reports the change to
    // assistive technology. Pointer moves within one average redraw nothing,
    // and data updates under a fixed marker stay silent, so no transfer tick is
    // announced.
    private void Mark(double? time)
    {
        var snapped = Nearest(Averages(), time)?.Time;
        if (snapped == _marker)
        {
            return;
        }
        var previous = _value;
        _marker = snapped;
        Update();
        if (previous != _value)
        {
            FrameworkElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, previous, _value);
        }
    }

    private double TimeAt(double x) => Start + x / Plot.ActualWidth * _length;

    private void Hover(PointerRoutedEventArgs args)
    {
        var x = args.GetCurrentPoint(Plot).Position.X;
        _pointer = x;
        Mark(TimeAt(x));
    }

    private void OnRange(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is { Tag: long length })
        {
            SetView(length, Newest);
        }
    }

    private void OnLive(object sender, RoutedEventArgs args) => SetView(_length, Newest);

    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        Focus(FocusState.Pointer);
        if (args.GetCurrentPoint(Plot).Properties.IsLeftButtonPressed && Plot.CapturePointer(args.Pointer))
        {
            _drag = new Drag(args.GetCurrentPoint(Plot).Position.X, End);
        }
        Hover(args);
    }

    private void OnPointer(object sender, PointerRoutedEventArgs args)
    {
        if (Plot.ActualWidth <= 0)
        {
            return;
        }
        if (_drag is { } drag)
        {
            var x = args.GetCurrentPoint(Plot).Position.X;
            SetView(_length, drag.End + (drag.X - x) / Plot.ActualWidth * _length);
        }
        Hover(args);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs args)
    {
        _drag = null;
        Plot.ReleasePointerCapture(args.Pointer);
        var point = args.GetCurrentPoint(Plot).Position;
        if (point.X < 0 || point.Y < 0 || point.X > Plot.ActualWidth || point.Y > Plot.ActualHeight)
        {
            OnPointerExit(sender, args);
        }
    }

    private void OnPointerExit(object sender, PointerRoutedEventArgs args)
    {
        if (_drag is not null)
        {
            return;
        }
        _pointer = null;
        if (FocusState != FocusState.Keyboard)
        {
            Mark(null);
        }
    }

    // Each wheel notch zooms one level around the time under the pointer.
    private void OnWheel(object sender, PointerRoutedEventArgs args)
    {
        var properties = args.GetCurrentPoint(Plot).Properties;
        if (properties.IsHorizontalMouseWheel || Plot.ActualWidth <= 0)
        {
            return;
        }
        args.Handled = true;
        if (Math.Sign(properties.MouseWheelDelta) != Math.Sign(_wheel))
        {
            _wheel = 0;
        }
        _wheel += properties.MouseWheelDelta;
        var x = args.GetCurrentPoint(Plot).Position.X;
        while (Math.Abs(_wheel) >= Notch)
        {
            var direction = _wheel > 0 ? -1 : 1;
            _wheel -= Math.Sign(_wheel) * Notch;
            Zoom(direction, TimeAt(x));
        }
        Hover(args);
    }

    private void OnFocus(object sender, RoutedEventArgs args)
    {
        if (FocusState == FocusState.Keyboard && _marker is null)
        {
            Mark(Averages().LastOrDefault()?.Time);
        }
    }

    private void OnBlur(object sender, RoutedEventArgs args) => Mark(null);

    // The arrow keys move the marker, + and - zoom around it, and Page Up and
    // Page Down pan half a view, so the keyboard reaches what the pointer does.
    private void OnKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.OriginalSource != this)
        {
            return;
        }
        var anchor = _marker ?? End;
        switch (args.Key)
        {
            case VirtualKey.Escape when _marker is not null:
                Mark(null);
                break;
            case VirtualKey.Add or Plus:
                Zoom(-1, anchor);
                Mark(anchor);
                break;
            case VirtualKey.Subtract or Minus:
                Zoom(1, anchor);
                Mark(anchor);
                break;
            case VirtualKey.PageUp:
                Pan(-_length / 2.0);
                break;
            case VirtualKey.PageDown:
                Pan(_length / 2.0);
                break;
            case VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End:
                Step(args.Key);
                break;
            default:
                return;
        }
        _pointer = null;
        args.Handled = true;
    }

    private void Step(VirtualKey key)
    {
        var averages = Averages();
        if (averages.Length == 0)
        {
            return;
        }
        var index = Nearest(averages, _marker) is { } marked ? Array.IndexOf(averages, marked) : averages.Length - 1;
        var next = key switch
        {
            VirtualKey.Left => index - 1,
            VirtualKey.Right => index + 1,
            VirtualKey.Home => 0,
            _ => averages.Length - 1,
        };
        Mark(averages[Math.Clamp(next, 0, averages.Length - 1)].Time);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GraphPeer(this);

    private sealed class GraphPeer(SpeedGraph graph) : FrameworkElementAutomationPeer(graph), IValueProvider
    {
        protected override string GetClassNameCore() => nameof(SpeedGraph);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override object? GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.Value ? this : base.GetPatternCore(pattern);
        public bool IsReadOnly => true;
        public string Value => graph._value;
        public void SetValue(string value) => throw new InvalidOperationException();
    }

    private sealed record Average(double Time, double Download, double Upload);

    // Where a drag started and where the view ended then. Panning from the
    // start, not from the last move, keeps rounding from adding up.
    private sealed record Drag(double X, long End);

    private sealed record Extent(double Width, double Height, long Start, long Length, double Top)
    {
        public Point At(double time, double rate) => new((time - Start) * Width / Length, Height * (1 - rate / Top));
    }
}
