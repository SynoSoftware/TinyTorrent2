using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Controls;

public sealed partial class FieldColumns : Grid
{
    internal const double WideMinimum = 760;

    // Shared by every instance, because Library builds new columns for each
    // selected file.
    private static readonly UISettings Display = new();
    private readonly ContentPresenter _primary = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly ContentPresenter _secondary = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly Border _secondaryHost;

    /// <summary>
    /// The narrowest width that holds both columns at 100% Windows text size;
    /// a larger text size needs proportionally more.
    /// </summary>
    public double MinimumWidth { get; set; } = WideMinimum;
    public double Spacing { get; set; } = 48;
    public double StackSpacing { get; set; }

    /// <summary>
    /// The primary column's width while the columns sit side by side. Pages
    /// set it and <see cref="SecondaryWidth"/> in code, because XAML text is
    /// known to become a <see cref="GridLength"/> only for a dependency
    /// property, such as a column's width.
    /// </summary>
    public GridLength PrimaryWidth { get; set; } = new(3, GridUnitType.Star);
    public GridLength SecondaryWidth { get; set; } = new(2, GridUnitType.Star);

    // A divider separates the secondary column: beside it while the columns
    // sit side by side, above it once they stack.
    public bool SeparateSecondary { get; set; }
    public FrameworkElement? WidthReference { get; set; }

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
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(_primary);
        Children.Add(_secondaryHost);
        Loaded += (_, _) => Display.TextScaleFactorChanged += OnTextScale;
        Unloaded += (_, _) => Display.TextScaleFactorChanged -= OnTextScale;
    }

    /// <summary>
    /// Whether two columns that need <paramref name="minimum"/> DIPs at 100%
    /// text size fit <paramref name="width"/> at the current Windows text size.
    /// </summary>
    internal static bool Fits(double width, double minimum) =>
        width >= minimum * Display.TextScaleFactor;

    private void OnTextScale(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(InvalidateMeasure);

    protected override Size MeasureOverride(Size availableSize)
    {
        var wide = Fits(WidthReference?.ActualWidth ?? availableSize.Width, MinimumWidth);
        var secondary = _secondary.Content is not null;
        var beside = wide && secondary;
        var separated = SeparateSecondary && secondary;
        ColumnSpacing = beside ? Spacing : 0;
        RowSpacing = !wide && secondary ? StackSpacing : 0;
        ColumnDefinitions[0].Width = beside ? PrimaryWidth : new GridLength(1, GridUnitType.Star);
        ColumnDefinitions[1].Width = beside ? SecondaryWidth : new GridLength(0);
        _secondaryHost.Visibility = secondary ? Visibility.Visible : Visibility.Collapsed;
        SetColumn(_secondaryHost, wide ? 1 : 0);
        SetRow(_secondaryHost, wide ? 0 : 1);
        _secondaryHost.BorderThickness =
            !separated ? new Thickness(0)
            : wide ? new Thickness(1, 0, 0, 0)
            : new Thickness(0, 1, 0, 0);
        _secondaryHost.Padding =
            !separated ? new Thickness(0)
            : wide ? new Thickness(24, 0, 0, 0)
            : new Thickness(0, 24, 0, 0);
        return base.MeasureOverride(availableSize);
    }
}
