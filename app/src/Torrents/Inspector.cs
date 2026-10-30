using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Torrents;

public sealed partial class Inspector : INotifyPropertyChanged
{
    private static readonly TimeSpan UpdateBudget = TimeSpan.FromMilliseconds(16);
    private readonly MainViewModel _owner;
    private readonly PipeClient _client;
    private readonly DispatcherQueue _dispatcher;
    private readonly ICommand[] _commands;
    private FrameCallback? _refreshFrame;
    private FrameCallback? _readFrame;
    private CancellationTokenSource _reading = new();

    // File choices not yet sent, in the order the person made them.
    private readonly List<FileChoice> _fileChanges = [];
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
    private JsonElement? _fileReply;
    private PieceFiles? _pieceFiles;

    public Strings Text => _owner.Text;
    public Torrent? Target => _target;
    public string Name =>
        _target?.Name ?? (IsGlobal ? Text.Get("peers", "all_torrents") : EmptyText);
    public string SizeText => IsGlobal ? string.Empty : _target?.SizeText ?? string.Empty;

    internal bool IsVisible => _visible;
    internal CancellationToken Cancellation => _reading.Token;
    internal bool IsReplacing { get; private set; }
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
    public bool CanEdit => _visible && CanSave && !IsReplacing && !_owner.IsClosing;
    public bool IsPending => _pending is not null;
    private bool IsStarting => _readFailure is CommandException { Code: "starting" };
    public bool IsLoading => _fetching || (_readFrame is not null && _visible && IsAvailable) || (IsAvailable && IsStarting);
    public bool HasDraft => !IsPending && (HasFileChanges || HasTrackerChanges);
    internal bool HasTrackerChanges => _editingTrackers && _trackerInput != _trackerOriginal;
    private bool IsRemoved =>
        !IsGlobal && _owner.IsConnected && _target is not null && !_owner.Contains(_target);
    public bool HasError => IsRemoved || (_readFailure is not null && !IsStarting) || _editFailure is not null;
    public string Message =>
        IsRemoved ? Text.Get("inspector", "removed")
        : _editFailure is { } edit ? Text.Error(edit)
        : !IsStarting && _readFailure is { } read ? Text.Error(read)
        : string.Empty;
    public FileSelection Files { get; private set; }

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
    public bool IsIndeterminate => !HasMetadata && _target is { IsError: false };

    // Summary shows the error text, which the Error state's word would only
    // repeat. Another state with a problem keeps its word.
    public bool ShowsStatus => _target is { State: not TorrentState.Error };
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

    internal Inspector(MainViewModel owner, PipeClient client, DispatcherQueue dispatcher)
    {
        _owner = owner;
        _client = client;
        _dispatcher = dispatcher;
        DownloadCities = new RelayCommand(
            () => _owner.DownloadData(_owner.Geolocation),
            () => _owner.Geolocation.CanDownload && !_owner.IsClosing
        );
        LimitSpeed = owner.SpeedCommand(() => _target is { } target ? [target] : []);
        MoveFiles = owner.MoveCommand(() => _target is { } target ? [target] : []);
        OpenFolder = new RelayCommand(
            () => _owner.OpenTorrent(_target!, true),
            () =>
                _target is not null && _owner.Contains(_target) && IsAvailable && !IsReplacing && !_owner.IsClosing
        );
        CopyV1 = new RelayCommand(() => _owner.Copy(HashV1), () => !IsReplacing && HashV1.Length > 0);
        CopyV2 = new RelayCommand(() => _owner.Copy(HashV2), () => !IsReplacing && HashV2.Length > 0);
        CopyMagnet = new RelayCommand(() => _owner.Copy(Magnet), () => !IsReplacing && Magnet.Length > 0);
        ShowRatioLimit = new RelayCommand(
            () => _owner.ShowSetting(_owner.Settings.Ratio),
            () => true
        );
        ShowConnectionLimit = new RelayCommand(
            () => _owner.ShowSetting(_owner.Settings.ConnectionLimit),
            () => true
        );
        Files = CreateFiles();
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

    private FileSelection CreateFiles()
    {
        var files = new FileSelection(Text) { ShowsProgress = true, IsEnabled = false };
        files.Failed += (sender, error) =>
        {
            if (!ReferenceEquals(sender, Files))
                return;
            _readFailure = error;
            Refresh();
        };
        return files;
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
        IsReplacing = _visible && HasContent
            && (target is not null || section is InspectorSection.Peers or InspectorSection.Speed);
        Clear();
        _target = target;
        ShowsAllPeers = allPeers;
        PeerSelection = selection;
        _section = section;
        Invalidate();
        QueueRead();
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
        QueueRead();
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
            _pieceFiles = null;
            History = null;
            Invalidate();
            _readFailure = null;
            changed = true;
        }
        if (_visible && (changed || _access != (IsAvailable, CanEdit)))
            Refresh();
        QueueRead();
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
            QueueRead();
    }

