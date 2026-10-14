using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Controls;

public sealed partial class SettingsLink : Button
{
    private readonly TextBlock _header = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _description = new()
    {
        Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"],
        TextTrimming = TextTrimming.CharacterEllipsis,
        Visibility = Visibility.Collapsed,
    };
    private readonly FontIcon _icon = new()
    {
        FontFamily = Lucide.Font,
        FontSize = (double)Application.Current.Resources["TinyTorrentHeaderIconSize"],
    };
    private readonly FontIcon _ending = new()
    {
        FontFamily = Lucide.Font,
        FontSize = (double)Application.Current.Resources["TinyTorrentChevronIconSize"],
        Glyph = Lucide.ChevronRight,
    };

    public string Header
    {
        get => _header.Text;
        set
        {
            _header.Text = value;
            ToolTipService.SetToolTip(_header, value);
            AutomationProperties.SetName(this, value);
        }
    }

    public string Description
    {
        get => _description.Text;
        set
        {
            _description.Text = value;
            _description.Visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(_description, value);
            AutomationProperties.SetHelpText(this, value);
        }
    }

    public string Glyph
    {
        get => _icon.Glyph;
        set => _icon.Glyph = value;
    }

    public string EndGlyph
    {
        get => _ending.Glyph;
        set => _ending.Glyph = value;
    }

    public SettingsLink()
    {
        Style = (Style)Application.Current.Resources["TinyTorrentSettingsLinkStyle"];
        var content = new Grid { ColumnSpacing = 16 };
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 4, Children = { _header, _description } };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_ending, 2);
        AutomationProperties.SetAccessibilityView(_icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_ending, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        content.Children.Add(_icon);
        content.Children.Add(text);
        content.Children.Add(_ending);
        Content = content;
    }
}
