using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Services;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Controls;

public sealed partial class Speedometer : UserControl
{
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly DispatcherQueueTimer _animation;
    private CanvasControl? _canvas;
    private CanvasGeometry? _rail;
    private double? _maximum;
    private double? _speed;
    private string _automatic = string.Empty;
    private double _from;
    private double _target;
    private long _started;
    private static readonly string[] Digits =
    [
        "012345", "12", "01346", "01236", "1256",
        "02356", "023456", "012", "0123456", "012356",
    ];
    private static readonly (Vector2 Start, Vector2 End)[] Segments =
    [
        (new(4, 2), new(18, 2)), (new(20, 4), new(20, 16)),
        (new(20, 20), new(20, 32)), (new(4, 34), new(18, 34)),
        (new(2, 20), new(2, 32)), (new(2, 4), new(2, 16)),
        (new(4, 18), new(18, 18)),
    ];

    internal Speedometer(string glyph)
    {
        InitializeComponent();
        Icon.FontFamily = Syno.Lucide.Font;
        Icon.Glyph = glyph;
        _animation = DispatcherQueue.CreateTimer();
        _animation.Interval = TimeSpan.FromMilliseconds(33);
        _animation.Tick += (_, _) =>
        {
            if (!_settings.AnimationsEnabled || _accessibility.HighContrast)
                _from = _target;
            _canvas?.Invalidate();
            if (Progress >= 1 || _from == _target)
                _animation.Stop();
        };
        Loaded += (_, _) =>
        {
            Drawing.Height = 136 * _settings.TextScaleFactor;
            _settings.TextScaleFactorChanged += OnTextScale;
            _canvas = new CanvasControl();
            _canvas.CreateResources += (sender, _) =>
            {
                _rail?.Dispose();
                using var path = new CanvasPathBuilder(sender);
                path.BeginFigure(20, 142);
                path.AddLine(7, 111);
                path.AddQuadraticBezier(new(1, 98), new(10, 84));
                path.AddLine(43, 39);
                path.AddQuadraticBezier(new(52, 27), new(70, 25));
                path.AddLine(264, 8);
                path.EndFigure(CanvasFigureLoop.Open);
                _rail = CanvasGeometry.CreatePath(path);
            };
            _canvas.Draw += Draw;
            Drawing.Children.Add(_canvas);
        };
        Unloaded += (_, _) =>
        {
            _settings.TextScaleFactorChanged -= OnTextScale;
            _animation.Stop();
            _rail?.Dispose();
            _rail = null;
            _canvas?.RemoveFromVisualTree();
            _canvas = null;
        };
        ActualThemeChanged += (_, _) => _canvas?.Invalidate();
        foreach (var ink in new[] { Ink, Muted })
            ink.RegisterPropertyChangedCallback(TextBlock.ForegroundProperty, (_, _) => _canvas?.Invalidate());
        foreach (var brush in new[] { Accent, Track })
            brush.RegisterPropertyChangedCallback(Border.BackgroundProperty, (_, _) => _canvas?.Invalidate());
    }

    internal void Reset(double capacity)
    {
        _maximum = double.IsFinite(capacity) && capacity > 0 ? Ceiling(capacity) : null;
        _speed = null;
        _from = _target = 0;
        _animation.Stop();
        _canvas?.Invalidate();
    }

    internal void Show(double speed, string label, Strings text)
    {
        Label.Text = label;
        Unit.Text = text.Get("units", "mbps");
        _automatic = text.Get("connection_setup", "automatic_scale");
        _speed = speed > 0 ? speed : null;
        if (_speed is { } value && (_maximum is null || value > _maximum))
            _maximum = Ceiling(value);
        var target = _speed is { } measured && _maximum is { } maximum ? measured / maximum : 0;
        if (target != _target)
        {
            _from = Displayed;
            _target = target;
            _started = Stopwatch.GetTimestamp();
            if (_settings.AnimationsEnabled && !_accessibility.HighContrast && IsLoaded)
                _animation.Start();
            else
                _from = _target;
        }
        var description = text.Format("connection_setup", "speed_reading", label,
            _speed is { } rate ? rate.ToString("0.##", CultureInfo.CurrentCulture) : "—");
        AutomationProperties.SetName(this, description);
        ToolTipService.SetToolTip(this, description);
        _canvas?.Invalidate();
    }

