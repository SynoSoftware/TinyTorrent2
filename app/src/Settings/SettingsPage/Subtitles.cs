using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Library;
using Syno.TinyTorrent.Shared.Storage;
using Syno.TinyTorrent.Shell;
using SubtitleSettings = Syno.TinyTorrent.Subtitles.Settings;

namespace Syno.TinyTorrent.Settings;

public sealed partial class SettingsPage
{
    private bool _readingCoverage;
    private bool _coveragePending;
    private bool _readingStatistics;
    private Coverage? _coverage;
    private Statistics? _statistics;
    private long? _databaseSize;

    private bool ShowsCoverage =>
        IsLoaded
        && Main.Navigation.Current == WindowPage.Settings
        && _category == PluginsCategory
        && Plugins is not null;

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
                _coverage = coverage;
                RefreshStatistics();
                if (DatabaseSummary.Visibility == Visibility.Visible)
                {
                    var size = await Main.ReadDatabaseSize();
                    if (
                        ShowsCoverage
                        && !_coveragePending
                        && DatabaseSummary.Visibility == Visibility.Visible
                    )
                    {
                        _databaseSize = size;
                        RefreshStatistics();
                    }
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
            _statistics = await Main.ReadStatistics();
            RefreshStatistics();
        }
        finally
        {
            _readingStatistics = false;
        }
    }

    private void RefreshStatistics()
    {
        if (Plugins is null)
            return;
        VideoCoverage.Text = _coverage is { } coverage
            ? Model.Text.Format("plugins", "coverage", coverage.Informed, coverage.Total)
            : "—";
        if (SubtitlesContent.Content is SubtitleSettings subtitles)
            subtitles.Coverage =
                _coverage is { } current && Main.Subtitles is { } acquisition
                    ? Model.Text.Format(
                        "subtitles",
                        "coverage",
                        acquisition.WithSubtitles,
                        current.Total
                    )
                    : "—";
        DatabaseSize.Text = _databaseSize is { } size ? Model.Text.Bytes(size) : "—";
        if (_statistics is not { } statistics)
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
