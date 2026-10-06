using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Syno.TinyTorrent.Controls;

// A ContentDialog whose buttons keep their natural width: at the right with
// Footer at their left, or centred without one. The implicit style in App.xaml
// draws it, because ContentDialog's own template always splits the row into
// equal buttons.
public sealed partial class Dialog : ContentDialog
{
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(nameof(Footer), typeof(object),
        typeof(Dialog), new PropertyMetadata(null, (dialog, _) => ((Dialog)dialog).ShowFooter()));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string),
        typeof(Dialog), new PropertyMetadata(string.Empty, (dialog, _) => ((Dialog)dialog).ShowGlyph()));

    // Each button's Lucide icon and the tooltip that says what its one word does
    // not. Every dialog's Close button is Cancel, so it defaults to Cancel's icon.
    public static readonly DependencyProperty PrimaryGlyphProperty = Text(nameof(PrimaryGlyph), string.Empty);
    public static readonly DependencyProperty SecondaryGlyphProperty = Text(nameof(SecondaryGlyph), string.Empty);
    public static readonly DependencyProperty CloseGlyphProperty = Text(nameof(CloseGlyph), Lucide.X);
    public static readonly DependencyProperty PrimaryToolTipProperty = Text(nameof(PrimaryToolTip), null);
    public static readonly DependencyProperty SecondaryToolTipProperty = Text(nameof(SecondaryToolTip), null);
    public static readonly DependencyProperty CloseToolTipProperty = Text(nameof(CloseToolTip), null);

    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    // The Lucide icon before the title.
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string PrimaryGlyph { get => (string)GetValue(PrimaryGlyphProperty); set => SetValue(PrimaryGlyphProperty, value); }
    public string SecondaryGlyph { get => (string)GetValue(SecondaryGlyphProperty); set => SetValue(SecondaryGlyphProperty, value); }
    public string CloseGlyph { get => (string)GetValue(CloseGlyphProperty); set => SetValue(CloseGlyphProperty, value); }
    public string? PrimaryToolTip { get => (string?)GetValue(PrimaryToolTipProperty); set => SetValue(PrimaryToolTipProperty, value); }
    public string? SecondaryToolTip { get => (string?)GetValue(SecondaryToolTipProperty); set => SetValue(SecondaryToolTipProperty, value); }
    public string? CloseToolTip { get => (string?)GetValue(CloseToolTipProperty); set => SetValue(CloseToolTipProperty, value); }

    private static DependencyProperty Text(string name, string? value) =>
        DependencyProperty.Register(name, typeof(string), typeof(Dialog), new PropertyMetadata(value));

    public Dialog()
    {
        Opened += (_, _) => FocusOnOpen();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ShowGlyph();
        ShowFooter();
    }

    // Buttons sit at the right of a Footer. Without one, the empty footer
    // column and EndColumn share the free space, so the buttons are centred.
    private void ShowFooter()
    {
        if (GetTemplateChild("FooterHost") is UIElement host) host.Visibility = Footer is null ? Visibility.Collapsed : Visibility.Visible;
        if (GetTemplateChild("EndColumn") is ColumnDefinition end) end.Width = Footer is null ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    // The template cannot reach Lucide.Font, and an empty icon still takes the
    // column spacing.
    private void ShowGlyph()
    {
        if (GetTemplateChild("TitleIcon") is not FontIcon icon) return;
        icon.FontFamily = Lucide.Font;
        icon.Glyph = Glyph;
        icon.Visibility = Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // The person can type and press Escape at once. Focus goes to the first
    // text box that leaves Enter and Escape to the dialog; without one, to the
    // default button, so Enter runs the button the person sees focused.
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
            _ => null
        };
        if (name is not null && GetTemplateChild(name) is Button { IsEnabled: true } button) button.Focus(FocusState.Programmatic);
    }

    // A combo box and a search box use Enter and Escape for their own lists.
    private static TextBox? Field(DependencyObject parent)
    {
        if (parent is UIElement { Visibility: Visibility.Collapsed } or Control { IsEnabled: false } or ComboBox or AutoSuggestBox) return null;
        if (parent is TextBox { IsReadOnly: false, AcceptsReturn: false, IsTabStop: true } box) return box;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            if (Field(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
        }
        return null;
    }
}
