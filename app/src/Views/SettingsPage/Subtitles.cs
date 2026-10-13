using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Models;
using SubtitleSettings = Syno.TinyTorrent.Subtitles.Settings;

namespace Syno.TinyTorrent.Views;

public sealed partial class SettingsPage
{
    private bool _readingCoverage;
    private bool _readingStatistics;

    private async void RefreshCoverage()
    {
        if (!IsLoaded || Main.Navigation.Current != WindowPage.Settings || _category != PluginsCategory || Plugins is null || _readingCoverage)
            return;
        _readingCoverage = true;
        try
        {
            var coverage = await Main.Library.ReadCoverage();
            VideoCoverage.Text = coverage is null
                ? "—"
                : Model.Text.Format("plugins", "coverage", coverage.Informed, coverage.Total);
            if (SubtitlesContent.Content is SubtitleSettings subtitles)
                subtitles.Coverage = coverage is null || Main.Subtitles is null
                    ? "—"
                    : Model.Text.Format("subtitles", "coverage", Main.Subtitles.WithSubtitles, coverage.Total);
            if (Main.Page == WindowPage.Settings && _category == PluginsCategory
                && DatabaseSummary.Visibility == Visibility.Visible)
            {
                var size = await Main.ReadDatabaseSize();
                DatabaseSize.Text = size is null ? "—" : Model.Text.Bytes(size.Value);
            }
        }
        finally
        {
            _readingCoverage = false;
        }
    }

    private async void OnDatabaseExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (_readingStatistics)
            return;
        _readingStatistics = true;
        try
        {
            if (await Main.ReadStatistics() is not { } statistics)
            {
                LibraryFiles.Text = StoredInformation.Text = StoredSubtitles.Text = "—";
                return;
            }
            LibraryFiles.Text = statistics.Files.ToString("N0", CultureInfo.CurrentCulture);
            StoredInformation.Text = Model.Text.Format(
                "plugins",
                "information_counts",
                Model.Text.FormatCount("plugins", "movie_count", statistics.Movies),
                Model.Text.FormatCount("plugins", "series_count", statistics.Series),
                Model.Text.FormatCount("plugins", "episode_count", statistics.Episodes)
            );
            StoredSubtitles.Text = Model.Text.Format(
                "plugins",
                "subtitle_counts",
                Model.Text.FormatCount("plugins", "file_count", statistics.Subtitles),
                Model.Text.FormatCount("plugins", "language_count", statistics.Languages)
            );
        }
        finally
        {
            _readingStatistics = false;
        }
    }

    private void RefreshSubtitles()
    {
        RefreshCoverage();
        var visible = IsLoaded && Main.Navigation.Current == WindowPage.Settings;
        _automatic?.Attach(visible && _category is null ? Main.Subtitles : null);
        if (SubtitlesContent?.Content is SubtitleSettings content)
        {
            content.SetVisible(visible && _category == PluginsCategory);
            content.Attach(Main.Subtitles);
        }
    }

}
