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
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Controls;

// Draws the Speed view as docs/interface.md defines it: averages, not samples.
public sealed partial class SpeedGraph : UserControl
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples),
        typeof(IReadOnlyList<SpeedSample>), typeof(SpeedGraph), new PropertyMetadata(Array.Empty<SpeedSample>(), (graph, _) => ((SpeedGraph)graph).Update()));
    public static readonly DependencyProperty RangeProperty = DependencyProperty.Register(nameof(Range), typeof(SpeedRange),
        typeof(SpeedGraph), new PropertyMetadata(SpeedRange.FiveMinutes, (graph, _) => ((SpeedGraph)graph).Update()));

    // One notch of a mouse wheel. A touchpad sends smaller deltas that add up to
    // it, so one swipe does not race through every range.
    private const int Notch = 120;
    private Strings? _text;
    private double? _marker;
    private string _value = string.Empty;
    private int _wheel;

    public SpeedGraph() => InitializeComponent();

    public IReadOnlyList<SpeedSample> Samples { get => (IReadOnlyList<SpeedSample>)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public SpeedRange Range { get => (SpeedRange)GetValue(RangeProperty); set => SetValue(RangeProperty, value); }

    // The shown range ends at the newest sample.
    private long Start(Timescale scale) => (Samples.Count == 0 ? DateTimeOffset.Now.ToUnixTimeSeconds() : Samples[^1].Time) - scale.Length;

    internal void RefreshText(Strings text)
    {
        _text = text;
        AutomationProperties.SetName(this, text.Get("speed", "chart"));
        DownloadLabel.Text = text.Get("speed", "download");
        UploadLabel.Text = text.Get("speed", "upload");
        NoHistory.Text = text.Get("speed", "empty");
        Update();
    }

    private void OnSize(object sender, SizeChangedEventArgs args) => Update();

    private void Update()
    {
        if (_text is not { } text)
        {
            return;
        }
        var scale = Timescale.Of(Range);
        var start = Start(scale);
        var runs = Bucket(Samples, scale, start);
        var averages = runs.SelectMany(run => run).ToArray();
        var marked = Nearest(averages, _marker);
        var shown = marked ?? averages.LastOrDefault();
        var downloadPeak = averages.Select(average => average.Download).DefaultIfEmpty().Max();
        var uploadPeak = averages.Select(average => average.Upload).DefaultIfEmpty().Max();
        var top = Ceiling(Math.Max(downloadPeak, uploadPeak));
        var at = marked is null ? null : text.Format("speed", "at", Time(marked.Time, scale.Format));
        DownloadValue.Text = Rate(text, shown?.Download);
        UploadValue.Text = Rate(text, shown?.Upload);
        DownloadDetail.Text = at ?? text.Format("speed", "peak", Rate(text, downloadPeak));
        UploadDetail.Text = at ?? text.Format("speed", "peak", Rate(text, uploadPeak));
        Maximum.Text = averages.Length == 0 ? string.Empty : Rate(text, top);
        NoHistory.Visibility = averages.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _value = marked is null ? string.Empty : text.Format("speed", "point", Time(marked.Time, scale.Format),
            Rate(text, marked.Download), Rate(text, marked.Upload));
        var extent = new Extent(Plot.ActualWidth, Plot.ActualHeight, start, scale.Length, top);
        if (extent.Width <= 0 || extent.Height <= 0)
        {
            return;
        }
        Draw(runs, extent);
        DrawTicks(extent, scale);
        DrawMarker(extent, marked);
    }

    private static string Rate(Strings text, double? value) => value is { } rate ? text.Format("units", "rate", text.Bytes(rate)) : "—";

    private static string Time(double time, string format) =>
        DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(time)).ToLocalTime().ToString(format, CultureInfo.CurrentCulture);

    private static Average? Nearest(Average[] averages, double? time) =>
        time is { } target && averages.Length > 0 ? averages.MinBy(average => Math.Abs(average.Time - target)) : null;

    private Average[] Averages()
    {
        var scale = Timescale.Of(Range);
        return Bucket(Samples, scale, Start(scale)).SelectMany(run => run).ToArray();
    }

    // Splits the samples at unknown time, then averages each run in buckets
    // aligned to clock time, so a new sample changes only the newest bucket.
    private static List<List<Average>> Bucket(IReadOnlyList<SpeedSample> samples, Timescale scale, long start)
    {
        var runs = new List<List<Average>>();
        var bucket = new List<SpeedSample>();
        SpeedSample? previous = null;
        foreach (var sample in samples)
        {
            if (sample.Time < start)
            {
                continue;
            }
            if (previous is null || sample.Time - previous.Time > scale.Gap)
            {
                Close();
                runs.Add([]);
            }
            else if (sample.Time / scale.Bucket != previous.Time / scale.Bucket)
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

    // Marks round local clock times, such as every hour on the hour. A label
    // that would touch the previous one is left out; its line stays.
    private void DrawTicks(Extent extent, Timescale scale)
    {
        var ticks = new GeometryGroup();
        TickLabels.Children.Clear();
        var offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(extent.Start)).TotalSeconds;
        var first = (extent.Start + offset + scale.Tick - 1) / scale.Tick * scale.Tick - offset;
        var right = double.NegativeInfinity;
        for (var time = first; time <= extent.Start + extent.Length; time += scale.Tick)
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

    private void OnPointer(object sender, PointerRoutedEventArgs args)
    {
        if (Plot.ActualWidth <= 0)
        {
            return;
        }
        var scale = Timescale.Of(Range);
        var x = args.GetCurrentPoint(Plot).Position.X;
        Mark(Start(scale) + x / Plot.ActualWidth * scale.Length);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        Focus(FocusState.Pointer);
        OnPointer(sender, args);
    }

    private void OnPointerExit(object sender, PointerRoutedEventArgs args)
    {
        if (FocusState != FocusState.Keyboard)
        {
            Mark(null);
        }
    }

    private void OnFocus(object sender, RoutedEventArgs args)
    {
        if (FocusState == FocusState.Keyboard && _marker is null)
        {
            Mark(Averages().LastOrDefault()?.Time);
        }
    }

    private void OnBlur(object sender, RoutedEventArgs args) => Mark(null);

    private void OnKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape && _marker is not null)
        {
            Mark(null);
            args.Handled = true;
            return;
        }
        var averages = Averages();
        if (averages.Length == 0)
        {
            return;
        }
        var index = Nearest(averages, _marker) is { } marked ? Array.IndexOf(averages, marked) : averages.Length - 1;
        int? next = args.Key switch
        {
            VirtualKey.Left => index - 1,
            VirtualKey.Right => index + 1,
            VirtualKey.Home => 0,
            VirtualKey.End => averages.Length - 1,
            _ => null,
        };
        if (next is { } target)
        {
            Mark(averages[Math.Clamp(target, 0, averages.Length - 1)].Time);
            args.Handled = true;
        }
    }

    // The wheel steps through the same ranges as the range buttons: up shows a
    // shorter range, down a longer one, always ending now.
    private void OnWheel(object sender, PointerRoutedEventArgs args)
    {
        var properties = args.GetCurrentPoint(this).Properties;
        if (properties.IsHorizontalMouseWheel)
        {
            return;
        }
        args.Handled = true;
        if (Math.Sign(properties.MouseWheelDelta) != Math.Sign(_wheel))
        {
            _wheel = 0;
        }
        _wheel += properties.MouseWheelDelta;
        while (Math.Abs(_wheel) >= Notch)
        {
            var step = _wheel > 0 ? -1 : 1;
            _wheel -= Math.Sign(_wheel) * Notch;
            Range = (SpeedRange)Math.Clamp((int)Range + step, (int)SpeedRange.FiveMinutes, (int)SpeedRange.Day);
        }
        OnPointer(sender, args);
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

    private sealed record Timescale(long Length, long Bucket, long Gap, long Tick, string Format)
    {
        // The engine samples once a second for five minutes and once a minute
        // for the day. A longer pause between two samples is unknown time, such
        // as an engine restart, so the line breaks there.
        public static Timescale Of(SpeedRange range) => range switch
        {
            SpeedRange.FiveMinutes => new(Length: 300, Bucket: 5, Gap: 2, Tick: 60, Format: "T"),
            SpeedRange.Hour => new(Length: 3600, Bucket: 60, Gap: 90, Tick: 900, Format: "t"),
            SpeedRange.SixHours => new(Length: 21600, Bucket: 300, Gap: 90, Tick: 3600, Format: "t"),
            _ => new(Length: 86400, Bucket: 900, Gap: 90, Tick: 21600, Format: "t"),
        };
    }

    private sealed record Average(double Time, double Download, double Upload);

    private sealed record Extent(double Width, double Height, long Start, long Length, double Top)
    {
        public Point At(double time, double rate) => new((time - Start) * Width / Length, Height * (1 - rate / Top));
    }
}
