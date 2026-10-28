using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Desktop;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Torrents;

public sealed class AddDraft : IDraft
{
    private readonly MainViewModel _owner;
    private readonly PipeClient _client;
    private readonly Strings _strings;
    private string _destination;
    private string _defaultDestination;
    private bool _defaultPaused;
    private bool _defaultQueueTop;
    private bool _paused;
    private bool _queueTop;
    private bool _sequential;
    private bool _firstLast;
    private Exception? _failure;

    // The engine refuses a folder, not one torrent, so a refused folder is the
    // draft's failure until the person changes the folder.
    private Exception? _destinationFailure;

    // The last Add ended with sources still in the draft.
    private bool _addStopped;
    private CancellationTokenSource? _polling;
    private Task _poller = Task.CompletedTask;
    private Task _pass = Task.CompletedTask;
    private Task _spaceReader = Task.CompletedTask;
    private long? _space;
    private readonly FileSelection _emptyFiles;
    private AdditionPhase _phase;
    private Addition? _addition;
    private Task _arrivals = Task.CompletedTask;

    // A submit holds the dialog open; preparing a source does not, because
    // AcceptMagnet releases a preview whose dialog has closed. Closing the
    // window waits for both, and for sources still arriving.
    public bool IsPending => _phase == AdditionPhase.Submitting;
    internal bool IsBusy => _phase != AdditionPhase.Idle || !_arrivals.IsCompleted;

