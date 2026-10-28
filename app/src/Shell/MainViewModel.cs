using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Desktop;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shared.Network;
using Syno.TinyTorrent.Torrents;
using LibraryBrowser = Syno.TinyTorrent.Library.Browser;
using SubtitleAcquisition = Syno.TinyTorrent.Subtitles.Acquisition;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PipeClient _client;
    private readonly ICommand[] _commands;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, Torrent> _byId = [];
    private readonly List<Torrent> _torrents = [];
    private readonly object _snapshotGate = new();
    private JsonElement? _latest;
    private bool _applyQueued;
    private JsonElement? _detail;
    private bool _detailQueued;
    private bool _refreshDetail;
    private Torrent[] _selected = [];
    private Torrent? _current;
    private double _downloadRate;
    private double _uploadRate;
    private double? _moveRate;
    private double? _checkRate;
    private long? _freeSpace;
    private Task? _spaceRead;
    private string? _spacePath;
    private long _spaceChecked;

    // The last startup state an engine reported; it stays after a disconnect,
    // which shows the last known state.
    private EngineStartup _startup = EngineStartup.Loading;
    private bool _languageSaved = true;
    private string? _startupError;
    private bool _changingLanguage;
    private string _requestedLanguage = "en";
    private int _languageRevision;
    private Task _languageLoad = Task.CompletedTask;
    private string[] _revealIds = [];

    // Null until an engine answers; then the last session that did.
    private string? _sessionId;
    private bool _connected;
    private PipeClient.Disconnection? _disconnection;
    private bool _loaded;
    private bool _shuttingDown;
    private bool _closed;
    private bool _picking;
    private bool _dark;
    private bool _toolbarOpen = true;
    private bool _showsSettingsIndex = true;
    private bool _applyingSnapshot;
    private long _limitsMinute;
    private (bool CanClose, bool CanExit, bool HasDraft, bool AddOpen)? _taskState;
    private (SubtitleAcquisition? Subtitles, int LookupLimit)? _featureState;

    // The update check uses it with or without the Library database.
    private readonly ProviderHttp _providerHttp = new();
    private bool _applyingSettings;

    public Strings Text { get; }
    internal BackgroundWork Work { get; }
    public IReadOnlyList<Torrent> Torrents => _torrents;
    public AddDraft AddDraft { get; }
    public Editor Settings { get; }
    public bool ShowsSettingsIndex
    {
        get => _showsSettingsIndex;
        set
        {
            if (_showsSettingsIndex == value)
                return;
            _showsSettingsIndex = value;
            Changed(nameof(ShowsSettingsIndex));
        }
    }

    // Where Settings opens without a category: its index, or General when the
    // person hid the index.
    public SettingsCategory? SettingsHome => ShowsSettingsIndex ? null : SettingsCategory.General;
    public LibraryBrowser Library { get; }
    internal Features Features { get; }
    internal Updates Updates { get; }
    internal SubtitleAcquisition? Subtitles => Features.Subtitles;
    internal Geolocation Geolocation { get; }
    internal int LookupLimit => Features.LookupLimit;
    public string Theme => Settings.Theme.ConfirmedText is { Length: > 0 } theme ? theme : "system";
    public bool ShowsTitleSpeeds => Settings.ShowTitleSpeeds.IsOn;
    public bool ShowsFreeSpace =>
        IsRunning
        && Settings.ShowFreeSpace.IsOn
        && _spacePath == Settings.Destination.ConfirmedText
        && _freeSpace.HasValue;
    public string FreeSpaceStatus =>
        _spacePath == Settings.Destination.ConfirmedText && _freeSpace is { } space
            ? Text.Format("window", "free_space", Text.Bytes(space))
            : Text.Get("window", "space_unknown");
    public string DownloadSpeed => Rate(_downloadRate);
    public string UploadSpeed => Rate(_uploadRate);
    // Empty while no move runs.
    public string MoveSpeed => _moveRate is { } rate ? Text.CopyRate(rate) : string.Empty;
    public string MoveTip => Text.Format("window", "move_rate", MoveSpeed);
    // Empty while no torrent's files are being verified.
    public string CheckSpeed => _checkRate is { } rate ? Text.CopyRate(rate) : string.Empty;
    public string CheckTip => Text.Format("window", "check_rate", CheckSpeed);
    public bool IsConnected => _connected;

    // The engine answers and has loaded its saved settings and transfers.
    internal bool IsRunning => _connected && _startup == EngineStartup.Ready;

    // The window can show its first state: the saved settings, or the storage
    // failure that replaces them.
    private bool IsDisplayable => _connected && _startup != EngineStartup.Loading;
    internal double DownloadRate => _downloadRate;
    internal double UploadRate => _uploadRate;
    internal TransferTotals SessionTotals { get; private set; }
    internal TransferTotals LifetimeTotals { get; private set; }
    public bool IsLoading => !_connected || _startup == EngineStartup.Loading;
    public bool IsStorageFailed => _startup == EngineStartup.Failed;
    public bool IsEmptyVisible => _startup != EngineStartup.Failed;
    internal bool IsClosed => _closed;
    public bool CanClose =>
        !_changingLanguage
        && !_picking
        && !_receivingActivations
        && !AddDraft.IsBusy
        && !Inspector.IsPending
        && !Settings.IsPending
        && !MoveDraft.IsPending
        && !RemovalDraft.IsPending
        && !SpeedLimit.IsPending
        && _limitsChoice is not { Sequence: null };
    public bool CanExit => _connected && CanClose;
    public string? DataDirectory => _client.DataDirectory;

    // The first draft the window resolves before it closes, in the order it
    // resolves them; null when none remains.
    internal UnfinishedDraft? RemainingDraft =>
        AddDraft.HasDraft ? UnfinishedDraft.Add
        : MoveDraft.HasDraft ? UnfinishedDraft.Move
        : Inspector.HasDraft ? UnfinishedDraft.Inspector
        : null;
    public bool HasDraft => RemainingDraft is not null;
    internal bool CanSave => IsRunning && !_shuttingDown && !_picking;
    public bool CanEdit => CanSave && !IsClosing;
    public bool IsPicking
    {
        get => _picking;
        set
        {
            if (_picking == value)
                return;
            _picking = value;
            Refresh();
        }
    }
    public bool IsDark
    {
        get => _dark;
        set
        {
            if (_dark == value)
                return;
            _dark = value;
            Changed(nameof(IsDark));
        }
    }
    public ICommand Add { get; }
    public ICommand Pause { get; }
    public ICommand Resume { get; }
    public ICommand Exit { get; }
    public ICommand CloseWindow { get; }
    public ICommand SwitchTheme { get; }
    public bool IsToolbarOpen
    {
        get => _toolbarOpen;
        set
        {
            if (_toolbarOpen == value)
                return;
            _toolbarOpen = value;
            Changed(nameof(IsToolbarOpen));
        }
    }
    public ICommand SwitchToolbar { get; }

    private string Rate(double rate) => IsRunning ? Text.Rate(rate) : "—";

