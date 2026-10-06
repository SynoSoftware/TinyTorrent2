using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Controls;

public sealed partial class SpeedGraph : Microsoft.UI.Xaml.Controls.UserControl
{
    private IReadOnlyList<SpeedSample> _samples = [];
    private Strings? _text;
    private bool _day;

    public SpeedGraph() => InitializeComponent();

    internal void Show(Inspector? model)
    {
        if (model is null)
        {
            _samples = [];
            _text = null;
            Draw();
            return;
        }
        var samples = model.History;
        var text = model.Text;
        var day = model.IsDay;
        var changed = !ReferenceEquals(_samples, samples) || _day != day;
        _samples = samples;
        _text = text;
        _day = day;
        var current = samples.LastOrDefault();
        var downloadPeak = samples.Select(sample => sample.DownloadRate).DefaultIfEmpty().Max();
        var uploadPeak = samples.Select(sample => sample.UploadRate).DefaultIfEmpty().Max();
        Download.Text = text.Format("speed", "download", Rate(model.IsAvailable ? current?.DownloadRate : null), Rate(downloadPeak));
        Upload.Text = text.Format("speed", "upload", Rate(model.IsAvailable ? current?.UploadRate : null), Rate(uploadPeak));
        Maximum.Text = Rate(Math.Max(downloadPeak, uploadPeak));
        NoHistory.Text = text.Get("speed", "empty");
        NoHistory.Visibility = samples.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var end = current is null ? DateTimeOffset.Now : DateTimeOffset.FromUnixTimeSeconds(current.Time).ToLocalTime();
        End.Text = end.ToString(day ? "g" : "T", CultureInfo.CurrentCulture);
        Start.Text = end.AddSeconds(day ? -86400 : -300).ToString(day ? "g" : "T", CultureInfo.CurrentCulture);
        if (changed) Draw();
    }

    private string Rate(double? value) => value is { } rate && _text is { } text ? text.Format("units", "rate", text.Bytes(rate)) : "—";
    private void OnSize(object sender, SizeChangedEventArgs args) => Draw();

    private void Draw()
    {
        if (_samples.Count == 0 || Plot.ActualWidth <= 0 || Plot.ActualHeight <= 0)
        {
            DownloadLine.Data = UploadLine.Data = null;
            return;
        }
        var end = _samples[^1].Time;
        var duration = _day ? 86400 : 300;
        var peak = Math.Max(1, _samples.Max(sample => Math.Max(sample.DownloadRate, sample.UploadRate)));
        DownloadLine.Data = Trace(sample => sample.DownloadRate);
        UploadLine.Data = Trace(sample => sample.UploadRate);

        PathGeometry Trace(Func<SpeedSample, double> rate)
        {
            var geometry = new PathGeometry();
            PathFigure? figure = null;
            long previous = 0;
            foreach (var sample in _samples)
            {
                if (sample.Time < end - duration) continue;
                var point = new Point((sample.Time - end + duration) * Plot.ActualWidth / duration,
                    Plot.ActualHeight * (1 - rate(sample) / peak));
                if (figure is null || sample.Time - previous > (_day ? 90 : 2))
                {
                    figure = new PathFigure { StartPoint = point, IsClosed = false, IsFilled = false };
                    geometry.Figures.Add(figure);
                }
                else figure.Segments.Add(new LineSegment { Point = point });
                previous = sample.Time;
            }
            return geometry;
        }
    }
}
