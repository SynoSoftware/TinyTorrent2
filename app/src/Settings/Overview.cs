using System.ComponentModel;
using System.Globalization;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shell;
using ElementTheme = Microsoft.UI.Xaml.ElementTheme;

namespace Syno.TinyTorrent.Settings;

/// <summary>
/// The Settings header's facts. While Settings shows, it raises
/// <see cref="PropertyChanged"/> whenever a value that the header facts or
/// Home's links read changes, so the page refreshes its links from this one
/// list of changes.
/// </summary>
public sealed class Overview : INotifyPropertyChanged
{
    private readonly Editor _settings;
    private readonly MainViewModel _main;

    internal Overview(Editor settings, MainViewModel main)
    {
        _settings = settings;
        _main = main;
        settings.PropertyChanged += (_, _) => Refresh();
        main.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.CurrentStatus)
                or nameof(MainViewModel.Page)
                or nameof(MainViewModel.Subtitles)
                or nameof(MainViewModel.FreeSpaceStatus)
                or nameof(MainViewModel.ShowsFreeSpace)
                or nameof(MainViewModel.SessionTotals)
                or nameof(MainViewModel.LifetimeTotals)
                or nameof(MainViewModel.Torrents)
                or nameof(MainViewModel.LimitsSummary)
                or nameof(MainViewModel.ScheduleSummary)
                or nameof(MainViewModel.LimitsNow)
                or nameof(MainViewModel.IncomingStatus)
                or "")
                Refresh();
        };
    }

    public SettingsCategory? Category => _main.Navigation.Current == WindowPage.Connection
        ? SettingsCategory.Limits : _main.Navigation.Settings?.Category;
    public string PageTitle => Category is { } category ? _settings.Text.Get(category) : string.Empty;
    // The captions live here rather than in the category states, because a
    // setter does not update its target while its state stays active, so a
    // language change would not reach the shown tab.
    public string Title => Category switch
    {
        null => Caption("home"),
        SettingsCategory.Transfers => Caption("default_destination"),
        SettingsCategory.Limits => Caption("current_limits"),
        SettingsCategory.Schedule => Caption(
            _main.LimitsIndex < 0 ? "weekly_schedule"
                : _main.FollowsSchedule ? "schedule_active" : "schedule_inactive"),
        SettingsCategory.Network => _settings.SelectedAdapter?.Text ?? Strings.NoValue,
        SettingsCategory.Plugins => _settings.Text.Get("library", "title"),
        _ => string.Empty,
    };
    public string FirstLabel => Caption(Category switch
    {
        null => "session_totals",
        SettingsCategory.General => "torrent_files",
        SettingsCategory.Transfers => "queue",
        SettingsCategory.Limits => "standard_caps",
        SettingsCategory.Schedule => "current_limits",
        SettingsCategory.Appearance => "theme",
        SettingsCategory.Network => "proxy_server",
        SettingsCategory.Plugins => "video_information",
        _ => "checking",
    });
    public string SecondLabel => Category switch
    {
        null => Caption("lifetime_totals"),
        SettingsCategory.General => Caption("start_signin"),
        SettingsCategory.Transfers => Caption("seeding"),
        SettingsCategory.Limits => Caption("alternative_caps"),
        SettingsCategory.Schedule => Caption("saved_periods"),
        SettingsCategory.Appearance => Caption("language"),
        SettingsCategory.Network => Caption("encryption"),
        SettingsCategory.Plugins => _settings.Text.Get("subtitles", "supplier"),
        _ => Caption("speed_history"),
    };
    public string Subtitle => Category switch
    {
        SettingsCategory.Transfers => _settings.Destination.ConfirmedText ?? Strings.NoValue,
        SettingsCategory.Limits => _main.FollowsSchedule ? _main.LimitsNow : _main.LimitsHeading,
        // The inactive title and the Current limits fact already say what applies.
        SettingsCategory.Schedule => _main.FollowsSchedule ? _main.ScheduleSummary : string.Empty,
        SettingsCategory.Network => _main.IsConnected ? _main.IncomingStatus : Strings.NoValue,
        SettingsCategory.Plugins => _main.Library.Status,
        null => _settings.Text.Format("settings", "home_activity",
            _main.Torrents.Count(torrent => torrent.Matches(TorrentFilter.Downloading)),
            _main.Torrents.Count(torrent => torrent.Matches(TorrentFilter.Seeding))),
        _ => string.Empty,
    };

    public string FirstValue => Category switch
    {
        SettingsCategory.General => _settings.HasRegistration ? _settings.TorrentApp : Strings.NoValue,
        SettingsCategory.Transfers => _settings.QueueSummary,
        SettingsCategory.Limits => _settings.StandardSummary,
        SettingsCategory.Schedule => _main.LimitsHeading,
        SettingsCategory.Appearance => _settings.Text.Get("settings", Editor.ReadTheme(_settings.Theme.Input) switch
        {
            ElementTheme.Light => "light_theme",
            ElementTheme.Dark => "dark_theme",
            _ => "system_theme",
        }),
        SettingsCategory.Network => _settings.Proxy.Summary,
        SettingsCategory.Plugins => _settings.VideoProvider,
        SettingsCategory.Advanced => _settings.CheckingSummary,
        _ => TotalsDescription(_main.SessionTotals),
    };
    public string SecondValue => Category switch
    {
        SettingsCategory.General => !_settings.HasRegistration ? Strings.NoValue
            : _settings.Startup ? _settings.OnText : _settings.OffText,
        SettingsCategory.Transfers => _settings.SeedingSummary,
        SettingsCategory.Limits => _settings.AlternativeSummary,
        SettingsCategory.Schedule => _settings.Schedule.Periods.Count.ToString("N0", CultureInfo.CurrentCulture),
        SettingsCategory.Appearance => _settings.Text.LanguageName(_settings.Text.Language),
        SettingsCategory.Network => _settings.EncryptionSummary,
        SettingsCategory.Plugins => _main.Subtitles?.SupplierId.ToString() ?? Strings.NoValue,
        SettingsCategory.Advanced => _settings.HistorySummary,
        _ => TotalsDescription(_main.LifetimeTotals),
    };

    public bool ShowsFirstTransfers => Category is null or SettingsCategory.Transfers or SettingsCategory.Limits;
    public bool ShowsSecondTransfers => Category is null or SettingsCategory.Limits;
    public string FirstDownload => Category switch
    {
        SettingsCategory.Transfers => _settings.QueueDownloads,
        SettingsCategory.Limits => _settings.Text.SpeedLimit(_settings.Download.ConfirmedNumber),
        _ => Amount(_main.SessionTotals.Downloaded),
    };
    public string FirstUpload => Category switch
    {
        SettingsCategory.Transfers => _settings.QueueSeeds,
        SettingsCategory.Limits => _settings.Text.SpeedLimit(_settings.Upload.ConfirmedNumber),
        _ => Amount(_main.SessionTotals.Uploaded),
    };
    public string? FirstTotal => Category == SettingsCategory.Transfers ? _settings.QueueTotal : null;
    public string SecondDownload => Category == SettingsCategory.Limits
        ? _settings.Text.SpeedLimit(_settings.AlternativeDownload.ConfirmedNumber)
        : Amount(_main.LifetimeTotals.Downloaded);
    public string SecondUpload => Category == SettingsCategory.Limits
        ? _settings.Text.SpeedLimit(_settings.AlternativeUpload.ConfirmedNumber)
        : Amount(_main.LifetimeTotals.Uploaded);

    private string Caption(string key) => _settings.Text.Get("settings", key);
    private string Amount(long bytes) =>
        _main.IsConnected ? _settings.Text.Bytes(bytes) : Strings.NoValue;
    private string TotalsDescription(TransferTotals totals) => _main.IsConnected
        ? _settings.Text.Format("settings", "transfer_totals_hint", Amount(totals.Downloaded), Amount(totals.Uploaded))
        : _settings.Text.Get("connection", "unavailable");

    public event PropertyChangedEventHandler? PropertyChanged;

    // A change rereads every value, so it waits until Settings shows; showing
    // Settings changes the page and so refreshes.
    private void Refresh()
    {
        if (_main.Page == WindowPage.Settings)
            PropertyChanged?.Invoke(this, new(string.Empty));
    }
}
