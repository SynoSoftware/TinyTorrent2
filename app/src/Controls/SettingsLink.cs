using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Controls;

public sealed partial class SettingsLink : Button
{
    private readonly TextBlock _header = new()
    {
        Style = (Style)Application.Current.Resources["TinyTorrentBodyTextStyle"],
    };
    private readonly TextBlock _description = new()
    {
        Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"],
        Visibility = Visibility.Collapsed,
    };
    private string _descriptionText = string.Empty;
    private string _summary = string.Empty;
    private readonly FontIcon _icon = new()
    {
        FontFamily = Segoe.Font,
        FontSize = (double)Application.Current.Resources["TinyTorrentHeaderIconSize"],
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly FontIcon _ending = new()
    {
        FontFamily = Segoe.Font,
        FontSize = (double)Application.Current.Resources["TinyTorrentChevronIconSize"],
        Glyph = Segoe.ChevronRight,
    };

    public string Header
    {
        get => _header.Text;
        set
        {
            _header.Text = value;
            AutomationProperties.SetName(this, value);
        }
    }

    /// <summary>Help text under the header. It wraps, as every setting's description does.</summary>
    public string Description
    {
        get => _descriptionText;
        set
        {
            _descriptionText = value;
            RefreshDescription();
        }
    }

    /// <summary>
    /// A value under the header, shown when there is no description. It stays on one line,
    /// because a value that changes must not change the link's height.
    /// </summary>
    public string Summary
    {
        get => _summary;
        set
        {
            _summary = value;
            RefreshDescription();
        }
    }

    private void RefreshDescription()
    {
        var wraps = _descriptionText.Length > 0;
        var text = wraps ? _descriptionText : _summary;
        _description.Text = text;
        _description.TextWrapping = wraps ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _description.TextTrimming = wraps ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        _description.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetHelpText(this, text);
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
        Trimming.SetShowsToolTip(_header, true);
        Trimming.SetShowsToolTip(_description, true);
        // The icon column and gap of a SettingsSection header, so a link's
        // text lines up with the cards around it.
        var content = new Grid { ColumnSpacing = 24 };
        content.ColumnDefinitions.Add(new() { Width = new(20) });
        content.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 4, Children = { _header, _description } };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_ending, 2);
        AutomationProperties.SetAccessibilityView(
            _icon,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw
        );
        AutomationProperties.SetAccessibilityView(
            _ending,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw
        );
        content.Children.Add(_icon);
        content.Children.Add(text);
        content.Children.Add(_ending);
        Content = content;
    }
}
