using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Syno.TinyTorrent.Controls;

public sealed partial class Pane : ContentControl
{
    private Grid? _heading;
    private ContentPresenter? _identity;
    private ContentPresenter? _navigation;
    private ContentPresenter? _actions;

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(object), typeof(Pane), new PropertyMetadata(null));
    public static readonly DependencyProperty NavigationProperty = DependencyProperty.Register(
        nameof(Navigation), typeof(object), typeof(Pane), new PropertyMetadata(null));
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(Pane), new PropertyMetadata(null));

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public object? Navigation
    {
        get => GetValue(NavigationProperty);
        set => SetValue(NavigationProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    internal double HeaderHeight => _heading?.ActualHeight ?? 0;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _heading = (Grid)GetTemplateChild("Heading");
        _identity = (ContentPresenter)GetTemplateChild("Identity");
        _navigation = (ContentPresenter)GetTemplateChild("Navigation");
        _actions = (ContentPresenter)GetTemplateChild("Actions");
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_heading is not null && _identity is not null && _navigation is not null && _actions is not null)
        {
            _heading.ColumnDefinitions[0].Width = Navigation is null
                ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            _heading.ColumnDefinitions[1].Width = Navigation is null
                ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            _navigation.Margin = Navigation is null ? new Thickness(0) : new Thickness(12, 0, 0, 0);
            _actions.Margin = Actions is null ? new Thickness(0) : new Thickness(12, 0, 0, 0);
            // An empty presenter can fall back to the templated parent's body content.
            _navigation.Visibility = Navigation is null ? Visibility.Collapsed : Visibility.Visible;
            _actions.Visibility = Actions is null ? Visibility.Collapsed : Visibility.Visible;
            var size = base.MeasureOverride(availableSize);
            // Reserve the tabs and actions before allowing the identity to grow.
            var maximum = Navigation is null ? double.PositiveInfinity : Math.Max(0,
                Math.Min(availableSize.Width / 2, availableSize.Width - 24 - BorderThickness.Left - BorderThickness.Right
                    - _navigation.DesiredSize.Width - _actions.DesiredSize.Width));
            if (_identity.MaxWidth == maximum)
                return size;
            _identity.MaxWidth = maximum;
        }
        return base.MeasureOverride(availableSize);
    }
}