    // Whether the Add dialog shows this draft. Sources that arrive then join
    // the dialog, and its previews refresh every second.
    internal bool IsOpen => _polling is not null;
    public ObservableCollection<AddSource> Sources { get; } = [];
    public bool HasSources => Sources.Count > 0;
    public FileSelection Files => Sources.Count == 1 ? Sources[0].Files : _emptyFiles;
    private string _magnet = string.Empty;
    private Exception? _magnetFailure;
    public string Magnet
    {
        get => _magnet;
        set
        {
            _magnet = value;
            _magnetFailure = null;
            Refresh();
        }
    }
    public bool HasMagnetError => _magnetFailure is not null;
    public string MagnetMessage =>
        _magnetFailure is null ? string.Empty : _strings.Error(_magnetFailure);
    public bool EditingMagnet { get; internal set; }
    public string Source =>
        string.Join(Environment.NewLine, Sources.Select(source => source.Source));
    public string Destination
    {
        get => _destination;
        set
        {
            if (_destination == value)
                return;
            _destination = value;
            _space = null;
            RefreshSpace();
            _destinationFailure = null;
            Refresh();
        }
    }
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            Refresh();
        }
    }
    public bool QueueTop
    {
        get => _queueTop;
        set
        {
            _queueTop = value;
            Refresh();
        }
    }
    public bool Sequential
    {
        get => _sequential;
        set
        {
            _sequential = value;
            Refresh();
        }
    }
    public bool FirstLast
    {
        get => _firstLast;
        set
        {
            _firstLast = value;
            Refresh();
        }
    }
    public DialogPreference Confirmation { get; }
    public bool HasFiles => Sources.Count == 1 && Sources[0].Content is SourceContent.Files;
    public bool GettingMetadata =>
        Sources.Any(source => source.Content is SourceContent.Pending && source.Problem is null);
    public string Heading =>
        Sources.Count switch
        {
            0 => _strings.Get("add", "title"),
            1 => Sources[0].Name,
            _ => _strings.Format("add", "sources", Sources.Count),
        };
    public string Detail =>
        Sources.Count != 1 ? string.Empty
        : Sources[0].MetadataReady ? _strings.Bytes(Sources[0].Size)
        : _strings.Get("add", "metadata");
    public string SubmitText => _strings.Get("add", IsPending ? "pending" : "submit");
    public string SubmitToolTip =>
        _strings.Format(
            "add",
            (SubmitCount > 1, _paused) switch
            {
                (true, true) => "submit_tip_all_paused",
                (true, false) => "submit_tip_all",
                (false, true) => "submit_tip_paused",
                (false, false) => "submit_tip",
            },
            SubmitCount
        );

    // Magnet text not yet accepted becomes one more source when Add runs.
    private int SubmitCount => Sources.Count + (Magnet.Trim().Length > 0 ? 1 : 0);
    public string Space =>
        _space is not { } free ? string.Empty
        : Needed > free
            ? _strings.Format("add", "short", _strings.Bytes(Needed), _strings.Bytes(free))
        : _strings.Format("add", "free", _strings.Bytes(free));
    public bool HasSpace => _space is not null;
    public bool LacksSpace => _space is { } free && Needed > free;
    private long Needed =>
        Sources
            .Where(source => source.Content is SourceContent.Files)
            .Sum(source => source.Files.WantedBytes);
    public string[] Folders =>
        _owner
            .Torrents.OrderByDescending(torrent => torrent.Added)
            .Select(torrent =>
                torrent.FinalFolder.Length > 0 ? torrent.FinalFolder : torrent.SavePath
            )
            .Prepend(_defaultDestination)
            .Select(folder => Path.TrimEndingDirectorySeparator(folder.Trim()))
            .Where(folder => folder.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    public string Shared =>
        string.Join(
            Environment.NewLine,
            Sources.Select(source => source.Shared).Where(text => text.Length > 0)
        );
    public bool IsShared => Shared.Length > 0;
    internal Exception? Failure => _failure ?? _destinationFailure ?? StoppedFailure;
    private Exception? SourceFailure =>
        Sources.Select(source => source.Problem?.Error).FirstOrDefault(error => error is not null);

    // The rows show each source's problem. Once an Add stops on them, the bar
    // also names them as its reason, until they resolve or the next Submit,
    // new sources or a discard.
    private Exception? StoppedFailure => _addStopped ? SourceFailure : null;

    // The engine checks the folder only for content it is about to add, so a
    // refused folder fails every source that is not already in the list.
    private Exception? FailureOf(AddSource source) =>
        source.Problem?.Error
        ?? (source.Content is SourceContent.Present ? null : _destinationFailure);

    internal static bool IsDestinationError(Exception error) =>
        error is CommandException { Code: "invalid_destination" };

    public bool HasDestinationError => _destinationFailure is not null;
    public string DestinationMessage =>
        _destinationFailure is { } failure ? _strings.Error(failure) : string.Empty;
    public string Message =>
        !_owner.IsConnected && _owner.CurrentStatus is { } status
            ? status.Body.Length == 0
                ? status.Title
                : _strings.Format("errors", "detail", status.Title, status.Body)
            : (_failure ?? StoppedFailure) is { } failure
                ? _strings.Error(failure)
                : HasFiles && !Files.HasWanted
                    ? _strings.Get("add", "no_files")
                    : string.Empty;
    public bool HasError => Message.Length > 0;
    public InfoBarSeverity Severity => InfoBarSeverity.Error;
    private bool CanSave => _owner.CanSave && _phase == AdditionPhase.Idle;
    public bool CanEdit => CanSave && !_owner.IsClosing;
    public bool CanSubmit => CanEdit && IsValid;
    private bool IsValid =>
        !HasMagnetError
        && HasDraft
        && !string.IsNullOrWhiteSpace(_destination)
        && Sources.All(source => source.PreviewId is not null || source.Problem is not null)
        && Sources.All(source =>
            source.PreviewId is null
            || !source.MetadataReady
            || source.SharedDestination == _destination
        )
        && !Sources.Any(source => source.NeedsFiles);
    public bool HasDraft => Sources.Count > 0 || !string.IsNullOrWhiteSpace(Magnet);
    public ClosePolicy ClosePolicy =>
        new ClosePolicy.Ask("add", Lucide.CirclePlus, () => Heading, () => SubmitText);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal AddDraft(MainViewModel owner, PipeClient client, Strings strings)
    {
        _owner = owner;
        _client = client;
        _strings = strings;
        Confirmation = new(owner.Settings);
        _destination = _defaultDestination = Windows.Storage.UserDataPaths.GetDefault().Downloads;
        _emptyFiles = new FileSelection(strings);
    }

    // Sources dropped, pasted, picked or sent by another instance. Picked
    // sources open one Add dialog however many files the person picks, unless
    // they turned the dialog off. Later arrivals stay outside the draft until
    // the preceding one has finished its cleanup.
    internal async Task<ArrivalOutcome> Receive(string[] sources, bool picked)
    {
        if (sources.All(string.IsNullOrWhiteSpace))
            return ArrivalOutcome.Settled;
        var previous = _arrivals;
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _arrivals = completed.Task;
        Refresh();
        try
        {
            await previous;
            Own(sources);
            if (picked || IsOpen || Sources.Count <= 1)
                return await AddOwned();
            await AddTogether();
            return ArrivalOutcome.Settled;
        }
        catch (Exception error)
        {
            _owner.Notifications.Report(error);
            return ArrivalOutcome.Settled;
        }
        finally
        {
            completed.SetResult();
            Refresh();
        }
    }

    // Several sources arriving at once are added without the Add dialog or any
    // question, so one drop never opens a cascade of dialogs. A torrent already
    // in the list is shown where it is, and a source that fails is reported and
    // dropped rather than left for a later Add dialog. Sources that could not be
    // tried stay in the draft, so none is lost silently.
    private async Task AddTogether()
    {
        await using var addition = BeginAddition();
        await PrepareAll();
        if (!CanEdit || IsOpen || IsAdditionCancelled)
            return;
        Exception? failure = null;
        var rejected = Sources.Where(source => source.NeedsFiles).ToArray();
        if (rejected.Length > 0)
        {
            failure = new CommandException("invalid_priorities", null, _strings, "add");
            await Discard(rejected);
        }
        if (CanEdit && !IsOpen && HasDraft && !await Submit() && Failure is { } error)
        {
            failure = error;
            await Discard(
                Sources
                    .Where(source =>
                        FailureOf(source) is not null
                        && source.Problem is not SourceProblem.Unconfirmed
                    )
                    .ToArray()
            );
        }
        if (failure is not null)
            _owner.Notifications.Report(failure);
    }

    private async Task<ArrivalOutcome> AddOwned()
    {
        if (Sources.Count == 0)
            return ArrivalOutcome.Settled;
        if (IsOpen)
        {
            await PrepareAll();
            return ArrivalOutcome.Settled;
        }
        // Without the engine nothing is added below and the sources wait for
        // the Add dialog, so an unconfirmed preference changes nothing.
        var showDialog = Sources.SelectMany(source => source.Inputs)
            .Any(input => _owner.Settings.AddPreference(input).ConfirmedOn == true);
        await using (var addition = BeginAddition())
        {
            await PrepareAll();
            if (IsOpen || IsAdditionCancelled)
                return ArrivalOutcome.Settled;
            // A torrent already in the list needs no Add dialog: Submit shows it where
            // it is, and only trackers it lacks are worth a question.
            if (Sources.All(source => source.Content is SourceContent.Present))
            {
                if (Sources.Any(source => source.MergeAvailable))
                    return ArrivalOutcome.Merge;
                if (await Submit())
                    _owner.Notifications.Announce(_strings.Get("errors", "duplicate"));
                return ArrivalOutcome.Settled;
            }
            if (!showDialog)
            {
                if (!Sources.Any(source => source.MergeAvailable))
                    await Submit();
                if (IsAdditionCancelled)
                    return ArrivalOutcome.Settled;
            }
        }
        return Sources.Count > 0 ? ArrivalOutcome.Dialog : ArrivalOutcome.Settled;
    }

    private void Own(IEnumerable<string> sources)
    {
        foreach (var source in sources.Where(source => !string.IsNullOrWhiteSpace(source)))
            if (!Sources.Any(existing => existing.Inputs.Contains(source, StringComparer.Ordinal)))
                Own(new AddSource(source, _strings, this));
        Refresh();
    }

    private void Own(AddSource source)
    {
        source.Files.Changed += (_, _) => Refresh();
        Sources.Add(source);
    }

    public async Task PrepareMagnet()
    {
        if (!CanEdit || string.IsNullOrWhiteSpace(Magnet))
            return;
        if (!await AcceptMagnet())
            return;
        await PrepareAll();
    }

    private async Task<bool> AcceptMagnet()
    {
        var input = Magnet;
        var value = input.Trim();
        if (value.Length == 0)
            return true;
        _phase = AdditionPhase.Preparing;
        _magnetFailure = null;
        Refresh();
        try
        {
            if (
                !Uri.TryCreate(value, UriKind.Absolute, out var address)
                || address.Scheme != "magnet"
            )
                throw new CommandException("invalid_source", null, _strings, "preview");
            await _pass;
            var source = new AddSource(value, _strings, this);
            var destination = _destination;
            var preview = await PreviewSource(source, destination);
            var files = await ReadFiles(source, preview);
            if (Magnet != input || !IsOpen && !_owner.IsClosing)
            {
                await Release(preview.GetProperty("preview_id").GetString()!);
                return false;
            }
            await ApplyPreview(source, preview, destination, files);
            Magnet = string.Empty;
            return true;
        }
        catch (Exception error)
        {
            if (Magnet == input)
            {
                _magnetFailure = error;
                _owner.Notifications.Announce(MagnetMessage);
            }
            return false;
        }
        finally
        {
            _phase = AdditionPhase.Idle;
            Refresh();
        }
    }

    private async Task PrepareAll()
    {
        if (!CanEdit)
            return;
        _phase = AdditionPhase.Preparing;
        Refresh();
        try
        {
            await _pass;
            _addition?.Report();
            foreach (var source in Sources.Where(source => source.PreviewId is null).ToArray())
            {
                if (IsAdditionCancelled)
                    break;
                await Acquire(source);
            }
            _failure = null;
            _addStopped = false;
            if (IsOpen && SourceFailure is { } failure)
                _owner.Notifications.Report(failure);
        }
        finally
        {
            _phase = AdditionPhase.Idle;
            Refresh();
        }
    }

    private bool IsAdditionCancelled => _addition?.IsCancelled == true;

    private Addition? BeginAddition() =>
        IsOpen || Sources.Count == 0 ? null : _addition = new Addition(this);

    private sealed class Addition : IAsyncDisposable
    {
        private readonly AddDraft _draft;
        private readonly AddSource[] _accepted;
        private readonly CancellableWork _work;
        private BackgroundWork.Operation Operation => _work.Operation;

        // The token stays cancelled after the work ends, so the cleanup still
        // sees the person's Cancel.
        internal bool IsCancelled => _work.Token.IsCancellationRequested;

        internal Addition(AddDraft draft)
        {
            _draft = draft;
            _accepted = draft.Sources.ToArray();
            _work = new(
                draft._owner.Work,
                new(
                    new WorkDescription.Sources(),
                    new WorkProgress.Unknown(),
                    new WorkExecution.Waiting()
                ),
                Cancel
            );
            // Added torrents appear in the list.
            Operation.ReportsSuccess = false;
        }

        internal void Report(AddSource? submitting = null)
        {
            var remaining = _accepted.Where(_draft.Sources.Contains).ToArray();
            _work.Report(
                _work.Observation with
                {
                    Progress =
                        _draft._phase == AdditionPhase.Preparing
                            ? new WorkProgress.Unknown()
                            : new WorkProgress.Fraction(
                                _accepted.Length - remaining.Length,
                                _accepted.Length,
                                WorkUnit.Files
                            ),
                    Execution =
                        submitting is not null && remaining.All(source => source == submitting)
                            ? new WorkExecution.Finishing()
                            : new WorkExecution.Running(),
                }
            );
        }

        private Task Cancel(CancellableWork work)
        {
            if (work.TryCancel(out var signalling))
                System.Diagnostics.Debug.WriteLine(
                    $"Torrent addition cancelled ({_accepted.Length} sources)."
                );
            return signalling;
        }

        // The work ends only after the cleanup, so a cancelled addition shows
        // Cancelling until its sources are discarded. No report lands
        // meanwhile: a cancelled work refuses them, and an addition that was
        // not cancelled reaches the end without awaiting.
        public async ValueTask DisposeAsync()
        {
            var remaining = _accepted.Where(_draft.Sources.Contains).ToArray();
            // A source left without a failure waits for the Add dialog or the
            // merge question, so it has not failed.
            var failure = remaining
                .Select(_draft.FailureOf)
                .FirstOrDefault(error => error is not null);
            Operation.Outcome =
                IsCancelled ? WorkOutcome.Cancelled
                : failure is not null ? WorkOutcome.Failed
                : WorkOutcome.Completed;
            Operation.Error = failure;
            Operation.Summarize(
                "sources_result",
                _accepted.Length - remaining.Length,
                _accepted.Length
            );
            try
            {
                // An in-flight add is reconciled before this cleanup. An uncertain
                // reply stays in the draft so cancellation cannot hide an added torrent.
                if (IsCancelled)
                {
                    await _draft.Discard(
                        _accepted
                            .Where(source =>
                                _draft.Sources.Contains(source)
                                && source.Problem is not SourceProblem.Unconfirmed
                            )
                            .ToArray()
                    );
                    var unconfirmed = _accepted
                        .Where(_draft.Sources.Contains)
                        .Select(source => source.Problem)
                        .OfType<SourceProblem.Unconfirmed>()
                        .FirstOrDefault();
                    if (unconfirmed is not null)
                        _draft._owner.Notifications.Report(unconfirmed.Error);
                }
            }
            catch (Exception error)
            {
                Operation.Outcome = WorkOutcome.Failed;
                Operation.Error = error;
                _draft._owner.Notifications.Report(error);
            }
            finally
            {
                _draft._addition = null;
                await _work.DisposeAsync();
            }
        }
    }

    private async Task Acquire(AddSource source)
    {
        if (!Sources.Contains(source))
            return;
        try
        {
            var destination = _destination;
            var preview = await PreviewSource(source, destination);
            var files = await ReadFiles(source, preview);
            if (!Sources.Contains(source))
            {
                await Release(preview.GetProperty("preview_id").GetString()!);
                return;
            }
            await ApplyPreview(source, preview, destination, files);
        }
        catch (Exception error)
        {
            source.FailPreview(error);
        }
        source.Refresh();
        Refresh();
    }

    private async Task<JsonElement> PreviewSource(AddSource source, string destination)
    {
        var preview = await _client.Send(
            "preview",
            new { source = source.Source, destination },
            Requester.Window
        );
        foreach (var input in source.Inputs.Skip(1))
            preview = await _client.Send(
                "preview",
                new { source = input, destination },
                Requester.Window
            );
        return preview;
    }

    // The files of a preview that the source does not show yet, read before the
    // source applies the preview so it never shows an empty list.
    private async Task<FileSelection.Loader?> ReadFiles(AddSource source, JsonElement preview)
    {
        if (
            source.Content is SourceContent.Files
            || SourceContent.Read(preview) is not SourceContent.Files
        )
            return null;
        var load = source.Files.Load();
        await foreach (
            var page in _client.PreviewFiles(preview.GetProperty("preview_id").GetString()!)
        )
            load.Add(page);
        return load;
    }

    private async Task ApplyPreview(
        AddSource source,
        JsonElement preview,
        string destination,
        FileSelection.Loader? files
    )
    {
        source.Apply(preview, destination, files);
        var existing = Sources.FirstOrDefault(existing =>
            existing != source && existing.PreviewId == source.PreviewId
        );
        if (existing is not null)
        {
            existing.Inputs.AddRange(
                source.Inputs.Where(input =>
                    !existing.Inputs.Contains(input, StringComparer.Ordinal)
                )
            );
            existing.Apply(preview, destination, await ReadFiles(existing, preview));
            Sources.Remove(source);
        }
        else if (!Sources.Contains(source))
            Own(source);
    }

    public async Task<bool> Submit()
    {
        if (!CanSave || !IsValid || IsAdditionCancelled)
            return false;
        var sharing = Sources.ToDictionary(
            source => source,
            source =>
                source.Content is not SourceContent.Present
                && source.SharedDestination == _destination
                    ? source.SharedWith
                    : []
        );
        if (!await AcceptMagnet() || !CanSave || !IsValid)
            return false;
        _phase = AdditionPhase.Submitting;
        _addStopped = false;
        Refresh();
        var destination = _destination;
        var paused = _paused;
        var queueTop = _queueTop;
        var sequential = _sequential;
        var firstLast = _firstLast;
        try
        {
            await _pass;
            if (!IsValid)
                return false;
            var captured = Sources.ToArray();
            // Prepending in reverse preserves the order shown in the dialog.
            if (queueTop)
                Array.Reverse(captured);
            var revealed = new List<string>();
            // Reconcile and prepare the whole batch before adding any source.
            // Newly acquired shared-file warnings need another confirmation.
            foreach (var source in captured)
            {
                if (IsAdditionCancelled)
                    return false;
                if (
                    source.Problem is SourceProblem.Unconfirmed
                    && _owner.Find(source.Hashes) is { } existing
                )
                {
                    Sources.Remove(source);
                    revealed.Add(existing.TorrentId);
                    if (source.PreviewId is { } previewId)
                        await Release(previewId);
                    continue;
                }
                if (source.PreviewId is null)
                    await Acquire(source);
            }
            if (
                Sources.Count > 0 && !IsValid
                || Sources.Any(source =>
                    source.PreviewId is not null
                    && source.Content is not SourceContent.Present
                    && source.SharedWith.Length > 0
                    && (
                        !sharing.TryGetValue(source, out var previous)
                        || source.SharedWith.Any(name =>
                            !previous.Contains(name, StringComparer.Ordinal)
                        )
                    )
                )
            )
            {
                _owner.Reveal([.. revealed]);
                return false;
            }
            await Confirmation.Save(
                captured
                    .SelectMany(source => source.Inputs)
                    .Select(_owner.Settings.AddPreference)
                    .ToArray()
            );
            foreach (var source in captured)
            {
                if (IsAdditionCancelled)
                    break;
                if (!Sources.Contains(source) || source.PreviewId is null)
                    continue;
                _addition?.Report(source);
                if (source.Content is SourceContent.Present present)
                {
                    if (source.Merge)
                        await _client.Send(
                            "merge_trackers",
                            new { preview_id = source.PreviewId, torrent_id = present.TorrentId },
                            Requester.Person
                        );
                    Sources.Remove(source);
                    await Release(source.PreviewId);
                    revealed.Add(present.TorrentId);
                    continue;
                }
                var previewId = source.PreviewId;
                source.PreviewId = null;
                JsonElement addition;
                try
                {
                    var priorities =
                        source.Content is SourceContent.Files ? source.Files.Priorities() : [];
                    addition = await _client.Send(
                        "add",
                        new
                        {
                            preview_id = previewId,
                            destination,
                            paused,
                            queue_top = queueTop,
                            sequential,
                            first_last = firstLast,
                            priorities,
                        },
                        Requester.Person
                    );
                }
                catch (Exception error) when (IsDestinationError(error))
                {
                    // The engine reports a duplicate before it checks the folder,
                    // so an earlier unconfirmed add did not happen.
                    source.Settle();
                    _destinationFailure = error;
                    source.Refresh();
                    continue;
                }
                catch (Exception error)
                {
                    source.FailAdd(error);
                    source.Refresh();
                    continue;
                }
                if (addition.GetProperty("duplicate").GetBoolean())
                {
                    source.Settle();
                    source.PreviewId = previewId;
                    try
                    {
                        var detail = await _client.Send(
                            "preview_detail",
                            new { preview_id = previewId, destination },
                            Requester.Window
                        );
                        source.Apply(detail, destination, await ReadFiles(source, detail));
                    }
                    catch (CommandException error) when (error.Code == "preview_expired")
                    {
                        source.PreviewId = null;
                        await Acquire(source);
                    }
                    continue;
                }
                Sources.Remove(source);
                revealed.Add(addition.GetProperty("torrent_id").GetString()!);
            }
            _owner.Reveal([.. revealed]);
            if (Sources.Count > 0)
            {
                _failure = null;
                _addStopped = true;
                var message = Message.Length > 0 ? Message : DestinationMessage;
                if (message.Length > 0)
                    _owner.Notifications.Announce(message);
                return false;
            }
            Clear();
            _owner.Notifications.AnnounceAccepted("add", "title");
            return true;
        }
        catch (Exception error)
        {
            _failure = error;
            _owner.Notifications.Announce(_strings.Error(error));
            return false;
        }
        finally
        {
            _phase = AdditionPhase.Idle;
            Refresh();
        }
    }

    // The Add dialog opened.
    internal void Open()
    {
        if (IsOpen)
            return;
        _polling = new CancellationTokenSource();
        RefreshSpace();
        _poller = Poll(_poller, _polling);
        Refresh();
    }

    // The Add dialog closed.
    internal void Close()
    {
        if (_polling is not { } polling)
            return;
        polling.Cancel();
        _polling = null;
        _space = null;
        _client.Withdraw(Consumer.Draft);
        Refresh();
    }

    private void RefreshSpace()
    {
        if (_polling is not null && _spaceReader.IsCompleted)
            _spaceReader = ReadSpace();
    }

    private async Task ReadSpace()
    {
        while (_polling is { } polling)
        {
            var destination = _destination;
            var free = await Task.Run(() => DiskSpace.Read(destination));
            if (_polling != polling || _destination != destination)
                continue;
            _space = free;
            PropertyChanged?.Invoke(this, new(nameof(Space)));
            PropertyChanged?.Invoke(this, new(nameof(HasSpace)));
            PropertyChanged?.Invoke(this, new(nameof(LacksSpace)));
            return;
        }
    }

    // Starts after the previous poller ends, so two never overlap. Each pass
    // stops once other draft work begins, and that work awaits the pass.
    private async Task Poll(Task previous, CancellationTokenSource stop)
    {
        using var _ = stop;
        await previous;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        // Stopping disposes the timer, which ends its wait without throwing.
        using var stopping = stop.Token.Register(timer.Dispose);
        try
        {
            while (await timer.WaitForNextTickAsync())
            {
                if (!CanEdit)
                    continue;
                _pass = Pass(stop.Token);
                await _pass;
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Torrent addition preview polling cancelled.");
        }
        catch (Exception error)
        {
            _owner.Notifications.Report(error);
            Refresh();
        }
    }

    private async Task Pass(CancellationToken token)
    {
        foreach (var source in Sources.ToArray())
        {
            if (token.IsCancellationRequested || !CanEdit)
                return;
            if (
                source.PreviewId is not null
                && source.MetadataReady
                && source.SharedDestination == _destination
            )
                continue;
            var hadFailure = source.Problem is not null;
            try
            {
                if (source.PreviewId is null)
                {
                    if (source.Problem is not SourceProblem.Unconfirmed)
                        await Acquire(source);
                }
                else
                {
                    var destination = _destination;
                    var preview = await _client.Read(
                        Consumer.Draft,
                        "preview_detail",
                        new { preview_id = source.PreviewId, destination }
                    );
                    if (token.IsCancellationRequested)
                        return;
                    if (Sources.Contains(source))
                        source.Apply(preview, destination, await ReadFiles(source, preview));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Torrent addition preview read cancelled ({source.PreviewId})."
                );
                return;
            }
            catch (Exception error)
            {
                source.FailPreview(error);
                source.Refresh();
            }
            if (token.IsCancellationRequested)
                return;
            if (!hadFailure && Sources.Contains(source) && source.Problem is { } problem)
                _owner.Notifications.Report(problem.Error);
        }
        RefreshSpace();
        Refresh();
    }

    public Task Discard() => Discard(Sources.ToArray());

    // Previews keep loading until Discard releases them, because the draft
    // can still be added after its dialog closes.
    public void Stop() { }

    private async Task Discard(IEnumerable<AddSource> sources)
    {
        var cancelled = sources.ToArray();
        foreach (var source in cancelled)
            Sources.Remove(source);
        if (Sources.Count == 0)
            Clear();
        else
        {
            _failure = null;
            _addStopped = false;
            Refresh();
        }
        foreach (var source in cancelled)
            if (source.PreviewId is { } previewId)
                await Release(previewId);
    }

    // The engine reuses a preview for content the connection already
    // previews, so a preview stays open while any source still uses it. An
    // acquisition that finishes after its source left releases its preview here.
    private async Task Release(string previewId)
    {
        if (!_owner.IsConnected || Sources.Any(source => source.PreviewId == previewId))
            return;
        await _client.Send("cancel_preview", new { preview_id = previewId }, Requester.Window);
    }

    internal void ApplyDefaults(string destination, bool startsDownload, bool queueTop)
    {
        if (
            _defaultDestination == destination
            && _defaultPaused == !startsDownload
            && _defaultQueueTop == queueTop
        )
            return;
        var usesDestination = _destination == _defaultDestination;
        var usesPaused = _paused == _defaultPaused;
        var usesQueue = _queueTop == _defaultQueueTop;
        _defaultDestination = destination;
        _defaultPaused = !startsDownload;
        _defaultQueueTop = queueTop;
        if (HasDraft)
            return;
        if (usesDestination)
        {
            _destination = _defaultDestination;
            _space = null;
            RefreshSpace();
        }
        if (usesPaused)
            _paused = _defaultPaused;
        if (usesQueue)
            _queueTop = _defaultQueueTop;
        Refresh();
    }

    internal void Invalidate()
    {
        foreach (var source in Sources)
            source.PreviewId = null;
        Refresh();
    }

    private void Clear()
    {
        Sources.Clear();
        Files.Clear();
        Magnet = string.Empty;
        EditingMagnet = false;
        _paused = _defaultPaused;
        _queueTop = _defaultQueueTop;
        _sequential = _firstLast = false;
        Confirmation.NeverShow = false;
        _failure = null;
        _addStopped = false;
        _destinationFailure = null;
        _destination = _defaultDestination;
        _space = null;
        RefreshSpace();
        Refresh();
    }

    internal void Refresh()
    {
        foreach (var source in Sources)
        {
            source.Files.IsEnabled = CanEdit;
            source.Refresh();
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public sealed class AddSource : INotifyPropertyChanged
{
    private readonly Strings _strings;
    private readonly AddDraft _draft;
    internal List<string> Inputs { get; }
    public string Source => Inputs[0];
    public FileSelection Files { get; }
    public string Name { get; private set; }
    public ICommand SelectAll { get; }
    public long Size { get; private set; }
    internal SourceContent Content { get; private set; } = new SourceContent.Pending();
    public bool MetadataReady =>
        Content is SourceContent.Files or SourceContent.Present { MetadataReady: true };
    public bool NeedsFiles => Content is SourceContent.Files && !Files.HasWanted;
    public bool MergeAvailable => Content is SourceContent.Present { MergeAvailable: true };
    public bool Merge { get; set; }
    public string[] Hashes { get; private set; } = [];
    public string[] SharedWith { get; private set; } = [];
    internal string SharedDestination { get; private set; } = string.Empty;
    internal string? PreviewId { get; set; }
    internal SourceProblem? Problem { get; private set; }
    public string Description =>
        Problem is { } problem ? _strings.Error(problem.Error)
        : Content is SourceContent.Present ? _strings.Get("add", "already_added")
        : Content is SourceContent.Pending ? _strings.Get("add", "metadata")
        : !Files.HasWanted ? _strings.Get("add", "no_files")
        : _strings.Bytes(Size);

    // An already added source adds no files, so it shares none.
    public string Shared =>
        Content is SourceContent.Present
        || SharedWith.Length == 0
        || SharedDestination != _draft.Destination
            ? string.Empty
            : _strings.Format("add", "shared", string.Join(", ", SharedWith));
    public string MergeLabel => _strings.Get("add", "merge_trackers");
    public string AllLabel => _strings.Get("add", "all_files");
    public string AllToolTip => _strings.Get("add", "all_files_tip");
    public event PropertyChangedEventHandler? PropertyChanged;

    internal AddSource(string source, Strings strings, AddDraft draft)
    {
        Inputs = [source];
        Name = source;
        _strings = strings;
        _draft = draft;
        Files = new FileSelection(strings);
        SelectAll = new RelayCommand(
            () =>
            {
                Files.SelectAll();
                return Task.CompletedTask;
            },
            () => draft.CanEdit && NeedsFiles
        );
    }

    // A failed preview leaves an unconfirmed add in place, because only an
    // answer to an add or the torrent list can settle it.
    internal void FailPreview(Exception error)
    {
        if (Problem is not SourceProblem.Unconfirmed)
            Problem = new SourceProblem.Preview(error);
    }

    // A refusal is the engine's answer; any other failure can be a lost reply
    // to an add that succeeded.
    internal void FailAdd(Exception error) =>
        Problem =
            error is CommandException
                ? new SourceProblem.Refused(error)
                : new SourceProblem.Unconfirmed(error);

    // An answer to a later add, or a listed torrent that holds the content,
    // ends any earlier add failure.
    internal void Settle()
    {
        if (Problem is SourceProblem.Refused or SourceProblem.Unconfirmed)
            Problem = null;
    }

    // Files holds the rows read from the preview's file pages when the source
    // shows no files yet and the preview now has them.
    internal void Apply(JsonElement preview, string destination, FileSelection.Loader? files)
    {
        var hadFiles = Content is SourceContent.Files;
        PreviewId = preview.GetProperty("preview_id").GetString();
        Name = preview.GetProperty("name").GetString()!;
        Size = preview.GetProperty("size").GetInt64();
        Content = SourceContent.Read(preview);
        if (Content is SourceContent.Present)
            Settle();
        if (Problem is SourceProblem.Preview)
            Problem = null;
        var error = preview.GetProperty("error").GetString();
        if (!string.IsNullOrEmpty(error) && Problem is null)
            Problem = new SourceProblem.Preview(
                new CommandException("preview_failed", error, _strings, "preview_detail")
            );
        Hashes = preview
            .GetProperty("hashes")
            .EnumerateArray()
            .Select(hash => hash.GetString()!)
            .ToArray();
        SharedWith = preview
            .GetProperty("shared_with")
            .EnumerateArray()
            .Select(name => name.GetString()!)
            .ToArray();
        SharedDestination = destination;
        if (!hadFiles && Content is SourceContent.Files)
            files?.Commit();
        Refresh();
    }

    internal void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        ((RelayCommand)SelectAll).Refresh();
    }

    public override string ToString() => Name;
}

// What the engine's latest preview says an Add source holds.
internal abstract record SourceContent
{
    private SourceContent() { }

    // The metadata has not arrived, so the files are unknown.
    internal sealed record Pending : SourceContent;

    // The files are known, so the person chooses them and Add adds them.
    internal sealed record Files : SourceContent;

    // A listed torrent already holds the content, so Add shows that torrent
    // and adds nothing.
    internal sealed record Present(string TorrentId, bool MergeAvailable, bool MetadataReady)
        : SourceContent;

    internal static SourceContent Read(JsonElement preview)
    {
        var ready = preview.GetProperty("metadata_ready").GetBoolean();
        // The engine sends an empty torrent_id when no listed torrent holds the content.
        if (preview.GetProperty("torrent_id").GetString() is { Length: > 0 } torrentId)
            return new Present(torrentId, preview.GetProperty("merge_available").GetBoolean(), ready);
        return ready ? new Files() : new Pending();
    }
}

// Why the last request for an Add source failed. A refused folder is the
// draft's failure instead.
internal abstract record SourceProblem
{
    private SourceProblem(Exception error) => Error = error;

    internal Exception Error { get; }

    // The next successful preview clears it.
    internal sealed record Preview : SourceProblem
    {
        internal Preview(Exception error)
            : base(error) { }
    }

    // The engine refused the add; it stays until another add is answered or a
    // listed torrent holds the content.
    internal sealed record Refused : SourceProblem
    {
        internal Refused(Exception error)
            : base(error) { }
    }

    // The reply to an add was lost, so the torrent may exist. Polling does not
    // preview the source again, and the cleanup after a cancelled or failed
    // addition keeps it, so a torrent that was added is never hidden.
    internal sealed record Unconfirmed : SourceProblem
    {
        internal Unconfirmed(Exception error)
            : base(error) { }
    }
}