    private void QueueRead()
    {
        if (_readFrame is not null || !_visible || !IsAvailable || _fetching || IsPending || _owner.IsClosing)
            return;
        _readFrame = new FrameCallback(_dispatcher, () =>
        {
            _readFrame = null;
            _ = Read();
        });
    }

    private async Task Read()
    {
        if (!_visible || !IsAvailable || _fetching || IsPending || _owner.IsClosing)
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
                IsReplacing = false;
                _readFailure = null;
                changed = true;
                return;
            }
            if (section == InspectorSection.Peers)
            {
                changed = await ReadPeers(context);
                return;
            }
            await ReadPages(target, section, context);
            if (!IsCurrent(context))
            {
                return;
            }
            await ReadDetail(
                target,
                section,
                context,
                async detail =>
                {
                    if (await ApplyDetail(detail, context))
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
        Func<JsonElement, Task> apply
    )
    {
        var cancellation = _reading.Token;
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var receivedPush = false;
        (JsonElement Data, bool Fresh)? latest = null;
        var applying = false;
        _awaitedPush = (
            torrent.TorrentId,
            completion,
            data =>
            {
                receivedPush = true;
                Queue(data, true);
            }
        );
        try
        {
            var arguments = new Dictionary<string, object>
            {
                ["torrent_id"] = torrent.TorrentId,
                ["context"] = context,
                ["view"] = section.ToString().ToLowerInvariant(),
            };
            if (section == InspectorSection.Files)
            {
                var keys = Files.Keys;
                arguments["folders"] = keys.Folders;
                arguments["files"] = keys.Files;
            }
            var read = _client.Read(Consumer.Inspector, "torrent", arguments).WaitAsync(cancellation);
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
                Queue(reply, false);
            }
            await completion.Task.WaitAsync(PipeClient.RequestTimeout, cancellation);
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

        void Queue(JsonElement data, bool fresh)
        {
            if (!IsCurrent(context) || !_owner.Contains(torrent))
                return;
            latest = (data, fresh);
            if (!applying)
                _ = Drain();
        }

        async Task Drain()
        {
            applying = true;
            try
            {
                while (IsCurrent(context) && latest is { } next)
                {
                    latest = null;
                    await apply(next.Data);
                    if (latest is null && next.Fresh && next.Data.GetProperty("ready").GetBoolean())
                        completion.TrySetResult();
                }
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
            finally
            {
                applying = false;
            }
        }
    }

    internal void RefreshScope()
    {
        IsReplacing = _visible && HasContent;
        Clear();
        _readFailure = null;
        if (!CanShow(_section))
            _section = InspectorSection.Peers;
        Invalidate();
        Refresh();
        QueueRead();
    }

    internal void RefreshDetail()
    {
        if (!_visible)
            return;
        Invalidate();
        QueueRead();
    }

    private async Task<bool> ApplyDetail(JsonElement reply, int context)
    {
        var cancellation = _reading.Token;
        var changed = _readFailure is not null || IsReplacing;
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
                if (!_filesLoaded && reply.GetProperty("metadata_ready").GetBoolean())
                    return false;
                var files = (HasFiles, HasFileChanges, _confirmedFiles is not null, _editFailure);
                ApplyFiles(reply);
                changed |=
                    files != (HasFiles, HasFileChanges, _confirmedFiles is not null, _editFailure);
                break;
            case InspectorSection.Trackers:
                if (reply.GetProperty("trackers").ValueKind == JsonValueKind.Null)
                    return false;
                var nextTrackers = await Task.Run(() => reply.GetProperty("trackers")
                    .EnumerateArray().Select(data =>
                    {
                        cancellation.ThrowIfCancellationRequested();
                        return new Tracker(Text, data);
                    }).ToArray(), cancellation);
                await Yield();
                if (!IsCurrent(context))
                    return false;
                var started = Stopwatch.GetTimestamp();
                var trackers = (Trackers ?? []).ToDictionary(tracker => tracker.Url);
                for (var index = 0; index < nextTrackers.Length; index++)
                {
                    if (!trackers.TryGetValue(nextTrackers[index].Url, out var tracker))
                        continue;
                    if (Stopwatch.GetElapsedTime(started) >= UpdateBudget)
                    {
                        await Yield();
                        if (!IsCurrent(context))
                            return false;
                        started = Stopwatch.GetTimestamp();
                    }
                    tracker.Update(nextTrackers[index]);
                    nextTrackers[index] = tracker;
                }
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
                    var pieceFiles = _pieceFiles ?? PieceFiles.None;
                    var pieces = await Task.Run(() => new Pieces(reply, pieceFiles, cancellation), cancellation);
                    await Yield();
                    if (!IsCurrent(context))
                        return false;
                    Pieces = pieces;
                    changed = true;
                }
                else
                    return false;
                break;
        }
        IsReplacing = false;
        _readFailure = null;
        return changed;
    }

