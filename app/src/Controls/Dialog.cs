using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Views;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Controls;

// A ContentDialog with natural button widths, unless a Check footer's button
// joins them. Buttons sit at the right with Footer at their left, or centred
// in a narrow dialog without one. The implicit style in App.xaml draws it,
// because ContentDialog's own template always splits the row into equal
// buttons.
public sealed partial class Dialog : ContentDialog
{
    private readonly UISettings _display = new();
    private DialogFooter? _footer;
    private FrameworkElement? _footerElement;
    private CheckBox? _optOut;
    private string _optOutText = string.Empty;
    private long _checkText;

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

    // The opt-out is the footer for the dialog's whole life, so a dialog that
    // offers it shows its other facts in its content.
    public DialogFooter? Footer
    {
        get => _footer;
        set
        {
            if (_footer == value)
                return;
            if (_footer is DialogFooter.OptOut)
                throw new InvalidOperationException("The opt-out check box is the whole footer.");
            if (_footer is DialogFooter.Check previous)
                previous.Status.Check.UnregisterPropertyChangedCallback(
                    ActionButton.TextProperty,
                    _checkText
                );
            _footer = value;
            if (value is DialogFooter.OptOut optOut)
                _optOut = NewOptOut(optOut.Preference);
            _footerElement = value switch
            {
                DialogFooter.OptOut => _optOut,
                DialogFooter.Detail detail => new TextBlock
                {
                    Text = detail.Text,
                    Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                DialogFooter.Check check => check.Status,
                _ => null,
            };
            // The check button belongs to content that can outlive the
            // dialog, so the dialog listens to it only while it is the footer.
            if (value is DialogFooter.Check current)
                _checkText = current.Status.Check.RegisterPropertyChangedCallback(
                    ActionButton.TextProperty,
                    (_, _) => AlignActions()
                );
            ShowFooter();
            AlignActions();
        }
    }

    // The opt-out's label. ShowDialog sets it with the dialog's other shared
    // text, before or after Footer.
    public string OptOutText
    {
        get => _optOutText;
        set
        {
            _optOutText = value;
            if (_optOut is not null)
                _optOut.Content = value;
        }
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
        Opened += (_, _) =>
        {
            AlignActions();
            FocusOnOpen();
        };
        Loaded += (_, _) => _display.TextScaleFactorChanged += OnTextScale;
        Unloaded += (_, _) => _display.TextScaleFactorChanged -= OnTextScale;
        RegisterPropertyChangedCallback(TitleProperty, (_, _) => ShowTitle());
    }

    // Lifts ContentDialog's maximum size, for content that sizes itself to the
    // window.
    public void SizeToContent()
    {
        Resources["ContentDialogMaxWidth"] = double.PositiveInfinity;
        Resources["ContentDialogMaxHeight"] = double.PositiveInfinity;
    }

    // The opt-out is enabled with the primary button, because only a submit
    // saves it.
    private CheckBox NewOptOut(DialogPreference preference)
    {
        var optOut = new CheckBox { Content = _optOutText };
        AutomationProperties.SetAutomationId(optOut, "NeverShow");
        optOut.SetBinding(
            ToggleButton.IsCheckedProperty,
            new Binding
            {
                Source = preference,
                Path = new PropertyPath(nameof(DialogPreference.NeverShow)),
                Mode = BindingMode.TwoWay,
            }
        );
        optOut.SetBinding(
            IsEnabledProperty,
            new Binding
            {
                Source = this,
                Path = new PropertyPath(nameof(IsPrimaryButtonEnabled)),
                Mode = BindingMode.OneWay,
            }
        );
        return optOut;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        foreach (
            var name in new[] { "CommandSpace", "PrimaryButton", "SecondaryButton", "CloseButton" }
        )
        {
            if (GetTemplateChild(name) is not FrameworkElement part)
                continue;
            part.SizeChanged += (_, _) => PlaceButtons();
            if (part is ActionButton button)
                button.RegisterPropertyChangedCallback(
                    ActionButton.TextProperty,
                    (_, _) => AlignActions()
                );
        }
        ShowTitle();
        ShowFooter();
        AlignActions();
    }

    private void OnTextScale(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(AlignActions);

    private void AlignActions()
    {
        if (Footer is not DialogFooter.Check check)
            return;
        var action = check.Status.Check;
        var buttons = new[] { "PrimaryButton", "SecondaryButton", "CloseButton" }
            .Select(GetTemplateChild)
            .OfType<ActionButton>()
            .Where(button => button.Text.Length > 0);
        ActionButton.Align(buttons.Prepend(action));
    }

    private void ShowFooter()
    {
        if (GetTemplateChild("FooterHost") is ContentControl host)
        {
            host.Content = _footerElement;
            host.Visibility = _footerElement is null ? Visibility.Collapsed : Visibility.Visible;
        }
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
