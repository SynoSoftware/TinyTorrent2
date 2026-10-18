using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shell;
using SubtitleSettings = Syno.TinyTorrent.Subtitles.Settings;

namespace Syno.TinyTorrent.Settings;

public sealed partial class SettingsPage
{
    private async void OnDatabaseExpanding(Expander sender, ExpanderExpandingEventArgs args) =>
        await Model.RefreshStatistics();

    private void RefreshSubtitleCoverage()
    {
        if (SubtitlesContent?.Content is SubtitleSettings subtitles)
            subtitles.Coverage = Model.SubtitleCoverage;
    }

    private void RefreshSubtitles()
    {
        RefreshSubtitleCoverage();
        var visible = IsLoaded && Main.Navigation.Current == WindowPage.Settings;
        if (visible && _category == PluginsCategory)
            _ = Model.ShowPlugins(database: DatabaseSummary?.Visibility == Visibility.Visible);
        else
            Model.HidePlugins();
        _automatic?.Attach(visible && _category is null ? Main.Subtitles : null);
        if (SubtitlesContent?.Content is SubtitleSettings content)
        {
            content.SetVisible(visible && _category == PluginsCategory);
            content.Attach(Main.Subtitles);
        }
    }
}
