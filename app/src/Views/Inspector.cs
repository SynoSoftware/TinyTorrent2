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

    // The torrent the section data belongs to.
    private Torrent? _source;
    private InspectorSection _section;
    private string _session = string.Empty;
    private int _context;
    private bool _fetching;
    private TaskCompletionSource? _pending;
    private TaskCompletionSource? _fileSave;
    private bool _filesLoaded;
    private bool _editingTrackers;
    private bool _open;
    private bool _visible = true;
    private Exception? _readFailure;
    private Exception? _editFailure;
    private string _trackerInput = string.Empty;
    private string _trackerOriginal = string.Empty;
    private JsonElement? _confirmedFiles;

    public Strings Text => _owner.Text;
    public Torrent? Target => _target;
    public string Name => _target?.Name ?? string.Empty;

    // The panel stays open while the selection changes; without a target it
    // shows how many torrents are selected.
    public bool IsOpen => _open;
    public string EmptyText =>
        _owner.Selected.Count == 0
            ? Text.Get("inspector", "none")
            : Text.FormatCount("inspector", "selection", _owner.Selected.Count);
    public bool IsAvailable =>
        _owner.IsConnected && _target is not null && _owner.Contains(_target);

    // Speed shows the engine's session-wide history, so it needs only the
    // connection; every other section reads the selected torrent.
    private bool CanRead => _section == InspectorSection.Speed ? _owner.IsConnected : IsAvailable;

    // A save keeps its controls enabled so later input keeps focus.
    private bool CanSave => IsAvailable && _owner.CanSave;
    public bool CanEdit => _visible && CanSave && !_owner.IsClosing;
    public bool IsPending => _pending is not null;
    public bool IsLoading => _fetching;
    public bool HasDraft => !IsPending && (HasFileChanges || HasTrackerChanges);
    internal bool HasTrackerChanges => _editingTrackers && _trackerInput != _trackerOriginal;
    private bool IsRemoved =>
        _owner.IsConnected && _target is not null && !_owner.Contains(_target);
    public bool HasError => IsRemoved || _readFailure is not null || _editFailure is not null;
    public string Message =>
        IsRemoved ? Text.Get("inspector", "removed")
        : _editFailure is { } edit ? Text.Error(edit)
        : _readFailure is { } read ? Text.Error(read)
        : string.Empty;
    public FileSelection Files { get; }

    // Section data stays until another torrent is shown or the engine session
    // ends, so returning to a section or to the torrent shows it at once. A
    // null list is unread, not empty.
    public IReadOnlyList<Peer>? Peers { get; private set; }
    public IReadOnlyList<Tracker>? Trackers { get; private set; }
    public IReadOnlyList<SpeedSample>? History { get; private set; }
    public Pieces? Pieces { get; private set; }
    public string Folder { get; private set; } = string.Empty;
    public string Comment { get; private set; } = string.Empty;
    public string Creator { get; private set; } = string.Empty;
    public string Magnet { get; private set; } = string.Empty;
    public string HashV1 =>
        _target?.Hashes.FirstOrDefault(hash => hash.Length == 40) ?? string.Empty;
    public string HashV2 =>
        _target?.Hashes.FirstOrDefault(hash => hash.Length == 64) ?? string.Empty;
    public string HashV1Label => Text.Get("inspector", HashV2.Length > 0 ? "hash_v1" : "hash");
    public string HashV2Label => Text.Get("inspector", HashV1.Length > 0 ? "hash_v2" : "hash");
    public long Created { get; private set; }
    public int PieceSize { get; private set; }
    public int PieceCount { get; private set; }
    public bool? IsPrivate { get; private set; }
    public bool HasMetadata => IsPrivate.HasValue;
    public bool IsIndeterminate => !HasMetadata || _target is { IsMoving: true };
    public string ProgressText =>
        HasMetadata ? _target?.ProgressText ?? string.Empty : string.Empty;
    public string Summary
    {
        get
        {
            if (_target is not { } torrent)
                return string.Empty;
            if (torrent.IsError)
                return torrent.ErrorText;
            if (!HasMetadata)
                return Text.Get("inspector", "size_unknown");
            if (torrent.IsMoving || torrent.Completed >= torrent.Size)
                return torrent.SizeText;
            var progress = Text.Format(
                "inspector",
                "progress",
                Text.Bytes(torrent.Completed),
                torrent.SizeText
            );
            return torrent.Eta is null
                ? progress
                : Text.Format("inspector", "progress_eta", progress, torrent.EtaText);
        }
    }
    public string CommentText => Comment.Length > 0 ? Comment : "—";
    public string CreatorText => Creator.Length > 0 ? Creator : "—";
    public string CreatedText =>
        Created <= 0 ? "—" : Text.Time(DateTimeOffset.FromUnixTimeSeconds(Created));
    public string PiecesText =>
        HasMetadata
            ? Text.Format(
                "inspector",
                "piece_count",
                PieceCount.ToString("N0", CultureInfo.CurrentCulture),
                Text.Bytes(PieceSize)
            )
            : "—";
    public string PrivacyText =>
        IsPrivate is { } privacy ? Text.Get("inspector", privacy ? "private" : "public") : "—";
    public string PrivacyGlyph =>
        IsPrivate switch
        {
            true => Syno.Lucide.Lock,
            false => Syno.Lucide.Globe,
            null => Syno.Lucide.Shield,
        };
    public string LimitText =>
        _target is { IsLimited: true } torrent
            ? torrent.LimitText
            : Text.Get("speed_limit", "none");
    public string LimitName =>
        _target is { IsLimited: true } torrent ? torrent.LimitName : LimitText;
    public bool IsEditingTrackers => _editingTrackers;
    public bool HasFileChanges => _fileChanges.Count > 0;
    public bool HasFiles => _filesLoaded;
    public InspectorSection Section => _section;
    public string TrackerInput
    {
        get => _trackerInput;
        set
        {
            value = value.ReplaceLineEndings("\n");
            if (_trackerInput == value)
                return;
            _trackerInput = value;
            _editFailure = null;
            Refresh();
        }
    }
    public ICommand EditTrackers { get; }
    public bool CanSaveTrackers => CanEdit && _editingTrackers;
    public ICommand CancelTrackers { get; }
    public ICommand Retry { get; }
    public string RetryToolTip =>
        Text.Get(
            "inspector",
            HasFileChanges ? "retry_files_tip"
                : _section == InspectorSection.Speed ? "retry_speed_tip"
                : "retry_tip"
        );
    public ICommand Restart => _owner.Restart;
    public bool CanRestart => _owner.CanRestart;
    public string RestartText => _owner.RestartText;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? TextChanged;
    internal event EventHandler? RecoveryRequested;
    internal event EventHandler<InspectorSection>? RowsUpdated;
    public ICommand Reannounce { get; }
    public ICommand OpenFolder { get; }
    public ICommand CopyV1 { get; }
    public ICommand CopyV2 { get; }
    public ICommand CopyMagnet { get; }
    public ICommand LimitSpeed { get; }
    public ICommand MoveFiles { get; }
    public ICommand ShowRatioLimit { get; }
    public ICommand ShowConnectionLimit { get; }

    internal Inspector(MainViewModel owner, PipeClient client)
    {
        _owner = owner;
        _client = client;
        LimitSpeed = owner.SpeedCommand(() => _target is { } target ? [target] : []);
        MoveFiles = owner.MoveCommand(() => _target is { } target ? [target] : []);
        OpenFolder = new RelayCommand(
            () => _owner.OpenTorrent(_target!, true),
            () => IsAvailable && !_owner.IsClosing
        );
        CopyV1 = new RelayCommand(() => _owner.Copy(HashV1), () => HashV1.Length > 0);
        CopyV2 = new RelayCommand(() => _owner.Copy(HashV2), () => HashV2.Length > 0);
        CopyMagnet = new RelayCommand(() => _owner.Copy(Magnet), () => Magnet.Length > 0);
        ShowRatioLimit = new RelayCommand(
            () => _owner.ShowSetting(_owner.Settings.Ratio),
            () => true
        );
        ShowConnectionLimit = new RelayCommand(
            () => _owner.ShowSetting(_owner.Settings.ConnectionLimit),
            () => true
        );
        Files = new FileSelection(owner.Text) { ShowsProgress = true, IsEnabled = false };
        Files.Edited += (_, changes) =>
        {
            foreach (var (index, priority) in changes)
                _fileChanges[index] = priority;
            _ = SaveFiles();
        };
        EditTrackers = new RelayCommand(
            () =>
            {
                BeginTrackers();
                return Task.CompletedTask;
            },
            () => CanEdit && Trackers is not null && !_editingTrackers
        );
        CancelTrackers = new RelayCommand(
            () =>
            {
                CancelTrackerDraft();
                return Task.CompletedTask;
            },
            () => !IsPending && _editingTrackers
        );
        Retry = new RelayCommand(
            () => HasFileChanges ? SaveFiles() : Read(),
            () => HasFileChanges ? CanEdit : CanRead && !_fetching
        );
        Reannounce = new RelayCommand(
            () => Apply("reannounce", new { torrent_id = _target!.TorrentId }),
            () => CanEdit && !_editingTrackers
        );
    }

    // Opens the panel on the target, or without one.
    internal bool Show(Torrent? target)
    {
        if (!Retarget(target))
            return false;
        _open = true;
        Refresh();
        return true;
    }

    internal bool Close()
    {
        if (!Retarget(null))
            return false;
        _open = false;
        Refresh();
        return true;
    }

    private bool Retarget(Torrent? target)
    {
        if (_target == target)
            return true;
        if (HasDraft || IsPending)
            return false;
        _readFailure = _editFailure = null;
        CancelDraft();
        // Section data outlives an empty selection, so selecting the same
        // torrent again shows it at once; another torrent replaces it.
        if (target is not null && target != _source)
        {
            Clear();
            _source = target;
        }
        _target = target;
        Invalidate();
        _ = Read();
        return true;
    }

    internal async Task Navigate(InspectorSection section)
    {
        if (_owner.IsClosing || _section == section)
            return;
        var target = _target;
        if (!await Depart())
        {
            if (_target == target)
                RecoveryRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!_owner.IsClosing && _target == target)
            Select(section);
    }

    internal void Select(InspectorSection section)
    {
        if (_section == section)
            return;
        _section = section;
        Invalidate();
        _readFailure = null;
        Refresh();
        _ = Read();
    }

    internal void Observe(string session)
    {
        if (_session != session)
        {
            _session = session;
            Peers = null;
            Trackers = null;
            Pieces = null;
            History = null;
            Invalidate();
            _readFailure = null;
        }
        Refresh();
        _ = Read();
    }

    internal void Disconnect()
    {
        Invalidate();
        History = null;
        Refresh();
    }

    internal void SetVisible(bool visible)
    {
        if (_visible == visible)
            return;
        _visible = visible;
        Invalidate();
        Refresh();
        if (visible)
            _ = Read();
    }

    private async Task Read()
    {
        if (!_visible || _target is null || !CanRead || _fetching)
            return;
        _fetching = true;
        var context = _context;
        var section = _section;
        var target = _target!;
        Refresh();
        try
        {
            if (section == InspectorSection.Speed)
            {
                var history = await ReadHistory();
                if (context != _context || !_visible || !CanRead)
                    return;
                History = history;
                _readFailure = null;
                return;
            }
            var reply = await _client.Read(
                Consumer.Inspector,
                "torrent",
                new
                {
                    torrent_id = target.TorrentId,
                    view = section.ToString().ToLowerInvariant(),
                    include_files = section == InspectorSection.Pieces
                        && Pieces is not { MetadataReady: true },
                }
            );
            if (context != _context || !_visible || !CanRead)
                return;
            switch (section)
            {
                case InspectorSection.General:
                    ApplyGeneral(reply);
                    break;
                case InspectorSection.Files:
                    ApplyFiles(reply);
                    break;
                case InspectorSection.Peers:
                    var peers = (Peers ?? []).ToDictionary(peer => peer.Endpoint);
                    // The endpoint is the row's identity, but libtorrent lists
                    // connections, and one peer can briefly hold two before
                    // libtorrent closes the duplicate. The first one represents it.
                    var nextPeers = reply
                        .GetProperty("peers")
                        .EnumerateArray()
                        .DistinctBy(data => data.GetProperty("endpoint").GetString()!)
                        .Select(data =>
                        {
                            if (
                                !peers.TryGetValue(
                                    data.GetProperty("endpoint").GetString()!,
                                    out var peer
                                )
                            )
                                return new Peer(Text, data);
                            peer.Update(data);
                            return peer;
                        })
                        .ToArray();
                    if (Peers is null || !Peers.SequenceEqual(nextPeers))
                        Peers = nextPeers;
                    else
                        RowsUpdated?.Invoke(this, InspectorSection.Peers);
                    break;
                case InspectorSection.Trackers:
                    var trackers = (Trackers ?? []).ToDictionary(tracker => tracker.Url);
                    var nextTrackers = reply
                        .GetProperty("trackers")
                        .EnumerateArray()
                        .Select(data =>
                        {
                            if (
                                !trackers.TryGetValue(
                                    data.GetProperty("url").GetString()!,
                                    out var tracker
                                )
                            )
                                return new Tracker(Text, data);
                            tracker.Update(data);
                            return tracker;
                        })
                        .ToArray();
                    if (Trackers is null || !Trackers.SequenceEqual(nextTrackers))
                        Trackers = nextTrackers;
                    else
                        RowsUpdated?.Invoke(this, InspectorSection.Trackers);
                    break;
                case InspectorSection.Pieces:
                    Pieces = new Pieces(reply, Pieces?.Files ?? []);
                    break;
            }
            _readFailure = null;
        }
        catch (Exception error)
        {
            if (context == _context)
                _readFailure = error;
        }
        finally
        {
            _fetching = false;
            // Start the current section's read before notifying, so observers do
            // not mistake the end of this stale read for that section loading.
            if (context != _context)
                _ = Read();
            Refresh();
        }
    }

    // Recent samples replace overlapping day samples; their intervals can differ
    // after an aggregation setting changes, so merge by timestamp.
    private async Task<SpeedSample[]> ReadHistory()
    {
        var day = Samples(await _client.Read(Consumer.Inspector, "history", new { range = "day" }));
        var recent = Samples(
            await _client.Read(Consumer.Inspector, "history", new { range = "five_minutes" })
        );
        var cut = recent.Length == 0 ? long.MaxValue : recent[0].Time;
        return [.. day.Where(sample => sample.Time < cut), .. recent];

        static SpeedSample[] Samples(JsonElement reply) =>
            reply
                .GetProperty("samples")
                .EnumerateArray()
                .Select(sample => new SpeedSample(
                    sample.GetProperty("time").GetInt64(),
                    sample.GetProperty("download_rate").GetDouble(),
                    sample.GetProperty("upload_rate").GetDouble()
                ))
                .ToArray();
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
        PieceCount = reply.GetProperty("piece_count").GetInt32();
        var privacy = reply.GetProperty("private");
        IsPrivate = privacy.ValueKind == JsonValueKind.Null ? null : privacy.GetBoolean();
    }

    private void ApplyFiles(JsonElement reply)
    {
        if (!reply.GetProperty("metadata_ready").GetBoolean())
            return;
        var files = reply.GetProperty("files");
        _confirmedFiles = files.Clone();
        if (!_filesLoaded)
        {
            Files.Load(files);
            _filesLoaded = true;
        }
        var priorities = files
            .EnumerateArray()
            .ToDictionary(
                file => file.GetProperty("index").GetInt32(),
                file => file.GetProperty("priority").GetInt32()
            );
        if (
            !IsPending
            && _fileChanges.Count > 0
            && _fileChanges.All(change =>
                priorities.TryGetValue(change.Key, out var priority) && priority == change.Value
            )
        )
        {
            _fileChanges.Clear();
            _editFailure = null;
        }
        Files.Apply(files, IsPending || HasFileChanges);
    }

    // A choice made while a save runs joins that save, which sends it when it
    // ends. A choice the engine already holds is dropped; a changed one stays.
    private async Task SaveFiles()
    {
        if (_fileSave is { } running)
        {
            await running.Task;
            return;
        }
        var save = _fileSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        try
        {
            while (CanSave && _fileChanges.Count > 0)
            {
                var sent = new Dictionary<int, int>(_fileChanges);
                var priorities = sent.Select(change => new
                    {
                        index = change.Key,
                        priority = change.Value,
                    })
                    .ToArray();
                await Apply(
                    "edit",
                    new { torrent_id = _target!.TorrentId, changes = new { priorities } },
                    () =>
                    {
                        foreach (var (index, priority) in sent)
                            if (
                                _fileChanges.TryGetValue(index, out var current)
                                && current == priority
                            )
                                _fileChanges.Remove(index);
                    }
                );
                if (_editFailure is not null)
                    break;
            }
        }
        finally
        {
            _fileSave = null;
            save.SetResult();
        }
    }

    private void BeginTrackers()
    {
        if (Trackers is null)
            return;
        _trackerOriginal = string.Join(
            "\n\n",
            Trackers
                .GroupBy(tracker => tracker.Tier)
                .OrderBy(group => group.Key)
                .Select(group => string.Join("\n", group.Select(tracker => tracker.Url)))
        );
        _trackerInput = _trackerOriginal;
        _editingTrackers = true;
        _editFailure = null;
        Refresh();
    }

    internal async Task CommitTrackers()
    {
        if (!CanSave || !_editingTrackers)
            return;
        if (_trackerInput == _trackerOriginal)
        {
            CancelTrackerDraft();
            return;
        }
        var trackers = new List<object>();
        var tier = 0;
        foreach (var line in _trackerInput.Split('\n'))
        {
            var url = line.Trim();
            if (url.Length == 0)
            {
                tier++;
                continue;
            }
            if (
                !Uri.TryCreate(url, UriKind.Absolute, out var address)
                || address.Scheme is not "http" and not "https" and not "udp"
            )
            {
                _editFailure = new CommandException("invalid_trackers", url, Text);
                Refresh();
                return;
            }
            trackers.Add(new { url, tier });
        }
        // Text typed while the save runs stays as the new draft.
        var sent = _trackerInput;
        await Apply(
            "edit",
            new { torrent_id = _target!.TorrentId, changes = new { trackers } },
            () =>
            {
                if (!_editingTrackers)
                    return;
                _trackerOriginal = sent;
                if (_trackerInput == sent)
                    CancelTrackerDraft();
            }
        );
    }

    private async Task Apply(string command, object arguments, Action? confirmed = null)
    {
        while (_pending is { } running)
            await running.Task;
        Invalidate();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = pending;
        _editFailure = null;
        Refresh();
        try
        {
            await _client.Send(command, arguments);
            confirmed?.Invoke();
        }
        catch (Exception error)
        {
            _editFailure = error;
        }
        finally
        {
            _pending = null;
            pending.SetResult();
            Refresh();
            _ = Read();
        }
    }

    private void CancelTrackerDraft()
    {
        _editingTrackers = false;
        _trackerInput = _trackerOriginal;
        _editFailure = null;
        Refresh();
    }

    // Leaving applies the edits, and an edit that fails keeps the person at its
    // error. File choices the engine cannot receive are dropped instead,
    // because they have no Cancel and Retry is unavailable, so keeping them
    // would trap the person on this torrent.
    internal async Task<bool> Depart()
    {
        var target = _target;
        var session = _session;
        bool Current() => _target == target && _session == session;
        while (_pending is { } pending)
        {
            await pending.Task;
            if (!Current() || HasDraft && _editFailure is not null && CanSave)
                return false;
        }
        if (_editingTrackers)
            await CommitTrackers();
        if (!Current() || HasTrackerChanges)
            return false;
        if (HasFileChanges)
            await SaveFiles();
        if (!Current())
            return false;
        if (!HasFileChanges)
            return true;
        if (CanSave)
            return false;
        CancelDraft();
        return true;
    }

    public void CancelDraft()
    {
        var restoreFiles = HasFileChanges;
        _fileChanges.Clear();
        if (restoreFiles && _confirmedFiles is { } files)
            Files.Apply(files, false);
        CancelTrackerDraft();
    }

    private void Clear()
    {
        Files.Clear();
        _filesLoaded = false;
        _confirmedFiles = null;
        Peers = null;
        Trackers = null;
        Pieces = null;
        Folder = Comment = Creator = Magnet = string.Empty;
        Created = PieceSize = PieceCount = 0;
        IsPrivate = null;
    }

    internal void RefreshText()
    {
        foreach (var peer in Peers ?? [])
            peer.RefreshText();
        foreach (var tracker in Trackers ?? [])
            tracker.RefreshText();
        Files.Refresh();
        Refresh();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void Refresh()
    {
        var enabled = CanEdit && _filesLoaded;
        if (Files.IsEnabled != enabled)
            Files.IsEnabled = enabled;
        foreach (
            RelayCommand command in new[]
            {
                EditTrackers,
                CancelTrackers,
                Retry,
                Reannounce,
                OpenFolder,
                CopyV1,
                CopyV2,
                CopyMagnet,
                LimitSpeed,
                MoveFiles,
            }
        )
            command.Refresh();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public sealed record SpeedSample(long Time, double DownloadRate, double UploadRate);
