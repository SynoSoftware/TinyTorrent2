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
    private bool _coveragePending;
    private bool _readingStatistics;

    private bool ShowsCoverage => IsLoaded && Main.Navigation.Current == WindowPage.Settings
        && _category == PluginsCategory && Plugins is not null;

    private void OnCoverage(object? sender, EventArgs args) => RefreshCoverage();

    private async void RefreshCoverage()
    {
        if (!ShowsCoverage)
            return;
        _coveragePending = true;
        if (_readingCoverage)
            return;
        _readingCoverage = true;
        try
        {
            while (_coveragePending && ShowsCoverage)
            {
                _coveragePending = false;
                var coverage = await Main.Library.ReadCoverage();
                if (!ShowsCoverage)
                    return;
                if (_coveragePending)
                    continue;
                VideoCoverage.Text = coverage is null
                    ? "—"
                    : Model.Text.Format("plugins", "coverage", coverage.Informed, coverage.Total);
                if (SubtitlesContent.Content is SubtitleSettings subtitles)
                    subtitles.Coverage = coverage is null || Main.Subtitles is null
                        ? "—"
                        : Model.Text.Format("subtitles", "coverage", Main.Subtitles.WithSubtitles, coverage.Total);
                if (DatabaseSummary.Visibility == Visibility.Visible)
                {
                    var size = await Main.ReadDatabaseSize();
                    if (ShowsCoverage && !_coveragePending && DatabaseSummary.Visibility == Visibility.Visible)
                        DatabaseSize.Text = size is null ? "—" : Model.Text.Bytes(size.Value);
                }
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
