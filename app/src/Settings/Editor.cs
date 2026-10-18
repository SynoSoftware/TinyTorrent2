using System.ComponentModel;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows.Input;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shell;
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Settings;

public sealed partial class Editor : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;
    private readonly PipeClient _client;
    private JsonElement _registration;
    private bool _registering;
    private AdapterChoice? _unavailableAdapter;
    private Task _adapterRefresh = Task.CompletedTask;
    public Strings Text => _owner.Text;
    public Overview Overview { get; }
    public Setting Destination { get; }
    public Setting IncompleteFolder { get; }
    public Setting UseIncompleteFolder { get; }
    public Setting AppendSuffix { get; }
    public Setting ConfirmExit { get; }
    public Setting ShowExternalIp { get; }
    public Setting ShowTitleSpeeds { get; }
    public Setting ShowFreeSpace { get; }
    public Setting ShowMemory { get; }
    public Setting RefreshInterval { get; }
    public Setting RecentInterval { get; }
    public Setting HistoryInterval { get; }
    public Setting CapacityDownload { get; }
    public Setting CapacityUpload { get; }
    public ConnectionSetup Connection { get; }
    public Setting DiskBuffer { get; }
    public Setting CheckingMemory { get; }
    public Setting HashingThreads { get; }
    public Setting FilePool { get; }
    public Setting TorrentDialog { get; }
    public Setting MagnetDialog { get; }
    public Setting LookupLimit { get; }

    public Setting StartDownload { get; }
    public Setting QueueTop { get; }

    public Setting UseLastFolder { get; }
    public Setting StartPaused { get; }
    public Setting RaiseAdd { get; }
    public Setting Layout { get; }
    public Setting Duplicates { get; }
    public Setting Exclude { get; }
    public Setting Patterns { get; }
    public Setting ConfirmDelete { get; }

    // The Remove dialog's check boxes as the person last submitted them.
    // Only that dialog shows them, so they have no row in Settings.
    public Setting DeleteDownloaded { get; }
    public Setting DeleteTorrentFile { get; }
    public Setting InactiveTime { get; }
    public Setting SeedRule { get; }
    public Setting Watch { get; }
    public Setting WatchPath { get; }
    public Setting WatchRecursive { get; }
    public Setting WatchDestination { get; }
    public Setting RecheckFinished { get; }
    public Setting ShowSplash { get; }
    public Setting StartInTray { get; }
    public Setting Download { get; }
    public Setting Upload { get; }
    public Setting AlternativeDownload { get; }
    public Setting AlternativeUpload { get; }
    public Setting ActiveDownloads { get; }
    public Setting ActiveSeeds { get; }
    public Setting ActiveTotal { get; }
    public Setting TorrentConnections { get; }
    public Setting IgnoreSlow { get; }
    public Setting SlowDownload { get; }
    public Setting SlowUpload { get; }
    public Setting SlowWait { get; }
    public Setting ActiveChecking { get; }
    public Setting Transport { get; }
    public Setting IpFamily { get; }
    public Setting OutgoingRate { get; }
    public Setting Dht { get; }
    public Setting Pex { get; }
    public Setting Lsd { get; }
    public Setting IncludeOverhead { get; }
    public Setting LimitLan { get; }
    public Setting ConnectionLimit { get; }
    public Setting Encryption { get; }
    public Proxy Proxy { get; }
    public Setting Ratio { get; }
    public Setting SeedingMinutes { get; }
    public Setting Adapter { get; }
    public IReadOnlyList<AdapterChoice> Adapters { get; private set; } = [];
    public AdapterChoice? SelectedAdapter =>
        Adapters.FirstOrDefault(choice => choice.AdapterId == Adapter.Input);
    public Setting PortMapping { get; }
    public Setting Port { get; }

    // Peers cannot connect in through a proxy, so the port and its forwarding
    // have no effect while one is in use.
    private bool CanListen() => !Proxy.IsInUse;

    public string PortHint => Text.Get("settings", Proxy.IsInUse ? "proxy_port_hint" : "port_hint");
    public string MappingHint =>
        Text.Get("settings", Proxy.IsInUse ? "proxy_port_hint" : "mapping_hint");
    public Setting ProblemNotifications { get; }
    public Setting FinishedNotifications { get; }
    public Setting AddedNotifications { get; }
    public Setting BackgroundNotifications { get; }
    public Setting PreventSleep { get; }
    public Setting SeedingSleep { get; }
    public Setting Updates { get; }
    public Schedule Schedule { get; }
    public Setting Language { get; }
    public Setting Theme { get; }
    public string SeedingSummary
    {
        get
        {
            var limits = new List<string>();
            if (Ratio.ConfirmedNumber > 0)
                limits.Add(Text.Format("settings", "ratio_summary", Ratio.ConfirmedNumber));
            if (SeedingMinutes.ConfirmedNumber > 0)
                limits.Add(
                    Text.Format("settings", "seed_time_summary", DurationText(SeedingMinutes))
                );
            if (InactiveTime.ConfirmedNumber > 0)
                limits.Add(Text.Format("settings", "idle_summary", DurationText(InactiveTime)));
            if (limits.Count > 1)
                limits.Add(
                    Text.Get(
                        "settings",
                        SeedRule.ConfirmedText == "all" ? "seed_all_summary" : "seed_any_summary"
                    )
                );
            return limits.Count == 0
                ? Text.Get("settings", "no_seeding_limits")
                : string.Join(" · ", limits);
        }
    }

    public string StartupSummary =>
        Text.Get(
            "settings",
            StartInTray.ConfirmedOn ? "startup_tray_summary" : "startup_window_summary"
        )
        + (
            StartPaused.ConfirmedOn
                ? " · " + Text.Get("settings", "startup_paused_summary")
                : string.Empty
        );
    public string AddingSummary =>
        Text.Get(
            "settings",
            StartDownload.ConfirmedOn ? "adding_start_summary" : "adding_paused_summary"
        )
        + " · "
        + Text.Get("settings", QueueTop.ConfirmedOn ? "queue_top_summary" : "queue_bottom_summary");
    public string PowerSummary =>
        Text.Get(
            "settings",
            !PreventSleep.ConfirmedOn ? "power_off_summary"
                : SeedingSleep.ConfirmedOn ? "power_both_summary"
                : "power_download_summary"
        );
    public string ConfirmationsSummary
    {
        get
        {
            var disabled = new List<string>();
            if (!TorrentDialog.ConfirmedOn)
                disabled.Add(Text.Get("settings", "torrent_dialog_off"));
            if (!MagnetDialog.ConfirmedOn)
                disabled.Add(Text.Get("settings", "magnet_dialog_off"));
            if (!ConfirmDelete.ConfirmedOn)
                disabled.Add(Text.Get("settings", "delete_confirmation_off"));
            if (!ConfirmExit.ConfirmedOn)
                disabled.Add(Text.Get("settings", "exit_confirmation_off"));
            return disabled.Count == 0
                ? Text.Get("settings", "confirmations_on_summary")
                : string.Join(" · ", disabled);
        }
    }
    public string AccountingSummary =>
        Text.Get(
            "settings",
            IncludeOverhead.ConfirmedOn ? "accounting_overhead" : "accounting_payload"
        )
        + " · "
        + Text.Get(
            "settings",
            LimitLan.ConfirmedOn ? "accounting_lan" : "accounting_lan_unlimited"
        );
    internal string QueueDownloads => QueueCount(ActiveDownloads, "queue_download");
    internal string QueueSeeds => QueueCount(ActiveSeeds, "queue_seed");
    internal string QueueTotal => ActiveTotal.ConfirmedNumber == 0
        ? Text.Get("settings", "queue_unlimited")
        : Text.Format("settings", "queue_total", CountText(ActiveTotal));
    public string QueueSummary => string.Join(" · ", QueueDownloads, QueueSeeds, QueueTotal);

    private string QueueCount(Setting setting, string key) => setting.ConfirmedNumber == 0
        ? Text.Get("settings", key + "_unlimited")
        : Text.FormatCount("settings", key, (long)setting.ConfirmedNumber);
    public string PeersSummary =>
        Text.Format(
            "settings",
            "peers_summary",
            CountText(ConnectionLimit),
            CountText(TorrentConnections),
            Text.Get("settings", "transport_" + Transport.ConfirmedText)
        );
    public string DiscoverySummary
    {
        get
        {
            var enabled = new List<string>();
            if (Dht.ConfirmedOn)
                enabled.Add(Text.Get("settings", "discovery_dht"));
            if (Pex.ConfirmedOn)
                enabled.Add(Text.Get("settings", "discovery_pex"));
            if (Lsd.ConfirmedOn)
                enabled.Add(Text.Get("settings", "discovery_lsd"));
            return enabled.Count == 0
                ? Text.Get("settings", "discovery_off")
                : string.Join(" · ", enabled);
        }
    }

    public string IncomingSummary =>
        Proxy.IsInUse
            ? PortHint
            : Text.Format(
                "settings",
                "incoming_summary",
                Port.ConfirmedNumber,
                PortMapping.ConfirmedOn ? OnText : OffText
            );
    public string WatchedSummary =>
        Watch.ConfirmedOn
            ? WatchPath.ConfirmedText
                + (
                    WatchRecursive.ConfirmedOn
                        ? " · " + Text.Get("settings", "watch_children_summary")
                        : string.Empty
                )
            : Text.Get("settings", "watch_off_summary");
    public string CheckingSummary =>
        Text.FormatCount("settings", "checking_count", (int)ActiveChecking.ConfirmedNumber)
        + " · "
        + Text.FormatCount("settings", "checking_threads", (int)HashingThreads.ConfirmedNumber);
    public string HistorySummary =>
        Text.Format(
            "settings",
            "history_summary",
            RecentInterval.ConfirmedNumber,
            HistoryInterval.ConfirmedNumber
        );

    public string RaiseAddHint => DependentHint(RaiseAdd, null, "enable_add_dialog");
    public string SeedingSleepHint => DependentHint(SeedingSleep, null, "enable_sleep_prevention");
    public string IncompleteFolderHint =>
        DependentHint(IncompleteFolder, null, "enable_incomplete_folder");
    public string WatchRecursiveHint => DependentHint(WatchRecursive, null, "enable_watching");
    public string WatchDestinationHint =>
        DependentHint(WatchDestination, "watch_destination_hint", "enable_watching");
    public string SeedRuleHint => DependentHint(SeedRule, "seed_rule_hint", "enable_seed_rule");
    public string IpFamilyHint => DependentHint(IpFamily, null, "ip_family_hint");
    public string LayoutHint =>
        Text.Get("settings", "layout_hint")
        + (
            Layout.Input is "keep" or "create" or "strip"
                ? " · " + Text.Get("settings", "layout_example_" + Layout.Input)
                : string.Empty
        );

    private string DependentHint(Setting setting, string? enabled, string disabled) =>
        setting.Condition?.Invoke() == false ? Text.Get("settings", disabled)
        : enabled is null ? string.Empty
        : Text.Get("settings", enabled);

    private string CountText(Setting setting) =>
        setting.ConfirmedNumber == 0
            ? Text.Get("transfer_limits", "unlimited")
            : setting.ConfirmedNumber.ToString("G", CultureInfo.CurrentCulture);

    private string DurationText(Setting setting) =>
        (setting.ConfirmedNumber / setting.DurationScale).ToString("G", CultureInfo.CurrentCulture)
        + " "
        + Text.Get("settings", setting.DurationUnit + "_unit");

    public string StandardSummary => LimitSummary(Download, Upload);
    public string AlternativeSummary => LimitSummary(AlternativeDownload, AlternativeUpload);
    public string EncryptionSummary =>
        Encryption.ConfirmedText.Length == 0
            ? "—"
            : Text.Get("settings", "encryption_" + Encryption.ConfirmedText);

    private string LimitSummary(Setting download, Setting upload) =>
        Text.Format("settings", "caps_summary", LimitText(download), LimitText(upload));

    private string LimitText(Setting setting) => Text.SpeedLimit(setting.ConfirmedNumber);

    public IReadOnlyList<Setting> All { get; }
    public bool IsPending =>
        All.Any(setting => setting.IsPending)
        || _registering
        || Schedule.IsPending
        || Connection.IsPending
        || Proxy.IsPending;
    public bool CanEdit => _owner.CanEdit;
    internal bool CanSave => _owner.CanSave;

    // Where a setting saves unless it names another store.
    internal SettingStore Engine { get; }

    internal void Report(Exception error) => _owner.Report(error, "failed_settings", null, null);

    internal LimitProfile? FixedLimits => _owner.FixedLimits;
    public bool CanRegister => CanEdit && !_registering;
    public bool HasRegistration => _registration.ValueKind == JsonValueKind.Object;

    // Another TinyTorrent copy's entry is still TinyTorrent's registration, so
    // it counts as on and the caution flyout names that copy. An entry Windows
    // has disabled does not start anything, so it counts as off.
    public bool Startup =>
        Registered("startup") != "none"
        && Field("startup_disabled").ValueKind != JsonValueKind.True;
    public bool StartupUsesOtherCopy => Registered("startup") == "other";
    public string StartupOtherMessage => Other("startup");
    public bool HandlersRegistered => Registered("handlers") != "none";
    public bool HandlersUseOtherCopy => Registered("handlers") == "other";

    // On only while Windows opens both kinds with TinyTorrent, so turning it
    // on is the one step that makes TinyTorrent open them. A default Windows
    // does not report counts as TinyTorrent's.
    public bool Handlers =>
        HandlersRegistered
        && Field("torrent_default").ValueKind != JsonValueKind.False
        && Field("magnet_default").ValueKind != JsonValueKind.False;

    // A default that starts a missing program needs action before another
    // TinyTorrent copy does, so its message takes the caution flyout.
    public string HandlersCaution =>
        HasBrokenHandlers
            ? Text.Format(
                "settings",
                "handlers_broken",
                string.Join(
                    ", ",
                    Broken()
                        .Select(entry => entry.Program)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                )
            )
            : Other("handlers");
    public bool HasBrokenHandlers => Broken().Any();

    // Repair then asks Windows for an administrator, which Windows marks with
    // a shield on the control that asks.
    public bool Elevates => Broken().Any(entry => entry.Machine);

    // The switch covers every other step, so the action appears only for the
    // two it cannot take: removing a missing program's handlers, which can
    // need an administrator, and moving another copy's entries to this one.
    public bool NeedsDefaults => HasBrokenHandlers || HandlersUseOtherCopy;
    public string DefaultsAction =>
        Text.Get("settings", HasBrokenHandlers ? "repair_defaults_tip" : "handlers_use_hint");

    // Both copies are TinyTorrent, so "Use" alone does not identify the target.
    public string DefaultsText =>
        HasBrokenHandlers ? Text.Get("settings", "repair_defaults") : Text.Get("settings", "use");
    public string DefaultsGlyph =>
        HasBrokenHandlers ? (Elevates ? Lucide.Shield : Lucide.Wrench) : Lucide.ArrowLeftRight;

    // The apps Windows opens each kind with, so the person sees what a click
    // on a torrent file or magnet link starts.
    public string TorrentApp => App("torrent_app");
    public string MagnetApp => App("magnet_app");

    private string App(string name) =>
        Field(name) is { ValueKind: JsonValueKind.String } app
        && app.GetString() is { Length: > 0 } text
            ? text
            : Text.Get("settings", "no_app");

    public ICommand FixHandlers { get; }
    public ICommand UseStartup { get; }

    // "this", "other" or "none": whether TinyTorrent's entry starts this copy,
    // another TinyTorrent copy, or nothing.
    private string Registered(string name) =>
        Field(name) is { ValueKind: JsonValueKind.String } state
            ? state.GetString() ?? "none"
            : "none";

    private string Other(string name) =>
        Registered(name) == "other"
        && Field(name + "_target") is { ValueKind: JsonValueKind.String } target
            ? Text.Format("settings", name + "_other", target.GetString() ?? string.Empty)
            : string.Empty;

    // The torrent and magnet handlers, of any app, that start a missing
    // program, their class, and whether removing one needs an administrator.
    private IEnumerable<(string Program, string Class, bool Machine)> Broken() =>
        Field("broken") is { ValueKind: JsonValueKind.Array } entries
            ? entries
                .EnumerateArray()
                .Select(entry =>
                    (
                        entry.TryGetProperty("program", out var program)
                            ? program.GetString() ?? string.Empty
                            : string.Empty,
                        entry.TryGetProperty("class", out var progId)
                            ? progId.GetString() ?? string.Empty
                            : string.Empty,
                        entry.TryGetProperty("machine", out var machine)
                            && machine.ValueKind == JsonValueKind.True
                    )
                )
            : [];

    // An engine from another build can omit a field; a missing field reads as
    // Undefined, so the page shows the setting as off instead of failing.
    private JsonElement Field(string name) =>
        HasRegistration && _registration.TryGetProperty(name, out var value) ? value : default;

    public bool CanSelectLanguage => CanEdit;
    public string OnText => Text.Get("settings", "on");
    public string OffText => Text.Get("settings", "off");
    public bool CanSelectTheme => CanEdit;

    public async Task SelectTheme(string theme)
    {
        if (!CanSelectTheme)
            return;
        Theme.Input = theme;
        if (!Theme.HasDraft && !Theme.IsPending)
        {
            Theme.Cancel();
            return;
        }
        await Commit(Theme);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? TextChanged;

    internal Editor(MainViewModel owner, PipeClient client)
    {
        _owner = owner;
        _client = client;
        Engine = new(
            () => CanEdit,
            () => CanSave,
            (setting, value) => Save(new Dictionary<string, object> { [setting.Name] = value }),
            // Other failures can lose the reply to a save the engine applied.
            error => error is CommandException
        );
        Overview = new(this, owner);
        LookupLimit = new(this, "lookups", SettingKind.Integer, SettingsCategory.Plugins, 1)
        {
            Section = "plugins",
            ValidationKey = "invalid_lookups",
            Store = new(
                () => _owner.Library.IsAvailable,
                () => _owner.Library.IsAvailable,
                (_, value) => _owner.SaveLookupLimit((int)value),
                // A failed SQLite save keeps the earlier value.
                _ => true
            ),
        };
        ConfirmLookupLimit();
        _owner.PropertyChanged += OnOwner;
        Destination = new(
            this,
            "default_destination",
            SettingKind.Text,
            SettingsCategory.Transfers
        );
        UseIncompleteFolder = new(
            this,
            "use_incomplete_folder",
            SettingKind.Boolean,
            SettingsCategory.Transfers
        );
        IncompleteFolder = new(
            this,
            "incomplete_folder",
            SettingKind.Text,
            SettingsCategory.Transfers
        )
        {
            Condition = () => UseIncompleteFolder.IsOn,
        };
        AppendSuffix = new(this, "append_suffix", SettingKind.Boolean, SettingsCategory.Transfers);
        ConfirmExit = new(this, "confirm_exit", SettingKind.Boolean, SettingsCategory.General);
        ShowExternalIp = new(
            this,
            "show_external_ip",
            SettingKind.Boolean,
            SettingsCategory.Appearance
        );
        ShowTitleSpeeds = new(
            this,
            "show_title_speeds",
            SettingKind.Boolean,
            SettingsCategory.Appearance
        );
        ShowFreeSpace = new(
            this,
            "show_free_space",
            SettingKind.Boolean,
            SettingsCategory.Appearance
        );
        ShowMemory = new(this, "show_memory", SettingKind.Boolean, SettingsCategory.Appearance);
        RefreshInterval = new(
            this,
            "refresh_interval",
            SettingKind.Integer,
            SettingsCategory.Advanced,
            1000,
            10000
        );
        RecentInterval = new(
            this,
            "recent_interval",
            SettingKind.Integer,
            SettingsCategory.Advanced,
            1,
            10
        );
        HistoryInterval = new(
            this,
            "history_interval",
            SettingKind.Integer,
            SettingsCategory.Advanced,
            10,
            300
        );
        CapacityDownload = new(
            this,
            "capacity_download",
            SettingKind.Number,
            SettingsCategory.Limits
        );
        CapacityUpload = new(this, "capacity_upload", SettingKind.Number, SettingsCategory.Limits);
        DiskBuffer = new(
            this,
            "disk_buffer_mib",
            SettingKind.Integer,
            SettingsCategory.Advanced,
            1,
            1024
        );
        CheckingMemory = new(
            this,
            "checking_memory_mib",
            SettingKind.Integer,
            SettingsCategory.Advanced,
            1,
            1024
        );
        HashingThreads = new(
            this,
            "hashing_threads",
            SettingKind.Integer,
            SettingsCategory.Advanced,
            1,
            64
        );
        FilePool = new(
            this,
            "file_pool_size",
            SettingKind.Integer,
            SettingsCategory.Advanced,
            1,
            10000
        );
        TorrentDialog = new(this, "torrent_dialog", SettingKind.Boolean, SettingsCategory.General);
        MagnetDialog = new(this, "magnet_dialog", SettingKind.Boolean, SettingsCategory.General);
        StartDownload = new(
            this,
            "starts_download",
            SettingKind.Boolean,
            SettingsCategory.Transfers
        );
        QueueTop = new(this, "queue_top", SettingKind.Boolean, SettingsCategory.Transfers);
        UseLastFolder = new(
            this,
            "use_last_folder",
            SettingKind.Boolean,
            SettingsCategory.Transfers
        );
        StartPaused = new(this, "start_paused", SettingKind.Boolean, SettingsCategory.General);
        RaiseAdd = new(this, "raise_add", SettingKind.Boolean, SettingsCategory.General)
        {
            Condition = () => TorrentDialog.IsOn || MagnetDialog.IsOn,
        };
        Layout = new(this, "layout", SettingKind.Text, SettingsCategory.Transfers);
        Duplicates = new(this, "duplicates", SettingKind.Text, SettingsCategory.Transfers);
        Exclude = new(this, "exclude", SettingKind.Boolean, SettingsCategory.Transfers);
        Patterns = new(this, "patterns", SettingKind.Text, SettingsCategory.Transfers)
        {
            Condition = () => Exclude.IsOn,
        };
        ConfirmDelete = new(this, "confirm_delete", SettingKind.Boolean, SettingsCategory.General);
        DeleteDownloaded = new(
            this,
            "delete_downloaded",
            SettingKind.Boolean,
            SettingsCategory.General
        );
        DeleteTorrentFile = new(
            this,
            "delete_torrent_file",
            SettingKind.Boolean,
            SettingsCategory.General
        );
        InactiveTime = new(
            this,
            "inactive_time",
            SettingKind.Integer,
            SettingsCategory.Transfers,
            0,
            525600
        )
        {
            IsDuration = true,
        };
        Watch = new(this, "watch", SettingKind.Boolean, SettingsCategory.Transfers);
        WatchPath = new(this, "watch_path", SettingKind.Text, SettingsCategory.Transfers);
        WatchRecursive = new(
            this,
            "watch_recursive",
            SettingKind.Boolean,
            SettingsCategory.Transfers
        )
        {
            Condition = () => Watch.IsOn,
        };
        WatchDestination = new(
            this,
            "watch_destination",
            SettingKind.Text,
            SettingsCategory.Transfers
        )
        {
            Condition = () => Watch.IsOn,
        };
        RecheckFinished = new(
            this,
            "recheck_finished",
            SettingKind.Boolean,
            SettingsCategory.Advanced
        );
        ShowSplash = new(this, "show_splash", SettingKind.Boolean, SettingsCategory.General);
        StartInTray = new(this, "start_in_tray", SettingKind.Boolean, SettingsCategory.General);
        Download = new(this, "download_limit", SettingKind.Rate, SettingsCategory.Limits);
        Upload = new(this, "upload_limit", SettingKind.Rate, SettingsCategory.Limits);
        AlternativeDownload = new(
            this,
            "alternative_download_limit",
            SettingKind.Rate,
            SettingsCategory.Limits
        );
        AlternativeUpload = new(
            this,
            "alternative_upload_limit",
            SettingKind.Rate,
            SettingsCategory.Limits
        );
        ActiveDownloads = new(
            this,
            "active_downloads",
            SettingKind.Integer,
            SettingsCategory.Transfers
        );
        ActiveSeeds = new(this, "active_seeds", SettingKind.Integer, SettingsCategory.Transfers);
        ConnectionLimit = new(
            this,
            "connection_limit",
            SettingKind.Integer,
            SettingsCategory.Network
        );
        Encryption = new(this, "encryption", SettingKind.Text, SettingsCategory.Network);
        ActiveTotal = new(this, "active_total", SettingKind.Integer, SettingsCategory.Transfers);
        TorrentConnections = new(
            this,
            "per_torrent_connections",
            SettingKind.Integer,
            SettingsCategory.Network,
            2,
            10000
        )
        {
            HasUnlimited = true,
        };
        IgnoreSlow = new(this, "ignore_slow", SettingKind.Boolean, SettingsCategory.Transfers);
        SlowDownload = new(
            this,
            "slow_download",
            SettingKind.Rate,
            SettingsCategory.Transfers,
            0,
            1073741824
        )
        {
            HasUnlimited = false,
            RateScale = RateScale.KibPerSecond,
            Condition = () => IgnoreSlow.IsOn,
        };
        SlowUpload = new(
            this,
            "slow_upload",
            SettingKind.Rate,
            SettingsCategory.Transfers,
            0,
            1073741824
        )
        {
            HasUnlimited = false,
            RateScale = RateScale.KibPerSecond,
            Condition = () => IgnoreSlow.IsOn,
        };
        SlowWait = new(this, "slow_wait", SettingKind.Integer, SettingsCategory.Transfers, 1, 86400)
        {
            Condition = () => IgnoreSlow.IsOn,
        };
        ActiveChecking = new(
            this,
            "active_checking",
            SettingKind.Integer,
            SettingsCategory.Advanced,
            1,
            64
        );
        Transport = new(this, "transport", SettingKind.Text, SettingsCategory.Network);
        IpFamily = new(this, "ip_family", SettingKind.Text, SettingsCategory.Network)
        {
            Condition = CanListen,
        };
        OutgoingRate = new(
            this,
            "outgoing_rate",
            SettingKind.Integer,
            SettingsCategory.Network,
            1,
            1000
        );
        Dht = new(this, "dht", SettingKind.Boolean, SettingsCategory.Network);
        Pex = new(this, "pex", SettingKind.Boolean, SettingsCategory.Network);
        Lsd = new(this, "lsd", SettingKind.Boolean, SettingsCategory.Network);
        IncludeOverhead = new(
            this,
            "include_overhead",
            SettingKind.Boolean,
            SettingsCategory.Limits
        );
        LimitLan = new(this, "limit_lan", SettingKind.Boolean, SettingsCategory.Limits);
        Proxy = new(this, owner, client);
        Proxy.PropertyChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPending)));
        Ratio = new(this, "ratio_limit", SettingKind.Number, SettingsCategory.Transfers);
        SeedingMinutes = new(
            this,
            "seeding_minutes",
            SettingKind.Integer,
            SettingsCategory.Transfers
        )
        {
            IsDuration = true,
        };
        SeedRule = new(this, "seed_rule", SettingKind.Text, SettingsCategory.Transfers)
        {
            Condition = () =>
                new[] { Ratio, SeedingMinutes, InactiveTime }.Count(setting =>
                    setting.ConfirmedNumber > 0
                ) > 1,
        };
        Adapter = new(this, "network_interface", SettingKind.Text, SettingsCategory.Network);
        Adapters = [new(string.Empty, Text.Get("settings", "any_adapter"))];
        Adapter.PropertyChanged += (_, _) => UpdateAdapter();
        PortMapping = new(this, "port_mapping", SettingKind.Boolean, SettingsCategory.Network)
        {
            Condition = CanListen,
        };
        Port = new(this, "listen_port", SettingKind.Integer, SettingsCategory.Network, 1, 65535)
        {
            Condition = CanListen,
        };
        ProblemNotifications = new(
            this,
            "notify_problems",
            SettingKind.Boolean,
            SettingsCategory.General
        );
        FinishedNotifications = new(
            this,
            "notifications_enabled",
            SettingKind.Boolean,
            SettingsCategory.General
        );
        AddedNotifications = new(
            this,
            "notify_added",
            SettingKind.Boolean,
            SettingsCategory.General
        );
        BackgroundNotifications = new(
            this,
            "notify_background",
            SettingKind.Boolean,
            SettingsCategory.General
        );
        PreventSleep = new(this, "prevent_sleep", SettingKind.Boolean, SettingsCategory.General);
        // Seeding only extends Prevent sleep, so it has no effect while that is off.
        SeedingSleep = new(
            this,
            "prevent_sleep_seeding",
            SettingKind.Boolean,
            SettingsCategory.General
        )
        {
            Condition = () => PreventSleep.IsOn,
        };
        Updates = new(this, "check_for_updates", SettingKind.Boolean, SettingsCategory.General);
        Schedule = new(this);
        Language = new(this, "language", SettingKind.Text, SettingsCategory.Appearance);
        Theme = new(this, "theme", SettingKind.Text, SettingsCategory.Appearance);
        Schedule.PropertyChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPending)));
        All =
        [
            LookupLimit,
            Destination,
            TorrentDialog,
            MagnetDialog,
            StartDownload,
            QueueTop,
            UseLastFolder,
            StartPaused,
            RaiseAdd,
            Layout,
            Duplicates,
            Exclude,
            Patterns,
            ConfirmDelete,
            DeleteDownloaded,
            DeleteTorrentFile,
            InactiveTime,
            SeedRule,
            Watch,
            WatchPath,
            WatchRecursive,
            WatchDestination,
            RecheckFinished,
            Download,
            Upload,
            AlternativeDownload,
            AlternativeUpload,
            ActiveDownloads,
            ActiveSeeds,
            ActiveTotal,
            TorrentConnections,
            IgnoreSlow,
            SlowDownload,
            SlowUpload,
            SlowWait,
            ActiveChecking,
            Transport,
            IpFamily,
            OutgoingRate,
            Dht,
            Pex,
            Lsd,
            IncludeOverhead,
            LimitLan,
            ConnectionLimit,
            Encryption,
            Ratio,
            SeedingMinutes,
            Adapter,
            PortMapping,
            Port,
            ProblemNotifications,
            FinishedNotifications,
            AddedNotifications,
            BackgroundNotifications,
            PreventSleep,
            SeedingSleep,
            Updates,
            ShowSplash,
            StartInTray,
            Language,
            Theme,
            IncompleteFolder,
            UseIncompleteFolder,
            AppendSuffix,
            ConfirmExit,
            ShowExternalIp,
            ShowTitleSpeeds,
            ShowFreeSpace,
            ShowMemory,
            RefreshInterval,
            RecentInterval,
            HistoryInterval,
            CapacityDownload,
            CapacityUpload,
            DiskBuffer,
            CheckingMemory,
            HashingThreads,
            FilePool,
        ];
        FixHandlers = new RelayCommand(() => RegisterHandlers(), () => CanRegister);
        Connection = new(this, owner, client);
        UseStartup = new RelayCommand(() => Register("enable_startup"), () => CanRegister);
    }

    public Task RefreshAdapters() =>
        _adapterRefresh.IsCompleted ? _adapterRefresh = ReadAdapters() : _adapterRefresh;

    private async Task ReadAdapters()
    {
        try
        {
            var choices = await Task.Run(() =>
                NetworkInterface
                    .GetAllNetworkInterfaces()
                    .Select(adapter => new AdapterChoice(adapter.Id, adapter.Name))
                    .ToArray()
            );
            Adapters = [new(string.Empty, Text.Get("settings", "any_adapter")), .. choices];
            _unavailableAdapter = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Adapters)));
            UpdateAdapter();
        }
        catch (NetworkInformationException error)
        {
            _owner.Report(error);
        }
    }

    private void UpdateAdapter()
    {
        var choices = Adapters.Where(choice => choice != _unavailableAdapter).ToList();
        if (!choices.Any(choice => choice.AdapterId == Adapter.Input))
        {
            var unavailable =
                _unavailableAdapter is { } current && current.AdapterId == Adapter.Input
                    ? current
                    : new AdapterChoice(
                        Adapter.Input,
                        Text.Format("settings", "unavailable_adapter", Adapter.Input)
                    );
            _unavailableAdapter = unavailable;
            choices.Add(unavailable);
        }
        else
            _unavailableAdapter = null;
        if (!Adapters.SequenceEqual(choices))
        {
            Adapters = choices;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Adapters)));
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAdapter)));
    }

    // The proxy status is the snapshot's check of the proxy in use.
    internal void Apply(JsonElement settings, JsonElement proxy, JsonElement proxyCheck)
    {
        var changed = Proxy.Apply(settings, proxy, proxyCheck);
        foreach (var setting in All)
            if (settings.TryGetProperty(setting.Name, out var value))
                changed |= setting.Confirm(value);
        if (settings.TryGetProperty("schedule", out var schedule))
            Schedule.Apply(schedule);
        if (changed)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    internal Setting AddPreference(string source) =>
        source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            ? MagnetDialog
            : TorrentDialog;

    internal async Task<long> Save(object changes)
    {
        var reply = await _client.Send("settings", new { changes });
        var confirmation = JsonSerializer.SerializeToElement(changes);
        var changed = false;
        foreach (var setting in All)
            if (confirmation.TryGetProperty(setting.Name, out var value))
                changed |= setting.Confirm(value);
        if (changed)
            Changed();
        return reply.GetProperty("snapshot_sequence").GetInt64();
    }

    // The engine publishes a snapshot with the defaults, so no setting is
    // confirmed here.
    internal Task ResetAll() => _client.Send("reset_settings");

    internal async Task SaveLanguage(string language)
    {
        Language.Input = language;
        await Submit(Language, language);
        if (Language.Failure is { } error)
            throw error;
    }

    public Task ObserveRegistration() => Register("observe");

    public Task SetStartup(bool enabled) =>
        Register(enabled ? "enable_startup" : "disable_startup");

    public Task SetHandlers(bool enabled) =>
        Register(enabled ? "register_handlers" : "unregister_handlers");

    // The engine removes the person's broken handlers itself. All-users ones
    // need an administrator, so the engine runs once more with Windows'
    // administrator prompt; declining it leaves them as they are. Only the
    // link, which shows the shield, asks for an administrator; the switch
    // never does.
    private async Task RegisterHandlers()
    {
        if (!await Register("register_handlers"))
            return;
        var classes = Broken()
            .Where(entry => entry.Machine)
            .Select(entry => entry.Class)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (classes.Count == 0)
            return;
        _registering = true;
        Changed(nameof(CanRegister), nameof(IsPending));
        try
        {
            await _client.RepairMachine(classes);
        }
        catch (Exception error)
        {
            _owner.Report(error, "failed_registration", null, null);
        }
        finally
        {
            _registering = false;
            Changed();
        }
        await Register("observe");
    }

    public string ChosenLanguage => _owner.ChosenLanguage;

    public void SelectLanguage(string language)
    {
        if (CanSelectLanguage)
            _owner.SelectLanguage(language);
    }

    private async Task<bool> Register(string action)
    {
        if (!CanRegister)
            return false;
        _registering = true;
        // Every property would also refresh the switches, which would show the
        // state before this change until the engine answers.
        Changed(nameof(CanRegister), nameof(IsPending));
        try
        {
            _registration = await _client.Send("registration", new { action });
            return true;
        }
        catch (Exception error)
        {
            _owner.Report(error, "failed_registration", null, null);
            if (error is CommandException && action != "observe")
                try
                {
                    _registration = await _client.Send("registration", new { action = "observe" });
                }
                catch (Exception) { }
            return false;
        }
        finally
        {
            _registering = false;
            Changed();
        }
    }

    /// <summary>
    /// Saves the value a choice control selected, which is its item's tag:
    /// "true" or "false" for an on/off setting, otherwise the input. The
    /// current value saves nothing.
    /// </summary>
    public async Task CommitSelection(Setting setting, string value)
    {
        if (value == setting.SelectedValue)
            return;
        if (setting.Kind == SettingKind.Boolean)
        {
            await Toggle(setting, value == "true");
            return;
        }
        setting.Input = value;
        await Commit(setting);
    }

    public async Task Commit(Setting setting)
    {
        if (!setting.CanEdit || !setting.IsPending && !setting.HasDraft)
            return;
        if (!TryValue(setting, out var value))
        {
            setting.MarkInvalid();
            return;
        }
        await Submit(setting, value);
    }

    public async Task PrepareLeave()
    {
        var saves = All.Select(Depart).ToArray();
        var schedule = Schedule.Depart();
        await Task.WhenAll(saves);
        await schedule;
    }

    public async Task Depart(Setting setting)
    {
        if (setting.IsCancelled || !setting.IsPending && !setting.HasDraft)
        {
            await setting.Saving;
            return;
        }
        // A draft that its condition has disabled can be neither corrected nor
        // saved, so leaving discards it like invalid input.
        if (!TryValue(setting, out var value) || setting.Condition?.Invoke() == false)
        {
            setting.Cancel();
            return;
        }
        if (!setting.Store.CanSave())
            return;
        if (setting.Failure is null)
            await Submit(setting, value);
    }

    public async Task Toggle(Setting setting, bool value)
    {
        if (!setting.CanEdit || setting.IsOn == value)
            return;
        setting.Choose(value);
        if (!setting.HasDraft && !setting.IsPending)
        {
            setting.Cancel();
            return;
        }
        await Submit(setting, value);
    }

    private async Task Submit(Setting setting, object value)
    {
        if (!setting.Store.CanSave())
        {
            var error = new IOException(Text.Get("connection", "unavailable"));
            setting.Reject(error);
            _owner.Report(error, "failed_settings", () => setting.Label, null);
            return;
        }
        var submitted = setting.Capture(value);
        if (setting.IsPending)
        {
            setting.Defer(submitted);
            await setting.Saving;
            return;
        }
        setting.Begin();
        Changed();
        try
        {
            while (true)
            {
                Exception? failure = null;
                try
                {
                    await setting.Store.Save(setting, submitted.Value);
                    setting.Accept(JsonSerializer.SerializeToElement(submitted.Value), submitted);
                }
                catch (Exception error)
                {
                    failure = error;
                    _owner.Report(error, "failed_settings", () => setting.Label, null);
                }
                if (
                    setting.Store.CanSave()
                    && (failure is null || setting.Store.Refused(failure))
                    && setting.TakeIntent() is { } next
                    && next != submitted
                )
                {
                    submitted = next;
                    continue;
                }
                if (failure is not null)
                    setting.Reject(failure, submitted);
                break;
            }
        }
        finally
        {
            setting.End();
            Changed();
        }
    }

    private static bool TryValue(Setting setting, out object value)
    {
        value = setting.Input.Trim();
        if (setting.Kind == SettingKind.Text)
            return true;
        if (setting.Kind == SettingKind.Boolean)
        {
            value = setting.IsOn;
            return true;
        }
        if (setting.HasUnlimited && setting.Input.Trim().Length == 0)
        {
            value = setting.Kind == SettingKind.Number ? 0.0 : (object)0;
            return true;
        }
        if (setting.IsRate)
        {
            if (
                !TryRate(setting.Input, out var bytes, setting.RateScale)
                || bytes > setting.Maximum
            )
                return false;
            value = bytes;
            return true;
        }
        double number;
        if (setting.Kind == SettingKind.Integer && !setting.IsDuration)
        {
            if (!TryInteger(setting.Input, out var integer))
                return false;
            number = integer;
        }
        else if (!TryNumber(setting.Input, out number))
            return false;
        if (setting.IsDuration)
            number = Math.Round(number * setting.DurationScale, 8);
        if (setting.Kind == SettingKind.Number)
        {
            value = number;
            return true;
        }
        if (
            number != Math.Truncate(number)
            || number > int.MaxValue
            || number < setting.Minimum && !(setting.HasUnlimited && number == 0)
            || number > setting.Maximum
        )
            return false;
        value = (int)number;
        return true;
    }

    internal static bool TryInteger(string input, out int number) =>
        int.TryParse(
            input,
            NumberStyles.Integer | NumberStyles.AllowThousands,
            CultureInfo.CurrentCulture,
            out number
        )
        && number >= 0;

    internal static bool TryNumber(string input, out double number) =>
        double.TryParse(
            input,
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.CurrentCulture,
            out number
        )
        && double.IsFinite(number)
        && number >= 0;

    // The engine stores bytes per second; each editor specifies its unit.
    internal static bool TryRate(string input, out int bytes, RateScale scale)
    {
        bytes = 0;
        if (!TryNumber(input, out var number) || number > int.MaxValue / (double)scale)
            return false;
        bytes = checked((int)Math.Round(number * (double)scale));
        return true;
    }

    // Small positive values retain editable precision; unchanged input never
    // changes a saved limit through display rounding.
    internal static string RateInput(int bytes, RateScale scale) =>
        (bytes / (double)scale).ToString(
            bytes > 0 && bytes < (int)scale / 100.0 ? "0.##########" : "0.##",
            CultureInfo.CurrentCulture
        );

    public Task CancelDraft() => Schedule.Close();

    public void RefreshText()
    {
        foreach (var choice in Adapters)
            if (choice.AdapterId.Length == 0)
                choice.Text = Text.Get("settings", "any_adapter");
        if (_unavailableAdapter is { } unavailable)
            unavailable.Text = Text.Format(
                "settings",
                "unavailable_adapter",
                unavailable.AdapterId
            );
        Changed();
        foreach (var setting in All)
            setting.Refresh();
        Proxy.Refresh();
        Schedule.RefreshText();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void Refresh()
    {
        Changed();
        foreach (var setting in All)
            setting.Refresh();
        Proxy.Refresh();
    }

    // Without names, every property changed.
    internal void Changed(params string[] names)
    {
        Schedule?.Refresh();
        foreach (var name in names.Length == 0 ? new[] { string.Empty } : names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        foreach (RelayCommand command in new[] { FixHandlers, UseStartup })
            command.Refresh();
    }
}

public sealed class Setting(
    Editor owner,
    string name,
    SettingKind kind,
    SettingsCategory category,
    int minimum = 0,
    int maximum = int.MaxValue
) : INotifyPropertyChanged
{
    private string _input = string.Empty;
    private string _confirmedInput = string.Empty;
    private JsonElement _confirmed;
    private Exception? _failure;
    private bool _invalid;
    private bool? _choice;
    private TaskCompletionSource? _saving;
    private Submission? _intent;
    private Submission? _uncertain;
    private bool _cancelled;
    public string Name { get; } = name;
    public SettingKind Kind { get; } = kind;
    public SettingsCategory Category { get; } = category;
    public int Minimum { get; } = minimum;
    public int Maximum { get; } = maximum;
    public string Section { get; init; } = kind == SettingKind.Rate ? "limits" : "settings";
    public string? ValidationKey { get; init; }
    public bool IsDuration { get; init; }

    public string DurationUnit { get; private set; } = "minutes";
    internal int DurationScale => IsDuration && DurationUnit == "hours" ? 60 : 1;

    // Other state that must hold before the setting can be edited.
    internal Func<bool>? Condition { get; init; }
    internal SettingStore Store { get; init; } = owner.Engine;
    public bool IsRate => Kind == SettingKind.Rate;
    internal RateScale RateScale { get; init; } = RateScale.MbPerSecond;
    public string RateUnit => owner.Text.RateUnit(RateScale);

    // Empty limits save 0, which the engine reads as unlimited.
    public bool HasUnlimited { get; init; } =
        kind is SettingKind.Rate or SettingKind.Integer or SettingKind.Number && minimum == 0;
    internal string ConfirmedText => _confirmedInput;
    internal double ConfirmedNumber =>
        _confirmed.ValueKind == JsonValueKind.Number ? _confirmed.GetDouble() : 0;
    internal bool ConfirmedOn => _confirmed.ValueKind == JsonValueKind.True;
    public string Label => owner.Text.Get(Section, Name);

    // Names the duration's unit selector after its setting, so each one reads
    // differently in Narrator.
    public string UnitLabel => owner.Text.Format("settings", "duration_unit", Label);
    public string Input
    {
        get => _input;
        set
        {
            if (_input == value)
                return;
            _input = value;
            _cancelled = false;
            _failure = null;
            _invalid = false;
            Refresh();
            owner.Changed();
        }
    }
    public bool IsOn => _choice ?? _confirmed.ValueKind == JsonValueKind.True;

    // The tag of the item a selector shows, as Editor.CommitSelection reads it.
    public string SelectedValue =>
        Kind == SettingKind.Boolean ? IsOn ? "true" : "false"
        : Input;
    public bool HasDraft =>
        _input != _confirmedInput
        || _choice is { } choice && choice != (_confirmed.ValueKind == JsonValueKind.True)
        || !_cancelled
            && _uncertain is { } submitted
            && (_input != submitted.Input || _choice != submitted.Choice);
    public bool IsPending => _saving is not null;
    internal bool IsCancelled => _cancelled;
    internal Exception? Failure => _failure;
    internal Task Saving => _saving?.Task ?? Task.CompletedTask;

    // A save keeps its control enabled so later input keeps focus.
    public bool CanEdit => Store.CanEdit() && (Condition?.Invoke() ?? true);
    public string Message
    {
        get
        {
            if (!_invalid)
                return string.Empty;
            if (ValidationKey is { } key)
                return owner.Text.Get(Section, key);
            if (IsDuration)
                return owner.Text.Get("settings", "invalid_duration");
            if (IsRate)
                return owner.Text.Format(
                    "settings",
                    "invalid_rate",
                    Maximum / (double)RateScale,
                    RateUnit
                );
            if (Minimum > 0)
                return owner.Text.Format("settings", "invalid_range", Minimum, Maximum);
            return owner.Text.Get(
                "settings",
                Kind == SettingKind.Integer ? "invalid_count" : "invalid_number"
            );
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool Confirm(JsonElement value)
    {
        var changed =
            _confirmed.ValueKind != value.ValueKind || !JsonElement.DeepEquals(_confirmed, value);
        if (changed)
        {
            var preserve = HasDraft || IsPending || _uncertain is not null;
            // Cloning a child of the snapshot's clone retains the whole snapshot.
            _confirmed = JsonSerializer.SerializeToElement(value);
            _confirmedInput =
                value.ValueKind == JsonValueKind.String ? value.GetString()!
                : value.ValueKind != JsonValueKind.Number || HasUnlimited && value.GetDouble() == 0
                    ? string.Empty
                : IsRate ? Editor.RateInput(value.GetInt32(), RateScale)
                : (value.GetDouble() / DurationScale).ToString("G", CultureInfo.CurrentCulture);
            if (!preserve)
            {
                _input = _confirmedInput;
                _choice = null;
            }
        }
        if (
            !IsPending
            && _uncertain is { } submitted
            && JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(submitted.Value))
        )
        {
            Settle(submitted);
            changed = true;
        }
        if (changed)
            Refresh();
        return changed;
    }

    internal sealed record Submission(object Value, string Input, bool? Choice);

    public void SelectUnit(string unit)
    {
        if (DurationUnit == unit)
            return;
        var scale = DurationScale;
        DurationUnit = unit;
        if (Editor.TryNumber(_input, out var number))
            _input = (number * scale / DurationScale).ToString("G", CultureInfo.CurrentCulture);
        _confirmedInput =
            ConfirmedNumber == 0
                ? string.Empty
                : (ConfirmedNumber / DurationScale).ToString("G", CultureInfo.CurrentCulture);
        Refresh();
        owner.Changed();
    }

    internal Submission Capture(object value)
    {
        _cancelled = false;
        return new(value, _input, _choice);
    }

    internal void Defer(Submission submitted) => _intent = submitted;

    internal Submission? TakeIntent()
    {
        var intent = _intent;
        _intent = null;
        return intent;
    }

    internal void Accept(JsonElement value, Submission submitted)
    {
        Confirm(value);
        Settle(submitted);
        Refresh();
    }

    private void Settle(Submission submitted)
    {
        var preserve = !_cancelled && (_input != submitted.Input || _choice != submitted.Choice);
        if (!preserve)
        {
            _input = _confirmedInput;
            _choice = null;
            _invalid = false;
        }
        _cancelled = false;
        _failure = null;
        _uncertain = null;
    }

    internal void Begin()
    {
        _saving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _failure = null;
        _uncertain = null;
        _invalid = false;
        Refresh();
    }

    internal void End()
    {
        var saving = _saving;
        _saving = null;
        _intent = null;
        if (_uncertain is null)
            _cancelled = false;
        Refresh();
        saving?.TrySetResult();
    }

    internal void Reject(Exception error, Submission? submitted = null)
    {
        var current =
            submitted is null
            || !_cancelled && _input == submitted.Input && _choice == submitted.Choice;
        var refused = Store.Refused(error);
        if (_cancelled && submitted is not null && refused)
            Settle(submitted);
        _failure = current ? error : null;
        // A lost reply still needs confirmation, even after the input changes
        // or is cancelled; Settle preserves any newer input when it arrives.
        _uncertain = refused ? null : submitted;
        Refresh();
    }

    internal void MarkInvalid()
    {
        _invalid = true;
        Refresh();
    }

    internal void Choose(bool value)
    {
        _choice = value;
        _cancelled = false;
        _failure = null;
        _invalid = false;
        Refresh();
        owner.Changed();
    }

    public void Cancel()
    {
        _intent = null;
        _cancelled = IsPending || _uncertain is not null;
        _input = _confirmedInput;
        _choice = null;
        _failure = null;
        _invalid = false;
        Refresh();
        owner.Changed();
    }

    internal void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// Where a <see cref="Setting"/> saves: whether it can be edited and saved
/// now, the save of a submitted value, and whether a failed save proves the
/// value is unsaved. Otherwise the setting waits for a confirmed value.
/// </summary>
internal sealed record SettingStore(
    Func<bool> CanEdit,
    Func<bool> CanSave,
    Func<Setting, object, Task> Save,
    Func<Exception, bool> Refused
);

public sealed class AdapterChoice(string adapterId, string text) : INotifyPropertyChanged
{
    private string _text = text;
    public string AdapterId { get; } = adapterId;
    public string Text
    {
        get => _text;
        internal set
        {
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Text;
}