    private Task Yield()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => completion.TrySetResult()))
            completion.TrySetCanceled();
        return completion.Task;
    }

    // Switch histories at a shared sample end. Splitting a coarse average
    // cannot recover the rate of its older portion without its observations.
    private async Task<SpeedSample[]> ReadHistory(int context)
    {
        var cancellation = _reading.Token;
        var target = _target;
        string[] ids = IsGlobal
            ? []
            : Scope.Select(torrent => torrent.TorrentId).ToArray();
        if (!ShowsAllPeers && PeerSelection is not null && ids.Length == 0)
            return [];
        var data = await _client.Read(Consumer.Inspector, "history", HistoryArguments("day")).WaitAsync(cancellation);
        var day = await Task.Run(() => Samples(data), cancellation);
        if (!IsCurrent(context))
            return [];
        if (History is null)
        {
            History = day;
            IsReplacing = false;
            Refresh();
        }
        data = await _client.Read(Consumer.Inspector, "history", HistoryArguments("five_minutes")).WaitAsync(cancellation);
        return await Task.Run(() =>
        {
            var recent = Samples(data);
            if (recent.Length == 0)
                return day;
            var ends = recent.Select(sample => sample.Time).ToHashSet();
            var cut = day.FirstOrDefault(sample => ends.Contains(sample.Time))?.Time ?? recent[0].Start - 1;
            return day.Where(sample => sample.Time <= cut)
                .Concat(recent.Where(sample => sample.Start > cut)).ToArray();
        }, cancellation);

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

        SpeedSample[] Samples(JsonElement reply) =>
            reply
                .GetProperty("samples")
                .EnumerateArray()
                .Select(sample =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    return new SpeedSample(
                        sample.GetProperty("time").GetInt64(),
                        sample.GetProperty("download_rate").GetDouble(),
                        sample.GetProperty("upload_rate").GetDouble(),
                        sample.GetProperty("start").GetInt64(),
                        sample.GetProperty("count").GetInt32()
                    );
                })
                .ToArray();
    }

    // A read for the old context is stale, so an unsent one is withdrawn.
    private void Invalidate()
    {
        _readFrame?.Dispose();
        _readFrame = null;
        _refreshFrame?.Dispose();
        _refreshFrame = null;
        _context++;
        _reading.Cancel();
        _reading.Dispose();
        _reading = new();
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

    // Names, sizes and pieces come from the file tree and pages, which
    // ReadPages reads before this view, so the rows exist once the engine has
    // the metadata.
    private async Task ReadPages(Torrent torrent, InspectorSection section, int context)
    {
        var cancellation = _reading.Token;
        if (section == InspectorSection.Files && !_filesLoaded)
        {
            JsonElement root;
            try
            {
                root = await ReadTree(torrent, new() { ["folder"] = string.Empty }).WaitAsync(cancellation);
            }
            catch (CommandException error) when (error.Code == "metadata_unavailable")
            {
                return;
            }
            if (!IsCurrent(context))
            {
                return;
            }
            var files = CreateFiles();
            await Task.Run(() => files.Show(root,
                request => ReadTree(torrent, request), EditFiles, cancellation), cancellation);
            await Yield();
            if (!IsCurrent(context))
                return;
            Files = files;
            _filesLoaded = true;
            IsReplacing = false;
            Refresh();
        }
        else if (section == InspectorSection.Pieces && _pieceFiles is null)
        {
            // A torrent without metadata has no files yet.
            var count = await _client.FileCount(torrent.TorrentId).WaitAsync(cancellation);
            if (!IsCurrent(context) || count == 0)
            {
                return;
            }
            PieceFiles files;
            if (count <= FileSelection.Window)
            {
                List<PieceFile> named = [];
                await foreach (var file in _client.TorrentFiles(torrent.TorrentId))
                {
                    if (!IsCurrent(context))
                    {
                        return;
                    }
                    named.Add(new PieceFile(file.Path, file.Pieces));
                }
                files = new PieceFiles.Named(named);
            }
            else
            {
                List<PieceRange> counted = [];
                await foreach (var pieces in _client.TorrentPieces(torrent.TorrentId))
                {
                    if (!IsCurrent(context))
                    {
                        return;
                    }
                    counted.Add(pieces);
                }
                files = new PieceFiles.Counted(counted);
            }
            if (IsCurrent(context))
            {
                _pieceFiles = files;
            }
        }
    }

    private void ApplyFiles(JsonElement reply)
    {
        if (!reply.GetProperty("metadata_ready").GetBoolean() || !_filesLoaded)
            return;
        var ready = reply.GetProperty("ready").GetBoolean();
        if (ready)
            _confirmedFiles = reply;
        if (
            ready
            && !IsPending
            && _fileChanges.Count > 0
            && _fileChanges.All(change => Files.Holds(reply, change))
        )
        {
            _fileChanges.Clear();
            _editFailure = null;
        }
        Files.Apply(reply, IsPending || HasFileChanges);
    }

    private Task<JsonElement> ReadTree(Torrent torrent, Dictionary<string, object> request)
    {
        request["torrent_id"] = torrent.TorrentId;
        return _client.Read("tree", request);
    }

    // The browser's choices join the draft, which sends them one edit at a
    // time. Gives the reply to the last send once the draft is sent, or null
    // while choices remain.
    private async Task<JsonElement?> EditFiles(IReadOnlyList<FileChoice> choices)
    {
        foreach (var choice in choices)
        {
            // A choice replaces an earlier one for the same file or folder,
            // and a folder's choice replaces those under it. A query choice
            // keeps the choices before it, which are sent first.
            var queued = _fileChanges.FindLastIndex(change => change.Target is FileTarget.Query) + 1;
            for (var index = _fileChanges.Count - 1; index >= queued; index--)
            {
                var target = _fileChanges[index].Target;
                if (
                    target == choice.Target
                    || choice.Target is FileTarget.Folder folder && target.IsUnder(folder)
                )
                    _fileChanges.RemoveAt(index);
            }
            _fileChanges.Add(choice);
        }
        Refresh();
        await SaveFiles();
        return HasFileChanges ? null : _fileReply;
    }

    // A choice made while a save runs joins that save, which sends it when it
    // ends. One edit sends the choices up to the next query choice, which the
    // engine takes only alone; it applies folders before files, so a file
    // chosen after its folder keeps its own choice.
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
                var sent = _fileChanges[0].Target is FileTarget.Query
                    ? new[] { _fileChanges[0] }
                    : _fileChanges.TakeWhile(change => change.Target is not FileTarget.Query).ToArray();
                var reply = await Apply(
                    "edit",
                    new
                    {
                        torrent_id = _target!.TorrentId,
                        changes = FileChoice.Changes(sent),
                        loaded = Files.Loaded(sent),
                    },
                    () =>
                    {
                        foreach (var choice in sent)
                            _fileChanges.Remove(choice);
                    }
                );
                if (_editFailure is not null)
                    break;
                _fileReply = reply;
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

    // Gives the engine's reply, or null when the command failed.
    private async Task<JsonElement?> Apply(
        string command,
        object arguments,
        Action? confirmed = null
    )
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
            var reply = await _client.Send(command, arguments, Requester.Person);
            confirmed?.Invoke();
            return reply;
        }
        catch (Exception error)
        {
            _editFailure = error;
            return null;
        }
        finally
        {
            _pending = null;
            pending.SetResult();
            Refresh();
            QueueRead();
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
        Files.Detach();
        Files = CreateFiles();
        _filesLoaded = false;
        _confirmedFiles = null;
        _fileReply = null;
        Peers = null;
        _located.Clear();
        Trackers = null;
        Pieces = null;
        _pieceFiles = null;
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
        if (_owner.IsClosed)
            return;
        _access = (IsAvailable, CanEdit);
        _loading = IsLoading;
        var enabled = CanEdit && _confirmedFiles is not null;
        if (Files.IsEnabled != enabled)
            Files.IsEnabled = enabled;
        foreach (RelayCommand command in _commands)
            command.Refresh();
        if (_refreshFrame is not null)
            return;
        _refreshFrame = new FrameCallback(_dispatcher, () =>
        {
            _refreshFrame = null;
            // Keep the displayed detail bound until its replacement is ready.
            if (IsReplacing)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReplacing)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEdit)));
            }
            else
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        });
    }

    internal void Close()
    {
        _visible = false;
        Invalidate();
    }

    private void RefreshLoading()
    {
        if (_loading == IsLoading)
            return;
        Refresh();
    }
}

public sealed record SpeedSample(
    long Time,
    double DownloadRate,
    double UploadRate,
    long Start,
    double Weight
);
