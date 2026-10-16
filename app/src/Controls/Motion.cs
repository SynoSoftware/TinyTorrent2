using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Controls;

internal sealed class Motion
{
    private static readonly Duration Duration = new(TimeSpan.FromMilliseconds(250));
    private readonly UISettings _settings = new();
    private readonly Storyboard _storyboard = new();
    private readonly List<TranslateTransform> _translations = [];
    private FrameworkElement? _outgoing;
    private bool _hitTest;
    internal FrameworkElement? Current { get; private set; }
    internal bool IsActive => _storyboard.GetCurrentState() == ClockState.Active;

    internal Motion() => _storyboard.Completed += (_, _) => Stop();

    internal void Show(FrameworkElement content, bool forward = true) =>
        Change(content, forward, false);

    internal void CrossFade(FrameworkElement content) => Change(content, true, true);

    internal void Replace(FrameworkElement content)
    {
        var previous = Current;
        Stop();
        Current = content;
        content.Visibility = Visibility.Visible;
        if (previous is not null && !ReferenceEquals(previous, content))
            previous.Visibility = Visibility.Collapsed;
    }

    private void Change(FrameworkElement content, bool forward, bool crossFade)
    {
        if (ReferenceEquals(Current, content))
            return;
        var previous = Current;
        var departure = (previous?.RenderTransform as TranslateTransform)?.X ?? 0;
        var arrival = ReferenceEquals(_outgoing, content)
            ? (content.RenderTransform as TranslateTransform)?.X
            : null;
        var departureOpacity = previous?.Opacity ?? 1;
        var arrivalOpacity = ReferenceEquals(_outgoing, content) ? content.Opacity : 0;
        Stop();
        Current = content;
        content.Visibility = Visibility.Visible;
        if (previous is null)
            return;

        _outgoing = previous;
        _hitTest = previous.IsHitTestVisible;
        if (!previous.IsLoaded || !_settings.AnimationsEnabled)
        {
            Stop();
            return;
        }
        previous.IsHitTestVisible = false;
        if (crossFade)
        {
            Blend(previous, departureOpacity, 0);
            Blend(content, arrivalOpacity, 1);
        }
        else
        {
            var distance = GetOffset(previous, forward);
            Slide(previous, departure, -distance);
            Slide(content, arrival ?? departure + distance, 0);
        }
        Begin(content);
    }

    internal void Enter(FrameworkElement content, bool forward = true, Action? update = null)
    {
        var active = ReferenceEquals(Current, content) && IsActive;
        var offset = GetOffset(content, forward);
        var start = active ? (content.RenderTransform as TranslateTransform)?.X ?? offset : offset;
        Stop();
        if (content.Visibility != Visibility.Visible || !_settings.AnimationsEnabled)
        {
            update?.Invoke();
            return;
        }
        Current = content;
        Slide(content, start, 0);
        Begin(content, update);
    }

    private void Begin(FrameworkElement content, Action? update = null)
    {
        try
        {
            update?.Invoke();
            content.UpdateLayout();
            _storyboard.Begin();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    internal void Fade(FrameworkElement content, double opacity) =>
        Fade(content, content.Opacity, opacity);

    internal void Fade(FrameworkElement content, double from, double to)
    {
        Stop();
        Current = content;
        content.Opacity = to;
        if (!content.IsLoaded || !_settings.AnimationsEnabled || from == to)
            return;
        Blend(content, from, to);
        _storyboard.Begin();
    }

    private void Blend(FrameworkElement content, double from, double to) =>
        Animate(content, nameof(UIElement.Opacity), from, to);

    private void Slide(FrameworkElement content, double from, double to)
    {
        var transform = new TranslateTransform { X = from };
        content.RenderTransform = transform;
        _translations.Add(transform);
        Animate(transform, nameof(TranslateTransform.X), from, to);
    }

    private void Animate(DependencyObject target, string property, double from, double to)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = Duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        _storyboard.Children.Add(animation);
    }

    internal void Stop()
    {
        _storyboard.Stop();
        _storyboard.Children.Clear();
        foreach (var transform in _translations)
            transform.X = 0;
        _translations.Clear();
        if (_outgoing is { } outgoing)
        {
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.IsHitTestVisible = _hitTest;
            _outgoing = null;
        }
    }

    internal static void Clip(object sender, SizeChangedEventArgs args) =>
        ((FrameworkElement)sender).Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height),
        };

    private static double GetOffset(FrameworkElement content, bool forward)
    {
        var host = VisualTreeHelper.GetParent(content) as FrameworkElement;
        var width = Math.Max(content.ActualWidth, host?.ActualWidth ?? 0);
        return forward ? width : -width;
    }
}
