using System.Collections.Frozen;
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
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Shell;

public sealed partial class Inspector : INotifyPropertyChanged
{
    private static readonly TimeSpan UpdateBudget = TimeSpan.FromMilliseconds(16);
    private readonly MainViewModel _owner;
    private readonly PipeClient _client;
    private readonly DispatcherQueue _dispatcher;
    private readonly ICommand[] _commands;
    // Each command's answer its last CanExecuteChanged announced, or null before the first.
    private readonly bool?[] _enabled;
    private FrameCallback? _refreshFrame;
    // The pending refresh raises every property, not only the facts.
    private bool _refreshesAll;
    private FrameCallback? _readFrame;
    private CancellationTokenSource _reading = new();

    // File choices not yet sent, in the order the person made them.
    private readonly List<FileChoice> _fileChanges = [];
    private Torrent? _target;

    private InspectorSection _section;
    private string _session = string.Empty;
    private int _context;
    private bool _fetching;

    // The view the engine's subscription serves, until a new view replaces it.
    private Subscription? _subscription;

    // Observations of the subscription waiting to apply, the newest of each torrent.
    private readonly Dictionary<string, PipeClient.Observation> _observed = new(StringComparer.Ordinal);
    private bool _observing;

    // The torrents whose fresh facts the current round still lacks, and those
    // that failed, which a round does not wait for.
    private readonly HashSet<string> _unready = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private TaskCompletionSource? _firstRound;
    private bool _loading;
    private (bool Available, bool Editable) _access;
    private TaskCompletionSource? _pending;
    private TaskCompletionSource? _fileSave;
    private bool _filesLoaded;
    private bool _editingTrackers;
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

    internal bool IsVisible => _workspace == WindowPage.Torrents;
    internal CancellationToken Cancellation => _reading.Token;
    internal bool IsReplacing { get; private set; }
    private int SelectionCount => PeerSelection is null ? _owner.Selected.Count : Scope.Count();
    public string EmptyText =>
        SelectionCount == 0
            ? Text.Get("inspector", "none")
            : Text.FormatCount("inspector", "selection", SelectionCount);
    public bool IsAvailable =>
        _owner.IsConnected && HasContent && (_target is null || _owner.Contains(_target));

    private bool IsCurrent(int context) => context == _context && IsVisible && IsAvailable;

