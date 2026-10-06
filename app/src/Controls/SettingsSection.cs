using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Controls;

// One LabForms settings section: a card headed by a large icon, a title and a
// description, holding its SettingsRow content. The implicit style in
// PreferencesForm.xaml draws it.
public sealed partial class SettingsSection : ContentControl
{
    public static readonly DependencyProperty GlyphProperty = Text(nameof(Glyph));
    public static readonly DependencyProperty HeaderProperty = Text(nameof(Header));
    public static readonly DependencyProperty DescriptionProperty = Text(nameof(Description));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    private static DependencyProperty Text(string name) => DependencyProperty.Register(name, typeof(string),
        typeof(SettingsSection), new PropertyMetadata(string.Empty, (section, _) => ((SettingsSection)section).Update()));

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Update();
    }

    // An empty TextBlock still takes a line.
    private void Update()
    {
        if (GetTemplateChild("DescriptionText") is UIElement text)
            text.Visibility = Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