#if CAPTURE
    private Status? _presentedStatus;

    // A status a capture journey shows without the state behind it.
    internal Status? PresentedStatus
    {
        get => _presentedStatus;
        set
        {
            _presentedStatus = value;
            Changed(nameof(CurrentStatus));
        }
    }
#endif

    // The lasting problem the window reports in its message bar, most
    // important first; null when nothing needs saying. While the engine
    // starts, the list shows its own loading state instead.
    internal Status? CurrentStatus
    {
        get
        {
#if CAPTURE
            if (_presentedStatus is { } presented)
                return presented;
#endif
            var reason = _disconnection?.Reason ?? string.Empty;
            if (!_connected)
            {
                if (_sessionId is null && reason.Length == 0)
                    return null;
                var title =
                    _disconnection is { IsOtherVersion: true } ? "other_version"
                    : _sessionId is null ? "not_started"
                    : "stopped";
                var stopped = Text.Get("window", "disconnected");
                return new(
                    Text.Get("notifications", title),
                    _sessionId is null ? reason
                        : reason.Length == 0 ? stopped
                        : Text.Format("errors", "detail", stopped, reason),
                    InfoBarSeverity.Error,
                    CanRestart
                        ? new StatusAction(Restart, RestartText, Syno.Lucide.RotateCcw, Text.Get("connection", "restart_tip"))
                        : null
                );
            }
            if (_startup == EngineStartup.Failed)
                return new(
                    Text.Get("notifications", "storage"),
                    string.IsNullOrWhiteSpace(_startupError)
                        ? Text.Get("window", "storage_failed")
                        : Text.Format(
                            "errors",
                            "detail",
                            Text.Get("window", "storage_failed"),
                            _startupError
                        ),
                    InfoBarSeverity.Error,
                    new(
                        OpenDataFolder,
                        Text.Get("commands", "open_folder"),
                        Syno.Lucide.FolderOpen,
                        Text.Get("notifications", "storage_tip")
                    )
                );
            if (_startup == EngineStartup.Loading)
                return null;
            if (_stopped is { } condition)
                return StoppedStatus(condition);
            if (_locations.Count > 0)
                return LocationStatus();
            if (Features.Failure is { } featureFailure)
                return FeatureStatus(featureFailure);
            if (
                Subtitles is { Failure: var failure } subtitles
                && (subtitles.Enabled || subtitles.Finding)
                && failure
                    is SubtitleFailure.Authentication
                        or SubtitleFailure.Unconfigured
                        or SubtitleFailure.Unavailable
                        or SubtitleFailure.Route
                        or SubtitleFailure.Save
            )
                return new(
                    Text.Get("notifications", "subtitles_paused"),
                    Text.Get("subtitles", "failure_" + failure.ToString().ToLowerInvariant()),
                    InfoBarSeverity.Warning,
                    // A folder that refused a file is fixed outside the app,
                    // so its action tries the work again.
                    failure == SubtitleFailure.Save
                        ? new StatusAction(
                            RetrySubtitleWork,
                            Text.Get("inspector", "retry"),
                            Syno.Lucide.RotateCcw,
                            Text.Get("notifications", "subtitles_retry_tip")
                        )
                        : new StatusAction(
                            ShowSubtitleSettings,
                            Text.Get("notifications", "settings"),
                            Syno.Lucide.Settings,
                            Text.Get("notifications", "subtitles_tip")
                        )
                );
            if (!_languageSaved)
                return new(
                    Text.Get("notifications", "language"),
                    Text.Get("errors", "language_unsaved"),
                    InfoBarSeverity.Warning,
                    new(
                        ShowSettings,
                        Text.Get("notifications", "settings"),
                        Syno.Lucide.Settings,
                        Text.Get("notifications", "language_tip")
                    )
                );
            return null;
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? TextChanged;
    internal event EventHandler<SnapshotAppliedEventArgs>? SnapshotApplied;
    public event EventHandler<Torrent[]>? RevealRequested;
    public event EventHandler<Torrent[]>? ReleaseRequested;
    public event EventHandler? AddRequested;
    public event EventHandler? ActivateRequested;
    public event EventHandler? ShowRequested;
    internal event EventHandler<CloseRequest>? CloseRequested;
    public event EventHandler? ConfirmExitRequested;
    public ICommand Restart { get; }
    public bool CanRestart => IsDisconnected && _disconnection is { IsOtherVersion: false };
    public bool IsDisconnected => !_connected && _disconnection is not null;
    public string RestartText => Text.Get("connection", "restart");
    private ICommand RetryFeatures { get; }
    private ICommand RetryAll { get; }
    private ICommand ShowErrors { get; }
    private ICommand DeleteDatabase { get; }
    private ICommand RepairDatabase { get; }
    internal ICommand ShowSubtitleSettings { get; }
    private ICommand RetrySubtitleWork { get; }
    public ICommand OpenDataFolder { get; }

    internal MainViewModel(Strings strings, DispatcherQueue dispatcher, Navigation navigation)
    {
        Navigation = navigation;
        Navigation.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Navigation.Current))
                RefreshNavigation();
        };
        Text = strings;
        Notifications = new Notifications(this, strings);
        _dispatcher = dispatcher;
        Work = new(dispatcher);
        _client = new PipeClient(strings);
        Library = new LibraryBrowser(strings, dispatcher, Work);
        // Before Settings, whose lookup limit field reads LookupLimit.
        Features = new Features(this, _client, _providerHttp, dispatcher);
        Features.Changed += OnFeaturesChanged;
        Work.Completed += (_, result) =>
        {
            if (!IsClosing && !Features.IsClosed)
                Notifications.ShowWorkResult(result);
        };
        Settings = new Editor(this, _client);
        AddDraft = new AddDraft(this, _client, strings);
        MoveDraft = new MoveDraft(this, _client);
        RemovalDraft = new RemovalDraft(this, _client);
        SpeedLimit = new SpeedLimit(this, _client);
        Geolocation = new Geolocation(_providerHttp, Work);
        Geolocation.Failed += (_, error) => Notifications.Report(error);
        Inspector = new Inspector(this, _client);
        Updates = new Updates(_providerHttp, Work, _client, Settings.Updates);
        Updates.Changed += (_, _) => Changed(nameof(HasUpdate));
        Settings.AttachLibrary();
        Library.RetryRequested += (_, _) => RequestSnapshot();
        Library.FiltersRequested += (_, _) => IsFilterOpen = true;
        Library.PropertiesRequested += (_, _) => OpenInspector();
        Library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(Library.Providers) or "")
                RefreshProtectionScopes();
            if (Page != WindowPage.Library)
                return;
            if (args.PropertyName is nameof(Library.Query) or "")
                Changed(nameof(Query));
            if (args.PropertyName is nameof(Library.Status) or "")
                Changed(nameof(CountStatus));
        };
        Filters = Enum.GetValues<TorrentFilter>()
            .Select(filter => new FilterChoice(this, filter))
            .ToArray();
        AddDraft.PropertyChanged += OnTaskChanged;
        Inspector.PropertyChanged += OnTaskChanged;
        Inspector.RowsUpdated += (_, section) =>
        {
            if (section == InspectorSection.Peers)
                Changed(nameof(CountStatus));
        };
        Settings.PropertyChanged += (sender, args) =>
        {
            if (!_closed && !_applyingSettings && Features.HasRoute)
                _ = Features.ConfigureRoute();
            OnTaskChanged(sender, args);
        };
        MoveDraft.PropertyChanged += OnTaskChanged;
        RemovalDraft.PropertyChanged += OnTaskChanged;
        SpeedLimit.PropertyChanged += OnTaskChanged;
        Restart = new RelayCommand(
            () =>
            {
                try
                {
                    EngineProcess.Launch(Text, _client.DataDirectory);
                    // Restart starts a process instead of sending a command,
                    // so no reply clears the error.
                    Notifications.ClearError();
                }
                catch (Exception error)
                {
                    Notifications.Report(error);
                }
                return Task.CompletedTask;
            },
            () => CanRestart
        );
        RetryFeatures = new RelayCommand(
            Features.Retry,
            () => _connected && Features.Failure is not null
        );
        DeleteDatabase = new RelayCommand(
            () =>
            {
                DatabaseDeletionRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            },
            () => _connected && Features.Failure is { Database: DatabaseCondition.Incompatible }
        );
        RepairDatabase = new RelayCommand(
            Features.Repair,
            () => _connected && Features.Failure is not null
        );
        Add = new RelayCommand(
            () =>
            {
                FilesRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            },
            () => CanEdit && !AddDraft.IsOpen
        );
        Pause = new RelayCommand(
            () => ActOnSelection("pause"),
            () => CanEdit && _selected.Length > 0
        );
        Resume = new RelayCommand(
            () => ActOnSelection("resume"),
            () => CanEdit && _selected.Length > 0
        );
        Exit = new RelayCommand(RequestExit, () => CanExit);
        CloseWindow = new RelayCommand(
            () =>
            {
                CloseRequested?.Invoke(this, CloseRequest.Close);
                return Task.CompletedTask;
            },
            () => true
        );
        SwitchTheme = new RelayCommand(
            () => Settings.SelectTheme(_dark ? "light" : "dark"),
            () => CanEdit
        );
        AddMagnet = new RelayCommand(
            () =>
            {
                AddDraft.EditingMagnet = true;
                AddDraft.Refresh();
                AddRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            },
            () => CanEdit
        );
        Force = new RelayCommand(
            () => ActOnSelection("force"),
            () => CanEdit && _selected.Length > 0
        );
        Retry = new RelayCommand(
            () => SendRetry(_selected),
            () => CanEdit && _selected.Any(torrent => torrent.CanRetry)
        );
        // Commands refresh when the selection changes, not on every row
        // change, so the message bar's Retry reads no row to enable itself.
        // The bar shows only while the engine reports torrents that need it.
        // It sends retry even when no row can retry, because a removed
        // torrent's failed give-back has no row and every retry tries it again.
        RetryAll = new RelayCommand(
            () => Act("retry", [.. Torrents.Where(torrent => torrent.CanRetry)]),
            () => CanEdit
        );
        SwitchSequential = new RelayCommand(
            () => SetPieceOrder(PieceOrder.Sequential, Sequential != true),
            () => CanEditSelection
        );
        SwitchFirstLast = new RelayCommand(
            () => SetPieceOrder(PieceOrder.FirstLast, FirstLast != true),
            () => CanEditSelection
        );
        LimitSpeed = SpeedCommand(() => _selected);
        SwitchFilters = new RelayCommand(
            () =>
            {
                ShowsFilters = !ShowsFilters;
                return Task.CompletedTask;
            },
            () => true
        );
        SwitchToolbar = new RelayCommand(
            () =>
            {
                IsToolbarOpen = !IsToolbarOpen;
                return Task.CompletedTask;
            },
            () => true
        );
        Verify = new RelayCommand(
            () => ActOnSelection("verify"),
            () => CanEdit && _selected.Length > 0
        );
        Remove = new RelayCommand(
            () =>
            {
                RemoveRequested?.Invoke(this, _selected.ToArray());
                return Task.CompletedTask;
            },
            () => CanEdit && _selected.Length > 0
        );
        MoveFiles = MoveCommand(() => _selected);
        Up = new RelayCommand(() => Queue("up"), () => CanMove);
        Down = new RelayCommand(() => Queue("down"), () => CanMove);
        Top = new RelayCommand(() => Queue("top"), () => CanMove);
        Bottom = new RelayCommand(() => Queue("bottom"), () => CanMove);
        PauseAll = new RelayCommand(() => SessionPause(true), () => CanEdit);
        ResumeAll = new RelayCommand(() => SessionPause(false), () => CanEdit);
        ResolvePause = new RelayCommand(
            () =>
                PausedBy == PauseReason.Adapter ? ShowSetting(Settings.Adapter) : SessionPause(false),
            () => CanEdit
        );
        Limits = new RelayCommand(
            () => RequestSettings(new(SettingsCategory.Limits, "limit_mode")),
            () => true
        );
        Open = new RelayCommand(() => OpenTorrent(false), () => CanEdit && _selected.Length == 1);
        OpenFolder = new RelayCommand(
            () => OpenTorrent(true),
            () => CanEdit && _selected.Length == 1
        );
        CopyMagnet = new RelayCommand(
            () => CopyTorrent(false),
            () => CanEdit && _selected.Length == 1
        );
        CopyHash = new RelayCommand(
            () => CopyTorrent(true),
            () => CanEdit && _selected.Length == 1
        );
        Properties = new RelayCommand(
            () => Inspect(Inspector.Section),
            () => CanEdit && _selected.Length == 1
        );
        ClearFilters = new RelayCommand(
            () =>
            {
                if (Page != WindowPage.Library)
                    return ClearFinding();
                Library.ClearFilters.Execute(null);
                return Task.CompletedTask;
            },
            () =>
                Page == WindowPage.Library
                    ? Library.ClearFilters.CanExecute(null)
                    : Filter != TorrentFilter.All
        );
        Library.ClearFilters.CanExecuteChanged += (_, _) => ((RelayCommand)ClearFilters).Refresh();
        ShowSettings = new RelayCommand(() => RequestSettings(new()), () => true);
        ShowSubtitleSettings = new RelayCommand(
            () => RequestSettings(new(SettingsCategory.Plugins, "subtitle_supplier")),
            () => true
        );
        RetrySubtitleWork = new RelayCommand(
            async () =>
            {
                try
                {
                    await Features.RetrySubtitles();
                }
                catch (Exception error)
                {
                    Features.Report(error);
                }
            },
            () => true
        );
        DownloadSubtitles = new RelayCommand(
            () =>
            {
                var entries = Library
                    .Selection.Items.Cast<Syno.TinyTorrent.Library.LibraryRow>()
                    .Where(row => row.Entry.Kind == FileKind.Video)
                    .Select(row => row.Entry.EntryId)
                    .ToHashSet();
                SubtitlesRequested?.Invoke(this, entries);
                return Task.CompletedTask;
            },
            () => CanDownloadSubtitles
        );
        Library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(Library.Selection) or nameof(Library.IsAvailable) or "")
                ((RelayCommand)DownloadSubtitles).Refresh();
        };
        OpenDataFolder = new RelayCommand(
            () =>
            {
                if (DataDirectory is { } directory)
                    RequestOpen(directory);
                return Task.CompletedTask;
            },
            () => true
        );
        ShowTorrents = new RelayCommand(
            () =>
            {
                TorrentsRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            },
            () => true
        );
        Library.TorrentsRequested += (_, _) => ShowTorrents.Execute(null);
        ShowErrors = new RelayCommand(
            () =>
            {
                Filter = TorrentFilter.Errors;
                ShowsFilters = true;
                ShowTorrents.Execute(null);
                return Task.CompletedTask;
            },
            () => true
        );
        ShowLibrary = new RelayCommand(
            () =>
            {
                LibraryRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            },
            () => true
        );
        ShowAbout = new RelayCommand(
            () =>
            {
                AboutRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            },
            () => true
        );
        _commands =
        [
            Add,
            AddMagnet,
            Pause,
            Resume,
            Force,
            Retry,
            RetryAll,
            SwitchSequential,
            SwitchFirstLast,
            LimitSpeed,
            SwitchFilters,
            SwitchToolbar,
            Verify,
            Remove,
            MoveFiles,
            Up,
            Down,
            Top,
            Bottom,
            PauseAll,
            ResumeAll,
            ResolvePause,
            Open,
            OpenFolder,
            CopyMagnet,
            CopyHash,
            Properties,
            Limits,
            ClearFilters,
            ShowSettings,
            ShowTorrents,
            ShowAbout,
            Exit,
            SwitchTheme,
            Restart,
            RetryFeatures,
            DeleteDatabase,
            RepairDatabase,
        ];
        Settings.Updates.PropertyChanged += (_, _) => ObserveUpdates();
        _client.Snapshot += QueueSnapshot;
        _client.Detail += detail => QueueDetail(detail);
        _client.CommandReplied += reply =>
        {
            QueueDetail(null);
            if (reply is { Succeeded: true, Requester: Requester.Person })
                _dispatcher.TryEnqueue(Notifications.ClearError);
        };
        _client.Toast += toast => _dispatcher.TryEnqueue(() => Notifications.Receive(toast));
        _client.TorrentRequested += torrentId =>
            _dispatcher.TryEnqueue(() =>
            {
                if (!_closed)
                    Reveal([torrentId]);
            });
        // Every card the engine opens is on the General page.
        _client.SettingsRequested += setting =>
            _dispatcher.TryEnqueue(() =>
            {
                if (!_closed)
                    RequestSettings(new(SettingsCategory.General, setting));
            });
        _client.Disconnected += disconnection =>
            _dispatcher.TryEnqueue(() =>
            {
                if (_closed)
                    return;
                _connected = false;
                if (_limitsChoice is { Sequence: not null })
                    _limitsChoice = null;
                _limits = null;
                _waiting = null;
                _restriction = null;
                Unfinished = null;
                _unfinishedAnswered = false;
                _disconnection = disconnection;
                // The engine resolves its conditions only while connected.
                _stopped = null;
                _locations.Clear();
                AddDraft.Invalidate();
                Inspector.Disconnect();
                foreach (var torrent in Torrents)
                    torrent.Disconnect();
                Refresh();
            });
        _client.Control += control =>
            _dispatcher.TryEnqueue(() =>
            {
                if (_closed)
                    return;
                if (control == "activate")
                    ActivateRequested?.Invoke(this, EventArgs.Empty);
                else if (control == "activations")
                    _ = ReceiveActivations();
                else if (control == "confirm_exit")
                    ConfirmExitRequested?.Invoke(this, EventArgs.Empty);
                else
                    CloseRequested?.Invoke(this, CloseRequest.Exit);
            });
        PropertyChanged += (_, _) =>
        {
            if (!_applyingSnapshot)
                SyncMessages();
        };
    }

    public void Start()
    {
        if (_loaded)
            return;
        _loaded = true;
        Refresh();
        _client.Start();
    }

    internal bool Contains(Torrent torrent) =>
        _byId.TryGetValue(torrent.TorrentId, out var current) && ReferenceEquals(current, torrent);

    private void Apply(JsonElement snapshot)
    {
        _applyingSnapshot = true;
        try
        {
            ApplySnapshot(snapshot);
            SyncMessages();
        }
        finally
        {
            _applyingSnapshot = false;
        }
    }

    private void ApplySnapshot(JsonElement snapshot)
    {
        var previousAccess = (_connected, _startup, _shuttingDown);
        var previousError = _startupError;
        var previousLimits = (_limits, MissingAdapter, LimitsIndex);
        var previousIncoming = (
            HasIncoming,
            MissingAdapter,
            Settings.Proxy.IsInUse,
            Settings.Proxy.Route
        );
        var previousIp = (_externalIpv4, _externalIpv6);
        var previousRates = (_downloadRate, _uploadRate, _moveRate, _checkRate);
        var previousTotals = (SessionTotals, LifetimeTotals);
        var previousCount = Torrents.Count;
        var previousSelection = _selected.Length;
        var previousLanguageSaved = _languageSaved;
        var sessionId = snapshot.GetProperty("session_id").GetString()!;
        // Only a disconnect shows the first state again. A new session id alone
        // does not, because a snapshot applied before a queued disconnect can
        // already carry the next session.
        var wasDisplayable = IsDisplayable;
        if (_sessionId != sessionId)
            AddDraft.Invalidate();
        _sessionId = sessionId;
        _connected = true;
        _disconnection = null;
        _startup =
            snapshot.TryGetProperty("storage_failed", out var storage) && storage.GetBoolean()
                ? EngineStartup.Failed
            : snapshot.TryGetProperty("loading", out var loading) && loading.GetBoolean()
                ? EngineStartup.Loading
            : EngineStartup.Ready;
        _startupError = snapshot.TryGetProperty("startup_error", out var detail)
            ? detail.GetString()
            : null;
        _shuttingDown = snapshot.GetProperty("shutting_down").GetBoolean();
        var accessChanged = previousAccess != (_connected, _startup, _shuttingDown);
        var first = !wasDisplayable && IsDisplayable;
        if (_startup != EngineStartup.Ready)
        {
            _limits = null;
            if (accessChanged)
                Refresh();
            else if (previousError != _startupError)
                Changed(nameof(CurrentStatus));
            ObserveUpdates();
            if (first)
                ShowRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        var settings = snapshot.GetProperty("settings");
        _applyingSettings = true;
        try
        {
            Settings.Apply(
                settings,
                snapshot.GetProperty("proxy"),
                snapshot.GetProperty("proxy_check")
            );
            _ = Features.ConfigureRoute();
        }
        finally
        {
            _applyingSettings = false;
        }
        HasIncoming = snapshot.GetProperty("has_incoming").GetBoolean();
        _externalIpv4 = snapshot.GetProperty("external_ipv4").GetString() ?? string.Empty;
        _externalIpv6 = snapshot.GetProperty("external_ipv6").GetString() ?? string.Empty;
        MissingAdapter = snapshot.TryGetProperty("missing_adapter", out var missing)
            ? missing.GetString() ?? string.Empty
            : string.Empty;
        ApplyLimits(snapshot.GetProperty("limits"), snapshot.GetProperty("sequence").GetInt64());
        Settings.Connection.Observe(snapshot.GetProperty("connection_test"));
        // A failed language save keeps the person's choice and its error until they change or cancel it.
        if (
            !_changingLanguage
            && Settings.Language.Failure is null
            && Settings.Language.ConfirmedText is { Length: > 0 } tag
            && tag != _requestedLanguage
        )
            _languageLoad = LoadLanguage(tag);
        var present = new HashSet<string>();
        var rowsChanged = false;
        var inspectedChanged = false;
        foreach (var item in snapshot.GetProperty("torrents").EnumerateArray())
        {
            var torrentId = item.GetProperty("torrent_id").GetString()!;
            present.Add(torrentId);
            if (!_byId.TryGetValue(torrentId, out var torrent))
            {
                torrent = new Torrent(
                    torrentId,
                    Text,
                    id => _byId.TryGetValue(id, out var other) ? other.Name : null
                );
                _byId.Add(torrentId, torrent);
                _torrents.Add(torrent);
                rowsChanged = true;
            }
            var changed = torrent.Update(item);
            rowsChanged |= changed;
            inspectedChanged |= changed && ReferenceEquals(torrent, Inspector.Target);
        }
        foreach (var torrent in Torrents.Where(row => !present.Contains(row.TorrentId)).ToArray())
        {
            _torrents.Remove(torrent);
            _byId.Remove(torrent.TorrentId);
            rowsChanged = true;
        }
        _selected = _selected.Where(row => present.Contains(row.TorrentId)).ToArray();
        var selectionChanged =
            previousSelection != _selected.Length || rowsChanged && _selected.Length > 0;
        if (_current is not null && !present.Contains(_current.TorrentId))
            _current = null;
        var inspected = Inspector.Target;
        if (inspected is { } target && !Contains(target))
            Inspector.Show(
                null,
                allPeers: Inspector.ShowsAllPeers,
                selection: Inspector.PeerSelection
            );
        if (
            Inspector.Target is null
            && (previousSelection != _selected.Length || inspected is not null)
        )
        {
            if (HasInspector && _selected.Length == 1 && !Inspector.HasPeerScope)
                Inspector.Show(_selected[0]);
            else
                Inspector.RefreshScope();
        }
        var languageSaved = snapshot.GetProperty("language_saved").GetBoolean();
        _languageSaved = languageSaved;
        AddDraft.ApplyDefaults(
            settings.GetProperty("addition_destination").GetString() ?? string.Empty,
            settings.GetProperty("starts_download").GetBoolean(),
            settings.GetProperty("queue_top").GetBoolean()
        );
        _downloadRate = snapshot.GetProperty("download_rate").GetDouble();
        _uploadRate = snapshot.GetProperty("upload_rate").GetDouble();
        var moveRate = snapshot.GetProperty("move_rate");
        _moveRate = moveRate.ValueKind == JsonValueKind.Null ? null : moveRate.GetDouble();
        var checkRate = snapshot.GetProperty("check_rate");
        _checkRate = checkRate.ValueKind == JsonValueKind.Null ? null : checkRate.GetDouble();
        var transfers = snapshot.GetProperty("transfers");
        SessionTotals = ReadTotals(transfers.GetProperty("session"));
        LifetimeTotals = ReadTotals(transfers.GetProperty("all_time"));
        ObserveMemory();
        ObserveSpace();
        var published = rowsChanged && Project();
        if (accessChanged)
            Refresh();
        else
        {
            if (rowsChanged)
            {
                Changed(nameof(Torrents));
                foreach (var choice in Filters)
                    choice.Refresh(false);
                if (MoveDraft.Torrents.Length > 0)
                    MoveDraft.Refresh();
                if (RemovalDraft.Torrents.Length > 0)
                    RemovalDraft.Refresh();
            }
            if (
                published
                || previousCount != Torrents.Count
                || previousSelection != _selected.Length
            )
                Changed(nameof(CountStatus));
            if (previousCount != Torrents.Count)
            {
                Changed(nameof(EmptyTitle));
                Changed(nameof(EmptyInstruction));
                Changed(nameof(EmptyActionText));
                Changed(nameof(EmptyActionGlyph));
                Changed(nameof(EmptyActionToolTip));
                Changed(nameof(EmptyAction));
            }
            if (selectionChanged)
            {
                Changed(nameof(SelectionText));
                Changed(nameof(HasSelection));
                Changed(nameof(CanEditSelection));
                Changed(nameof(Sequential));
                Changed(nameof(FirstLast));
                Changed(nameof(Limited));
            }
            var limitsChanged = previousLimits != (_limits, MissingAdapter, LimitsIndex);
            var minute = DateTime.Now.Ticks / TimeSpan.TicksPerMinute;
            if (limitsChanged || minute != _limitsMinute)
                RefreshLimits();
            _limitsMinute = minute;
            if (limitsChanged || previousRates != (_downloadRate, _uploadRate, _moveRate, _checkRate))
                RefreshRates();
            if (
                previousIncoming
                != (HasIncoming, MissingAdapter, Settings.Proxy.IsInUse, Settings.Proxy.Route)
            )
                RefreshIncoming();
            if (previousIp != (_externalIpv4, _externalIpv6))
                Changed(nameof(ExternalIp));
            if (previousTotals.SessionTotals != SessionTotals)
                Changed(nameof(SessionTotals));
            if (previousTotals.LifetimeTotals != LifetimeTotals)
            {
                Changed(nameof(LifetimeTotals));
                Changed(nameof(LifetimeDownloaded));
                Changed(nameof(LifetimeUploaded));
            }
            if (previousLanguageSaved != _languageSaved)
                Changed(nameof(CurrentStatus));
            if (selectionChanged || limitsChanged || previousCount != Torrents.Count)
                foreach (RelayCommand command in _commands)
                    command.Refresh();
        }
        ObserveUpdates();
        if (first)
            ShowRequested?.Invoke(this, EventArgs.Empty);
        Features.Observe(snapshot);
        ObserveUnfinished(snapshot);
        Inspector.Observe(
            sessionId,
            inspectedChanged
                || (Inspector.Target is null && rowsChanged)
                || previousSelection != _selected.Length
        );
        SnapshotApplied?.Invoke(this, new SnapshotAppliedEventArgs(published, rowsChanged));
        RevealPending();
    }

    private static TransferTotals ReadTotals(JsonElement totals) =>
        new(totals.GetProperty("downloaded").GetInt64(), totals.GetProperty("uploaded").GetInt64());

    // The saved language, which the window needs before it appears.
    internal Task LanguageLoad => _languageLoad;

    // Reports that the window has rendered its first complete frame. The engine
    // answers when the window may appear, after a splash on screen has stayed
    // for its minimum time.
    internal async Task<bool> SendReady()
    {
        try
        {
            await _client.Send("ready", null, Requester.Window);
            return true;
        }
        catch (Exception error)
        {
            Notifications.Report(error);
            return false;
        }
    }

    private void QueueSnapshot(JsonElement snapshot)
    {
        lock (_snapshotGate)
        {
            _latest = snapshot;
            if (_applyQueued)
                return;
            _applyQueued = true;
        }
        _dispatcher.TryEnqueue(() =>
        {
            JsonElement? latest;
            lock (_snapshotGate)
            {
                latest = _latest;
                _latest = null;
                _applyQueued = false;
            }
            if (!_closed && latest is not null)
                Apply(latest.Value);
        });
    }

    private void QueueDetail(JsonElement? detail)
    {
        lock (_snapshotGate)
        {
            if (detail is null)
            {
                // Replies invalidate the context before any buffered detail can apply.
                _refreshDetail = true;
                _detail = null;
            }
            else if (!_refreshDetail)
                _detail = detail;
            if (_detailQueued)
                return;
            _detailQueued = true;
        }
        _dispatcher.TryEnqueue(() =>
        {
            JsonElement? latest;
            bool refresh;
            lock (_snapshotGate)
            {
                latest = _detail;
                refresh = _refreshDetail;
                _detail = null;
                _refreshDetail = false;
                _detailQueued = false;
            }
            if (_closed)
                return;
            if (refresh)
                Inspector.RefreshDetail();
            else if (latest is { } message)
                Inspector.Receive(message);
        });
    }

    // Runs again the torrents among these that the engine could not recover
    // by itself: a stopped torrent under its own Start, Stop or Force choice,
    // or a failed move.
    internal Task SendRetry(IEnumerable<Torrent> torrents)
    {
        Torrent[] retrying = [.. torrents.Where(torrent => torrent.CanRetry)];
        return retrying.Length > 0 ? Act("retry", retrying) : Task.CompletedTask;
    }

    private Task ActOnSelection(string command) =>
        _selected.Length > 0 ? Act(command, [.. _selected]) : Task.CompletedTask;

    // Sends the person's command for these torrents.
    private async Task Act(string command, Torrent[] torrents)
    {
        if (!CanEdit)
            return;
        var torrentIds = torrents.Select(row => row.TorrentId).ToArray();
        try
        {
            await _client.Send(command, new { torrent_ids = torrentIds }, Requester.Person);
            _waiting = command is "resume" or "force" && IsSessionPaused ? torrents : null;
            Changed(nameof(HasResumeToast));
            if (HasResumeToast)
                Notifications.Announce(ResumeMessage);
            else
                Notifications.AnnounceAccepted("commands", command);
        }
        catch (CommandException refusal)
            when (refusal.Code is "alias_conflict" or "metadata_unavailable"
                && command is "retry" or "resume" or "force" or "verify")
        {
            // The engine acted on every other torrent, so the toast names
            // only the refused ones and why.
            Notifications.Report(
                refusal,
                command == "verify" ? "refused_verify" : "refused_start",
                null,
                () =>
                    Text.Error(
                        refusal.Code == "alias_conflict" ? "alias_listed" : refusal.Code,
                        refusal.Detail
                    )
            );
        }
        catch (Exception error)
        {
            Notifications.Report(error);
        }
    }

    private async Task RequestExit()
    {
        if (!CanExit)
            return;
        try
        {
            await _client.Send("exit", null, Requester.Person);
        }
        catch (Exception error)
        {
            Notifications.Report(error);
        }
    }

    private async Task LoadLanguage(string language)
    {
        _requestedLanguage = language;
        var revision = ++_languageRevision;
        try
        {
            var catalogue = await Task.Run(() => Text.Prepare(language));
            if (revision != _languageRevision || _closed)
                return;
            Publish(catalogue);
            if (Settings.Language.Failure is null)
                Settings.Language.Cancel();
        }
        catch (Exception error)
        {
            if (revision != _languageRevision || _closed)
                return;
            // The failed language stays requested, so later snapshots that
            // carry the same saved language do not load it and report again.
            Notifications.Report(error);
        }
    }

    // The person's choice while it loads, then the language the text uses.
    internal string ChosenLanguage => _changingLanguage ? _requestedLanguage : Text.Language;

    internal void SelectLanguage(string language)
    {
        if (!CanEdit)
            return;
        _requestedLanguage = language;
        ++_languageRevision;
        if (_changingLanguage)
            return;
        _changingLanguage = true;
        Refresh();
        _ = ChangeLanguage();
    }

    private async Task ChangeLanguage()
    {
        try
        {
            while (!_closed)
            {
                var language = _requestedLanguage;
                var revision = _languageRevision;
                var published = false;
                try
                {
                    var catalogue = await Task.Run(() => Text.Prepare(language));
                    if (_closed)
                        return;
                    if (revision != _languageRevision)
                        continue;
                    Publish(catalogue);
                    published = true;
                    await Settings.SaveLanguage(language);
                    if (revision != _languageRevision)
                        continue;
                    break;
                }
                catch (Exception error)
                {
                    if (revision != _languageRevision)
                        continue;
                    if (!published)
                    {
                        _requestedLanguage = Text.Language;
                        Notifications.Report(error);
                    }
                    break;
                }
            }
        }
        finally
        {
            _changingLanguage = false;
            Refresh();
        }
    }

    private void Publish(Strings.Catalogue catalogue)
    {
        Text.Publish(catalogue);
        foreach (var torrent in Torrents)
            torrent.RefreshText();
        AddDraft.Refresh();
        Inspector.RefreshText();
        Library.RefreshText();
        _ = Features.Refresh();
        Settings.RefreshText();
        MoveDraft.Refresh();
        RemovalDraft.Refresh();
        Refresh();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task CancelDraft()
    {
        await AddDraft.Discard();
        Inspector.CancelDraft();
        await Settings.CancelDraft();
        await MoveDraft.Discard();
        await RemovalDraft.Discard();
    }

    internal void Reveal(string[] torrentIds)
    {
        if (torrentIds.Length == 0)
            return;
        Filter = TorrentFilter.All;
        _revealIds = torrentIds;
        RevealPending();
    }

    private void RevealPending()
    {
        // Torrents added together are shown together, once all are in the list.
        if (_revealIds.Length > 0 && _revealIds.All(_byId.ContainsKey))
        {
            var revealed = _revealIds.Select(id => _byId[id]).ToArray();
            _revealIds = [];
            RevealRequested?.Invoke(this, revealed);
        }
    }

    internal void RequestSnapshot() => _ = _client.RefreshSnapshot();

    private void ObserveSpace()
    {
        if (_closed)
            return;
        var path = Settings.Destination.ConfirmedText;
        if (_spacePath != path)
            _freeSpace = null;
        if (
            path is null
            || !Settings.ShowFreeSpace.IsOn
            || _spaceRead is { IsCompleted: false }
            || _spacePath == path && Environment.TickCount64 - _spaceChecked < 10000
        )
            return;
        _spacePath = path;
        _spaceChecked = Environment.TickCount64;
        _spaceRead = ReadSpace(path);
    }

    private async Task ReadSpace(string path)
    {
        var space = await Task.Run(() => DiskSpace.Read(path));
        if (_closed || path != Settings.Destination.ConfirmedText)
            return;
        _freeSpace = space;
        Changed(nameof(FreeSpaceStatus));
        Changed(nameof(ShowsFreeSpace));
    }

    private void Refresh()
    {
        if (_closed)
            return;
        AddDraft.Refresh();
        Inspector.Refresh();
        Settings.Refresh();
        MoveDraft.Refresh();
        RemovalDraft.Refresh();
        SpeedLimit.Refresh();
        RefreshWindow();
    }

    private void RefreshWindow()
    {
        if (_closed)
            return;
        foreach (var choice in Filters)
            choice.Refresh();
        _taskState = (CanClose, CanExit, HasDraft, AddDraft.IsOpen);
        Changed(string.Empty);
        foreach (RelayCommand command in _commands)
            command.Refresh();
    }

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (ReferenceEquals(sender, Inspector))
            Changed(nameof(CountStatus));
        if (ReferenceEquals(sender, Settings))
        {
            ObserveSpace();
            ObserveMemory();
            Changed(nameof(Theme));
            Changed(nameof(ShowsExternalIp));
            Changed(nameof(ShowsTitleSpeeds));
            Changed(nameof(ShowsFreeSpace));
            Changed(nameof(ShowsMemory));
            RefreshIncoming();
            RefreshLimits();
            RefreshRates();
        }
        var state = (CanClose, CanExit, HasDraft, AddOpen: AddDraft.IsOpen);
        var previous = _taskState;
        _taskState = state;
        if (previous?.CanClose != state.CanClose)
            Changed(nameof(CanClose));
        if (previous?.CanExit != state.CanExit)
        {
            Changed(nameof(CanExit));
            ((RelayCommand)Exit).Refresh();
        }
        if (previous?.HasDraft != state.HasDraft)
            Changed(nameof(HasDraft));
        if (previous?.AddOpen != state.AddOpen)
            ((RelayCommand)Add).Refresh();
    }

    private void OnFeaturesChanged(object? sender, EventArgs args)
    {
        var state = (Subtitles, LookupLimit);
        var previous = _featureState;
        _featureState = state;
        if (!ReferenceEquals(previous?.Subtitles, state.Subtitles))
        {
            Changed(nameof(Subtitles));
            RefreshProtectionScopes();
        }
        if (previous?.LookupLimit != state.LookupLimit)
            Changed(nameof(LookupLimit));
        RefreshFeedback();
    }

    private void RefreshRates()
    {
        Changed(nameof(DownloadSpeed));
        Changed(nameof(UploadSpeed));
        Changed(nameof(MoveSpeed));
        Changed(nameof(CheckSpeed));
        Changed(nameof(DownloadStatus));
        Changed(nameof(UploadStatus));
        Changed(nameof(DownloadTip));
        Changed(nameof(UploadTip));
    }

    private void RefreshIncoming()
    {
        Changed(nameof(HasIncoming));
        Changed(nameof(MissingAdapter));
        Changed(nameof(Incoming));
        Changed(nameof(IncomingStatus));
        Changed(nameof(IncomingGlyph));
        Changed(nameof(IsIncomingProxied));
        Changed(nameof(IsIncomingOpen));
        Changed(nameof(IsIncomingClosed));
    }

    private void RefreshLimits()
    {
        Changed(nameof(IsSessionPaused));
        Changed(nameof(FixedLimits));
        Changed(nameof(FollowsSchedule));
        Changed(nameof(LimitsIndex));
        Changed(nameof(HasStandardLimits));
        Changed(nameof(HasAlternativeLimits));
        Changed(nameof(IsPausedByChoice));
        Changed(nameof(HasRestriction));
        Changed(nameof(RestrictionStatus));
        Changed(nameof(RestrictionTip));
        Changed(nameof(ShowsAlternativeLimits));
        Changed(nameof(HasCaps));
        Changed(nameof(AlternativeTip));
        Changed(nameof(LimitsHeading));
        Changed(nameof(LimitsSummary));
        Changed(nameof(ScheduleSummary));
        Changed(nameof(DownloadCap));
        Changed(nameof(UploadCap));
        Changed(nameof(PauseStatus));
        Changed(nameof(LimitsNow));
    }

    private void Changed(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public void Dispose()
    {
        _closed = true;
        _ = Updates.Close();
        AddDraft.Close();
        Library.Dispose();
        Geolocation.Dispose();
        Work.Dispose();
        _client.Dispose();
        lock (_snapshotGate)
            _latest = null;
    }
}

internal sealed class SnapshotAppliedEventArgs(bool published, bool rowsChanged) : EventArgs
{
    public bool Published { get; } = published;
    public bool RowsChanged { get; } = rowsChanged;
}

internal readonly record struct TransferTotals(long Downloaded, long Uploaded);
