using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Library;
using Syno.TinyTorrent.Shared.Storage;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Settings;

public sealed partial class Editor
{
    private Coverage? _coverage;
    private Statistics? _statistics;
    private long? _databaseSize;
    private Task _coverageRead = Task.CompletedTask;
    private Task _statisticsRead = Task.CompletedTask;
    private bool _coveragePending;
    private bool _sizePending;
    private bool _pluginsVisible;
    private bool _databaseVisible;

    public string VideoProvider => _owner.Library.Option?.Name ?? "—";

    public string VideoCoverage => _coverage is { } coverage
        ? Text.Format("plugins", "coverage", coverage.Informed, coverage.Total)
        : "—";
    public string SubtitleCoverage => _coverage is { } coverage && _owner.Subtitles is { } acquisition
        ? Text.Format("subtitles", "coverage", acquisition.WithSubtitles, coverage.Total)
        : "—";
    public string DatabaseSize => _databaseSize is { } size ? Text.Bytes(size) : "—";
    public string LibraryFiles => _statistics?.Files.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
    public string StoredInformation => _statistics is { } statistics
        ? Text.Format(
            "plugins",
            "information_counts",
            Text.FormatCount("plugins", "movie_count", statistics.Movies),
            Text.FormatCount("plugins", "series_count", statistics.Series),
            Text.FormatCount("plugins", "episode_count", statistics.Episodes)
        )
        : "—";
    public string StoredSubtitles => _statistics is { } statistics
        ? Text.Format(
            "plugins",
            "subtitle_counts",
            Text.FormatCount("plugins", "file_count", statistics.Subtitles),
            Text.FormatCount("plugins", "language_count", statistics.Languages)
        )
        : "—";

    internal void AttachLibrary()
    {
        _owner.Library.CoverageChanged += OnCoverage;
        _owner.Library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(_owner.Library.IsAvailable) or "")
            {
                LookupLimit.Refresh();
                if (_pluginsVisible)
                    _ = RefreshCoverage();
            }
            if (args.PropertyName is nameof(_owner.Library.Status)
                or nameof(_owner.Library.Option)
                or nameof(_owner.Library.Providers)
                or "")
                Overview.Refresh();
        };
    }

    private void ConfirmLookupLimit() =>
        LookupLimit.Confirm(JsonSerializer.SerializeToElement(_owner.LookupLimit));

    private void OnOwner(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.LookupLimit) or "")
            ConfirmLookupLimit();
        if (args.PropertyName is nameof(MainViewModel.Subtitles) or "")
            Changed(nameof(SubtitleCoverage));
    }

    /// <summary>
    /// Reads coverage, and the database size when <paramref name="database"/>
    /// is shown, while Plugins is shown, until <see cref="HidePlugins"/>.
    /// </summary>
    public Task ShowPlugins(bool database)
    {
        _pluginsVisible = true;
        _databaseVisible = database;
        return RefreshCoverage();
    }

    public void HidePlugins()
    {
        _pluginsVisible = false;
        _databaseVisible = false;
    }

    private async void OnCoverage(object? sender, EventArgs args)
    {
        if (_pluginsVisible)
            await RefreshCoverage();
    }

    public Task RefreshCoverage()
    {
        _coveragePending = true;
        _sizePending |= _databaseVisible;
        if (_coverageRead.IsCompleted)
            _coverageRead = ReadCoverage();
        return _coverageRead;
    }

    private async Task ReadCoverage()
    {
        while (_coveragePending)
        {
            _coveragePending = false;
            var includeSize = _sizePending;
            _sizePending = false;
            var coverage = await _owner.Library.ReadCoverage();
            if (_coveragePending)
            {
                _sizePending |= includeSize;
                continue;
            }
            _coverage = coverage;
            Changed(nameof(VideoCoverage), nameof(SubtitleCoverage));
            if (includeSize)
            {
                _databaseSize = await _owner.Features.ReadDatabaseSize();
                Changed(nameof(DatabaseSize));
            }
        }
    }

    public Task RefreshStatistics()
    {
        if (_statisticsRead.IsCompleted)
            _statisticsRead = ReadStatistics();
        return _statisticsRead;
    }

    private async Task ReadStatistics()
    {
        _statistics = await _owner.Features.ReadStatistics();
        Changed(nameof(LibraryFiles), nameof(StoredInformation), nameof(StoredSubtitles));
    }
}
