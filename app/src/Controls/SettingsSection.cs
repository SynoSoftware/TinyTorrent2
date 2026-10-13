using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Controls;

// App.xaml supplies the shared template for each settings group.
public sealed partial class SettingsSection : ContentControl
{
    public bool IsAdvanced { get; set; }
    public static readonly DependencyProperty GlyphProperty = Text(nameof(Glyph));
    public static readonly DependencyProperty HeaderProperty = Text(nameof(Header));
    public static readonly DependencyProperty DescriptionProperty = Text(nameof(Description));
    public static readonly DependencyProperty SummaryProperty = Text(nameof(Summary));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(SettingsSection),
        new PropertyMetadata(false, (section, _) => ((SettingsSection)section).RefreshSummary())
    );
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string Summary
    {
        get => (string)GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    internal FrameworkElement Surface =>
        GetTemplateChild("GroupCard") as FrameworkElement
        ?? GetTemplateChild("SectionExpander") as FrameworkElement
        ?? this;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        if (GetTemplateChild("HeaderLayout") is Grid header)
        {
            header.ColumnDefinitions[0].Width = new GridLength(Glyph.Length == 0 ? 0 : 20);
            header.ColumnSpacing = Glyph.Length == 0 ? 0 : 24;
        }
        if (GetTemplateChild("SummaryLabel") is TextBlock label)
        {
            label.Text = Description.Length > 0 ? Description : Summary;
            label.Visibility = label.Text.Length > 0 && (!IsExpanded || Description.Length > 0)
                ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(label, label.Text);
        }
    }

    private static DependencyProperty Text(string name) =>
        DependencyProperty.Register(
            name,
            typeof(string),
            typeof(SettingsSection),
            new PropertyMetadata(string.Empty, (section, _) => ((SettingsSection)section).RefreshSummary())
        );
}