    // A save keeps its controls enabled so later input keeps focus.
    private bool CanSave =>
        _target is not null && _owner.Contains(_target) && IsAvailable && _owner.CanSave;
    public bool CanEdit => IsVisible && CanSave && !IsReplacing && !_owner.IsClosing;
    public bool IsPending => _pending is not null;
    private bool IsStarting => _readFailure is CommandException { Reason: EngineFailure.Starting };
    public bool IsLoading =>
        _subjectFrame is not null
        || _fetching
        || (_readFrame is not null && IsVisible && IsAvailable)
        || (IsAvailable && IsStarting);
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
        // A new view, whose peers the map groups again.
        private set
        {
            _mapReplies = null;
            ShowPeers(value);
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
    public DateTimeOffset? Created { get; private set; }
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
    public string CommentText => Comment.Length > 0 ? Comment : Strings.NoValue;
    public string CreatorText => Creator.Length > 0 ? Creator : Strings.NoValue;
    public string CreatedText => Created is { } created ? Text.Time(created) : Strings.NoValue;
    public string PiecesText =>
        HasMetadata
            ? Text.Format(
                "inspector",
                "piece_count",
                PieceCount.ToString("N0", CultureInfo.CurrentCulture),
                Text.Bytes(PieceSize)
            )
            : Strings.NoValue;
    public string PrivacyText =>
        IsPrivate is { } privacy
            ? Text.Get("inspector", privacy ? "private" : "public")
            : Strings.NoValue;
    public string PrivacyGlyph =>
        IsPrivate switch
        {
            true => Syno.Segoe.Lock,
            false => Syno.Segoe.Globe,
            null => Syno.Segoe.Shield,
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
        _enabled = new bool?[_commands.Length];
        owner.Geolocation.PropertyChanged += (_, _) =>
        {
            Refresh();
            // Each reply looks its kept peers up again in new location data,
            // and a new subscription asks for the shown torrents' replies at once.
            if (_locationVersion != _owner.Geolocation.Version)
            {
                _locationVersion = _owner.Geolocation.Version;
                if (_section == InspectorSection.Peers)
                {
                    RefreshDetail();
                }
            }
            _ = LocateOrigin();
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
        _selection = _owner.Selected;
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
        RenewPeerSubject();
        _readFailure = _editFailure = null;
        CancelDraft();
        IsReplacing =
            IsVisible && HasContent
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

    internal Task Copy(string text) => _owner.Copy(text);

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
        if (_subscription is not null && !IsAvailable)
        {
            Invalidate();
        }
        var renewed = _session != session;
        if (renewed)
        {
            _session = session;
            ForgetSwarms();
            Map.ResetPeak();
            Trackers = null;
            Pieces = null;
            _pieceFiles = null;
            History = null;
            if (IsVisible)
                Invalidate();
            else if (!IsActive)
                CancelReading();
            _readFailure = null;
        }
        if (!IsVisible)
            return;
        if (_target is not null && !_owner.Contains(_target))
        {
            QueueSubject();
            return;
        }
        if (renewed || _access != (IsAvailable, CanEdit))
            Refresh();
        else if (changed)
            Refresh(everything: false);
        QueueRead();
    }

    internal void Disconnect()
    {
        if (IsVisible)
            Invalidate();
        ForgetHiddenSwarms();
        History = null;
        Refresh();
    }

    private void QueueRead()
    {
        if (
            _subjectFrame is not null
            || _readFrame is not null
            || !IsVisible
            || !IsAvailable
            || _fetching
            || IsPending
            || _owner.IsClosing
            || IsServed
        )
            return;
        _readFrame = new FrameCallback(_dispatcher, () =>
        {
            _readFrame = null;
            _ = Read();
        });
    }

    // The engine keeps a subscription's view current; only a new view
    // subscribes again. Every snapshot asks for a read, and a queued read
    // counts as loading, so a served view queues none.
    private bool IsServed =>
        _section != InspectorSection.Speed
        && _subscription is { } held
        && held.Serves(_context, Subjects(), SubscribedKeys());

    private async Task Read()
    {
        if (!IsVisible || !IsAvailable || _fetching || IsPending || _owner.IsClosing)
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
            await Subscribe(context, [target], section);
        }
        catch (Exception error)
        {
            if (context == _context)
            {
                // The next read subscribes again, as the engine may not have
                // taken this subscription.
                _subscription = null;
                _readFailure = error;
                changed = true;
            }
        }
        finally
        {
            if (context == _context)
            {
                _fetching = false;
                if (section == InspectorSection.Peers)
                    EndMapRound();
                if (changed)
                    Refresh();
                else
                    RefreshLoading();
            }
        }
    }

    /// <summary>The torrents the section reads: the peers scope's torrents that have peers, or the one inspected.</summary>
    private Torrent[] Subjects() =>
        _section == InspectorSection.Peers && _target is null
            ? Scope.Where(torrent => torrent.PeerCount > 0 || torrent.KnownPeerCount > 0).ToArray()
            : _target is { } target ? [target] : [];

    // The Files browser's loaded folders and files, whose values the engine sends.
    private FileKeys? SubscribedKeys() => _section == InspectorSection.Files ? Files.Keys : null;

    /// <summary>
    /// Subscribes to the section of <paramref name="targets"/>, then waits for
    /// the first round: each torrent's fresh facts or its failure. The engine
    /// then pushes fresh facts at the refresh interval until the view changes.
    /// </summary>
    private async Task Subscribe(int context, Torrent[] targets, InspectorSection section)
    {
        var keys = SubscribedKeys();
        var subscription = new Subscription(context, targets, keys);
        var round = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _subscription = subscription;
        ForgetObserved();
        _failed.Clear();
        _unready.Clear();
        _unready.UnionWith(subscription.Ids);
        _firstRound = round;
        if (_unready.Count == 0)
            round.TrySetResult();
        var arguments = new Dictionary<string, object>
        {
            ["section"] = section.ToString().ToLowerInvariant(),
            ["torrent_ids"] = subscription.Ids,
        };
        if (keys is not null)
        {
            arguments["folders"] = keys.Folders;
            arguments["files"] = keys.Files;
        }
        var subscribed = _client.Subscribe(context, arguments);
        // Invalidate withdraws an unsent subscription; a cancelled task is
        // awaited without throwing, because every new view can abandon one.
        await ((Task)subscribed).ConfigureAwait(
            ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing
        );
        if (subscribed.IsCanceled || !IsCurrent(context))
            return;
        await subscribed;
        var ready = round.Task.WaitAsync(PipeClient.RequestTimeout, _reading.Token);
        await ((Task)ready).ConfigureAwait(
            ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing
        );
        if (!ready.IsCanceled)
            await ready;
    }

    internal void Receive(PipeClient.Observation observation)
    {
        if (
            _subscription is not { } subscription
            || observation.SessionId != _session
            || observation.Context != subscription.Context
        )
        {
            observation.Return();
            return;
        }
        if (_observed.Remove(observation.TorrentId, out var replaced))
        {
            replaced.Return();
        }
        _observed[observation.TorrentId] = observation;
        if (!_observing)
            _ = ApplyObserved();
    }

    // Applies the observations in turns that never overlap, so each turn
    // starts from the view the last one left.
    private async Task ApplyObserved()
    {
        _observing = true;
        try
        {
            while (_observed.Count > 0 && _subscription is { } subscription)
            {
                PipeClient.Observation[] batch = [.. _observed.Values];
                _observed.Clear();
                try
                {
                    await Apply(batch, subscription);
                }
                finally
                {
                    // Apply has awaited every reader of the batch, the peers'
                    // worker included, so its buffers go back to the pool.
                    foreach (var observation in batch)
                        observation.Return();
                }
            }
        }
        finally
        {
            _observing = false;
        }
    }

    private async Task Apply(PipeClient.Observation[] batch, Subscription subscription)
    {
        var context = subscription.Context;
        if (!IsCurrent(context))
            return;
        List<(int Slot, string TorrentId, JsonElement Rows)> peers = [];
        JsonElement? detail = null;
        List<string> answered = [];
        foreach (var observation in batch)
        {
            var torrentId = observation.TorrentId;
            if (observation.Failure is { } error)
            {
                // A torrent that cannot be read answers its part of the round;
                // its failure replaces the view only when it is the view's one torrent.
                _failed.Add(torrentId);
                answered.Add(torrentId);
                if (subscription.Ids.Length == 1)
                {
                    _readFailure = error;
                    Refresh();
                }
                continue;
            }
            var data = observation.Detail;
            _failed.Remove(torrentId);
            if (data.GetProperty("ready").GetBoolean())
                answered.Add(torrentId);
            if (_section != InspectorSection.Peers)
                detail = data;
            else if (data.GetProperty("peers") is { ValueKind: not JsonValueKind.Null } rows)
                peers.Add((Array.IndexOf(subscription.Ids, torrentId), torrentId, rows));
        }
        try
        {
            if (peers.Count > 0)
            {
                var refresh = await ApplyPeers(peers, context);
                if (!IsCurrent(context))
                    return;
                refresh |= _readFailure is not null;
                _readFailure = null;
                if (refresh)
                    Refresh();
            }
            // The section keeps parts of its values past the observation's
            // return, so it applies a copy.
            if (detail is { } latest && await ApplyDetail(latest.Clone(), context))
                Refresh();
        }
        catch (OperationCanceledException) when (_reading.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error)
        {
            if (context == _context)
            {
                _readFailure = error;
                Refresh();
            }
            return;
        }
        if (!IsCurrent(context))
            return;
        _unready.ExceptWith(answered);
        if (_unready.Count == 0)
            EndRound(subscription);
    }

    // Every torrent sent fresh facts or failed: the first round ends the
    // loading, and every round gives the map its changes once.
    private void EndRound(Subscription subscription)
    {
        _firstRound?.TrySetResult();
        if (_section == InspectorSection.Peers)
            EndMapRound();
        _unready.UnionWith(subscription.Ids.Where(id => !_failed.Contains(id)));
    }

    /// <summary>The view a subscription serves: its context, torrents in slot order, and loaded files.</summary>
    private sealed record Subscription(int Context, string[] Ids, FileKeys? Keys)
    {
        internal Subscription(int context, Torrent[] targets, FileKeys? keys)
            : this(context, [.. targets.Select(torrent => torrent.TorrentId)], keys) { }

        // The browser keeps its keys until its rows change, so unchanged keys
        // are the same object.
        internal bool Serves(int context, Torrent[] targets, FileKeys? keys) =>
            Context == context
            && Ids.SequenceEqual(targets.Select(torrent => torrent.TorrentId))
            && (
                ReferenceEquals(Keys, keys)
                || Keys is not null
                    && keys is not null
                    && Keys.Folders.SequenceEqual(keys.Folders)
                    && Keys.Files.SequenceEqual(keys.Files)
            );
    }

    internal void RefreshScope()
    {
        RenewPeerSubject();
        IsReplacing = IsVisible && HasContent;
        History = null;
        _readFailure = null;
        if (!CanShow(_section))
            _section = InspectorSection.Peers;
        Invalidate();
        Refresh();
        QueueRead();
    }

    internal void RefreshDetail()
    {
        if (!IsVisible)
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
                var updated = false;
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
                    updated |= tracker.Update(nextTrackers[index]);
                    nextTrackers[index] = tracker;
                }
                if (Trackers is null || !Trackers.SequenceEqual(nextTrackers))
                {
                    Trackers = nextTrackers;
                    changed = true;
                }
                // A kept tracker keeps its row object, also in a new list, so the
                // table reads its new sort key only when told.
                if (updated)
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
        var data = await _client.Read("history", HistoryArguments("day")).WaitAsync(cancellation);
        var day = await Task.Run(() => Samples(data), cancellation);
        if (!IsCurrent(context))
            return [];
        if (History is null)
        {
            History = day;
            IsReplacing = false;
            Refresh();
        }
        data = await _client.Read("history", HistoryArguments("five_minutes")).WaitAsync(cancellation);
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
            var arguments = new Dictionary<string, object> { ["range"] = range };
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
        CancelReading();
        _client.Withdraw(Consumer.Inspector);
        if (!IsVisible || IsGlobal || _section == InspectorSection.Speed || _target is null)
            _ = Release();
    }