    private double Progress => Math.Clamp(Stopwatch.GetElapsedTime(_started).TotalMilliseconds / 550, 0, 1);
    private double Displayed => _from + (_target - _from) * (1 - Math.Pow(1 - Progress, 3));

    private static double Ceiling(double value)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var step = value / magnitude;
        return (step <= 1 ? 1 : step <= 2 ? 2 : step <= 5 ? 5 : 10) * magnitude;
    }

    private void OnTextScale(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        Drawing.Height = 136 * _settings.TextScaleFactor;
        _canvas?.Invalidate();
    });

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_rail is null || sender.ActualWidth <= 0 || sender.ActualHeight <= 0)
            return;
        var drawing = args.DrawingSession;
        var scale = new Vector2((float)sender.ActualWidth / 280, (float)sender.ActualHeight / 160);
        drawing.Transform = Matrix3x2.CreateScale(scale);
        var length = _rail.ComputePathLength();
        var accent = ((SolidColorBrush)Accent.Background).Color;
        var track = ((SolidColorBrush)Track.Background).Color;
        for (var index = 0; index < 30; index++)
        {
            var start = _rail.ComputePointOnPath(length * index / 30);
            var end = _rail.ComputePointOnPath(length * (index + 0.78f) / 30);
            drawing.DrawLine(start, end, track, 4);
            var lit = Math.Clamp(Displayed * 30 - index, 0, 1);
            if (lit > 0)
            {
                var color = accent;
                color.A = (byte)(color.A * lit);
                drawing.DrawLine(start, end, color, 4);
            }
        }
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 14,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
        };
        var muted = ((SolidColorBrush)Muted.Foreground).Color;
        drawing.DrawText("0", new Rect(-10, 145, 60, 20), muted, format);
        if (_maximum is { } maximum)
        {
            drawing.DrawText((maximum / 2).ToString("0.##", CultureInfo.CurrentCulture),
                new Rect(112, 30, 60, 20), muted, format);
            format.HorizontalAlignment = CanvasHorizontalAlignment.Right;
            drawing.DrawText(maximum.ToString("0.##", CultureInfo.CurrentCulture),
                new Rect(184, 15, 80, 20), muted, format);
        }
        else
            drawing.DrawText(_automatic, new Rect(164, 15, 110, 20), muted, format);

        var number = _speed is { } speed ? speed.ToString(speed < 1 ? "0.00" : "0.0", CultureInfo.CurrentCulture) : "----";
        var width = number.Sum(character => char.IsDigit(character) || character == '-' ? 28 : 10);
        var digitScale = Math.Min(sender.ActualWidth * 0.84 / width, sender.ActualHeight * 0.56 / 40);
        var left = sender.ActualWidth * 0.12 + (sender.ActualWidth * 0.84 - width * digitScale) / 2;
        var top = sender.ActualHeight * 0.36 + (sender.ActualHeight * 0.56 - 40 * digitScale) / 2;
        drawing.Transform = Matrix3x2.CreateScale((float)digitScale)
            * Matrix3x2.CreateTranslation((float)left, (float)top);
        using var stroke = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Square, EndCap = CanvasCapStyle.Square };
        var ink = ((SolidColorBrush)Ink.Foreground).Color;
        float offset = 0;
        for (var index = 0; index < number.Length; index++)
        {
            var character = number[index];
            var color = ink;
            if (index == number.Length - 1 && _speed is not null)
                color.A = (byte)(color.A * 0.7);
            if (char.IsDigit(character) || character == '-')
            {
                var segments = character == '-' ? "6" : Digits[(int)char.GetNumericValue(character)];
                foreach (var segment in segments)
                {
                    var line = Segments[segment - '0'];
                    drawing.DrawLine(line.Start + new Vector2(offset, 2), line.End + new Vector2(offset, 2), color, 3.6f, stroke);
                }
                offset += 28;
            }
            else
            {
                drawing.FillCircle(new Vector2(offset + 3, 35), 2, color);
                if (character is ',' or '\u066B')
                    drawing.DrawLine(new Vector2(offset + 3, 36), new Vector2(offset + 1, 39), color, 1.6f, stroke);
                offset += 10;
            }
        }
    }
}
