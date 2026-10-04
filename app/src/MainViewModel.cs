using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PipeClient _client;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, Torrent> _identities = [];
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
    private bool _settingsPending;
    private string _requestedLanguage = "en";
    private int _languageRevision;
    private string? _revealId;
    private string _sessionId = string.Empty;
    private bool _connected;
    private string? _connectionReason;
    private bool _loaded;
    private bool _ready;
    private bool _writable;
    private bool _closed;
    private bool _busy;
    private bool _picking;
    private bool _addOpen;
    private bool _dark;

    public Strings Text { get; }
    public ObservableCollection<Torrent> Torrents { get; } = [];
    public AddDraft Draft { get; }
    public string Theme { get; private set; } = "default";
    public bool IsConnected => _connected;
    public bool IsLoading => _loading || !_connected;
    public bool IsStorageFailed => _storageFailed;
    public bool IsEmptyVisible => !_storageFailed;
    public bool IsBusy => _busy;
    public bool CanClose => !_busy && !_settingsPending && !_picking;
    public bool HasDraft => Draft.HasChanges;
    internal bool CanEdit => _writable && !_busy && !_picking;
    public bool IsPicking
    {
        get => _picking;
        set { if (_picking == value) return; _picking = value; Refresh(); }
    }
    public bool IsAddOpen
    {
        get => _addOpen;
        set { if (_addOpen == value) return; _addOpen = value; Refresh(); }
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
    public ICommand SwitchLanguage { get; }
    public ICommand SwitchTheme { get; }
    public string Rates => Text.Format("window", "rates",
        _connected && !_loading && !_storageFailed ? Text.Format("units", "rate", Text.Bytes(_downloadRate)) : "—",
        _connected && !_loading && !_storageFailed ? Text.Format("units", "rate", Text.Bytes(_uploadRate)) : "—");
    public string TorrentError => _current is null || _current.ErrorCode.Length == 0 ? string.Empty :
        Text.Format("errors", "torrent", _current.Name, _current.ErrorText);
    public string Message => !_connected ? _connectionReason ?? Text.Get("window", _loaded ? "disconnected" : "connecting") :
        _storageFailed ? Text.Error("storage_failed", _startupError) :
        _loading ? Text.Get("window", "connecting") :
        _error is not null ? FormatError(_error) :
        !_languageSaved ? Text.Get("errors", "language_unsaved") : string.Empty;
    public bool HasFeedback => Message.Length > 0;
    public InfoBarSeverity Severity => _connected && (_storageFailed || (!_loading && _error is not null)) ?
        InfoBarSeverity.Error : InfoBarSeverity.Warning;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? TextChanged;
    public event EventHandler? SnapshotApplied;
    public event EventHandler<Torrent>? RevealRequested;
    public event EventHandler? AddRequested;
    public event EventHandler? ActivateRequested;
    public event EventHandler<bool>? CloseRequested;

    internal MainViewModel(Strings strings, DispatcherQueue dispatcher)
    {
        Text = strings;
        _dispatcher = dispatcher;
        _client = new PipeClient(strings);
        Draft = new AddDraft(this, _client, strings);
        Add = new Command(() => { AddRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; },
            () => CanEdit && !_addOpen);
        Pause = new Command(() => Transfer("pause"), () => CanEdit && _selected.Length > 0);
        Resume = new Command(() => Transfer("resume"), () => CanEdit && _selected.Length > 0);
        Exit = new Command(ExitEngine, () => _connected && CanClose);
        SwitchLanguage = new Command(ChangeLanguage, () => _connected && !_settingsPending);
        SwitchTheme = new Command(ChangeTheme, () => _connected && !_settingsPending);
        _client.Snapshot += QueueSnapshot;
        _client.Disconnected += reason => _dispatcher.TryEnqueue(() =>
        {
            if (_closed) return;
            _connected = false;
            _connectionReason = reason;
            _ready = false;
            _writable = false;
            Draft.Invalidate();
            foreach (var torrent in Torrents) torrent.Disconnect();
            Refresh();
        });
        _client.Control += control => _dispatcher.TryEnqueue(() =>
        {
            if (_closed) return;
            if (control == "activate") ActivateRequested?.Invoke(this, EventArgs.Empty);
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

    public void Select(IEnumerable<Torrent> items, Torrent? current)
    {
        _selected = items.ToArray();
        _current = current;
        Refresh();
    }

    private void Apply(JsonElement snapshot)
    {
        var sessionId = snapshot.GetProperty("session_id").GetString()!;
        if (_sessionId != sessionId) Draft.Invalidate();
        _sessionId = sessionId;
        _connected = true;
        _connectionReason = null;
        _loading = snapshot.TryGetProperty("loading", out var startup) && startup.GetBoolean();
        _storageFailed = snapshot.TryGetProperty("storage_failed", out var storage) && storage.GetBoolean();
        _startupError = snapshot.TryGetProperty("startup_error", out var detail) ? detail.GetString() : null;
        _writable = !_loading && !_storageFailed && !snapshot.GetProperty("stopping").GetBoolean();
        if (!_ready && !_loading)
        {
            _ready = true;
            _ = ReportReady();
        }
        if (_loading || _storageFailed)
        {
            Refresh();
            return;
        }
        var settings = snapshot.GetProperty("settings");
        if (settings.TryGetProperty("theme", out var theme)) Theme = theme.GetString()!;
        if (!_settingsPending && settings.TryGetProperty("language", out var language) &&
            language.GetString() is { } tag && tag != _requestedLanguage)
            _ = LoadLanguage(tag);
        var present = new HashSet<string>();
        foreach (var item in snapshot.GetProperty("torrents").EnumerateArray())
        {
            var torrentId = item.GetProperty("torrent_id").GetString()!;
            present.Add(torrentId);
            if (!_identities.TryGetValue(torrentId, out var torrent))
            {
                torrent = new Torrent(torrentId, Text);
                _identities.Add(torrentId, torrent);
                Torrents.Add(torrent);
            }
            torrent.Update(item);
        }
        foreach (var torrent in Torrents.Where(row => !present.Contains(row.TorrentId)).ToArray())
        {
            Torrents.Remove(torrent);
            _identities.Remove(torrent.TorrentId);
        }
        _selected = _selected.Where(row => present.Contains(row.TorrentId)).ToArray();
        if (_current is not null && !present.Contains(_current.TorrentId)) _current = null;
        var languageSaved = snapshot.GetProperty("language_saved").GetBoolean();
        if (!_languageSaved && languageSaved && _error is CommandFailure { Command: "settings" }) _error = null;
        _languageSaved = languageSaved;
        if (settings.TryGetProperty("default_destination", out var destination))
            Draft.UseDefault(destination.GetString()!);
        _downloadRate = snapshot.GetProperty("download_rate").GetDouble();
        _uploadRate = snapshot.GetProperty("upload_rate").GetDouble();
        Refresh();
        SnapshotApplied?.Invoke(this, EventArgs.Empty);
        if (_revealId is not null && _identities.TryGetValue(_revealId, out var revealed))
        {
            _revealId = null;
            RevealRequested?.Invoke(this, revealed);
        }
    }

    private async Task ReportReady()
    {
        try { await _client.Send("ready"); }
        catch (Exception error) { Report(error); }
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

    private async Task Transfer(string command)
    {
        if (!CanEdit || _selected.Length == 0) return;
        var identities = _selected.Select(row => row.TorrentId).ToArray();
        Busy(true);
        try
        {
            await _client.Send(command, new { torrent_ids = identities });
            _error = null;
            await _client.Send("snapshot");
        }
        catch (Exception error) { Report(error); }
        finally { Busy(false); }
    }

    private async Task ExitEngine()
    {
        if (!_connected || !CanClose) return;
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
        }
        catch (Exception error)
        {
            if (revision != _languageRevision || _closed) return;
            _requestedLanguage = Text.Language;
            Report(error);
        }
    }

    private async Task ChangeLanguage()
    {
        if (!_connected || _settingsPending) return;
        var language = Text.Language == "es" ? "en" : "es";
        _settingsPending = true;
        Refresh();
        try
        {
            var catalogue = await Task.Run(() => Strings.Prepare(language));
            if (_closed) return;
            ++_languageRevision;
            _requestedLanguage = language;
            Publish(catalogue);
            await _client.Send("settings", new { changes = new { language } });
            _error = null;
            Refresh();
        }
        catch (Exception error) { Report(error); }
        finally { _settingsPending = false; Refresh(); }
    }

    private void Publish(Strings.Catalogue catalogue)
    {
        Text.Publish(catalogue);
        foreach (var torrent in Torrents) torrent.RefreshText();
        Draft.Refresh();
        Refresh();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ChangeTheme()
    {
        if (!_connected || _settingsPending) return;
        var theme = _dark ? "light" : "dark";
        _settingsPending = true;
        Refresh();
        try
        {
            await _client.Send("settings", new { changes = new { theme } });
            Theme = theme;
            _error = null;
        }
        catch (Exception error) { Report(error); }
        finally { _settingsPending = false; Refresh(); }
    }

    public async Task Close(bool engineExit)
    {
        if (_connected)
            await _client.Send(engineExit ? "close_reply" : "ui_closed", new { cancelled = false });
    }

    public async Task CancelClose()
    {
        if (_connected) await _client.Send("close_reply", new { cancelled = true });
    }

    public Task CancelDraft() => Draft.Cancel();

    public void Report(Exception error)
    {
        if (_closed) return;
        _error = error;
        Refresh();
    }

    internal string FormatError(Exception error) => error is CommandFailure ? error.Message : Text.Error("unknown", error.Message);

    internal void Busy(bool value) { _busy = value; Refresh(); }

    internal async Task Reveal(string torrentId)
    {
        _revealId = torrentId;
        _error = null;
        await _client.Send("snapshot");
    }

    internal void ClearError() { _error = null; Refresh(); }

    private void Refresh()
    {
        if (_closed) return;
        Changed(string.Empty);
        Draft.Refresh();
        foreach (Command command in new[] { Add, Pause, Resume, Exit, SwitchLanguage, SwitchTheme }) command.Refresh();
    }

    private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public void Dispose()
    {
        _closed = true;
        _client.Dispose();
        lock (_snapshotGate) _latest = null;
    }
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
