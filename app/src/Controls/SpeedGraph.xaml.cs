using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Controls;

// Draws the Speed view as docs/interface.md defines it: averages, not samples.
public sealed partial class SpeedGraph : Microsoft.UI.Xaml.Controls.UserControl
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples),
        typeof(IReadOnlyList<SpeedSample>), typeof(SpeedGraph), new PropertyMetadata(Array.Empty<SpeedSample>(), (graph, _) => ((SpeedGraph)graph).Update()));
    public static readonly DependencyProperty IsDayProperty = DependencyProperty.Register(nameof(IsDay), typeof(bool),
        typeof(SpeedGraph), new PropertyMetadata(false, (graph, _) => ((SpeedGraph)graph).Update()));
    public static readonly DependencyProperty IsAvailableProperty = DependencyProperty.Register(nameof(IsAvailable), typeof(bool),
        typeof(SpeedGraph), new PropertyMetadata(false, (graph, _) => ((SpeedGraph)graph).Update()));

    // The engine samples once a second for five minutes and once a minute for
    // the day. A longer pause between two samples is unknown time, such as an
    // engine restart, so the line breaks there.
    private static readonly Timescale Minutes = new(Length: 300, Bucket: 5, Gap: 2, Format: "T");
    private static readonly Timescale Day = new(Length: 86400, Bucket: 300, Gap: 90, Format: "g");
    private static readonly double[] Steps = [1, 1.5, 2, 3, 4, 5, 6, 8, 10];
    private Strings? _text;

    public SpeedGraph() => InitializeComponent();

    public IReadOnlyList<SpeedSample> Samples { get => (IReadOnlyList<SpeedSample>)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public bool IsDay { get => (bool)GetValue(IsDayProperty); set => SetValue(IsDayProperty, value); }
    public bool IsAvailable { get => (bool)GetValue(IsAvailableProperty); set => SetValue(IsAvailableProperty, value); }

    internal void RefreshText(Strings text)
    {
        _text = text;
        Update();
    }

    private void OnSize(object sender, SizeChangedEventArgs args) => Update();

    private void Update()
    {
        if (_text is not { } text)
        {
            return;
        }
        var scale = IsDay ? Day : Minutes;
        var samples = Samples;
        var end = samples.Count == 0 ? DateTimeOffset.Now.ToUnixTimeSeconds() : samples[^1].Time;
        var runs = Bucket(samples, scale, end - scale.Length);
        var averages = runs.SelectMany(run => run).ToArray();
        var current = IsAvailable ? averages.LastOrDefault() : null;
        var downloadPeak = averages.Select(average => average.Download).DefaultIfEmpty().Max();
        var uploadPeak = averages.Select(average => average.Upload).DefaultIfEmpty().Max();
        var top = Ceiling(Math.Max(downloadPeak, uploadPeak));
        Download.Text = text.Format("speed", "download", Rate(text, current?.Download), Rate(text, downloadPeak));
        Upload.Text = text.Format("speed", "upload", Rate(text, current?.Upload), Rate(text, uploadPeak));
        Maximum.Text = averages.Length == 0 ? string.Empty : Rate(text, top);
        NoHistory.Text = text.Get("speed", "empty");
        NoHistory.Visibility = averages.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var last = DateTimeOffset.FromUnixTimeSeconds(end).ToLocalTime();
        End.Text = last.ToString(scale.Format, CultureInfo.CurrentCulture);
        Start.Text = last.AddSeconds(-scale.Length).ToString(scale.Format, CultureInfo.CurrentCulture);
        Draw(runs, scale, end - scale.Length, top);
    }

    private static string Rate(Strings text, double? value) => value is { } rate ? text.Format("units", "rate", text.Bytes(rate)) : "—";

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
        foreach (var step in Steps)
        {
            var top = step * power;
            if (top >= value)
            {
                return (top < 1000 ? top : 1024) * unit;
            }
        }
        return 1024 * unit;
    }

    private void Draw(List<List<Average>> runs, Timescale scale, long start, double top)
    {
        var width = Plot.ActualWidth;
        var height = Plot.ActualHeight;
        var area = new PathGeometry();
        var download = new PathGeometry();
        var upload = new PathGeometry();
        if (width > 0 && height > 0)
        {
            foreach (var run in runs)
            {
                var downloads = run.Select(average => Position(average.Time, average.Download)).ToArray();
                var uploads = run.Select(average => Position(average.Time, average.Upload)).ToArray();
                var fill = new PathFigure { StartPoint = new Point(downloads[0].X, height), IsClosed = true };
                fill.Segments.Add(new LineSegment { Point = downloads[0] });
                fill.Segments.Add(Curve(downloads));
                fill.Segments.Add(new LineSegment { Point = new Point(downloads[^1].X, height) });
                area.Figures.Add(fill);
                download.Figures.Add(Line(downloads));
                upload.Figures.Add(Line(uploads));
            }
        }
        DownloadArea.Data = area;
        DownloadLine.Data = download;
        UploadLine.Data = upload;

        Point Position(double time, double rate) => new((time - start) * width / scale.Length, height * (1 - rate / top));
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

    private sealed record Timescale(long Length, long Bucket, long Gap, string Format);

    private sealed record Average(double Time, double Download, double Upload);
}
