using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Syno.TinyTorrent.Controls;

public sealed partial class FieldColumns : Grid
{
    internal const double WideMinimum = 760;
    private readonly ContentPresenter _primary = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly ContentPresenter _secondary = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly Border _secondaryHost;

    public double MinimumWidth { get; set; } = WideMinimum;
    public double Spacing { get; set; } = 48;
    public double StackSpacing { get; set; }
    public GridLength SecondaryWidth { get; set; } = new(2, GridUnitType.Star);
    public bool SeparateSecondary { get; set; }
    public FrameworkElement? WidthReference { get; set; }
    public bool IsWide => (WidthReference?.ActualWidth ?? ActualWidth) >= MinimumWidth;

    public UIElement? Primary
    {
        get => _primary.Content as UIElement;
        set => _primary.Content = value;
    }

    public UIElement? Secondary
    {
        get => _secondary.Content as UIElement;
        set => _secondary.Content = value;
    }

    public FieldColumns()
    {
        _secondaryHost = new Border
        {
            Child = _secondary,
            Style = (Style)Application.Current.Resources["InspectorRailStyle"],
        };
        ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) }
        );
        ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }
        );
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(_primary);
        Children.Add(_secondaryHost);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var wide = (WidthReference?.ActualWidth ?? availableSize.Width) >= MinimumWidth;
        var secondary = _secondary.Content is not null;
        ColumnSpacing = wide && secondary ? Spacing : 0;
        RowSpacing = !wide && secondary ? StackSpacing : 0;
        ColumnDefinitions[1].Width = wide && secondary ? SecondaryWidth : new GridLength(0);
        _secondaryHost.Visibility = secondary ? Visibility.Visible : Visibility.Collapsed;
        SetColumn(_secondaryHost, wide ? 1 : 0);
        SetRow(_secondaryHost, wide ? 0 : 1);
        _secondaryHost.BorderThickness =
            wide && SeparateSecondary ? new Thickness(1, 0, 0, 0) : new Thickness(0);
        _secondaryHost.Padding =
            wide && SeparateSecondary ? new Thickness(24, 0, 0, 0) : new Thickness(0);
        return base.MeasureOverride(availableSize);
    }
}
