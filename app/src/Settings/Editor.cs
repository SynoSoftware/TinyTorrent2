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
    private readonly List<Setting> _all = [];
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

    private bool UsesIncompleteFolder() => UseIncompleteFolder.IsOn;

    // The seed rule says how seeding limits combine, so it needs two of them.
    private bool CombinesSeedLimits() =>
        new[] { Ratio, SeedingMinutes, InactiveTime }.Count(setting =>
            setting.ConfirmedNumber > 0
        ) > 1;

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

    public IReadOnlyList<Setting> All => _all;
    public bool IsPending =>
        All.Any(setting => setting.IsPending)
        || _registering
        || Schedule.IsPending
        || Connection.IsPending
        || Proxy.IsPending;
    public bool CanEdit => _owner.CanEdit;
    internal bool CanSave => _owner.CanSave;

    // Where the engine's settings save.
    private SettingStore Engine { get; }

    internal void Report(Exception error) =>
        _owner.Notifications.Report(error, "failed_settings", null, null);

    private void Report(Setting setting, Exception error) =>
        _owner.Notifications.Report(error, "failed_settings", () => setting.Label, null);

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
        await Theme.Commit();
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
        Proxy = new(this, owner, client);
        Proxy.PropertyChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPending)));
        Schedule = new(this);
        Schedule.PropertyChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPending)));
        // Settings register in the order search lists them.
        var library = new SettingStore(
            () => _owner.Library.IsAvailable,
            () => _owner.Library.IsAvailable,
            (_, value) => _owner.Features.SaveLookupLimit((int)value),
            // A failed SQLite save keeps the earlier value.
            _ => true
        );
        LookupLimit = Add(
            new(library, Text, "lookups", SettingKind.Integer, SettingsCategory.Plugins)
            {
                Minimum = 1,
                Section = "plugins",
                ValidationKey = "invalid_lookups",
            }
        );
        ConfirmLookupLimit();
        _owner.PropertyChanged += OnOwner;
        Destination = Add(
            new(Engine, Text, "default_destination", SettingKind.Text, SettingsCategory.Transfers)
        );
        TorrentDialog = Add(
            new(Engine, Text, "torrent_dialog", SettingKind.Boolean, SettingsCategory.General)
        );
        MagnetDialog = Add(
            new(Engine, Text, "magnet_dialog", SettingKind.Boolean, SettingsCategory.General)
        );
        StartDownload = Add(
            new(Engine, Text, "starts_download", SettingKind.Boolean, SettingsCategory.Transfers)
        );
        QueueTop = Add(
            new(Engine, Text, "queue_top", SettingKind.Boolean, SettingsCategory.Transfers)
        );
        UseLastFolder = Add(
            new(Engine, Text, "use_last_folder", SettingKind.Boolean, SettingsCategory.Transfers)
        );
        StartPaused = Add(
            new(Engine, Text, "start_paused", SettingKind.Boolean, SettingsCategory.General)
        );
        RaiseAdd = Add(
            new(Engine, Text, "raise_add", SettingKind.Boolean, SettingsCategory.General)
            {
                Condition = () => TorrentDialog.IsOn || MagnetDialog.IsOn,
            }
        );
        Layout = Add(new(Engine, Text, "layout", SettingKind.Text, SettingsCategory.Transfers));
        Duplicates = Add(
            new(Engine, Text, "duplicates", SettingKind.Text, SettingsCategory.Transfers)
        );
        Exclude = Add(
            new(Engine, Text, "exclude", SettingKind.Boolean, SettingsCategory.Transfers)
        );
        Patterns = Add(
            new(Engine, Text, "patterns", SettingKind.Text, SettingsCategory.Transfers)
            {
                Condition = () => Exclude.IsOn,
            }
        );
        ConfirmDelete = Add(
            new(Engine, Text, "confirm_delete", SettingKind.Boolean, SettingsCategory.General)
        );
        DeleteDownloaded = Add(
            new(Engine, Text, "delete_downloaded", SettingKind.Boolean, SettingsCategory.General)
        );
        DeleteTorrentFile = Add(
            new(Engine, Text, "delete_torrent_file", SettingKind.Boolean, SettingsCategory.General)
        );
        InactiveTime = Add(
            new(
                Engine,
                Text,
                "inactive_time",
                SettingKind.Integer,
                SettingsCategory.Transfers
            )
            {
                Maximum = 525600,
                IsDuration = true,
            }
        );
        SeedRule = Add(
            new(Engine, Text, "seed_rule", SettingKind.Text, SettingsCategory.Transfers)
            {
                Condition = CombinesSeedLimits,
            }
        );
        Watch = Add(new(Engine, Text, "watch", SettingKind.Boolean, SettingsCategory.Transfers));
        WatchPath = Add(
            new(Engine, Text, "watch_path", SettingKind.Text, SettingsCategory.Transfers)
        );
        WatchRecursive = Add(
            new(Engine, Text, "watch_recursive", SettingKind.Boolean, SettingsCategory.Transfers)
            {
                Condition = () => Watch.IsOn,
            }
        );
        WatchDestination = Add(
            new(Engine, Text, "watch_destination", SettingKind.Text, SettingsCategory.Transfers)
            {
                Condition = () => Watch.IsOn,
            }
        );
        RecheckFinished = Add(
            new(Engine, Text, "recheck_finished", SettingKind.Boolean, SettingsCategory.Advanced)
        );
        Download = Add(
            new(Engine, Text, "download_limit", SettingKind.Rate, SettingsCategory.Limits)
        );
        Upload = Add(new(Engine, Text, "upload_limit", SettingKind.Rate, SettingsCategory.Limits));
        AlternativeDownload = Add(
            new(
                Engine,
                Text,
                "alternative_download_limit",
                SettingKind.Rate,
                SettingsCategory.Limits
            )
        );
        AlternativeUpload = Add(
            new(Engine, Text, "alternative_upload_limit", SettingKind.Rate, SettingsCategory.Limits)
        );
        ActiveDownloads = Add(
            new(Engine, Text, "active_downloads", SettingKind.Integer, SettingsCategory.Transfers)
        );
        ActiveSeeds = Add(
            new(Engine, Text, "active_seeds", SettingKind.Integer, SettingsCategory.Transfers)
        );
        ActiveTotal = Add(
            new(Engine, Text, "active_total", SettingKind.Integer, SettingsCategory.Transfers)
        );
        TorrentConnections = Add(
            new(
                Engine,
                Text,
                "per_torrent_connections",
                SettingKind.Integer,
                SettingsCategory.Network
            )
            {
                Minimum = 2,
                Maximum = 10000,
                HasUnlimited = true,
            }
        );
        IgnoreSlow = Add(
            new(Engine, Text, "ignore_slow", SettingKind.Boolean, SettingsCategory.Transfers)
        );
        SlowDownload = Add(
            new(
                Engine,
                Text,
                "slow_download",
                SettingKind.Rate,
                SettingsCategory.Transfers
            )
            {
                Maximum = 1073741824,
                HasUnlimited = false,
                RateScale = RateScale.KibPerSecond,
                Condition = () => IgnoreSlow.IsOn,
            }
        );
        SlowUpload = Add(
            new(
                Engine,
                Text,
                "slow_upload",
                SettingKind.Rate,
                SettingsCategory.Transfers
            )
            {
                Maximum = 1073741824,
                HasUnlimited = false,
                RateScale = RateScale.KibPerSecond,
                Condition = () => IgnoreSlow.IsOn,
            }
        );
        SlowWait = Add(
            new(
                Engine,
                Text,
                "slow_wait",
                SettingKind.Integer,
                SettingsCategory.Transfers
            )
            {
                Minimum = 1,
                Maximum = 86400,
                Condition = () => IgnoreSlow.IsOn,
            }
        );
        ActiveChecking = Add(
            new(
                Engine,
                Text,
                "active_checking",
                SettingKind.Integer,
                SettingsCategory.Advanced
            )
            {
                Minimum = 1,
                Maximum = 64,
            }
        );
        Transport = Add(new(Engine, Text, "transport", SettingKind.Text, SettingsCategory.Network));
        IpFamily = Add(
            new(Engine, Text, "ip_family", SettingKind.Text, SettingsCategory.Network)
            {
                Condition = CanListen,
            }
        );
        OutgoingRate = Add(
            new(
                Engine,
                Text,
                "outgoing_rate",
                SettingKind.Integer,
                SettingsCategory.Network
            )
            {
                Minimum = 1,
                Maximum = 1000,
            }
        );
        Dht = Add(new(Engine, Text, "dht", SettingKind.Boolean, SettingsCategory.Network));
        Pex = Add(new(Engine, Text, "pex", SettingKind.Boolean, SettingsCategory.Network));
        Lsd = Add(new(Engine, Text, "lsd", SettingKind.Boolean, SettingsCategory.Network));
        IncludeOverhead = Add(
            new(Engine, Text, "include_overhead", SettingKind.Boolean, SettingsCategory.Limits)
        );
        LimitLan = Add(
            new(Engine, Text, "limit_lan", SettingKind.Boolean, SettingsCategory.Limits)
        );
        ConnectionLimit = Add(
            new(Engine, Text, "connection_limit", SettingKind.Integer, SettingsCategory.Network)
        );
        Encryption = Add(
            new(Engine, Text, "encryption", SettingKind.Text, SettingsCategory.Network)
        );
        Ratio = Add(
            new(Engine, Text, "ratio_limit", SettingKind.Number, SettingsCategory.Transfers)
        );
        SeedingMinutes = Add(
            new(Engine, Text, "seeding_minutes", SettingKind.Integer, SettingsCategory.Transfers)
            {
                IsDuration = true,
            }
        );
        Adapter = Add(
            new(Engine, Text, "network_interface", SettingKind.Text, SettingsCategory.Network)
        );
        Adapters = [new(string.Empty, Text.Get("settings", "any_adapter"))];
        Adapter.PropertyChanged += (_, _) => UpdateAdapter();
        PortMapping = Add(
            new(Engine, Text, "port_mapping", SettingKind.Boolean, SettingsCategory.Network)
            {
                Condition = CanListen,
            }
        );
        Port = Add(
            new(
                Engine,
                Text,
                "listen_port",
                SettingKind.Integer,
                SettingsCategory.Network
            )
            {
                Minimum = 1,
                Maximum = 65535,
                Condition = CanListen,
            }
        );
        ProblemNotifications = Add(
            new(Engine, Text, "notify_problems", SettingKind.Boolean, SettingsCategory.General)
        );
        FinishedNotifications = Add(
            new(
                Engine,
                Text,
                "notifications_enabled",
                SettingKind.Boolean,
                SettingsCategory.General
            )
        );
        AddedNotifications = Add(
            new(Engine, Text, "notify_added", SettingKind.Boolean, SettingsCategory.General)
        );
        BackgroundNotifications = Add(
            new(Engine, Text, "notify_background", SettingKind.Boolean, SettingsCategory.General)
        );
        PreventSleep = Add(
            new(Engine, Text, "prevent_sleep", SettingKind.Boolean, SettingsCategory.General)
        );
        // Seeding only extends Prevent sleep, so it has no effect while that is off.
        SeedingSleep = Add(
            new(
                Engine,
                Text,
                "prevent_sleep_seeding",
                SettingKind.Boolean,
                SettingsCategory.General
            )
            {
                Condition = () => PreventSleep.IsOn,
            }
        );
        Updates = Add(
            new(Engine, Text, "check_for_updates", SettingKind.Boolean, SettingsCategory.General)
        );
        ShowSplash = Add(
            new(Engine, Text, "show_splash", SettingKind.Boolean, SettingsCategory.General)
        );
        StartInTray = Add(
            new(Engine, Text, "start_in_tray", SettingKind.Boolean, SettingsCategory.General)
        );
        Language = Add(
            new(Engine, Text, "language", SettingKind.Text, SettingsCategory.Appearance)
        );
        Theme = Add(new(Engine, Text, "theme", SettingKind.Text, SettingsCategory.Appearance));
        IncompleteFolder = Add(
            new(Engine, Text, "incomplete_folder", SettingKind.Text, SettingsCategory.Transfers)
            {
                Condition = UsesIncompleteFolder,
            }
        );
        UseIncompleteFolder = Add(
            new(
                Engine,
                Text,
                "use_incomplete_folder",
                SettingKind.Boolean,
                SettingsCategory.Transfers
            )
        );
        AppendSuffix = Add(
            new(Engine, Text, "append_suffix", SettingKind.Boolean, SettingsCategory.Transfers)
        );
        ConfirmExit = Add(
            new(Engine, Text, "confirm_exit", SettingKind.Boolean, SettingsCategory.General)
        );
        ShowExternalIp = Add(
            new(Engine, Text, "show_external_ip", SettingKind.Boolean, SettingsCategory.Appearance)
        );
        ShowTitleSpeeds = Add(
            new(Engine, Text, "show_title_speeds", SettingKind.Boolean, SettingsCategory.Appearance)
        );
        ShowFreeSpace = Add(
            new(Engine, Text, "show_free_space", SettingKind.Boolean, SettingsCategory.Appearance)
        );
        ShowMemory = Add(
            new(Engine, Text, "show_memory", SettingKind.Boolean, SettingsCategory.Appearance)
        );
        RefreshInterval = Add(
            new(
                Engine,
                Text,
                "refresh_interval",
                SettingKind.Integer,
                SettingsCategory.Advanced
            )
            {
                Minimum = 1000,
                Maximum = 10000,
            }
        );
        RecentInterval = Add(
            new(
                Engine,
                Text,
                "recent_interval",
                SettingKind.Integer,
                SettingsCategory.Advanced
            )
            {
                Minimum = 1,
                Maximum = 10,
            }
        );
        HistoryInterval = Add(
            new(
                Engine,
                Text,
                "history_interval",
                SettingKind.Integer,
                SettingsCategory.Advanced
            )
            {
                Minimum = 10,
                Maximum = 300,
            }
        );
        CapacityDownload = Add(
            new(Engine, Text, "capacity_download", SettingKind.Number, SettingsCategory.Limits)
        );
        CapacityUpload = Add(
            new(Engine, Text, "capacity_upload", SettingKind.Number, SettingsCategory.Limits)
        );
        DiskBuffer = Add(
            new(
                Engine,
                Text,
                "disk_buffer_mib",
                SettingKind.Integer,
                SettingsCategory.Advanced
            )
            {
                Minimum = 1,
                Maximum = 1024,
            }
        );
        CheckingMemory = Add(
            new(
                Engine,
                Text,
                "checking_memory_mib",
                SettingKind.Integer,
                SettingsCategory.Advanced
            )
            {
                Minimum = 1,
                Maximum = 1024,
            }
        );
        HashingThreads = Add(
            new(
                Engine,
                Text,
                "hashing_threads",
                SettingKind.Integer,
                SettingsCategory.Advanced
            )
            {
                Minimum = 1,
                Maximum = 64,
            }
        );
        FilePool = Add(
            new(
                Engine,
                Text,
                "file_pool_size",
                SettingKind.Integer,
                SettingsCategory.Advanced
            )
            {
                Minimum = 1,
                Maximum = 10000,
            }
        );
        FixHandlers = new RelayCommand(() => RegisterHandlers(), () => CanRegister);
        Connection = new(this, owner, client);
        UseStartup = new RelayCommand(() => Register("enable_startup"), () => CanRegister);
    }

    // Snapshots confirm, departure saves and search lists only registered settings.
    private Setting Add(Setting setting)
    {
        setting.Changed += (_, _) => Changed();
        setting.Failed += (_, error) => Report(setting, error);
        _all.Add(setting);
        return setting;
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
            _owner.Notifications.Report(error);
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
            Changed();
    }

    internal Setting AddPreference(string source) =>
        source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            ? MagnetDialog
            : TorrentDialog;

    internal async Task<long> Save(object changes)
    {
        var reply = await _client.Send("settings", new { changes }, Requester.Person);
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
    internal Task ResetAll() => _client.Send("reset_settings", null, Requester.Person);

    // The window shows the language before it saves, so a language that the
    // engine cannot save is reported instead of skipped like a disabled field.
    internal async Task SaveLanguage(string language)
    {
        Language.Input = language;
        await Language.Submit(language);
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
            await EngineProcess.RepairMachine(classes);
        }
        catch (Exception error)
        {
            _owner.Notifications.Report(error, "failed_registration", null, null);
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
            _registration = await _client.Send(
                "registration",
                new { action },
                action == "observe" ? Requester.Window : Requester.Person
            );
            return true;
        }
        catch (Exception error)
        {
            _owner.Notifications.Report(error, "failed_registration", null, null);
            if (error is CommandException && action != "observe")
                try
                {
                    _registration = await _client.Send(
                        "registration",
                        new { action = "observe" },
                        Requester.Window
                    );
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

    public async Task PrepareLeave()
    {
        var saves = All.Select(setting => setting.Depart()).ToArray();
        var schedule = Schedule.Depart();
        await Task.WhenAll(saves);
        await schedule;
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
        // A condition reads other state, so a row bound to its setting needs
        // the setting's own notice to enable or disable its control.
        if (names.Length == 0)
            foreach (var setting in All.Where(setting => setting.Condition is not null))
                setting.Refresh();
        foreach (var name in names.Length == 0 ? new[] { string.Empty } : names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        foreach (RelayCommand command in new[] { FixHandlers, UseStartup })
            command.Refresh();
    }
}

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
