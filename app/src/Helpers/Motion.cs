using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Helpers;

internal sealed class Motion
{
    private readonly UISettings _settings = new();
    private readonly Storyboard _storyboard = new();
    private FrameworkElement? _outgoing;
    internal FrameworkElement? Current { get; private set; }

    internal Motion() => _storyboard.Completed += (_, _) => Stop();

    internal void Show(FrameworkElement content, int direction = 1)
    {
        if (ReferenceEquals(Current, content))
            return;
        var previous = Current;
        var departure = (previous?.RenderTransform as TranslateTransform)?.X ?? 0;
        var arrival = ReferenceEquals(_outgoing, content)
            ? (content.RenderTransform as TranslateTransform)?.X : null;
        Stop();
        Current = content;
        content.Visibility = Visibility.Visible;
        if (previous is null)
            return;

        _outgoing = previous;
        if (!previous.IsLoaded || !_settings.AnimationsEnabled)
        {
            Stop();
            return;
        }
        var distance = direction * Math.Max(previous.ActualWidth, content.ActualWidth);
        previous.IsHitTestVisible = false;
        Slide(previous, departure, -distance);
        Slide(content, arrival ?? departure + distance, 0);
        _storyboard.Begin();
    }

    internal void Play(FrameworkElement content, double offset = 24)
    {
        var start = ReferenceEquals(Current, content) && _storyboard.GetCurrentState() == ClockState.Active
            ? (content.RenderTransform as TranslateTransform)?.X ?? offset : offset;
        Stop();
        if (content.Visibility != Visibility.Visible || !_settings.AnimationsEnabled)
            return;
        Current = content;
        Slide(content, start, 0);
        _storyboard.Begin();
    }

    private void Slide(FrameworkElement content, double from, double to)
    {
        var transform = new TranslateTransform();
        content.RenderTransform = transform;
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, nameof(TranslateTransform.X));
        _storyboard.Children.Add(animation);
    }

    internal void Stop()
    {
        _storyboard.Stop();
        _storyboard.Children.Clear();
        if (_outgoing is { } outgoing)
        {
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.IsHitTestVisible = true;
            _outgoing = null;
        }
    }

    internal static void Clip(object sender, SizeChangedEventArgs args) =>
        ((FrameworkElement)sender).Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height),
        };
}
