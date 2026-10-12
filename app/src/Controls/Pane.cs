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
        nameof(Header),
        typeof(object),
        typeof(Pane),
        new PropertyMetadata(null)
    );
    public static readonly DependencyProperty NavigationProperty = DependencyProperty.Register(
        nameof(Navigation),
        typeof(object),
        typeof(Pane),
        new PropertyMetadata(null)
    );
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions),
        typeof(object),
        typeof(Pane),
        new PropertyMetadata(null)
    );

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
        if (_identity is not null && _navigation is not null && _actions is not null)
        {
            // The tabs and actions sit in Auto columns, so the identity only gets the width they leave.
            _identity.HorizontalAlignment = Navigation is null
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Center;
            _identity.Margin = Navigation is null ? new Thickness(0) : new Thickness(12, 0, 0, 0);
            _actions.Margin = Actions is null ? new Thickness(0) : new Thickness(12, 0, 0, 0);
            // An empty presenter can fall back to the templated parent's body content.
            _navigation.Visibility = Navigation is null ? Visibility.Collapsed : Visibility.Visible;
            _actions.Visibility = Actions is null ? Visibility.Collapsed : Visibility.Visible;
        }
        return base.MeasureOverride(availableSize);
    }
}
