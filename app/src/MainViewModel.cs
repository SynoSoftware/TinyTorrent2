using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

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
    private Torrent[] _selected = [];
    private Torrent? _current;
    private Exception? _error;
    private double _downloadRate;
    private double _uploadRate;
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
    private bool _stopping;
    private bool _closed;
    private bool _closing;
    private bool _picking;
    private bool _addOpen;
    private bool _dark;
    private bool _toolbarOpen = true;

    public Strings Text { get; }
    public IReadOnlyList<Torrent> Torrents => _torrents;
    public AddDraft Draft { get; }
    public Preferences Preferences { get; }
    public string Theme => Preferences.Theme.ConfirmedText.Length > 0 ? Preferences.Theme.ConfirmedText : "system";
    public bool IsConnected => _connected;
    internal double DownloadRate => _downloadRate;
    internal double UploadRate => _uploadRate;
    public bool IsLoading => _loading || !_connected;
    public bool IsStorageFailed => _storageFailed;
    public bool IsEmptyVisible => !_storageFailed;
    public bool IsClosing => _closing;
    public bool CanClose => !_changingLanguage && !_picking && !_receivingSources && !Draft.IsPending &&
        !Inspector.IsPending && !Preferences.IsPending && !Files.IsPending;
    public bool CanExit => _connected && CanClose;
    public string? DataDirectory => _client.DataDirectory;
    public bool HasDraft => Draft.HasChanges || Inspector.HasDraft || Preferences.HasDraft || Files.HasDraft;
    internal bool CanSave => _connected && !_loading && !_storageFailed && !_stopping && !_picking;
    public bool CanEdit => CanSave && !_closing;
    public bool IsPicking
    {
        get => _picking;
        set { if (_picking == value) return; _picking = value; Refresh(); }
    }
    public bool IsAddOpen
    {
        get => _addOpen;
        set
        {
            if (_addOpen == value) return;
            _addOpen = value;
            if (value) Draft.StartPolling(); else Draft.StopPolling();
            Refresh();
        }
    }
    public bool IsDark
    {
        get => _dark;
        set { if (_dark == value) return; _dark = value; Changed(nameof(IsDark)); }
    }
    public ICommand Add { get; }
    public ICommand Pause { get; }
    public ICommand Resume { get; }
    public ICommand Exit { get; }
    public ICommand SwitchTheme { get; }
    public bool IsToolbarOpen
    {
        get => _toolbarOpen;
        set { if (_toolbarOpen == value) return; _toolbarOpen = value; Changed(nameof(IsToolbarOpen)); }
    }
    public ICommand SwitchToolbar { get; }
    public string DownloadText => Text.Format("window", "download_rate", Rate(_downloadRate));
    public string UploadText => Text.Format("window", "upload_rate", Rate(_uploadRate));
    public string PausedText => _connected && AllPaused ? Text.Get("status", "all_paused") : string.Empty;
    private string Rate(double rate) => _connected && !_loading && !_storageFailed ? Text.Format("units", "rate", Text.Bytes(rate)) : "—";
    public string TorrentError => _current is null || !_current.IsError ? string.Empty :
        Text.Format("errors", "torrent", _current.Name, _current.ErrorText);
    public string Message
    {
        get
        {
            if (!_connected)
            {
                var message = Text.Get("window", _sessionId.Length == 0 ? "connecting" : "disconnected");
                var detail = _connectionReason;
                return string.IsNullOrEmpty(detail) ? message : Text.Format("errors", "detail", message, detail);
            }
            if (_storageFailed) return Text.Error("storage_failed", _startupError);
            if (_loading) return Text.Get("window", "connecting");
            return !_languageSaved ? Text.Get("errors", "language_unsaved") : string.Empty;
        }
    }
    public bool HasFeedback => Message.Length > 0;
    public string CommandError => _error is null ? string.Empty : Text.Error(_error);
    public bool HasCommandError => _error is not null;
    public bool HasTorrentError => TorrentError.Length > 0;
    public InfoBarSeverity Severity => _connected && _storageFailed ?
        InfoBarSeverity.Error : InfoBarSeverity.Warning;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? TextChanged;
    internal event EventHandler<SnapshotAppliedEventArgs>? SnapshotApplied;
    public event EventHandler<Torrent[]>? RevealRequested;
    public event EventHandler? AddRequested;
    public event EventHandler? ActivateRequested;
    public event EventHandler? ShowRequested;
    public event EventHandler<bool>? CloseRequested;
    public event EventHandler<string>? AnnouncementRequested;
    public ICommand Restart { get; }
    public bool CanRestart => !_connected && _connectionReason is not null;
    public string RestartText => Text.Get("connection", "restart");

    internal MainViewModel(Strings strings, DispatcherQueue dispatcher)
    {
        Text = strings;
        _dispatcher = dispatcher;
        _client = new PipeClient(strings);
        OpenCompletion = new Command(OpenCompleted, () => CanOpenCompletion);
        Draft = new AddDraft(this, _client, strings);
        Files = new FileOperation(this, _client);
        Inspector = new Inspector(this, _client);
        Preferences = new Preferences(this, _client);
        Filters = Enum.GetValues<TorrentFilter>().Select(filter => new FilterChoice(this, filter)).ToArray();
        Draft.PropertyChanged += OnTaskChanged;
        Inspector.PropertyChanged += OnTaskChanged;
        Preferences.PropertyChanged += OnTaskChanged;
        Files.PropertyChanged += OnTaskChanged;
        Restart = new Command(() =>
        {
            try { _client.LaunchEngine(); ClearError(); }
            catch (Exception error) { Report(error); }
            return Task.CompletedTask;
        }, () => CanRestart);
        Add = new Command(() => { FilesRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; },
            () => CanEdit && !_addOpen);
        Pause = new Command(() => ActOnSelection("pause"), () => CanEdit && _selected.Length > 0);
        Resume = new Command(() => ActOnSelection("resume"), () => CanEdit && _selected.Length > 0);
        Exit = new Command(ExitEngine, () => CanExit);
        SwitchTheme = new Command(async () =>
        {
            await Preferences.SelectTheme(_dark ? "light" : "dark");
            if (Preferences.Theme.Failure is { } error) Report(error);
            else ClearError();
        }, () => CanEdit);
        AddMagnet = new Command(() =>
        {
            Draft.EditingMagnet = true;
            Draft.Refresh();
            AddRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }, () => CanEdit);
        Force = new Command(() => ActOnSelection("force"), () => CanEdit && _selected.Length > 0);
        SwitchSequential = new Command(() => SetPieceOrder(PieceOrder.Sequential, Sequential != true), () => CanEditSelection);
        SwitchFirstLast = new Command(() => SetPieceOrder(PieceOrder.FirstLast, FirstLast != true), () => CanEditSelection);
        SwitchFilters = new Command(() => { IsFilterOpen = !IsFilterOpen; return Task.CompletedTask; }, () => true);
        SwitchToolbar = new Command(() => { IsToolbarOpen = !IsToolbarOpen; return Task.CompletedTask; }, () => true);
        Verify = new Command(() => ActOnSelection("verify"), () => CanEdit && _selected.Length > 0);
        Remove = new Command(() => { RemoveRequested?.Invoke(this, _selected.ToArray()); return Task.CompletedTask; }, () => CanEdit && _selected.Length > 0);
        MoveFiles = new Command(() => { MoveRequested?.Invoke(this, _selected.ToArray()); return Task.CompletedTask; },
            () => CanEdit && _selected.Length > 0 && _selected.All(torrent => !torrent.IsMoving));
        DeleteFiles = new Command(() => { DeleteRequested?.Invoke(this, _selected.ToArray()); return Task.CompletedTask; },
            () => CanEdit && _selected.Length > 0 && _selected.All(torrent => !torrent.IsMoving));
        Up = new Command(() => Queue("up"), () => CanMove);
        Down = new Command(() => Queue("down"), () => CanMove);
        Top = new Command(() => Queue("top"), () => CanMove);
        Bottom = new Command(() => Queue("bottom"), () => CanMove);
        PauseAll = new Command(() => SessionPause(true), () => CanEdit);
        ResumeAll = new Command(() => SessionPause(false), () => CanEdit);
        Limits = new Command(() => RequestPreferences(new(PreferenceSection.Transfers, "download_limit")), () => true);
        Open = new Command(() => OpenTorrent(false), () => CanEdit && _selected.Length == 1);
        OpenFolder = new Command(() => OpenTorrent(true), () => CanEdit && _selected.Length == 1);
        CopyMagnet = new Command(() => CopyTorrent(false), () => CanEdit && _selected.Length == 1);
        CopyHash = new Command(() => CopyTorrent(true), () => CanEdit && _selected.Length == 1);
        Properties = new Command(() => Inspect(Inspector.Section), () => CanEdit && _selected.Length == 1);
        ClearFilters = new Command(ClearFinding, () => Filter != TorrentFilter.All);
        ShowPreferences = new Command(() => RequestPreferences(new(PreferenceSection.General)), () => true);
        ShowTorrents = new Command(() => { TorrentsRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }, () => true);
        ShowAbout = new Command(() => { AboutRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }, () => true);
        OpenUpdate = new Command(() => { OpenRequested?.Invoke(this, new(ReleasePage)); return Task.CompletedTask; }, () => HasUpdate);
        Preferences.Updates.PropertyChanged += (_, _) =>
        {
            ObserveUpdates();
            Changed(nameof(HasUpdate));
            ((Command)OpenUpdate).Refresh();
        };
        _client.Snapshot += QueueSnapshot;
        _client.Notice += notice => _dispatcher.TryEnqueue(() => ReceiveNotice(notice));
        _client.Disconnected += reason => _dispatcher.TryEnqueue(() =>
        {
            if (_closed) return;
            _connected = false;
            _connectionReason = reason;
            _ready = false;
            Draft.Invalidate();
            Inspector.Disconnect();
            foreach (var torrent in Torrents) torrent.Disconnect();
            Refresh();
        });
        _client.Control += control => _dispatcher.TryEnqueue(() =>
        {
            if (_closed) return;
            if (control == "activate") ActivateRequested?.Invoke(this, EventArgs.Empty);
            else if (control == "sources") _ = ReceiveSources();
            else CloseRequested?.Invoke(this, true);
        });
    }

    public void Start()
    {
        if (_loaded) return;
        _loaded = true;
        Refresh();
        _client.Start();
    }

    internal bool Contains(Torrent torrent) =>
        _byId.TryGetValue(torrent.TorrentId, out var current) && ReferenceEquals(current, torrent);

    private void Apply(JsonElement snapshot)
    {
        var previousAccess = (_connected, _loading, _storageFailed, _stopping);
        var sessionId = snapshot.GetProperty("session_id").GetString()!;
        if (_sessionId != sessionId) Draft.Invalidate();
        _sessionId = sessionId;
        _connected = true;
        _connectionReason = null;
        _loading = snapshot.TryGetProperty("loading", out var startup) && startup.GetBoolean();
        _storageFailed = snapshot.TryGetProperty("storage_failed", out var storage) && storage.GetBoolean();
        _startupError = snapshot.TryGetProperty("startup_error", out var detail) ? detail.GetString() : null;
        _stopping = snapshot.GetProperty("stopping").GetBoolean();
        var accessChanged = previousAccess != (_connected, _loading, _storageFailed, _stopping);
        // The first state the window can show: the saved settings, or the
        // storage failure that replaces them.
        var first = !_ready && !_loading;
        _ready |= first;
        if (_loading || _storageFailed)
        {
            if (accessChanged) Refresh(); else RefreshWindow();
            ObserveUpdates();
            if (first) ShowRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        var settings = snapshot.GetProperty("settings");
        Preferences.Apply(settings);
        AllPaused = snapshot.GetProperty("all_paused").GetBoolean();
        HasIncoming = snapshot.GetProperty("has_incoming").GetBoolean();
        MissingInterface = snapshot.TryGetProperty("missing_interface", out var missing) ? missing.GetString() ?? string.Empty : string.Empty;
        _alternativeLimits = snapshot.GetProperty("alternative_limits").GetBoolean();
        if (!_changingLanguage && Preferences.Language.ConfirmedText is { Length: > 0 } tag && tag != _requestedLanguage)
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
            eligibilityChanged |= existing && couldReorder != (torrent.Queue >= 0) && Matches(torrent, Filter);
        }
        foreach (var torrent in Torrents.Where(row => !present.Contains(row.TorrentId)).ToArray())
        {
            _torrents.Remove(torrent);
            _byId.Remove(torrent.TorrentId);
        }
        _selected = _selected.Where(row => present.Contains(row.TorrentId)).ToArray();
        if (_current is not null && !present.Contains(_current.TorrentId)) _current = null;
        if (Inspector.Target is { } target && !Contains(target)) CloseInspector();
        var languageSaved = snapshot.GetProperty("language_saved").GetBoolean();
        _languageSaved = languageSaved;
        Draft.UseDefault(Preferences.Destination.ConfirmedText);
        _downloadRate = snapshot.GetProperty("download_rate").GetDouble();
        _uploadRate = snapshot.GetProperty("upload_rate").GetDouble();
        var published = Project();
        if (accessChanged) Refresh(); else RefreshWindow();
        ObserveUpdates();
        if (first) ShowRequested?.Invoke(this, EventArgs.Empty);
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
    internal async Task<bool> Ready()
    {
        try { await _client.Send("ready"); return true; }
        catch (Exception error) { Report(error); return false; }
    }

    private void QueueSnapshot(JsonElement snapshot)
    {
        lock (_snapshotGate)
        {
            _latest = snapshot;
            if (_applyQueued) return;
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
            if (!_closed && latest is not null) Apply(latest.Value);
        });
    }

    private async Task ActOnSelection(string command)
    {
        if (!CanEdit || _selected.Length == 0) return;
        var identities = _selected.Select(row => row.TorrentId).ToArray();
        try
        {
            await _client.Send(command, new { torrent_ids = identities });
            Accepted("commands", command);
            _error = null;
            RequestSnapshot();
        }
        catch (Exception error) { Report(error); }
    }

    private async Task ExitEngine()
    {
        if (!CanExit) return;
        try { await _client.Send("exit"); }
        catch (Exception error) { Report(error); }
    }

    private async Task LoadLanguage(string language)
    {
        _requestedLanguage = language;
        var revision = ++_languageRevision;
        try
        {
            var catalogue = await Task.Run(() => Strings.Prepare(language));
            if (revision != _languageRevision || _closed) return;
            Publish(catalogue);
            if (Preferences.Language.Failure is { } error) Report(error);
            Preferences.Language.Cancel();
        }
        catch (Exception error)
        {
            if (revision != _languageRevision || _closed) return;
            _requestedLanguage = Text.Language;
            Report(error);
        }
    }

    internal void SelectLanguage(string language)
    {
        if (!CanEdit) return;
        _requestedLanguage = language;
        ++_languageRevision;
        if (_changingLanguage) return;
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
                    var catalogue = await Task.Run(() => Strings.Prepare(language));
                    if (_closed) return;
                    if (revision != _languageRevision) continue;
                    Publish(catalogue);
                    published = true;
                    await Preferences.SaveLanguage(language);
                    if (revision != _languageRevision) continue;
                    _error = null;
                    break;
                }
                catch (Exception error)
                {
                    if (revision != _languageRevision) continue;
                    if (!published)
                    {
                        _requestedLanguage = Text.Language;
                        Report(error);
                    }
                    break;
                }
            }
        }
        finally { _changingLanguage = false; Refresh(); }
    }

    private void Publish(Strings.Catalogue catalogue)
    {
        Text.Publish(catalogue);
        foreach (var torrent in Torrents) torrent.RefreshText();
        Draft.Refresh();
        Inspector.RefreshText();
        Preferences.RefreshText();
        Files.Refresh();
        Refresh();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    // Requests queued before this one reach the engine first, so a command that
    // sends one request needs no wait before the window closes.
    public async Task Close(bool engineExit)
    {
        if (_connected)
            await _client.Send(engineExit ? "close_reply" : "ui_closed", new { state = "closing" });
    }

    public async Task CancelClose()
    {
        if (_connected) await _client.Send("close_reply", new { state = "cancelled" });
    }

    public async Task DeferClose()
    {
        try
        {
            if (_connected) await _client.Send("close_reply", new { state = "waiting" });
        }
        catch (Exception error) { Report(error); }
    }

    internal async Task Activated(bool available)
    {
        try { await _client.Send("activate_reply", new { available }); }
        catch (Exception error) { Report(error); }
    }

    public async Task CancelDraft()
    {
        await Draft.Cancel();
        Inspector.CancelDraft();
        Preferences.CancelDraft();
        Files.Cancel();
    }

    public void Report(Exception error)
    {
        if (_closed) return;
        _error = error;
        Refresh();
        Announce(Text.Error(error));
    }

    internal void Accepted(string group, string key) => Announce(Text.Format("outcomes", "accepted", Text.Get(group, key)));

    internal void Announce(string message)
    {
        if (!_closed) AnnouncementRequested?.Invoke(this, message);
    }

    // Stops new operations while the window closes. Accepted work still
    // finishes, and CanClose reports when it has.
    internal bool BeginClose()
    {
        if (_closing) return false;
        _closing = true;
        Refresh();
        return true;
    }

    internal void EndClose()
    {
        _closing = false;
        Refresh();
        if (_sourcesPending) _ = ReceiveSources();
    }

    internal void Reveal(string[] torrentIds)
    {
        if (torrentIds.Length == 0) return;
        ErrorsOnly = false;
        Filter = TorrentFilter.All;
        _revealIds = torrentIds;
        _error = null;
        RequestSnapshot();
    }

    // Shows a command's effect without waiting for the next periodic refresh.
    // The command has already succeeded, so a failed refresh reports nothing;
    // a lost connection reports itself.
    internal void RequestSnapshot() => _ = _client.Read(Consumer.Summary, "snapshot");

    internal void ClearError() { _error = null; RefreshWindow(); }

    private void Refresh()
    {
        if (_closed) return;
        Draft.Refresh();
        Inspector.Refresh();
        Preferences.Refresh();
        Files.Refresh();
        RefreshWindow();
    }

    private void RefreshWindow()
    {
        if (_closed) return;
        foreach (var choice in Filters) choice.Refresh();
        Changed(string.Empty);
        foreach (Command command in new[] { Add, AddMagnet, Pause, Resume, Force, SwitchSequential, SwitchFirstLast, SwitchFilters, SwitchToolbar,
            Verify, Remove, MoveFiles, DeleteFiles,
            Up, Down, Top, Bottom, PauseAll, ResumeAll, Open, OpenFolder, CopyMagnet, CopyHash,
            Properties, Limits, ClearFilters, ShowPreferences, ShowTorrents, ShowAbout, OpenUpdate, Exit, SwitchTheme, Restart, OpenCompletion }) command.Refresh();
    }

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (ReferenceEquals(sender, Preferences))
        {
            Changed(nameof(Theme));
        }
        Changed(nameof(CanClose));
        Changed(nameof(CanExit));
        Changed(nameof(HasDraft));
        Changed(nameof(HasInspector));
        ((Command)Exit).Refresh();
    }

    private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public void Dispose()
    {
        _closed = true;
        _updateRequest?.Cancel();
        Draft.StopPolling();
        _client.Dispose();
        lock (_snapshotGate) _latest = null;
    }
}

internal sealed class SnapshotAppliedEventArgs(bool published, bool eligibilityChanged) : EventArgs
{
    public bool Published { get; } = published;
    public bool EligibilityChanged { get; } = eligibilityChanged;
}

internal sealed class Command(Func<Task> execute, Func<bool> enabled) : ICommand
{
    public bool CanExecute(object? parameter) => enabled();
    public async void Execute(object? parameter)
    {
        if (CanExecute(parameter)) await execute();
    }
    public event EventHandler? CanExecuteChanged;
    internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
