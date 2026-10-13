using System.ComponentModel;
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
        if (!IsLoaded || Main.Page != WindowPage.Settings || _category != PluginsCategory || _readingCoverage)
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
            var statistics = await Main.ReadStatistics();
            ReusableSpace.Text = statistics is null ? "—" : Model.Text.Bytes(statistics.Reusable);
            StoredMovies.Text = Number(statistics?.Movies);
            StoredSeries.Text = Number(statistics?.Series);
            StoredEpisodes.Text = Number(statistics?.Episodes);
            LibraryFiles.Text = Number(statistics?.Files);
            CachedResponses.Text = Number(statistics?.Responses);
            TrackedSubtitles.Text = Number(statistics?.Subtitles);
            CreatedSubtitles.Text = Number(statistics?.Created);
            FoundSubtitles.Text = Number(statistics?.Found);
            SubtitleLanguages.Text = Number(statistics?.Languages);
        }
        finally
        {
            _readingStatistics = false;
        }

        static string Number(long? value) =>
            value?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
    }

    private void OnSubtitleMain(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.CurrentStatus) or nameof(MainViewModel.Page) or "")
            RefreshCoverage();
        if (
            args.PropertyName is nameof(MainViewModel.Subtitles) or ""
            && SubtitlesContent.Content is SubtitleSettings subtitles
        )
            subtitles.Attach(Main.Subtitles);
        if (args.PropertyName is nameof(MainViewModel.CurrentStatus) or nameof(MainViewModel.Page)
            or nameof(MainViewModel.Subtitles) or nameof(MainViewModel.FreeSpaceStatus)
            or nameof(MainViewModel.ShowsFreeSpace) or "")
            RefreshHome();
    }

    private void RefreshSubtitles()
    {
        RefreshCoverage();
        if (IsLoaded && SubtitlesContent.Content is SubtitleSettings content)
            content.Attach(Main.Subtitles);
    }

}
