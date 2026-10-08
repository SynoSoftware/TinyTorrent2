using System.ComponentModel;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows.Input;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Views;

public sealed class Settings : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;
    private readonly PipeClient _client;
    private JsonElement _registration;
    private (string Action, Exception Error)? _registrationError;
    private bool _registering;
    private AdapterChoice? _unavailableAdapter;
    public Strings Text => _owner.Text;
    public Setting Destination { get; }
    public Setting IncompleteFolder { get; }
    public Setting UseIncompleteFolder { get; }
    public Setting AppendSuffix { get; }
    public Setting ConfirmExit { get; }
    public Setting ShowExternalIp { get; }
    public Setting ShowTitleSpeeds { get; }
    public Setting ShowFreeSpace { get; }
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
    public Setting ShowAdd { get; }
    public Setting StartDownload { get; }
    public Setting QueueTop { get; }
    public Setting Preallocate { get; }
    public Setting UseLastFolder { get; }
    public Setting StartPaused { get; }
    public Setting RaiseAdd { get; }
    public Setting Layout { get; }
    public Setting Duplicates { get; }
    public Setting Exclude { get; }
    public Setting Patterns { get; }
    public Setting Deletion { get; }
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
    public Setting PreventSleep { get; }
    public Setting SeedingSleep { get; }
    public Setting Updates { get; }
    public Schedule Schedule { get; }
    public Setting Language { get; }
    public Setting Theme { get; }
    public string StandardSummary => LimitSummary(Download, Upload);
    public string AlternativeSummary => LimitSummary(AlternativeDownload, AlternativeUpload);

    private string LimitSummary(Setting download, Setting upload) =>
        Text.Format("settings", "caps_summary", LimitText(download), LimitText(upload));

    private string LimitText(Setting setting) =>
        setting.ConfirmedNumber > 0
            ? Text.Format("units", "rate", Text.Bytes(setting.ConfirmedNumber))
            : Text.Get("transfer_limits", "unlimited");
    public IReadOnlyList<Setting> All { get; }
    internal Setting? RefusedSetting =>
        All.FirstOrDefault(setting => setting.HasDraft && setting.Failure is CommandException);
    public bool HasError =>
        RefusedSetting is not null || Schedule.HasDraft && Schedule.HasScheduleError;
    public bool IsPending =>
        All.Any(setting => setting.IsPending) || _registering || Schedule.IsPending || Connection.IsPending;
    public bool CanEdit => _owner.CanEdit;
    internal bool CanSave => _owner.CanSave;
    public bool CanRegister => CanEdit && !_registering;
    private bool HasStartupError =>
        _registrationError?.Action is "enable_startup" or "disable_startup" or "open_startup";
    private string RegistrationMessage =>
        _registrationError is { } failure ? Text.Error(failure.Error) : string.Empty;
    public string StartupMessage => HasStartupError ? RegistrationMessage : string.Empty;
    public string HandlersMessage => !HasStartupError ? RegistrationMessage : string.Empty;
    public bool HasRegistration => _registration.ValueKind == JsonValueKind.Object;

    // Another TinyTorrent copy's entry is still TinyTorrent's registration, so
    // it counts as on and the Other message names that copy.
    public bool Startup => Registered("startup") != "none";
    public string StartupOtherMessage => Other("startup");
    public bool HandlersRegistered => Registered("handlers") != "none";

    // A default that starts a missing program needs action before another
    // TinyTorrent copy does, so its message takes the caution line.
    public string HandlersCaution =>
        IsBroken
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
    private bool IsBroken => Broken().Any();

    // Repair then asks Windows for an administrator, which Windows marks with
    // a shield on the control that asks.
    public bool Elevates => Broken().Any(entry => entry.Machine);

    // Windows lets only the person choose the default app, so a registration
    // that is not the default yet offers Windows Default apps. A broken
    // default, whichever app it belongs to, offers Repair, and another copy's
    // registration offers Use this copy. All three run the same action, which
    // removes broken entries, registers this copy and opens Windows Default
    // apps only while another working app is the default, so the link offers
    // one step at a time.
    public bool NeedsDefaults =>
        IsBroken
        || HandlersRegistered
            && (
                Registered("handlers") == "other"
                || Field("torrent_default").ValueKind == JsonValueKind.False
                || Field("magnet_default").ValueKind == JsonValueKind.False
            );
    public string DefaultsAction =>
        Text.Get(
            "settings",
            IsBroken ? "repair_defaults"
                : Registered("handlers") == "other" ? "use_this_copy"
                : "defaults_settings"
        );
    public string StartupAction =>
        Text.Get(
            "settings",
            Registered("startup") == "other" ? "use_this_copy" : "startup_settings"
        );
    public string DefaultsMessage
    {
        get
        {
            if (IsBroken)
                return Text.Get("settings", "repair_defaults_tip");
            var detail = Text.Get("settings", "defaults_detail");
            if (!HandlersRegistered)
                return detail;
            var torrent = Field("torrent_default");
            var magnet = Field("magnet_default");
            if (torrent.ValueKind == JsonValueKind.True && magnet.ValueKind == JsonValueKind.False)
                return Text.Get("settings", "magnet_default");
            if (magnet.ValueKind == JsonValueKind.True && torrent.ValueKind == JsonValueKind.False)
                return Text.Get("settings", "torrent_default");
            return detail;
        }
    }
    public ICommand OpenDefaults { get; }
    public ICommand OpenStartup { get; }

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

    internal Settings(MainViewModel owner, PipeClient client)
    {
        _owner = owner;
        _client = client;
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
        ShowTitleSpeeds = new(this, "show_title_speeds", SettingKind.Boolean, SettingsCategory.Appearance);
        ShowFreeSpace = new(this, "show_free_space", SettingKind.Boolean, SettingsCategory.Appearance);
        RefreshInterval = new(this, "refresh_interval", SettingKind.Integer, SettingsCategory.Advanced, 1000, 10000);
        RecentInterval = new(this, "recent_interval", SettingKind.Integer, SettingsCategory.Advanced, 1, 10);
        HistoryInterval = new(this, "history_interval", SettingKind.Integer, SettingsCategory.Advanced, 10, 300);
        CapacityDownload = new(this, "capacity_download", SettingKind.Number, SettingsCategory.Limits);
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
        ShowAdd = new(this, "show_add", SettingKind.Boolean, SettingsCategory.Transfers);
        StartDownload = new(this, "starts_download", SettingKind.Boolean, SettingsCategory.Transfers);
        QueueTop = new(this, "queue_top", SettingKind.Boolean, SettingsCategory.Transfers);
        Preallocate = new(this, "preallocate", SettingKind.Boolean, SettingsCategory.Transfers);
        UseLastFolder = new(this, "use_last_folder", SettingKind.Boolean, SettingsCategory.Transfers);
        StartPaused = new(this, "start_paused", SettingKind.Boolean, SettingsCategory.General);
        RaiseAdd = new(this, "raise_add", SettingKind.Boolean, SettingsCategory.Transfers)
        {
            Condition = () => ShowAdd.IsOn,
        };
        Layout = new(this, "layout", SettingKind.Text, SettingsCategory.Transfers);
        Duplicates = new(this, "duplicates", SettingKind.Text, SettingsCategory.Transfers);
        Exclude = new(this, "exclude", SettingKind.Boolean, SettingsCategory.Transfers);
        Patterns = new(this, "patterns", SettingKind.Text, SettingsCategory.Transfers)
        {
            Condition = () => Exclude.IsOn,
        };
        Deletion = new(this, "deletion", SettingKind.Text, SettingsCategory.Transfers);
        InactiveTime = new(this, "inactive_time", SettingKind.Integer, SettingsCategory.Transfers, 0, 525600)
        {
            IsDuration = true,
        };
        Watch = new(this, "watch", SettingKind.Boolean, SettingsCategory.Transfers);
        WatchPath = new(this, "watch_path", SettingKind.Text, SettingsCategory.Transfers);
        WatchRecursive = new(this, "watch_recursive", SettingKind.Boolean, SettingsCategory.Transfers)
        {
            Condition = () => Watch.IsOn,
        };
        WatchDestination = new(this, "watch_destination", SettingKind.Text, SettingsCategory.Transfers)
        {
            Condition = () => Watch.IsOn,
        };
        RecheckFinished = new(this, "recheck_finished", SettingKind.Boolean, SettingsCategory.Advanced);
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
            SettingsCategory.Limits
        );
        ActiveSeeds = new(this, "active_seeds", SettingKind.Integer, SettingsCategory.Limits);
        ConnectionLimit = new(
            this,
            "connection_limit",
            SettingKind.Integer,
            SettingsCategory.Limits
        );
        Encryption = new(this, "encryption", SettingKind.Text, SettingsCategory.Network);
        ActiveTotal = new(this, "active_total", SettingKind.Integer, SettingsCategory.Limits);
        TorrentConnections = new(this, "per_torrent_connections", SettingKind.Integer, SettingsCategory.Limits, 2, 10000)
        {
            HasUnlimited = true,
        };
        IgnoreSlow = new(this, "ignore_slow", SettingKind.Boolean, SettingsCategory.Limits);
        SlowDownload = new(this, "slow_download", SettingKind.Rate, SettingsCategory.Limits, 0, 1073741824)
        {
            HasUnlimited = false,
            Condition = () => IgnoreSlow.IsOn,
        };
        SlowUpload = new(this, "slow_upload", SettingKind.Rate, SettingsCategory.Limits, 0, 1073741824)
        {
            HasUnlimited = false,
            Condition = () => IgnoreSlow.IsOn,
        };
        SlowWait = new(this, "slow_wait", SettingKind.Integer, SettingsCategory.Limits, 1, 86400)
        {
            Condition = () => IgnoreSlow.IsOn,
        };
        ActiveChecking = new(this, "active_checking", SettingKind.Integer, SettingsCategory.Advanced, 1, 64);
        Transport = new(this, "transport", SettingKind.Text, SettingsCategory.Network);
        IpFamily = new(this, "ip_family", SettingKind.Text, SettingsCategory.Network)
        {
            Condition = CanListen,
        };
        OutgoingRate = new(this, "outgoing_rate", SettingKind.Integer, SettingsCategory.Network, 1, 1000);
        Dht = new(this, "dht", SettingKind.Boolean, SettingsCategory.Network);
        Pex = new(this, "pex", SettingKind.Boolean, SettingsCategory.Network);
        Lsd = new(this, "lsd", SettingKind.Boolean, SettingsCategory.Network);
        IncludeOverhead = new(this, "include_overhead", SettingKind.Boolean, SettingsCategory.Limits);
        LimitLan = new(this, "limit_lan", SettingKind.Boolean, SettingsCategory.Limits);
        Proxy = new(this, client);
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
            Condition = () => new[] { Ratio, SeedingMinutes, InactiveTime }
                .Count(setting => setting.ConfirmedNumber > 0) > 1,
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
            Destination,
            ShowAdd,
            StartDownload,
            QueueTop,
            Preallocate,
            UseLastFolder,
            StartPaused,
            RaiseAdd,
            Layout,
            Duplicates,
            Exclude,
            Patterns,
            Deletion,
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
        OpenDefaults = new RelayCommand(() => RegisterHandlers(), () => CanRegister);
        Connection = new(this, owner, client);
        OpenStartup = new RelayCommand(
            () => Register(Registered("startup") == "other" ? "enable_startup" : "open_startup"),
            () => CanRegister
        );
    }

    public void RefreshAdapters()
    {
        try
        {
            var choices = new List<AdapterChoice>
            {
                new(string.Empty, Text.Get("settings", "any_adapter")),
            };
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                choices.Add(new(adapter.Id, adapter.Name));
            Adapters = choices;
            _unavailableAdapter = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Adapters)));
            UpdateAdapter();
        }
        catch (NetworkInformationException error)
        {
            Adapter.Reject(error);
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

    internal async Task Save(object changes)
    {
        await _client.Send("settings", new { changes });
        var confirmation = JsonSerializer.SerializeToElement(changes);
        var changed = false;
        foreach (var setting in All)
            if (confirmation.TryGetProperty(setting.Name, out var value))
                changed |= setting.Confirm(value);
        if (changed)
            Changed();
        _owner.RequestSnapshot();
    }

    internal async Task SaveLanguage(string language)
    {
        Language.Input = language;
        await Submit(Language, language);
        if (Language.Failure is { } error)
            throw error;
    }

    internal Task HideAddDialog() => Save(new { show_add = false });

    public Task ObserveRegistration() => Register("observe");

    public Task SetStartup(bool enabled) =>
        Register(enabled ? "enable_startup" : "disable_startup");

    public Task SetHandlers(bool enabled) =>
        Register(enabled ? "open_defaults" : "unregister_handlers");

    // The engine removes the person's broken handlers itself. All-users ones
    // need an administrator, so the engine runs once more with Windows'
    // administrator prompt; declining it leaves them as they are. Only the
    // link, which shows the shield, asks for an administrator; the switch
    // never does.
    private async Task RegisterHandlers()
    {
        // Register skips silently when it cannot run, which would leave an
        // earlier observation to decide the administrator repair.
        if (!CanRegister)
            return;
        await Register("open_defaults");
        var classes = Broken()
            .Where(entry => entry.Machine)
            .Select(entry => entry.Class)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_registrationError is not null || classes.Count == 0)
            return;
        _registering = true;
        Changed(nameof(CanRegister), nameof(IsPending));
        try
        {
            await _client.RepairMachine(classes);
        }
        finally
        {
            _registering = false;
        }
        await Register("observe");
    }

    public void SelectLanguage(string language)
    {
        if (CanSelectLanguage)
            _owner.SelectLanguage(language);
    }

    private async Task Register(string action)
    {
        if (!CanRegister)
            return;
        _registering = true;
        _registrationError = null;
        // Every property would also refresh the switches, which would show the
        // state before this change until the engine answers.
        Changed(
            nameof(CanRegister),
            nameof(IsPending),
            nameof(StartupMessage),
            nameof(HandlersMessage)
        );
        try
        {
            _registration = await _client.Send("registration", new { action });
        }
        catch (Exception error)
        {
            _registrationError = (action, error);
            if (error is CommandException && action != "observe")
                try
                {
                    _registration = await _client.Send("registration", new { action = "observe" });
                }
                catch (Exception) { }
        }
        finally
        {
            _registering = false;
            Changed();
        }
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

    public async Task<bool> PrepareLeave()
    {
        if (!await Connection.Depart())
            return false;
        await Task.WhenAll(All.Select(setting => setting.Saving));
        var saved = true;
        foreach (var setting in All)
            if (!await Depart(setting))
                saved = false;
        if (!saved)
            return false;
        while (All.FirstOrDefault(setting => !CanLeave(setting)) is { } remaining)
            if (!await Depart(remaining))
                return false;
        return await Schedule.Depart();
    }

    public async Task<bool> Depart(Setting setting)
    {
        await setting.Saving;
        if (!setting.HasDraft)
            return true;
        // A draft that its condition has disabled can be neither corrected nor
        // saved, so leaving discards it like invalid input.
        if (!TryValue(setting, out var value) || setting.Condition?.Invoke() == false)
        {
            setting.Cancel();
            return true;
        }
        if (!_owner.CanSave)
            return !_owner.IsPicking;
        if (setting.Failure is CommandException)
            return false;
        await Submit(setting, value);
        return CanLeave(setting);
    }

    private bool CanLeave(Setting setting) =>
        !setting.HasDraft
        || !_owner.CanSave && !_owner.IsPicking
        || setting.Failure is not null and not CommandException;

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
        if (!_owner.CanSave)
        {
            setting.Reject(new IOException(Text.Get("connection", "unavailable")));
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
                    await Save(new Dictionary<string, object> { [setting.Name] = submitted.Value });
                    setting.Accept(JsonSerializer.SerializeToElement(submitted.Value), submitted);
                }
                catch (Exception error)
                {
                    failure = error;
                }
                if (
                    _owner.CanSave
                    && (failure is null or CommandException)
                    && setting.TakeIntent() is { } next
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
            if (!TryRate(setting.Input, out var bytes) || bytes > setting.Maximum)
                return false;
            value = bytes;
            return true;
        }
        if (
            !double.TryParse(
                setting.Input,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture,
                out var number
            )
        )
            return false;
        if (!double.IsFinite(number) || number < 0)
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

    // A speed limit is typed in KiB/s and sent in bytes a second.
    internal static bool TryRate(string input, out int bytes)
    {
        bytes = 0;
        if (
            !double.TryParse(
                input,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture,
                out var number
            )
            || !double.IsFinite(number)
            || number < 0
            || number > int.MaxValue / 1024.0
        )
            return false;
        bytes = checked((int)Math.Round(number * 1024));
        return true;
    }

    internal static string RateInput(int bytes) =>
        (bytes / 1024.0).ToString("G", CultureInfo.CurrentCulture);

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
        Schedule.Refresh();
    }

    // Without names, every property changed.
    internal void Changed(params string[] names)
    {
        Connection?.Refresh();
        foreach (var name in names.Length == 0 ? new[] { string.Empty } : names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        foreach (RelayCommand command in new[] { OpenDefaults, OpenStartup })
            command.Refresh();
    }
}

