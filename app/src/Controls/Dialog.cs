using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Controls;

// A ContentDialog with natural button widths unless a FooterAction joins them.
// Buttons sit at the right with Footer at their left, or centred in a narrow
// dialog without one. The implicit style in App.xaml draws it, because
// ContentDialog's own template always splits the row into equal buttons.
public sealed partial class Dialog : ContentDialog
{
    private readonly UISettings _display = new();
    // WinUI's generated XAML assigns this property after construction.
    public ActionButton? FooterAction { get; set; }
    private long _footerText;
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer),
        typeof(object),
        typeof(Dialog),
        new PropertyMetadata(null, (dialog, _) => ((Dialog)dialog).ShowFooter())
    );

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(Dialog),
        new PropertyMetadata(string.Empty, (dialog, _) => ((Dialog)dialog).ShowTitle())
    );

    // Each button's Lucide icon and the tooltip that says what its one word does
    // not. Every dialog's Close button is Cancel, so it defaults to Cancel's icon.
    public static readonly DependencyProperty PrimaryGlyphProperty = Text(
        nameof(PrimaryGlyph),
        string.Empty
    );
    public static readonly DependencyProperty SecondaryGlyphProperty = Text(
        nameof(SecondaryGlyph),
        string.Empty
    );
    public static readonly DependencyProperty CloseGlyphProperty = Text(
        nameof(CloseGlyph),
        Lucide.X
    );
    public static readonly DependencyProperty PrimaryToolTipProperty = Text(
        nameof(PrimaryToolTip),
        null
    );
    public static readonly DependencyProperty SecondaryToolTipProperty = Text(
        nameof(SecondaryToolTip),
        null
    );
    public static readonly DependencyProperty CloseToolTipProperty = Text(
        nameof(CloseToolTip),
        null
    );

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    // The Lucide icon before the title.
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
    public string PrimaryGlyph
    {
        get => (string)GetValue(PrimaryGlyphProperty);
        set => SetValue(PrimaryGlyphProperty, value);
    }
    public string SecondaryGlyph
    {
        get => (string)GetValue(SecondaryGlyphProperty);
        set => SetValue(SecondaryGlyphProperty, value);
    }
    public string CloseGlyph
    {
        get => (string)GetValue(CloseGlyphProperty);
        set => SetValue(CloseGlyphProperty, value);
    }
    public string? PrimaryToolTip
    {
        get => (string?)GetValue(PrimaryToolTipProperty);
        set => SetValue(PrimaryToolTipProperty, value);
    }
    public string? SecondaryToolTip
    {
        get => (string?)GetValue(SecondaryToolTipProperty);
        set => SetValue(SecondaryToolTipProperty, value);
    }
    public string? CloseToolTip
    {
        get => (string?)GetValue(CloseToolTipProperty);
        set => SetValue(CloseToolTipProperty, value);
    }

    private static DependencyProperty Text(string name, string? value) =>
        DependencyProperty.Register(
            name,
            typeof(string),
            typeof(Dialog),
            new PropertyMetadata(value)
        );

    // The default button is the act the person chose, as the interface
    // contract's button rulings require.
    public Dialog()
    {
        DefaultButton = ContentDialogButton.Primary;
        Opened += (_, _) => { AlignActions(); FocusOnOpen(); };
        // The footer button belongs to the content, which can outlive the
        // dialog, so the dialog listens to it only while shown.
        Loaded += (_, _) =>
        {
            _display.TextScaleFactorChanged += OnTextScale;
            _footerText =
                FooterAction?.RegisterPropertyChangedCallback(
                    ActionButton.TextProperty,
                    (_, _) => AlignActions()
                ) ?? 0;
        };
        Unloaded += (_, _) =>
        {
            _display.TextScaleFactorChanged -= OnTextScale;
            FooterAction?.UnregisterPropertyChangedCallback(ActionButton.TextProperty, _footerText);
        };
        RegisterPropertyChangedCallback(TitleProperty, (_, _) => ShowTitle());
    }

    // Lifts ContentDialog's maximum size, for content that sizes itself to the
    // window.
    public void SizeToContent()
    {
        Resources["ContentDialogMaxWidth"] = double.PositiveInfinity;
        Resources["ContentDialogMaxHeight"] = double.PositiveInfinity;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        foreach (var name in new[] { "CommandSpace", "PrimaryButton", "SecondaryButton", "CloseButton" })
        {
            if (GetTemplateChild(name) is not FrameworkElement part)
                continue;
            part.SizeChanged += (_, _) => PlaceButtons();
            if (part is ActionButton button)
                button.RegisterPropertyChangedCallback(ActionButton.TextProperty, (_, _) => AlignActions());
        }
        ShowTitle();
        ShowFooter();
        AlignActions();
    }

    private void OnTextScale(UISettings sender, object args) => DispatcherQueue.TryEnqueue(AlignActions);

    private void AlignActions()
    {
        if (FooterAction is not { } action)
            return;
        var buttons = new[] { "PrimaryButton", "SecondaryButton", "CloseButton" }
            .Select(GetTemplateChild).OfType<ActionButton>().Where(button => button.Text.Length > 0);
        ActionButton.Align(buttons.Prepend(action));
    }

    private void ShowFooter()
    {
        if (GetTemplateChild("FooterHost") is UIElement host)
            host.Visibility = Footer is null ? Visibility.Collapsed : Visibility.Visible;
        PlaceButtons();
    }

    // The first column and EndColumn share the space the buttons leave free;
    // EndColumn takes half of it only to centre the buttons. A gap of less
    // than a third of the row reads as leftover rather than as a deliberate
    // division, so buttons that fill more than two thirds are centred.
    private void PlaceButtons()
    {
        if (
            GetTemplateChild("CommandSpace") is not Grid row
            || GetTemplateChild("EndColumn") is not ColumnDefinition end
        )
            return;
        var free = row.ColumnDefinitions[0].ActualWidth + end.ActualWidth;
        var width = row.ActualWidth - row.Padding.Left - row.Padding.Right;
        end.Width =
            Footer is null && free < width / 3
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
    }

    // The template cannot reach Lucide.Font. An empty icon still takes the
    // column spacing, and an empty title row still takes its margin, which
    // pushes down content that draws its own heading.
    private void ShowTitle()
    {
        if (GetTemplateChild("TitleIcon") is FontIcon icon)
        {
            icon.FontFamily = Lucide.Font;
            icon.Glyph = Glyph;
            icon.Visibility = Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (GetTemplateChild("TitleArea") is UIElement area)
        {
            area.Visibility =
                Title is null or "" && Glyph.Length == 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;
        }
    }

    // Focus starts in the first text field, or on the default button when
    // there is no editable text, so the person can act without first tabbing.
    private void FocusOnOpen()
    {
        if (Content is DependencyObject content && Field(content) is { } field)
        {
            field.Focus(FocusState.Programmatic);
            return;
        }
        var name = DefaultButton switch
        {
            ContentDialogButton.Primary => "PrimaryButton",
            ContentDialogButton.Secondary => "SecondaryButton",
            ContentDialogButton.Close => "CloseButton",
            _ => null,
        };
        if (name is not null && GetTemplateChild(name) is Button { IsEnabled: true } button)
            button.Focus(FocusState.Programmatic);
    }

    // A combo box uses Enter and Escape for its own list.
    private static Control? Field(DependencyObject parent)
    {
        if (
            parent
            is UIElement { Visibility: Visibility.Collapsed }
                or Control { IsEnabled: false }
                or ComboBox
        )
            return null;
        if (parent is AutoSuggestBox { IsTabStop: true } search)
            return search;
        if (parent is TextBox { IsReadOnly: false, AcceptsReturn: false, IsTabStop: true } box)
            return box;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            if (Field(VisualTreeHelper.GetChild(parent, index)) is { } found)
                return found;
        }
        return null;
    }
}