    private void CancelReading()
    {
        _readFrame?.Dispose();
        _readFrame = null;
        _refreshFrame?.Dispose();
        _refreshFrame = null;
        _context++;
        _ = Cancel(_reading);
        _reading = new();
        _fetching = false;
        _subscription = null;
        ForgetObserved();
        _firstRound = null;
    }

    // The waiting observations are in no batch, so nothing else reads them.
    private void ForgetObserved()
    {
        foreach (var observation in _observed.Values)
        {
            observation.Return();
        }
        _observed.Clear();
    }

    // CancelAsync marks the token immediately; callbacks must not run in a click.
    private static async Task Cancel(CancellationTokenSource reading)
    {
        using (reading)
            await reading.CancelAsync().ConfigureAwait(false);
    }

    // The outcome does not matter, and while the release is unsent a later
    // Withdraw can cancel it or a later subscription can replace it, so it is
    // awaited without throwing.
    private async Task Release() =>
        await ((Task)_client.Release()).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private void ApplyGeneral(JsonElement reply)
    {
        Folder = reply.GetProperty("folder").GetString()!;
        if (reply.GetProperty("magnet").GetString() is { } magnet)
            Magnet = magnet;
        Comment = reply.GetProperty("comment").GetString()!;
        Creator = reply.GetProperty("creator").GetString()!;
        Created = PipeClient.ReadTime(reply.GetProperty("created"));
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
            catch (CommandException error) when (error.Reason == EngineFailure.MetadataUnavailable)
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
                _editFailure = new CommandException("invalid_trackers", url);
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
        // Until files load, Files is an empty browser, which the next target keeps.
        if (_filesLoaded)
        {
            Files.Detach();
            Files = CreateFiles();
            _filesLoaded = false;
        }
        _confirmedFiles = null;
        _fileReply = null;
        ForgetView();
        Trackers = null;
        Pieces = null;
        _pieceFiles = null;
        History = null;
        Folder = Comment = Creator = Magnet = string.Empty;
        Created = null;
        PieceSize = PieceCount = 0;
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

    // The properties a poll changes while the subject, section and access
    // stay: the target's facts, and the scope's rates and selection count.
    private static readonly (PropertyChangedEventArgs Notice, Func<Inspector, object?> Read)[] Facts =
    [
        (new(nameof(Name)), inspector => inspector.Name),
        (new(nameof(SizeText)), inspector => inspector.SizeText),
        (new(nameof(EmptyText)), inspector => inspector.EmptyText),
        (new(nameof(HashV1)), inspector => inspector.HashV1),
        (new(nameof(HashV2)), inspector => inspector.HashV2),
        (new(nameof(HashV1Label)), inspector => inspector.HashV1Label),
        (new(nameof(HashV2Label)), inspector => inspector.HashV2Label),
        (new(nameof(IsIndeterminate)), inspector => inspector.IsIndeterminate),
        (new(nameof(ShowsStatus)), inspector => inspector.ShowsStatus),
        (new(nameof(ProgressText)), inspector => inspector.ProgressText),
        (new(nameof(Summary)), inspector => inspector.Summary),
        (new(nameof(LimitText)), inspector => inspector.LimitText),
        (new(nameof(LimitName)), inspector => inspector.LimitName),
        (new(nameof(DownloadText)), inspector => inspector.DownloadText),
        (new(nameof(UploadText)), inspector => inspector.UploadText),
        (new(nameof(GeneralState)), inspector => inspector.GeneralState),
    ];
    private static readonly FrozenSet<string> FactNames =
        Facts.Select(fact => fact.Notice.PropertyName!).ToFrozenSet();
    private static readonly PropertyChangedEventArgs Everything = new(string.Empty);

    // Each fact as the pane last read it, so a poll raises only the facts it changed.
    private readonly object?[] _facts = new object?[Facts.Length];

    /// <summary>
    /// The target's states that the General section shows through its visual
    /// state and progress style rather than a binding, as one fact, so a
    /// change of them alone reaches the pane.
    /// </summary>
    internal (bool IsProgressMuted, bool CanRetry, bool IsError)? GeneralState =>
        _target is { } target ? (target.IsProgressMuted, target.CanRetry, target.IsError) : null;

    /// <summary>
    /// Whether a notice names one of the facts a poll changes, which the pane
    /// shows through its bindings without rebuilding.
    /// </summary>
    internal static bool IsFact(string? name) => name is not null && FactNames.Contains(name);

    internal void Refresh() => Refresh(everything: true);

    /// <param name="everything">
    /// False when only <see cref="Facts"/> changed, so the pane does not
    /// rebuild and its bindings re-read only the facts whose value changed.
    /// </param>
    private void Refresh(bool everything)
    {
        if (_owner.IsClosed || !IsVisible)
            return;
        _access = (IsAvailable, CanEdit);
        _loading = IsLoading;
        var enabled = CanEdit && _confirmedFiles is not null;
        if (Files.IsEnabled != enabled)
            Files.IsEnabled = enabled;
        for (var index = 0; index < _commands.Length; index++)
        {
            var command = _commands[index];
            var executes = command.CanExecute(null);
            if (_enabled[index] != executes)
            {
                _enabled[index] = executes;
                ((RelayCommand)command).Refresh();
            }
        }
        _refreshesAll |= everything;
        if (_refreshFrame is not null)
            return;
        _refreshFrame = new FrameCallback(_dispatcher, () =>
        {
            _refreshFrame = null;
            var all = _refreshesAll;
            _refreshesAll = false;
            // Keep the displayed detail bound until its replacement is ready.
            if (IsReplacing)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReplacing)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEdit)));
            }
            else if (all)
            {
                PropertyChanged?.Invoke(this, Everything);
                for (var index = 0; index < Facts.Length; index++)
                {
                    _facts[index] = Facts[index].Read(this);
                }
            }
            else
            {
                for (var index = 0; index < Facts.Length; index++)
                {
                    var value = Facts[index].Read(this);
                    if (!Equals(_facts[index], value))
                    {
                        _facts[index] = value;
                        PropertyChanged?.Invoke(this, Facts[index].Notice);
                    }
                }
            }
        });
    }

    private void RefreshLoading()
    {
        if (_loading == IsLoading)
            return;
        Refresh();
    }
}
