using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Views;

public sealed class Inspector : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;
    private readonly PipeClient _client;
    private readonly Dictionary<int, int> _fileChanges = [];
    private Torrent? _target;
    private InspectorSection _section;
    private string _session = string.Empty;
    private int _context;
    private bool _fetching;
    private bool _pending;
    private bool _filesLoaded;
    private bool _trackersLoaded;
    private bool _editingTrackers;
    private SpeedRange _range;
    private bool _visible = true;
    private Exception? _readFailure;
    private Exception? _editFailure;
    private string _trackerInput = string.Empty;
    private string _trackerOriginal = string.Empty;
    private JsonElement? _confirmedFiles;

    public Strings Text => _owner.Text;
    public Torrent? Target => _target;
    public string Name => _target?.Name ?? string.Empty;
    public bool IsOpen => _target is not null;
    public bool IsAvailable => _owner.IsConnected && _target is not null && _owner.Contains(_target);
    // Speed shows the engine's session-wide history, so it needs only the
    // connection; every other section reads the selected torrent.
    private bool CanRead => _section == InspectorSection.Speed ? _owner.IsConnected : IsAvailable;
    private bool CanSave => IsAvailable && _owner.CanSave && !_pending;
    public bool CanEdit => _visible && CanSave && !_owner.IsClosing;
    public bool IsPending => _pending;
    public bool IsLoading => _fetching;
    public bool HasDraft => !_pending && (HasFileDraft || HasTrackerDraft);
    internal bool HasTrackerDraft => _editingTrackers && _trackerInput != _trackerOriginal;
    private bool IsRemoved => _owner.IsConnected && _target is not null && !_owner.Contains(_target);
    public bool HasError => IsRemoved || _readFailure is not null || _editFailure is not null;
    public string Message => IsRemoved ? Text.Get("inspector", "removed") : _editFailure is { } edit ? Text.Error(edit) :
        _readFailure is { } read ? Text.Error(read) : string.Empty;
    public FileSelection Files { get; }
    public IReadOnlyList<Peer> Peers { get; private set; } = [];
    public IReadOnlyList<Tracker> Trackers { get; private set; } = [];
    public IReadOnlyList<SpeedSample> History { get; private set; } = [];
    public Pieces? Pieces { get; private set; }
    public string Folder { get; private set; } = string.Empty;
    public string Comment { get; private set; } = string.Empty;
    public string Creator { get; private set; } = string.Empty;
    public string Magnet { get; private set; } = string.Empty;
    public string Hashes => _target is null ? string.Empty : string.Join(Environment.NewLine, _target.Hashes);
    public long Created { get; private set; }
    public int PieceSize { get; private set; }
    public bool? IsPrivate { get; private set; }
    public string CreatedText => Created <= 0 ? "—" : DateTimeOffset.FromUnixTimeSeconds(Created).LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string PieceSizeText => PieceSize <= 0 ? "—" : Text.Bytes(PieceSize);
    public string PrivacyText => IsPrivate is { } privacy ? Text.Get("inspector", privacy ? "private" : "public") : "—";
    public bool IsEditingTrackers => _editingTrackers;
    public bool HasFileDraft => _fileChanges.Count > 0;
    public bool HasFiles => _filesLoaded;
    public InspectorSection Section => _section;
    public string TrackerInput
    {
        get => _trackerInput;
        set
        {
            value = value.ReplaceLineEndings("\n");
            if (_trackerInput == value) return;
            _trackerInput = value;
            _editFailure = null;
            Refresh();
        }
    }
    // The engine keeps a five-minute and a day history; the longer ranges all
    // show part of the day history, so switching among them reads nothing new.
    public SpeedRange Range
    {
        get => _range;
        set
        {
            if (_range == value)
            {
                return;
            }
            var reload = _range == SpeedRange.FiveMinutes || value == SpeedRange.FiveMinutes;
            _range = value;
            if (reload)
            {
                Invalidate();
                History = [];
            }
            Refresh();
            if (reload)
            {
                _ = Read();
            }
        }
    }
    public ICommand EditTrackers { get; }
    public ICommand SaveTrackers { get; }
    public ICommand CancelTrackers { get; }
    public ICommand RetryFiles { get; }
    public ICommand Retry { get; }
    public ICommand Restart => _owner.Restart;
    public bool CanRestart => _owner.CanRestart;
    public string RestartText => _owner.RestartText;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? TextChanged;
    internal event EventHandler<InspectorSection>? RowsUpdated;
    public ICommand Reannounce { get; }

    internal Inspector(MainViewModel owner, PipeClient client)
    {
        _owner = owner;
        _client = client;
        Files = new FileSelection(owner.Text) { ShowsProgress = true, IsEnabled = false };
        Files.Edited += (_, changes) =>
        {
            foreach (var (index, priority) in changes) _fileChanges[index] = priority;
            _ = SaveFiles();
        };
        EditTrackers = new Command(() => { BeginTrackers(); return Task.CompletedTask; },
            () => CanEdit && _trackersLoaded && !_editingTrackers);
        SaveTrackers = new Command(CommitTrackers, () => CanEdit && _editingTrackers);
        CancelTrackers = new Command(() => { CancelTrackerDraft(); return Task.CompletedTask; }, () => !_pending && _editingTrackers);
        RetryFiles = new Command(SaveFiles, () => CanEdit && HasFileDraft);
        Retry = new Command(Read, () => CanRead && !_fetching);
        Reannounce = new Command(() => Apply("reannounce", new { torrent_id = _target!.TorrentId }),
            () => CanEdit && !_editingTrackers);
    }

    internal bool Open(Torrent target)
    {
        if (_target == target) return true;
        if (HasDraft || _pending) return false;
        Clear();
        _target = target;
        Invalidate();
        Refresh();
        _ = Read();
        return true;
    }

    internal bool Close()
    {
        if (HasDraft || _pending) return false;
        _target = null;
        Invalidate();
        Clear();
        Refresh();
        return true;
    }

    public void Select(InspectorSection section)
    {
        if (_section == section) return;
        _section = section;
        Invalidate();
        _readFailure = null;
        if (section != InspectorSection.Peers) Peers = [];
        if (section != InspectorSection.Speed) History = [];
        if (section != InspectorSection.Pieces) Pieces = null;
        Refresh();
        _ = Read();
    }

    internal void Observe(string session)
    {
        if (_session != session)
        {
            _session = session;
            Peers = [];
            Trackers = [];
            _trackersLoaded = false;
            Invalidate();
            _readFailure = null;
        }
        Refresh();
        _ = Read();
    }

    internal void Disconnect()
    {
        Invalidate();
        History = [];
        Refresh();
    }

    internal void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        Invalidate();
        if (!visible) { Peers = []; History = []; Pieces = null; }
        Refresh();
        if (visible) _ = Read();
    }

    private async Task Read()
    {
        if (!_visible || !IsOpen || !CanRead || _fetching) return;
        _fetching = true;
        var context = _context;
        var section = _section;
        var target = _target!;
        Refresh();
        try
        {
            var reply = section == InspectorSection.Speed ? await _client.Read(Consumer.Inspector, "history", new { range = _range == SpeedRange.FiveMinutes ? "five_minutes" : "day" }) :
                await _client.Read(Consumer.Inspector, "torrent", new { torrent_id = target.TorrentId, view = section.ToString().ToLowerInvariant(),
                    include_files = section == InspectorSection.Pieces && Pieces is not { MetadataReady: true } });
            if (context != _context || !_visible || !CanRead) return;
            switch (section)
            {
                case InspectorSection.General: ApplyGeneral(reply); break;
                case InspectorSection.Files: ApplyFiles(reply); break;
                case InspectorSection.Peers:
                    var peers = Peers.ToDictionary(peer => peer.Endpoint);
                    var nextPeers = reply.GetProperty("peers").EnumerateArray().Select(data =>
                    {
                        if (!peers.TryGetValue(data.GetProperty("endpoint").GetString()!, out var peer)) return new Peer(Text, data);
                        peer.Update(data);
                        return peer;
                    }).ToArray();
                    if (!Peers.SequenceEqual(nextPeers)) Peers = nextPeers;
                    else RowsUpdated?.Invoke(this, InspectorSection.Peers);
                    break;
                case InspectorSection.Trackers:
                    var trackers = Trackers.ToDictionary(tracker => tracker.Url);
                    var nextTrackers = reply.GetProperty("trackers").EnumerateArray().Select(data =>
                    {
                        if (!trackers.TryGetValue(data.GetProperty("url").GetString()!, out var tracker)) return new Tracker(Text, data);
                        tracker.Update(data);
                        return tracker;
                    }).ToArray();
                    if (!Trackers.SequenceEqual(nextTrackers)) Trackers = nextTrackers;
                    else RowsUpdated?.Invoke(this, InspectorSection.Trackers);
                    _trackersLoaded = true;
                    break;
                case InspectorSection.Speed:
                    History = reply.GetProperty("samples").EnumerateArray().Select(sample => new SpeedSample(
                        sample.GetProperty("time").GetInt64(), sample.GetProperty("download_rate").GetDouble(),
                        sample.GetProperty("upload_rate").GetDouble())).ToArray();
                    break;
                case InspectorSection.Pieces: Pieces = new Pieces(reply, Pieces?.Files ?? []); break;
            }
            _readFailure = null;
        }
        catch (Exception error) { if (context == _context) _readFailure = error; }
        finally
        {
            _fetching = false;
            Refresh();
            if (context != _context && _visible && IsOpen && CanRead) _ = Read();
        }
    }

    // A read for the old context is stale, so an unsent one is withdrawn.
    private void Invalidate()
    {
        _context++;
        _client.Withdraw(Consumer.Inspector);
    }

    private void ApplyGeneral(JsonElement reply)
    {
        Folder = reply.GetProperty("folder").GetString()!;
        Magnet = reply.GetProperty("magnet").GetString()!;
        Comment = reply.GetProperty("comment").GetString()!;
        Creator = reply.GetProperty("creator").GetString()!;
        Created = reply.GetProperty("created").GetInt64();
        PieceSize = reply.GetProperty("piece_size").GetInt32();
        var privacy = reply.GetProperty("private");
        IsPrivate = privacy.ValueKind == JsonValueKind.Null ? null : privacy.GetBoolean();
    }

    private void ApplyFiles(JsonElement reply)
    {
        if (!reply.GetProperty("metadata_ready").GetBoolean()) return;
        var files = reply.GetProperty("files");
        _confirmedFiles = files.Clone();
        if (!_filesLoaded) { Files.Load(files); _filesLoaded = true; }
        var priorities = files.EnumerateArray().ToDictionary(file => file.GetProperty("index").GetInt32(), file => file.GetProperty("priority").GetInt32());
        if (!_pending && _fileChanges.Count > 0 && _fileChanges.All(change => priorities.TryGetValue(change.Key, out var priority) && priority == change.Value))
        {
            _fileChanges.Clear();
            _editFailure = null;
        }
        Files.Apply(files, _pending || HasFileDraft);
    }

    private async Task SaveFiles()
    {
        if (!CanSave || _fileChanges.Count == 0) return;
        var priorities = _fileChanges.Select(change => new { index = change.Key, priority = change.Value }).ToArray();
        await Apply("edit", new { torrent_id = _target!.TorrentId, changes = new { priorities } }, _fileChanges.Clear);
    }

    private void BeginTrackers()
    {
        _trackerOriginal = string.Join("\n\n", Trackers.GroupBy(tracker => tracker.Tier)
            .OrderBy(group => group.Key).Select(group => string.Join("\n", group.Select(tracker => tracker.Url))));
        _trackerInput = _trackerOriginal;
        _editingTrackers = true;
        _editFailure = null;
        Refresh();
    }

    private async Task CommitTrackers()
    {
        if (!CanSave || !_editingTrackers) return;
        if (_trackerInput == _trackerOriginal) { CancelTrackerDraft(); return; }
        var trackers = new List<object>();
        var tier = 0;
        foreach (var line in _trackerInput.Split('\n'))
        {
            var url = line.Trim();
            if (url.Length == 0) { tier++; continue; }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not "http" and not "https" and not "udp")
            {
                _editFailure = new CommandFailure("invalid_trackers", url, Text);
                Refresh();
                return;
            }
            trackers.Add(new { url, tier });
        }
        await Apply("edit", new { torrent_id = _target!.TorrentId, changes = new { trackers } }, CancelTrackerDraft);
    }

    private async Task Apply(string command, object arguments, Action? confirmed = null)
    {
        Invalidate();
        _pending = true;
        _editFailure = null;
        Refresh();
        try
        {
            await _client.Send(command, arguments);
            confirmed?.Invoke();
        }
        catch (Exception error) { _editFailure = error; }
        finally { _pending = false; Refresh(); _ = Read(); }
    }

    private void CancelTrackerDraft()
    {
        _editingTrackers = false;
        _trackerInput = _trackerOriginal;
        _editFailure = null;
        Refresh();
    }

    internal async Task<bool> SaveDraft()
    {
        if (_editingTrackers) await CommitTrackers();
        if (_editingTrackers && _trackerInput != _trackerOriginal) return false;
        if (HasFileDraft) await SaveFiles();
        return !HasDraft;
    }

    public void CancelDraft()
    {
        var restoreFiles = HasFileDraft;
        _fileChanges.Clear();
        if (restoreFiles && _confirmedFiles is { } files) Files.Apply(files, false);
        CancelTrackerDraft();
    }

    private void Clear()
    {
        Files.Clear();
        _filesLoaded = false;
        _trackersLoaded = false;
        _confirmedFiles = null;
        Peers = [];
        Trackers = [];
        History = [];
        Pieces = null;
        Folder = Comment = Creator = Magnet = string.Empty;
        Created = PieceSize = 0;
        IsPrivate = null;
        _readFailure = _editFailure = null;
        CancelDraft();
    }

    internal void RefreshText()
    {
        foreach (var peer in Peers) peer.RefreshText();
        foreach (var tracker in Trackers) tracker.RefreshText();
        Files.Refresh();
        Refresh();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void Refresh()
    {
        var enabled = CanEdit && _filesLoaded;
        if (Files.IsEnabled != enabled) Files.IsEnabled = enabled;
        foreach (Command command in new[] { EditTrackers, SaveTrackers, CancelTrackers, RetryFiles, Retry, Reannounce }) command.Refresh();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public sealed record SpeedSample(long Time, double DownloadRate, double UploadRate);