public sealed class Setting(
    Settings owner,
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
    public bool IsDuration { get; init; }
    public string DurationUnit { get; private set; } = "minutes";
    internal int DurationScale => IsDuration && DurationUnit == "hours" ? 60 : 1;

    // Other state that must hold before the setting can be edited.
    internal Func<bool>? Condition { get; init; }
    public bool IsRate => Kind == SettingKind.Rate;

    // Empty limits save 0, which the engine reads as unlimited.
    public bool HasUnlimited { get; init; } =
        kind is SettingKind.Rate or SettingKind.Integer or SettingKind.Number && minimum == 0;
    internal string ConfirmedText => _confirmedInput;
    internal double ConfirmedNumber =>
        _confirmed.ValueKind == JsonValueKind.Number ? _confirmed.GetDouble() : 0;
    internal bool ConfirmedOn => _confirmed.ValueKind == JsonValueKind.True;
    public string Label => owner.Text.Get(IsRate ? "limits" : "settings", Name);
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
            _uncertain = null;
            _invalid = false;
            Refresh();
            owner.Changed();
        }
    }
    public bool IsOn => _choice ?? _confirmed.ValueKind == JsonValueKind.True;
    public bool HasDraft =>
        _input != _confirmedInput
        || _choice is { } choice && choice != (_confirmed.ValueKind == JsonValueKind.True);
    public bool IsPending => _saving is not null;
    internal Exception? Failure => _failure;
    internal Task Saving => _saving?.Task ?? Task.CompletedTask;

    // A save keeps its control enabled so later input keeps focus.
    public bool CanEdit => owner.CanEdit && (Condition?.Invoke() ?? true);
    public string Message
    {
        get
        {
            if (!_invalid)
                return _failure is null ? string.Empty : owner.Text.Error(_failure);
            if (IsDuration)
                return owner.Text.Get("settings", "invalid_duration");
            if (Minimum > 0)
                return owner.Text.Format("settings", "invalid_range", Minimum, Maximum);
            return owner.Text.Get(
                IsRate ? "errors" : "settings",
                IsRate ? "invalid_limits"
                    : Kind == SettingKind.Integer ? "invalid_count"
                    : "invalid_number"
            );
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool Confirm(JsonElement value)
    {
        var changed =
            _confirmed.ValueKind != value.ValueKind
            || _confirmed.GetRawText() != value.GetRawText();
        if (changed)
        {
            var preserve = HasDraft || IsPending;
            _confirmed = value.Clone();
            _confirmedInput =
                value.ValueKind == JsonValueKind.String ? value.GetString()!
                : value.ValueKind != JsonValueKind.Number || HasUnlimited && value.GetDouble() == 0
                    ? string.Empty
                : IsRate ? Settings.RateInput(value.GetInt32())
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
        if (
            double.TryParse(
                _input,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture,
                out var number
            )
            && double.IsFinite(number)
            && number >= 0
        )
            _input = (number * scale / DurationScale).ToString("G", CultureInfo.CurrentCulture);
        _confirmedInput = ConfirmedNumber == 0
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
        _failure = error;
        _uncertain = error is CommandException ? null : submitted;
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
        _uncertain = null;
        _invalid = false;
        Refresh();
        owner.Changed();
    }

    public void Cancel()
    {
        _intent = null;
        _cancelled = IsPending;
        _input = _confirmedInput;
        _choice = null;
        _failure = null;
        _uncertain = null;
        _invalid = false;
        Refresh();
        owner.Changed();
    }

    internal void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
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
