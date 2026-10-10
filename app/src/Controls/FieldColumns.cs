using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Syno.TinyTorrent.Controls;

public sealed partial class FieldColumns : Grid
{
    internal const double WideMinimum = 760;
    private readonly ContentPresenter _primary = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ContentPresenter _secondary = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };

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
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(_primary);
        Children.Add(_secondary);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var wide = availableSize.Width >= WideMinimum;
        ColumnSpacing = wide ? 48 : 0;
        ColumnDefinitions[1].Width = wide ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        SetColumn(_secondary, wide ? 1 : 0);
        SetRow(_secondary, wide ? 0 : 1);
        return base.MeasureOverride(availableSize);
    }
}
