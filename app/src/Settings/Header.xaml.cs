using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Windows.Foundation;

namespace Syno.TinyTorrent.Settings;

public sealed partial class Header : UserControl
{
    public static readonly DependencyProperty PageTitleProperty = DependencyProperty.Register(
        nameof(PageTitle), typeof(string), typeof(Header), new PropertyMetadata(string.Empty)
    );
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(Header), new PropertyMetadata(string.Empty)
    );
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(Header), new PropertyMetadata(string.Empty)
    );
    public static readonly DependencyProperty FirstProperty = DependencyProperty.Register(
        nameof(First), typeof(Fact), typeof(Header), new PropertyMetadata(null)
    );
    public static readonly DependencyProperty SecondProperty = DependencyProperty.Register(
        nameof(Second), typeof(Fact), typeof(Header), new PropertyMetadata(null)
    );
    public static readonly DependencyProperty ArtworkProperty = DependencyProperty.Register(
        nameof(Artwork), typeof(ImageSource), typeof(Header),
        new PropertyMetadata(null)
    );
    public static readonly DependencyProperty HeadingLevelProperty = DependencyProperty.Register(
        nameof(HeadingLevel), typeof(AutomationHeadingLevel), typeof(Header),
        new PropertyMetadata(AutomationHeadingLevel.Level2)
    );

    public ImageSource? Artwork
    {
        get => (ImageSource?)GetValue(ArtworkProperty);
        set => SetValue(ArtworkProperty, value);
    }
    public AutomationHeadingLevel HeadingLevel
    {
        get => (AutomationHeadingLevel)GetValue(HeadingLevelProperty);
        set => SetValue(HeadingLevelProperty, value);
    }
    public string PageTitle
    {
        get => (string)GetValue(PageTitleProperty);
        set => SetValue(PageTitleProperty, value);
    }
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }
    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }
    public Fact? First
    {
        get => (Fact?)GetValue(FirstProperty);
        set => SetValue(FirstProperty, value);
    }
    public Fact? Second
    {
        get => (Fact?)GetValue(SecondProperty);
        set => SetValue(SecondProperty, value);
    }

    public Header()
    {
        InitializeComponent();
        Svg.Fit(ArtworkImage);
    }

    public static bool IsPageHeading(AutomationHeadingLevel level) => level == AutomationHeadingLevel.Level1;

    public bool FocusHeading(FocusState state) =>
        (PageTitleText.Visibility == Visibility.Visible ? PageTitleText : TitleText).Focus(state);

    protected override Size MeasureOverride(Size availableSize)
    {
        var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
        Subject.Measure(unlimited);
        FirstPresenter.Measure(unlimited);
        SecondPresenter.Measure(unlimited);
        var availableWidth = Math.Max(0, availableSize.Width - Padding.Left - Padding.Right);
        var factsWidth = FirstPresenter.DesiredSize.Width + Facts.ColumnSpacing + SecondPresenter.DesiredSize.Width;
        var state = factsWidth > availableWidth ? "StackedFacts"
            : Subject.DesiredSize.Width + Layout.ColumnSpacing + factsWidth > availableWidth ? "FactsBelow"
            : "InlineFacts";
        VisualStateManager.GoToState(this, state, false);
        return base.MeasureOverride(availableSize);
    }
}
