using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Syno.TinyTorrent.Helpers;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Controls;

public sealed partial class SettingsExpander : Expander
{
    private readonly UISettings _settings = new();
    private Border? _drawer;
    private Border? _body;
    private Storyboard? _animation;
    private double _target;

    public SettingsExpander()
    {
        DefaultStyleKey = typeof(Expander);
        RegisterPropertyChangedCallback(
            IsExpandedProperty,
            (_, _) => DispatcherQueue.TryEnqueue(Change)
        );
        RegisterPropertyChangedCallback(
            ContentProperty,
            (_, _) =>
            {
                if (!IsExpanded && Content is null)
                {
                    Stop();
                    Settle();
                }
            }
        );
        Loaded += (_, _) => Settle();
        Unloaded += (_, _) =>
        {
            Stop();
            Settle();
        };
    }

    protected override void OnApplyTemplate()
    {
        Stop();
        if (_drawer is not null)
            _drawer.SizeChanged -= Motion.Clip;
        if (_body is not null)
            _body.SizeChanged -= OnBodySize;
        _drawer = null;
        _body = null;
        base.OnApplyTemplate();
        _drawer = GetTemplateChild("ExpanderContentClip") as Border;
        _body = GetTemplateChild("ExpanderContent") as Border;
        if (_drawer is null || _body is null)
            return;

        _drawer.SizeChanged += Motion.Clip;
        _body.SizeChanged += OnBodySize;
        Settle();
    }

    private void OnBodySize(object sender, SizeChangedEventArgs args)
    {
        if (IsExpanded && _animation is not null && Math.Abs(args.NewSize.Height - _target) > 0.5)
            DispatcherQueue.TryEnqueue(Change);
    }

    private void Change()
    {
        if (_drawer is null || _body is null)
            return;
        var from = _drawer.Visibility == Visibility.Collapsed ? 0 : _drawer.ActualHeight;
        Stop();
        if (!IsLoaded || !_settings.AnimationsEnabled || ActualWidth <= 0)
        {
            Settle();
            return;
        }

        _drawer.Visibility = Visibility.Visible;
        _drawer.IsHitTestVisible = IsExpanded;
        _body.Visibility = Visibility.Visible;
        _body.Measure(new Size(ActualWidth, double.PositiveInfinity));
        _target = IsExpanded ? _body.DesiredSize.Height : 0;
        if (Math.Abs(from - _target) < 0.5)
        {
            Settle();
            return;
        }

        _drawer.Height = from;
        var animation = new DoubleAnimation
        {
            From = from,
            To = _target,
            Duration = new Duration(TimeSpan.FromMilliseconds(IsExpanded ? 260 : 220)),
            EasingFunction = new CubicEase
            {
                EasingMode = IsExpanded ? EasingMode.EaseOut : EasingMode.EaseInOut,
            },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, _drawer);
        Storyboard.SetTargetProperty(animation, nameof(Height));
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_animation, storyboard))
                return;
            Stop();
            Settle();
        };
        _animation = storyboard;
        storyboard.Begin();
    }

    private void Settle()
    {
        if (_drawer is null || _body is null)
            return;
        _drawer.Height = IsExpanded ? double.NaN : 0;
        _drawer.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        _drawer.IsHitTestVisible = IsExpanded;
        _body.Visibility = Visibility.Visible;
    }

    private void Stop()
    {
        _animation?.Stop();
        _animation = null;
    }
}
