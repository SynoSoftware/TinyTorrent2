using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Views;

public sealed partial class SettingsPage
{
    private readonly List<(SettingsLink Button, string Key, Func<string> Value)> _links = [];
    private bool _homeQueued;

    private void InitializeHome()
    {
        var integration = new WindowsIntegration(Model);
        integration.Section.Style = (Style)Resources["SettingsHomeGroupStyle"];
        HomeIntegrationContent.Content = integration;
        HomeThemeContent.Content = new ThemePicker(Model);
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
            new(SettingsCategory.Plugins, "video_provider"), Syno.Lucide.Film);
        _automatic = new Syno.TinyTorrent.Subtitles.Automatic(Model.Text, ReportSubtitles, home: true)
        {
            Margin = new Thickness(16, 0, 16, 0),
        };
        HomeLibrary.Children.Add(_automatic);
        AddLink(HomeLibrary, "subtitles", () => Main.Subtitles is { } acquisition
            ? acquisition.SupplierId + " · " + string.Join(", ", acquisition.EffectiveLanguages.Select(Model.Text.LanguageName)) : "—",
            new(SettingsCategory.Plugins, "subtitle_supplier"), Syno.Lucide.Captions);
        AddLink(HomeNetwork, "network_interface", () => Model.SelectedAdapter?.Text ?? "—",
            new(SettingsCategory.Network, "network_interface"), Syno.Lucide.Network);
        AddLink(HomeNetwork, "proxy_server", () => Model.Proxy.Summary,
            new(SettingsCategory.Network, "proxy"), Syno.Lucide.Globe);
        AddLink(HomeNetwork, "encryption", () => Model.EncryptionSummary,
            new(SettingsCategory.Network, "connection_encryption"), Syno.Lucide.Shield);
        AddLink(HomeSchedule, "weekly_schedule", () => Main.ScheduleSummary,
            new(SettingsCategory.Schedule, "weekly_schedule"), Syno.Lucide.CalendarClock);
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
        var button = new SettingsLink
        {
            Style = panel == RelatedLinks
                ? (Style)Application.Current.Resources["TinyTorrentSettingsLinkStyle"]
                : (Style)Resources["SettingsHomeLinkStyle"],
            Glyph = glyph,
        };
        button.Click += async (_, _) => await Open(target);
        panel.Children.Add(button);
        _links.Add((button, key, value));
    }

    private void QueueHome()
    {
        if (_homeQueued || !IsLoaded || Main.Navigation.Current != WindowPage.Settings)
            return;
        _homeQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _homeQueued = false;
            RefreshHome();
        });
    }

    private void RefreshHome()
    {
        if (!IsLoaded || Main.Navigation.Current != WindowPage.Settings)
            return;
        foreach (var (button, key, value) in _links)
        {
            if (_category is null ? button.Parent == RelatedLinks : _category != LimitsCategory || button.Parent != RelatedLinks)
                continue;
            button.Header = Model.Text.Get("settings", key);
            button.Description = value();
        }
        RefreshHeader();
    }

    private void OnHomeSize(object sender, SizeChangedEventArgs args) => UpdateHomeLayout();

    private void UpdateHomeLayout()
    {
        if (HomeGrid is null)
            return;
        var stacked = HomeGrid.ActualWidth < 900 * _display.TextScaleFactor;
        HomeGrid.ColumnSpacing = stacked ? 0 : 8;
        HomeGrid.RowSpacing = stacked ? 8 : 0;
        HomeGrid.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(HomeRight, stacked ? 0 : 1);
        Grid.SetRow(HomeRight, stacked ? 1 : 0);
    }
}
