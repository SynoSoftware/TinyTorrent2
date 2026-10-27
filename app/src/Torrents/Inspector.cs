using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Torrents;

public sealed partial class Inspector : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;
    private readonly PipeClient _client;
    private readonly ICommand[] _commands;
    private readonly Dictionary<int, int> _fileChanges = [];
    private Torrent? _target;

    private InspectorSection _section;
    private string _session = string.Empty;
    private int _context;
    private bool _fetching;
    private (
        string TorrentId,
        TaskCompletionSource Completion,
        Action<JsonElement> Apply
    )? _awaitedPush;
    private bool _loading;
    private (bool Available, bool Editable) _access;
    private TaskCompletionSource? _pending;
    private TaskCompletionSource? _fileSave;
    private bool _filesLoaded;
    private bool _editingTrackers;
    private bool _visible;
    private Exception? _readFailure;
    private Exception? _editFailure;
    private string _trackerInput = string.Empty;
    private string _trackerOriginal = string.Empty;
    private JsonElement? _confirmedFiles;

    public Strings Text => _owner.Text;
    public Torrent? Target => _target;
    public string Name =>
        _target?.Name ?? (IsGlobal ? Text.Get("peers", "all_torrents") : EmptyText);
    public string SizeText => IsGlobal ? string.Empty : _target?.SizeText ?? string.Empty;

    internal bool IsVisible => _visible;
    private int SelectionCount => PeerSelection is null ? _owner.Selected.Count : Scope.Count();
    public string EmptyText =>
        SelectionCount == 0
            ? Text.Get("inspector", "none")
            : Text.FormatCount("inspector", "selection", SelectionCount);
    public bool IsAvailable =>
        _owner.IsConnected && HasContent && (_target is null || _owner.Contains(_target));

    private bool IsCurrent(int context) => context == _context && _visible && IsAvailable;

    // A save keeps its controls enabled so later input keeps focus.
    private bool CanSave =>
        _target is not null && _owner.Contains(_target) && IsAvailable && _owner.CanSave;
    public bool CanEdit => _visible && CanSave && !_owner.IsClosing;
    public bool IsPending => _pending is not null;
    public bool IsLoading => _fetching;
    public bool HasDraft => !IsPending && (HasFileChanges || HasTrackerChanges);
    internal bool HasTrackerChanges => _editingTrackers && _trackerInput != _trackerOriginal;
    private bool IsRemoved =>
        !IsGlobal && _owner.IsConnected && _target is not null && !_owner.Contains(_target);
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
    public IReadOnlyList<Peer>? Peers
    {
        get => _peers;
        private set
        {
            _peers = value;
            Filter();
        }
    }
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
    public bool IsIndeterminate => !HasMetadata && _target is { StatusCode: not "error" };
    public bool ShowsStatus => _target is { StatusCode: not "error" };
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
    public ICommand RetryTorrent { get; }
    public ICommand OpenFolder { get; }
    public ICommand CopyV1 { get; }
    public ICommand CopyV2 { get; }
    public ICommand CopyMagnet { get; }
    public ICommand LimitSpeed { get; }
    public ICommand MoveFiles { get; }
    public ICommand ShowRatioLimit { get; }
    public ICommand ShowConnectionLimit { get; }
    internal ICommand DownloadCities { get; }

    internal Inspector(MainViewModel owner, PipeClient client)
    {
        _owner = owner;
        _client = client;
        DownloadCities = new RelayCommand(
            () => _owner.DownloadData(_owner.Geolocation),
            () => _owner.Geolocation.CanDownload && !_owner.IsClosing
        );
        LimitSpeed = owner.SpeedCommand(() => _target is { } target ? [target] : []);
        MoveFiles = owner.MoveCommand(() => _target is { } target ? [target] : []);
        OpenFolder = new RelayCommand(
            () => _owner.OpenTorrent(_target!, true),
            () =>
                _target is not null && _owner.Contains(_target) && IsAvailable && !_owner.IsClosing
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
            () => HasFileChanges ? CanEdit : IsAvailable && !IsLoading
        );
        Reannounce = new RelayCommand(
            () => Apply("reannounce", new { torrent_id = _target!.TorrentId }),
            () => CanEdit && !_editingTrackers
        );
        RetryTorrent = new RelayCommand(
            () => _owner.SendRetry([_target!]),
            () => CanEdit && _target is { CanRetry: true } target && _owner.Contains(target)
        );
        _commands =
        [
            EditTrackers,
            CancelTrackers,
            Retry,
            Reannounce,
            RetryTorrent,
            OpenFolder,
            CopyV1,
            CopyV2,
            CopyMagnet,
            LimitSpeed,
            MoveFiles,
            DownloadCities,
        ];
        owner.Geolocation.PropertyChanged += (_, _) =>
        {
            Refresh();
            _ = LocatePeers();
        };
    }

    // Another target starts at the given section, so the previous section is
    // not read first.
    internal bool Show(
        Torrent? target,
        InspectorSection? section = null,
        bool allPeers = false,
        IReadOnlyList<Torrent>? selection = null
    )
    {
        if (!Retarget(target, section ?? _section, allPeers, selection))
            return false;
        Refresh();
        return true;
    }

    private bool Retarget(
        Torrent? target,
        InspectorSection section,
        bool allPeers,
        IReadOnlyList<Torrent>? selection
    )
    {
        if (
            _target == target
            && ShowsAllPeers == allPeers
            && ReferenceEquals(PeerSelection, selection)
        )
            return true;
        if (HasDraft || IsPending)
            return false;
        _readFailure = _editFailure = null;
        CancelDraft();
        Clear();
        _target = target;
        ShowsAllPeers = allPeers;
        PeerSelection = selection;
        _section = section;
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

    internal void Observe(string session, bool changed)
    {
        if (_awaitedPush is not null && !IsAvailable)
        {
            Invalidate();
        }
        if (_session != session)
        {
            _session = session;
            _peerCache.Clear();
            Peers = null;
            Trackers = null;
            Pieces = null;
            History = null;
            Invalidate();
            _readFailure = null;
            changed = true;
        }
        if (_visible && (changed || _access != (IsAvailable, CanEdit)))
            Refresh();
        _ = Read();
    }

    internal void Disconnect()
    {
        Invalidate();
        _peerCache.Clear();
        History = null;
        Refresh();
    }

    internal void SetVisible(bool visible)
    {
        if (_visible == visible)
            return;
        _visible = visible;
        if (visible && !CanShow(_section))
            _section = InspectorSection.Peers;
        Invalidate();
        Refresh();
        if (visible)
            _ = Read();
    }

    private async Task Read()
    {
        if (!_visible || !IsAvailable || _fetching || IsPending)
            return;
        _fetching = true;
        var context = ++_context;
        var section = _section;
        var target = _target!;
        var changed = false;
        RefreshLoading();
        try
        {
            if (section == InspectorSection.Speed)
            {
                var history = await ReadHistory(context);
                if (!IsCurrent(context))
                {
                    return;
                }
                History = history;
                _readFailure = null;
                changed = true;
                return;
            }
            if (section == InspectorSection.Peers)
            {
                changed = await ReadPeers(context);
                return;
            }
            await ReadDetail(
                target,
                section,
                context,
                detail =>
                {
                    if (ApplyDetail(detail))
                    {
                        Refresh();
                    }
                }
            );
        }
        catch (Exception error)
        {
            if (context == _context)
            {
                _readFailure = error;
                changed = true;
            }
        }
        finally
        {
            if (context == _context)
            {
                _fetching = false;
                if (changed)
                    Refresh();
                else
                    RefreshLoading();
            }
        }
    }

    internal void Receive(JsonElement message)
    {
        if (
            _awaitedPush is not { } awaited
            || message.GetProperty("session_id").GetString() != _session
            || message.GetProperty("torrent_id").GetString() != awaited.TorrentId
            || message.GetProperty("context").GetInt64() != _context
        )
        {
            return;
        }
        try
        {
            var data = PipeClient.ReadOutcome(message, Text, "torrent");
            awaited.Apply(data);
            if (data.GetProperty("ready").GetBoolean())
                awaited.Completion.TrySetResult();
        }
        catch (Exception error)
        {
            awaited.Completion.TrySetException(error);
        }
    }

    // Every answer uses the same application path while this view is current;
    // completion only ends the wait for fresh detail.
    private async Task ReadDetail(
        Torrent torrent,
        InspectorSection section,
        int context,
        Action<JsonElement> apply
    )
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var receivedPush = false;
        _awaitedPush = (
            torrent.TorrentId,
            completion,
            data =>
            {
                receivedPush = true;
                Apply(data);
            }
        );
        try
        {
            var read = _client.Read(
                Consumer.Inspector,
                "torrent",
                new
                {
                    torrent_id = torrent.TorrentId,
                    context,
                    view = section.ToString().ToLowerInvariant(),
                    include_files = section == InspectorSection.Pieces
                        && Pieces is not { MetadataReady: true },
                }
            );
            // Invalidate withdraws an unsent read; a cancelled task is awaited
            // without throwing, because every refresh can abandon one.
            await ((Task)read).ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext
                    | ConfigureAwaitOptions.SuppressThrowing
            );
            if (read.IsCanceled)
            {
                return;
            }
            var reply = await read;
            // The push can reach the dispatcher before this await resumes, and
            // is then newer than the held facts.
            if (!completion.Task.IsCompleted && !receivedPush)
            {
                Apply(reply);
            }
            await completion.Task.WaitAsync(PipeClient.RequestTimeout);
        }
        catch (TimeoutException)
        {
            // Releasing the demand lets background file reads continue.
            if (context == _context)
            {
                await Release();
            }
            throw;
        }
        finally
        {
            if (ReferenceEquals(_awaitedPush?.Completion, completion))
            {
                _awaitedPush = null;
            }
        }

        void Apply(JsonElement data)
        {
            if (IsCurrent(context) && _owner.Contains(torrent))
                apply(data);
        }
    }

    internal void RefreshScope()
    {
        Clear();
        _readFailure = null;
        if (!CanShow(_section))
            _section = InspectorSection.Peers;
        Invalidate();
        Refresh();
        _ = Read();
    }

    internal void RefreshDetail()
    {
        if (!_visible)
            return;
        Invalidate();
        _ = Read();
    }

    private bool ApplyDetail(JsonElement reply)
    {
        var changed = _readFailure is not null;
        switch (_section)
        {
            case InspectorSection.General:
                var general = (
                    Folder,
                    Magnet,
                    Comment,
                    Creator,
                    Created,
                    PieceSize,
                    PieceCount,
                    IsPrivate
                );
                ApplyGeneral(reply);
                changed |=
                    general
                    != (
                        Folder,
                        Magnet,
                        Comment,
                        Creator,
                        Created,
                        PieceSize,
                        PieceCount,
                        IsPrivate
                    );
                break;
            case InspectorSection.Files:
                var files = (HasFiles, HasFileChanges, _confirmedFiles is not null, _editFailure);
                ApplyFiles(reply);
                changed |=
                    files != (HasFiles, HasFileChanges, _confirmedFiles is not null, _editFailure);
                break;
            case InspectorSection.Trackers:
                if (reply.GetProperty("trackers").ValueKind == JsonValueKind.Null)
                    break;
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
                {
                    Trackers = nextTrackers;
                    changed = true;
                }
                else
                    RowsUpdated?.Invoke(this, InspectorSection.Trackers);
                break;
            case InspectorSection.Pieces:
                if (reply.TryGetProperty("verified", out _))
                {
                    Pieces = new Pieces(reply, Pieces?.Files ?? []);
                    changed = true;
                }
                break;
        }
        _readFailure = null;
        return changed;
    }

    // Switch histories at a shared sample end. Splitting a coarse average
    // cannot recover the rate of its older portion without its observations.
    private async Task<SpeedSample[]> ReadHistory(int context)
    {
        var target = _target;
        string[] ids = IsGlobal
            ? []
            : Scope.Select(torrent => torrent.TorrentId).ToArray();
        if (!ShowsAllPeers && PeerSelection is not null && ids.Length == 0)
            return [];
        var day = Samples(
            await _client.Read(Consumer.Inspector, "history", HistoryArguments("day"))
        );
        if (!IsCurrent(context))
            return [];
        if (History is null)
        {
            History = day;
            Refresh();
        }
        var recent = Samples(
            await _client.Read(Consumer.Inspector, "history", HistoryArguments("five_minutes"))
        );
        if (recent.Length == 0)
            return day;
        var ends = recent.Select(sample => sample.Time).ToHashSet();
        var cut =
            day.FirstOrDefault(sample => ends.Contains(sample.Time))?.Time ?? recent[0].Start - 1;
        return
        [
            .. day.Where(sample => sample.Time <= cut),
            .. recent.Where(sample => sample.Start > cut),
        ];

        Dictionary<string, object> HistoryArguments(string range)
        {
            var arguments = new Dictionary<string, object>
            {
                ["range"] = range,
                ["release_detail"] = true,
            };
            if (target is not null)
                arguments["torrent_id"] = target.TorrentId;
            else if (ids.Length > 0)
                arguments["torrent_ids"] = ids;
            return arguments;
        }

        static SpeedSample[] Samples(JsonElement reply) =>
            reply
                .GetProperty("samples")
                .EnumerateArray()
                .Select(sample => new SpeedSample(
                    sample.GetProperty("time").GetInt64(),
                    sample.GetProperty("download_rate").GetDouble(),
                    sample.GetProperty("upload_rate").GetDouble(),
                    sample.GetProperty("start").GetInt64(),
                    sample.GetProperty("count").GetInt32()
                ))
                .ToArray();
    }

    // A read for the old context is stale, so an unsent one is withdrawn.
    private void Invalidate()
    {
        _context++;
        _fetching = false;
        _awaitedPush?.Completion.TrySetResult();
        _awaitedPush = null;
        _client.Withdraw(Consumer.Inspector);
        if (!_visible || IsGlobal || _section == InspectorSection.Speed || _target is null)
            _ = Release();
    }

    // The outcome does not matter, and a later Invalidate often withdraws
    // this read, so it is awaited without throwing.
    private async Task Release() =>
        await (
            (Task)_client.Read(Consumer.Inspector, "torrent", new { release = true })
        ).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private void ApplyGeneral(JsonElement reply)
    {
        Folder = reply.GetProperty("folder").GetString()!;
        if (reply.GetProperty("magnet").GetString() is { } magnet)
            Magnet = magnet;
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
        var ready = reply.GetProperty("ready").GetBoolean();
        if (ready)
            _confirmedFiles = files.Clone();
        if (!_filesLoaded)
        {
            Files.Load(files);
            _filesLoaded = true;
        }
        if (!ready)
        {
            Files.Apply(files, IsPending || HasFileChanges);
            return;
        }
        if (!IsPending && _fileChanges.Count > 0)
        {
            var priorities = files
                .EnumerateArray()
                .ToDictionary(
                    file => file.GetProperty("index").GetInt32(),
                    file => file.GetProperty("priority").GetInt32()
                );
            if (
                _fileChanges.All(change =>
                    priorities.TryGetValue(change.Key, out var priority) && priority == change.Value
                )
            )
            {
                _fileChanges.Clear();
                _editFailure = null;
            }
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
            await _client.Send(command, arguments, Requester.Person);
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
        _located.Clear();
        Trackers = null;
        Pieces = null;
        History = null;
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
        _access = (IsAvailable, CanEdit);
        _loading = IsLoading;
        var enabled = CanEdit && _confirmedFiles is not null;
        if (Files.IsEnabled != enabled)
            Files.IsEnabled = enabled;
        foreach (RelayCommand command in _commands)
            command.Refresh();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private void RefreshLoading()
    {
        if (_loading == IsLoading)
            return;
        _loading = IsLoading;
        ((RelayCommand)Retry).Refresh();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoading)));
    }
}

public sealed record SpeedSample(
    long Time,
    double DownloadRate,
    double UploadRate,
    long Start,
    double Weight
);
