using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;
using LibraryBrowser = Syno.TinyTorrent.Library.Browser;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PipeClient _client;
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
    private Exception? _error;
    private double _downloadRate;
    private double _uploadRate;
    private long? _freeSpace;
    private Task? _spaceRead;
    private string? _spacePath;
    private long _spaceChecked;
    private bool _loading = true;
    private bool _storageFailed;
    private bool _languageSaved = true;
    private string? _startupError;
    private bool _changingLanguage;
    private string _requestedLanguage = "en";
    private int _languageRevision;
    private Task _languageLoad = Task.CompletedTask;
    private string[] _revealIds = [];
    private string _sessionId = string.Empty;
    private bool _connected;
    private string? _connectionReason;
    private bool _loaded;
    private bool _ready;
    private bool _shuttingDown;
    private bool _closed;
    private bool _closing;
    private bool _picking;
    private bool _addOpen;
    private bool _dark;
    private bool _toolbarOpen = true;
    private bool _subtitleHelpSeen;

    public Strings Text { get; }
    public IReadOnlyList<Torrent> Torrents => _torrents;
    public AddDraft AddDraft { get; }
    public Settings Settings { get; }
    internal bool SubtitleHelpSeen
    {
        get => _subtitleHelpSeen;
        set
        {
            if (_subtitleHelpSeen == value)
                return;
            _subtitleHelpSeen = value;
            Changed(nameof(SubtitleHelpSeen));
        }
    }
    public LibraryBrowser Library { get; }
    public string Theme =>
        Settings.Theme.ConfirmedText.Length > 0 ? Settings.Theme.ConfirmedText : "system";
    public bool ShowsTitleSpeeds => Settings.ShowTitleSpeeds.ConfirmedOn;
    public bool ShowsFreeSpace =>
        _connected
        && !_loading
        && !_storageFailed
        && Settings.ShowFreeSpace.ConfirmedOn
        && _spacePath == Settings.Destination.ConfirmedText
        && _freeSpace is not null;
    public string FreeSpaceStatus =>
        _freeSpace is { } space ? Text.Format("window", "free_space", Text.Bytes(space)) : string.Empty;
    public string DownloadSpeed => Rate(_downloadRate);
    public string UploadSpeed => Rate(_uploadRate);
    public bool IsConnected => _connected;
    internal double DownloadRate => _downloadRate;
    internal double UploadRate => _uploadRate;
    public bool IsLoading => _loading || !_connected;
    public bool IsStorageFailed => _storageFailed;
    public bool IsEmptyVisible => !_storageFailed;
    public bool IsClosing => _closing;
    public bool CanClose =>
        !_changingLanguage
        && !_picking
        && !_receivingActivations
        && _arrivals.IsCompleted
        && !AddDraft.IsBusy
        && !Inspector.IsPending
        && !Settings.IsPending
        && !FileDraft.IsPending
        && !SpeedLimit.IsPending
        && _limitsChoice is null;
    public bool CanExit => _connected && CanClose;
    public string? DataDirectory => _client.DataDirectory;
    public bool HasDraft => AddDraft.HasDraft || Inspector.HasDraft || FileDraft.HasDraft;
    internal bool CanSave =>
        _connected && !_loading && !_storageFailed && !_shuttingDown && !_picking;
    public bool CanEdit => CanSave && !_closing;
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
    public bool IsAddOpen
    {
        get => _addOpen;
        set
        {
            if (_addOpen == value)
                return;
            _addOpen = value;
            if (value)
                AddDraft.StartPolling();
            else
                AddDraft.StopPolling();
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
            Changed(nameof(ShowsToolbar));
        }
    }
    public ICommand SwitchToolbar { get; }
    public bool ShowsToolbar => Page != WindowPage.Library && IsToolbarOpen;
    public bool IsSessionPaused => _connected && IsPaused;

    private string Rate(double rate) =>
        _connected && !_loading && !_storageFailed
            ? Text.Format("units", "rate", Text.Bytes(rate))
            : "—";

    public string TorrentError =>
        _current is null || !_current.IsError
            ? string.Empty
            : Text.Format("errors", "torrent", _current.Name, _current.ErrorText);
    public string Message
    {
        get
        {
            if (!_connected)
            {
                var message = Text.Get(
                    "window",
                    _sessionId.Length == 0 ? "connecting" : "disconnected"
                );
                var detail = _connectionReason;
                return string.IsNullOrEmpty(detail)
                    ? message
                    : Text.Format("errors", "detail", message, detail);
            }
            if (_storageFailed)
                return Text.Error("storage_failed", _startupError);
            if (_loading)
                return Text.Get("window", "connecting");
            if (HasFeatureFailure)
                return FeatureFailure;
            return !_languageSaved ? Text.Get("errors", "language_unsaved") : string.Empty;
        }
    }
    public bool HasFeedback => Message.Length > 0;
    public string CommandError => _error is null ? string.Empty : Text.Error(_error);
    public bool HasCommandError => _error is not null;
    public bool HasTorrentError => TorrentError.Length > 0;
    public InfoBarSeverity Severity =>
        _connected && (_storageFailed || HasFeatureFailure) ? InfoBarSeverity.Error : InfoBarSeverity.Warning;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? TextChanged;
    internal event EventHandler<SnapshotAppliedEventArgs>? SnapshotApplied;
    public event EventHandler<Torrent[]>? RevealRequested;
    public event EventHandler<Torrent[]>? ReleaseRequested;
    public event EventHandler? AddRequested;
    public event EventHandler? ActivateRequested;
    public event EventHandler? ShowRequested;
    public event EventHandler<bool>? CloseRequested;
    public event EventHandler? ConfirmExitRequested;
    public event EventHandler<string>? AnnouncementRequested;
    public ICommand Restart { get; }
    public bool CanRestart => !_connected && _connectionReason is not null;
    public string RestartText => Text.Get("connection", "restart");
    private ICommand RetryFeatures { get; }
    public ICommand Recovery => CanRestart ? Restart : RetryFeatures;
    public bool CanRecover => CanRestart || _connected && !_loading && !_storageFailed && HasFeatureFailure;
    public string RecoveryText => CanRestart ? RestartText : Text.Get("inspector", "retry");
    public string RecoveryTip => CanRestart ? Text.Get("connection", "restart_tip") : Text.Get("window", "retry_features_tip");

    internal MainViewModel(Strings strings, DispatcherQueue dispatcher)
    {
        Text = strings;
        _dispatcher = dispatcher;
        _client = new PipeClient(strings);
        OpenCompletion = new RelayCommand(OpenCompleted, () => CanOpenCompletion);
        AddDraft = new AddDraft(this, _client, strings);
        FileDraft = new FileDraft(this, _client);
        SpeedLimit = new SpeedLimit(this, _client);
        Inspector = new Inspector(this, _client);
        Settings = new Settings(this, _client);
        Library = new LibraryBrowser(strings, dispatcher);
        Library.RetryRequested += (_, _) => RequestSnapshot();
        Library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(Library.Failure) or "")
                RefreshFeedback();
            if (Page != WindowPage.Library)
                return;
            if (args.PropertyName is nameof(Library.Query) or "")
                Changed(nameof(Query));
            if (args.PropertyName is nameof(Library.IsOpen) or "")
                Changed(nameof(HasInspector));
            if (args.PropertyName is nameof(Library.Status) or "")
                Changed(nameof(CountStatus));
        };
        Filters = Enum.GetValues<TorrentFilter>()
            .Select(filter => new FilterChoice(this, filter))
            .ToArray();
        AddDraft.PropertyChanged += OnTaskChanged;
        Inspector.PropertyChanged += OnTaskChanged;
        Settings.PropertyChanged += (sender, args) =>
        {
            if (!_closed && !_applyingSettings && _providerRoute is not null)
                ConfigureRoute();
            OnTaskChanged(sender, args);
        };
        FileDraft.PropertyChanged += OnTaskChanged;
        SpeedLimit.PropertyChanged += OnTaskChanged;
        Restart = new RelayCommand(
            () =>
            {
                try
                {
                    _client.LaunchEngine();
                    ClearError();
                }
                catch (Exception error)
                {
                    Report(error);
                }
                return Task.CompletedTask;
            },
            () => CanRestart
        );
        RetryFeatures = new RelayCommand(async () =>
        {
            if (Library.HasFailure && Library.Retry.CanExecute(null))
                Library.Retry.Execute(null);
            else
                RequestSnapshot();
            try { await RetrySubtitles(); }
            catch (Exception error) { ReportFeature(error); }
        }, () => _connected && HasFeatureFailure);
        Add = new RelayCommand(
            () =>
            {
                FilesRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            },
            () => CanEdit && !_addOpen
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
                CloseRequested?.Invoke(this, false);
                return Task.CompletedTask;
            },
            () => true
        );
        SwitchTheme = new RelayCommand(
            async () =>
            {
                await Settings.SelectTheme(_dark ? "light" : "dark");
                if (Settings.Theme.Failure is { } error)
                    Report(error);
                else
                    ClearError();
            },
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
                IsFilterOpen = !IsFilterOpen;
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
        DeleteFiles = new RelayCommand(
            () =>
            {
                DeleteRequested?.Invoke(this, _selected.ToArray());
                return Task.CompletedTask;
            },
            () => CanEdit && _selected.Length > 0 && _selected.All(torrent => !torrent.IsMoving)
        );
        Up = new RelayCommand(() => Queue("up"), () => CanMove);
        Down = new RelayCommand(() => Queue("down"), () => CanMove);
        Top = new RelayCommand(() => Queue("top"), () => CanMove);
        Bottom = new RelayCommand(() => Queue("bottom"), () => CanMove);
        PauseAll = new RelayCommand(() => SessionPause(true), () => CanEdit);
        ResumeAll = new RelayCommand(() => SessionPause(false), () => CanEdit);
        ResolvePause = new RelayCommand(
            () =>
                _pause == PauseReason.Adapter ? ShowSetting(Settings.Adapter) : SessionPause(false),
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
        ClearFilters = new RelayCommand(() =>
        {
            if (Page != WindowPage.Library)
                return ClearFinding();
            Library.ClearFilters.Execute(null);
            return Task.CompletedTask;
        }, () => Page == WindowPage.Library ? Library.ClearFilters.CanExecute(null) : Filter != TorrentFilter.All);
        Library.ClearFilters.CanExecuteChanged += (_, _) => ((RelayCommand)ClearFilters).Refresh();
        ShowSettings = new RelayCommand(
            () => RequestSettings(new()),
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
        ShowLibrary = new RelayCommand(() =>
        {
            LibraryRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }, () => true);
        ShowAbout = new RelayCommand(
            () =>
            {
                AboutRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            },
            () => true
        );
        OpenUpdate = new RelayCommand(
            () =>
            {
                OpenRequested?.Invoke(this, new(ReleasePage));
                return Task.CompletedTask;
            },
            () => HasUpdate
        );
        Settings.Updates.PropertyChanged += (_, _) =>
        {
            ObserveUpdates();
            Changed(nameof(HasUpdate));
            ((RelayCommand)OpenUpdate).Refresh();
        };
        _client.Snapshot += QueueSnapshot;
        _client.Detail += detail => QueueDetail(detail);
        _client.CommandCompleted += () => QueueDetail(null);
        _client.Notice += notice => _dispatcher.TryEnqueue(() => ReceiveNotice(notice));
        _client.Disconnected += reason =>
            _dispatcher.TryEnqueue(() =>
            {
                if (_closed)
                    return;
                _connected = false;
                _waiting = null;
                _restriction = null;
                _connectionReason = reason;
                _ready = false;
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
                else if (control == "settings")
                    RequestSettings(new(SettingsCategory.General));
                else if (control == "confirm_exit")
                    ConfirmExitRequested?.Invoke(this, EventArgs.Empty);
                else
                    CloseRequested?.Invoke(this, true);
            });
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
        var previousAccess = (_connected, _loading, _storageFailed, _shuttingDown);
        var sessionId = snapshot.GetProperty("session_id").GetString()!;
        var reconnected = !_connected || _sessionId != sessionId;
        if (reconnected && _limitsError is not CommandException)
            _limitsError = null;
        if (_sessionId != sessionId)
            AddDraft.Invalidate();
        _sessionId = sessionId;
        _connected = true;
        _connectionReason = null;
        _loading = snapshot.TryGetProperty("loading", out var startup) && startup.GetBoolean();
        _storageFailed =
            snapshot.TryGetProperty("storage_failed", out var storage) && storage.GetBoolean();
        _startupError = snapshot.TryGetProperty("startup_error", out var detail)
            ? detail.GetString()
            : null;
        _shuttingDown = snapshot.GetProperty("shutting_down").GetBoolean();
        var accessChanged = previousAccess != (_connected, _loading, _storageFailed, _shuttingDown);
        // The first state the window can show: the saved settings, or the
        // storage failure that replaces them.
        var first = !_ready && !_loading;
        _ready |= first;
        if (_loading || _storageFailed)
        {
            if (accessChanged)
                Refresh();
            else
                RefreshWindow();
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
            ConfigureRoute();
        }
        finally
        {
            _applyingSettings = false;
        }
        _client.SetRefreshInterval(settings.GetProperty("refresh_interval").GetInt32());
        IsPaused = snapshot.GetProperty("session_paused").GetBoolean();
        HasIncoming = snapshot.GetProperty("has_incoming").GetBoolean();
        _externalIpv4 = snapshot.GetProperty("external_ipv4").GetString() ?? string.Empty;
        _externalIpv6 = snapshot.GetProperty("external_ipv6").GetString() ?? string.Empty;
        MissingAdapter = snapshot.TryGetProperty("missing_adapter", out var missing)
            ? missing.GetString() ?? string.Empty
            : string.Empty;
        ApplyLimits(snapshot.GetProperty("limits"));
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
        var eligibilityChanged = false;
        foreach (var item in snapshot.GetProperty("torrents").EnumerateArray())
        {
            var torrentId = item.GetProperty("torrent_id").GetString()!;
            present.Add(torrentId);
            var existing = _byId.TryGetValue(torrentId, out var torrent);
            if (torrent is null)
            {
                torrent = new Torrent(torrentId, Text);
                _byId.Add(torrentId, torrent);
                _torrents.Add(torrent);
            }
            var couldReorder = torrent.Queue >= 0;
            torrent.Update(item);
            eligibilityChanged |=
                existing && couldReorder != (torrent.Queue >= 0) && Matches(torrent, Filter);
        }
        foreach (var torrent in Torrents.Where(row => !present.Contains(row.TorrentId)).ToArray())
        {
            _torrents.Remove(torrent);
            _byId.Remove(torrent.TorrentId);
        }
        _selected = _selected.Where(row => present.Contains(row.TorrentId)).ToArray();
        if (_current is not null && !present.Contains(_current.TorrentId))
            _current = null;
        if (Inspector.Target is { } target && !Contains(target))
            Inspector.Show(null);
        var languageSaved = snapshot.GetProperty("language_saved").GetBoolean();
        _languageSaved = languageSaved;
        AddDraft.ApplyDefaults(
            settings.GetProperty("addition_destination").GetString() ?? string.Empty,
            settings.GetProperty("starts_download").GetBoolean(),
            settings.GetProperty("queue_top").GetBoolean()
        );
        _downloadRate = snapshot.GetProperty("download_rate").GetDouble();
        _uploadRate = snapshot.GetProperty("upload_rate").GetDouble();
        ObserveSpace();
        var published = Project();
        if (accessChanged)
            Refresh();
        else
        {
            FileDraft.Refresh();
            RefreshWindow();
        }
        ObserveUpdates();
        if (first)
            ShowRequested?.Invoke(this, EventArgs.Empty);
        ObserveFiles(snapshot);
        Inspector.Observe(sessionId);
        SnapshotApplied?.Invoke(this, new SnapshotAppliedEventArgs(published, eligibilityChanged));
        // Torrents added together are shown together, once all of them are in the list.
        if (_revealIds.Length > 0 && _revealIds.All(_byId.ContainsKey))
        {
            var revealed = _revealIds.Select(id => _byId[id]).ToArray();
            _revealIds = [];
            RevealRequested?.Invoke(this, revealed);
        }
    }

    // The saved language, which the window needs before it appears.
    internal Task LanguageLoad => _languageLoad;

    // Reports that the window has rendered its first complete frame. The engine
    // answers when the window may appear, after a splash on screen has stayed
    // for its minimum time.
    internal async Task<bool> SendReady()
    {
        try
        {
            await _client.Send("ready");
            return true;
        }
        catch (Exception error)
        {
            Report(error);
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

    private async Task ActOnSelection(string command)
    {
        if (!CanEdit || _selected.Length == 0)
            return;
        var torrents = _selected.ToArray();
        var torrentIds = torrents.Select(row => row.TorrentId).ToArray();
        try
        {
            await _client.Send(command, new { torrent_ids = torrentIds });
            _error = null;
            _waiting = command is "resume" or "force" && IsSessionPaused ? torrents : null;
            if (HasResumeNotice)
                Announce(ResumeNotice);
            else
                AnnounceAccepted("commands", command);
            RequestSnapshot();
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    private async Task RequestExit()
    {
        if (!CanExit)
            return;
        try
        {
            await _client.Send("exit");
        }
        catch (Exception error)
        {
            Report(error);
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
            _requestedLanguage = Text.Language;
            Report(error);
        }
    }

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
                    _error = null;
                    break;
                }
                catch (Exception error)
                {
                    if (revision != _languageRevision)
                        continue;
                    if (!published)
                    {
                        _requestedLanguage = Text.Language;
                        Report(error);
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
        _ = RefreshFeatures();
        Settings.RefreshText();
        FileDraft.Refresh();
        Refresh();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    // Requests queued before this one reach the engine first, so a command that
    // sends one request needs no wait before the window closes.
    public async Task Close(bool exiting)
    {
        if (_connected)
            await _client.Send(
                exiting ? "close_reply" : "window_closed",
                new { state = "closing" }
            );
    }

    public async Task CancelClose()
    {
        if (_connected)
            await _client.Send("close_reply", new { state = "cancelled" });
    }

    public async Task DeferClose()
    {
        try
        {
            if (_connected)
                await _client.Send("close_reply", new { state = "waiting" });
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    internal async Task ReplyExit(ExitAnswer answer)
    {
        try
        {
            await _client.Send(
                "exit_reply",
                new
                {
                    answer = answer switch
                    {
                        ExitAnswer.Confirmed => "confirmed",
                        ExitAnswer.Cancelled => "cancelled",
                        _ => "unavailable",
                    },
                }
            );
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    internal async Task ReplyActivation(bool available)
    {
        try
        {
            await _client.Send("activate_reply", new { available });
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    public async Task CancelDraft()
    {
        await AddDraft.Cancel();
        Inspector.CancelDraft();
        await Settings.CancelDraft();
        FileDraft.Cancel();
    }

    public void Report(Exception error)
    {
        if (_closed)
            return;
        _error = error;
        Refresh();
        Announce(Text.Error(error));
    }

    internal void AnnounceAccepted(string group, string key) =>
        Announce(Text.Format("outcomes", "accepted", Text.Get(group, key)));

    internal void Announce(string message)
    {
        if (!_closed)
            AnnouncementRequested?.Invoke(this, message);
    }

    // Stops new operations while the window closes. Accepted work still
    // finishes, and CanClose reports when it has.
    internal bool BeginClose()
    {
        if (_closing)
            return false;
        _closing = true;
        Refresh();
        return true;
    }

    internal void EndClose()
    {
        _closing = false;
        Refresh();
        if (_activationsPending)
            _ = ReceiveActivations();
    }

    internal void Reveal(string[] torrentIds)
    {
        if (torrentIds.Length == 0)
            return;
        Filter = TorrentFilter.All;
        _revealIds = torrentIds;
        _error = null;
        RequestSnapshot();
    }

    // Shows a command's effect without waiting for the next periodic refresh.
    internal void RequestSnapshot() => _ = _client.RefreshSnapshot();

    private void ObserveSpace()
    {
        var path = Settings.Destination.ConfirmedText;
        if (_spacePath != path)
            _freeSpace = null;
        if (
            !Settings.ShowFreeSpace.ConfirmedOn
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

    internal void ClearError()
    {
        _error = null;
        RefreshWindow();
    }

    private void Refresh()
    {
        if (_closed)
            return;
        AddDraft.Refresh();
        Inspector.Refresh();
        Settings.Refresh();
        FileDraft.Refresh();
        SpeedLimit.Refresh();
        RefreshWindow();
    }

    private void RefreshWindow()
    {
        if (_closed)
            return;
        foreach (var choice in Filters)
            choice.Refresh();
        Changed(string.Empty);
        foreach (
            RelayCommand command in new[]
            {
                Add,
                AddMagnet,
                Pause,
                Resume,
                Force,
                SwitchSequential,
                SwitchFirstLast,
                LimitSpeed,
                SwitchFilters,
                SwitchToolbar,
                Verify,
                Remove,
                MoveFiles,
                DeleteFiles,
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
                OpenUpdate,
                Exit,
                SwitchTheme,
                Restart,
                RetryFeatures,
                OpenCompletion,
            }
        )
            command.Refresh();
    }

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (ReferenceEquals(sender, Settings))
        {
            Changed(nameof(Theme));
            Changed(nameof(ShowsExternalIp));
            Changed(nameof(ShowsTitleSpeeds));
            Changed(nameof(ShowsFreeSpace));
        }
        Changed(nameof(CanClose));
        Changed(nameof(CanExit));
        Changed(nameof(HasDraft));
        Changed(nameof(HasInspector));
        ((RelayCommand)Exit).Refresh();
    }

    private void Changed(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public void Dispose()
    {
        _closed = true;
        _updateRequest?.Cancel();
        AddDraft.StopPolling();
        Library.Dispose();
        _client.Dispose();
        lock (_snapshotGate)
            _latest = null;
    }
}

internal sealed class SnapshotAppliedEventArgs(bool published, bool eligibilityChanged) : EventArgs
{
    public bool Published { get; } = published;
    public bool EligibilityChanged { get; } = eligibilityChanged;
}

internal sealed class RelayCommand(Func<Task> execute, Func<bool> enabled) : ICommand
{
    public bool CanExecute(object? parameter) => enabled();

    public async void Execute(object? parameter)
    {
        if (CanExecute(parameter))
            await execute();
    }

    public event EventHandler? CanExecuteChanged;

    internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
