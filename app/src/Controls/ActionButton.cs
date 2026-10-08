using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Controls;

// A labelled button: the Lucide Glyph before the one-word Text, as the
// interface contract's button ruling requires of every labelled button.
public sealed partial class ActionButton : Button
{
    public static readonly DependencyProperty GlyphProperty = Register(nameof(Glyph));
    public static readonly DependencyProperty TextProperty = Register(nameof(Text));

    private readonly FontIcon _icon = new()
    {
        FontFamily = Lucide.Font,
        FontSize = 16,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _text = new()
    {
        TextWrapping = TextWrapping.NoWrap,
        TextLineBounds = TextLineBounds.Tight,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ActionButton()
    {
        AutomationProperties.SetAccessibilityView(
            _icon,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw
        );
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _icon, _text },
        };
    }

    private static DependencyProperty Register(string name) =>
        DependencyProperty.Register(
            name,
            typeof(string),
            typeof(ActionButton),
            new PropertyMetadata(string.Empty, (button, _) => ((ActionButton)button).Update())
        );

    // A button whose content is not a string has no accessible name of its own,
    // so the word names it unless the caller has given a fuller name.
    private void Update()
    {
        var name = AutomationProperties.GetName(this);
        if (name.Length == 0 || name == _text.Text)
            AutomationProperties.SetName(this, Text);
        _icon.Glyph = Glyph;
        _text.Text = Text;
    }
}
