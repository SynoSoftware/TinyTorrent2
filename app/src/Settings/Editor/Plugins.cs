using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
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
    private DiskState _gozler = new(DiskStatus.Off);
    private IReadOnlyList<string> _hidden = [];
    private DiskSource? _hiddenSource;

    public bool DiscoversMedia => _owner.Disk is { State.Status: not DiskStatus.Off };

    // The option is saved in the Library database.
    public bool CanDiscoverMedia => _owner.Disk is not null;
    public string GozlerState => _gozler.Describe(Text);
    public string MediaSummary =>
        DiscoversMedia ? Text.Format("discovery", "summary", OnText, GozlerState) : OffText;
    public bool ShowsGozlerInstall => MainViewModel.Recovery(_gozler) == DiskRecovery.Install;
    public bool ShowsMediaRetry =>
        DiscoversMedia && MainViewModel.Recovery(_gozler) == DiskRecovery.Retry;
    public IReadOnlyList<string> HiddenFolders => _hidden;
    public string HiddenSummary =>
        _hidden.Count == 0
            ? Text.Get("discovery", "hidden_none")
            : Text.FormatCount("discovery", "hidden_count", _hidden.Count);

    public string VideoProvider => _owner.Library.Option?.Name ?? Strings.NoValue;
    public string VideoSummary => Text.Format(
        "plugins",
        "video_summary",
        VideoProvider,
        _owner.Library.IsAutomaticDownloadEnabled ? OnText : OffText
    );
    public string ProtectionSummary => Text.FormatCount("plugins", "search_limit", _owner.LookupLimit);

    public string VideoCoverage => _coverage is { } coverage
        ? Text.Format("plugins", "coverage", coverage.Informed, coverage.Total)
        : Strings.NoValue;
    public string SubtitleCoverage => _coverage is { } coverage && _owner.Subtitles is { } acquisition
        ? Text.Format("subtitles", "coverage", acquisition.WithSubtitles, coverage.Total)
        : Strings.NoValue;
    public string DatabaseSize => _databaseSize is { } size ? Text.Bytes(size) : Strings.NoValue;
    public string LibraryFiles =>
        _statistics?.Files.ToString("N0", CultureInfo.CurrentCulture) ?? Strings.NoValue;
    public string StoredInformation => _statistics is { } statistics
        ? Text.Format(
            "plugins",
            "information_counts",
            Text.FormatCount("plugins", "movie_count", statistics.Movies),
            Text.FormatCount("plugins", "series_count", statistics.Series),
            Text.FormatCount("plugins", "episode_count", statistics.Episodes)
        )
        : Strings.NoValue;
    public string StoredSubtitles => _statistics is { } statistics
        ? Text.Format(
            "plugins",
            "subtitle_counts",
            Text.FormatCount("plugins", "file_count", statistics.Subtitles),
            Text.FormatCount("plugins", "language_count", statistics.Languages)
        )
        : Strings.NoValue;

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
                or nameof(_owner.Library.IsAutomaticDownloadEnabled)
                or "")
            {
                // The overview follows every change of the editor.
                Changed(nameof(VideoSummary));
            }
        };
    }

    private void ConfirmLookupLimit() =>
        LookupLimit.Confirm(JsonSerializer.SerializeToElement(_owner.LookupLimit));

    private void OnOwner(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.LookupLimit) or "")
        {
            ConfirmLookupLimit();
            Changed(nameof(ProtectionSummary));
        }
        if (args.PropertyName is nameof(MainViewModel.Subtitles) or "")
            Changed(nameof(SubtitleCoverage));
        if (args.PropertyName is nameof(MainViewModel.Disk) or "" && _pluginsVisible)
        {
            ShowMedia();
            if (!ReferenceEquals(_hiddenSource, _owner.Disk))
                _ = ReadHidden();
        }
    }

    // While discovery is off, Settings shows whether gozler is installed, so
    // the person sees what switching it on needs.
    private void ShowMedia()
    {
        _gozler =
            _owner.Disk?.State is { Status: not DiskStatus.Off } state ? state
            : Gozler.Locate() switch
            {
                GozlerProgram.NotInstalled => new(DiskStatus.NotInstalled),
                GozlerProgram.ProgramMissing => new(DiskStatus.ProgramMissing),
                _ => new(DiskStatus.Current),
            };
        Changed(
            nameof(DiscoversMedia),
            nameof(CanDiscoverMedia),
            nameof(GozlerState),
            nameof(MediaSummary),
            nameof(ShowsGozlerInstall),
            nameof(ShowsMediaRetry)
        );
    }

    /// <summary>
    /// The switch shows the saved choice again afterwards, because the
    /// install prompt leaves the option off until gozler is installed.
    /// </summary>
    internal async Task DiscoverMedia(bool on)
    {
        await _owner.SwitchDiscovery(on);
        ShowMedia();
    }

    // Its files return as new entries at the next refresh.
    internal async Task Unhide(string folder)
    {
        if (_owner.Disk is not { } disk)
            return;
        try
        {
            await disk.Unhide(folder);
        }
        catch (Exception error)
        {
            _owner.Notifications.Report(error, "failed_settings", () => folder, null);
        }
        await ReadHidden();
    }

    private async Task ReadHidden()
    {
        var disk = _owner.Disk;
        _hiddenSource = disk;
        IReadOnlyList<string> hidden = [];
        if (disk is not null)
        {
            try
            {
                hidden = [.. (await disk.ReadHiddenFolders()).Order(StringComparer.CurrentCultureIgnoreCase)];
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine($"Hidden folders not read: {error.Message}");
                return;
            }
        }
        if (!ReferenceEquals(disk, _owner.Disk))
            return;
        _hidden = hidden;
        Changed(nameof(HiddenFolders), nameof(HiddenSummary));
    }

    /// <summary>
    /// Reads coverage, and the database size when <paramref name="database"/>
    /// is shown, while Plugins is shown, until <see cref="HidePlugins"/>.
    /// </summary>
    public Task ShowPlugins(bool database)
    {
        _pluginsVisible = true;
        _databaseVisible = database;
        ShowMedia();
        _ = ReadHidden();
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
