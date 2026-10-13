using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using SubtitleSettings = Syno.TinyTorrent.Subtitles.Settings;

namespace Syno.TinyTorrent.Views;

public sealed partial class SettingsPage
{
    private readonly List<(Button Button, TextBlock Title, TextBlock Summary, string Key, Func<string> Value)> _links = [];

    private void InitializeHome(SubtitleSettings subtitles)
    {
        _homeIntegration.Section.Style = (Style)Resources["SettingsHomeGroupStyle"];
        AddLink(HomeDownloads, "files", () => Main.ShowsFreeSpace
            ? Model.Destination.ConfirmedText + " · " + Main.FreeSpaceStatus
            : Model.Destination.ConfirmedText,
            new(SettingsCategory.Transfers, "download_locations"), Syno.Lucide.Folder);
        AddLink(HomeDownloads, "limits", () => Main.LimitsSummary,
            new(SettingsCategory.Limits, "limit_mode"), Syno.Lucide.SlidersHorizontal);
        AddLink(HomeDownloads, "seeding", () => Model.SeedingSummary,
            new(SettingsCategory.Transfers, "seeding"), Syno.Lucide.Upload);
        AddLink(HomeAppearance, "home_language", () => Model.Text.LanguageName(Model.Text.Language),
            new(SettingsCategory.Appearance, "language"), Syno.Lucide.Languages);
        AddLink(HomeLibrary, "video_information", () => VideoProvider,
            new(SettingsCategory.Plugins, "video_information"), Syno.Lucide.Film);
        var automatic = subtitles.CreateAutomaticRow();
        automatic.Margin = new Thickness(16, 0, 16, 0);
        HomeLibrary.Children.Add(automatic);
        AddLink(HomeLibrary, "subtitles", () => Main.Subtitles is { } acquisition
            ? acquisition.SupplierId + " · " + string.Join(", ", acquisition.EffectiveLanguages.Select(Model.Text.LanguageName)) : "—",
            new(SettingsCategory.Plugins, "subtitle_supplier"), Syno.Lucide.Captions);
        AddLink(HomeNetwork, "network_interface", () => Model.SelectedAdapter?.Text ?? "—",
            new(SettingsCategory.Network, "network_interface"), Syno.Lucide.Network);
        AddLink(HomeNetwork, "proxy_server", () => Model.Proxy.Summary,
            new(SettingsCategory.Network, "proxy"), Syno.Lucide.Globe);
        AddLink(HomeNetwork, "encryption", () => Model.Text.Get("settings", "encryption_" + Model.Encryption.ConfirmedText),
            new(SettingsCategory.Network, "connection_encryption"), Syno.Lucide.Shield);
        AddLink(HomeSchedule, "weekly_schedule", () => Main.ScheduleSummary,
            new(SettingsCategory.Schedule, "weekly_schedule"), Syno.Lucide.CalendarClock);
        AddLink(RelatedLinks, "schedule", () => Main.ScheduleSummary,
            new(SettingsCategory.Schedule, "weekly_schedule"), Syno.Lucide.CalendarClock);
        AddLink(RelatedLinks, "queue", () => Model.QueueSummary,
            new(SettingsCategory.Transfers, "queue"), Syno.Lucide.ListOrdered);
        var corners = (CornerRadius)Application.Current.Resources["ControlCornerRadius"];
        foreach (var panel in new[] { HomeDownloads, HomeAppearance, HomeLibrary, HomeNetwork, HomeSchedule })
        {
            var last = (Button)panel.Children[^1];
            last.BorderThickness = new Thickness(0);
            last.CornerRadius = new CornerRadius(0, 0, corners.BottomRight, corners.BottomLeft);
        }
    }

    private void AddLink(StackPanel panel, string key, Func<string> value, SettingTarget target, string glyph)
    {
        var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        var summary = new TextBlock
        {
            Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var text = new StackPanel { Spacing = 4, Children = { title, summary } };
        var icon = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = glyph, FontSize = 20 };
        var chevron = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.ChevronRight, FontSize = 12 };
        var content = new Grid { ColumnSpacing = 16 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(chevron, 2);
        content.Children.Add(icon);
        content.Children.Add(text);
        content.Children.Add(chevron);
        var button = new Button
        {
            Style = panel == RelatedLinks
                ? (Style)Application.Current.Resources["TinyTorrentSettingsLinkStyle"]
                : (Style)Resources["SettingsHomeLinkStyle"],
            Content = content,
        };
        button.Click += async (_, _) => await Open(target);
        panel.Children.Add(button);
        _links.Add((button, title, summary, key, value));
    }

    private void RefreshHome()
    {
        foreach (var (button, title, summary, key, value) in _links)
        {
            title.Text = Model.Text.Get("settings", key);
            summary.Text = value();
            ToolTipService.SetToolTip(title, title.Text);
            ToolTipService.SetToolTip(summary, summary.Text);
            AutomationProperties.SetName(button, title.Text);
            AutomationProperties.SetHelpText(button, summary.Text);
        }
        RefreshHeader();
    }

    private void OnHomeSize(object sender, SizeChangedEventArgs args) => UpdateHomeLayout();

    private void UpdateHomeLayout()
    {
        var stacked = HomeGrid.ActualWidth < 900 * _display.TextScaleFactor;
        HomeGrid.ColumnSpacing = stacked ? 0 : 8;
        HomeGrid.RowSpacing = stacked ? 8 : 0;
        HomeGrid.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(HomeRight, stacked ? 0 : 1);
        Grid.SetRow(HomeRight, stacked ? 1 : 0);
    }
}
