using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Library;

internal sealed partial class VideoLookup(
    Store store,
    Task settled,
    IReadOnlyList<ProviderOption> options,
    RequestPolicy policy,
    ProviderHttp http
) : IAsyncDisposable
{
    private readonly SemaphoreSlim _access = new(1);
    private readonly SemaphoreSlim _changes = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _requests = [];
    private ProviderException? _failure;
    private RetryCommand? _recheck;
    private Dictionary<string, string> _keys = [];
    internal required BackgroundWork Work { get; init; }
    private BatchRun? _batch;
    private Sweep? _sweep;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task _work = Task.CompletedTask;
    private bool _working;
    private CancellationTokenSource _context = new();
    private int _interactive;
    private string _language = "en";
    private bool _connected;
    private bool _closed;
    private bool _again;
    private IReadOnlySet<long> _displayed = new HashSet<long>();
    private volatile bool _changingContext;
    internal bool IsAutomaticDownloadEnabled { get; private set; }
    internal IReadOnlyList<ProviderOption> Options => options;
    internal ProviderOption? Option { get; private set; }
    internal IProvider? Provider { get; private set; }
    internal ProviderException? Failure
    {
        get
        {
            lock (_gate)
                return _failure;
        }
    }

    /// <summary>
    /// Raised when the provider, its failure or automatic download setting
    /// changes, and when the batch grows, stops or ends. A file the batch finishes
    /// raises <see cref="Stored"/>.
    /// </summary>
    internal event EventHandler? Changed;

    /// <summary>
    /// Raised from a worker thread when a lookup saves a file's match or miss,
    /// and when the batch finishes a file. Saved data can answer many files a second.
    /// </summary>
    internal event EventHandler? Stored;

    /// <summary>Raised when a failure ends the batch, such as a provider that cannot serve any file.</summary>
    internal event EventHandler<Exception>? BatchFailed;

    /// <summary>
    /// Raised from a worker thread when the batch ends, with the number of
    /// files it left unidentified because the search limit refused their search.
    /// </summary>
    internal event EventHandler<int>? BatchLimited;
    internal event EventHandler<Exception>? Failed;

    internal Task Initialize(string language) =>
        Change(async cancellation =>
        {
            var selected = await store.Provider(cancellation).ConfigureAwait(false);
            var automatic = await store.AutomaticDownload(cancellation).ConfigureAwait(false);
            await policy.Initialize(cancellation).ConfigureAwait(false);
            var keys = await store.Keys(cancellation).ConfigureAwait(false);
            var option = selected is null
                ? options.FirstOrDefault()
                : options.FirstOrDefault(option => option.ProviderId == selected);
            // An absent saved provider cannot supply automatic downloads.
            if (automatic && option is null)
                await store.DisableAutomaticDownload(cancellation).ConfigureAwait(false);
            lock (_gate)
            {
                _keys = keys;
                Option = option;
                Provider = option is null ? null : option.Create(Key(option));
                IsAutomaticDownloadEnabled = automatic && option is not null;
                _language = language;
            }
        });

    /// <summary>
    /// Saves <paramref name="providerId"/> for automatic video information downloads.
    /// An unavailable provider changes nothing and throws.
    /// </summary>
    internal Task EnableAutomaticDownload(string providerId) =>
        Change(
            cancellation =>
                Select(
                    options.FirstOrDefault(option => option.ProviderId == providerId)
                        ?? throw new ArgumentOutOfRangeException(nameof(providerId)),
                    cancellation,
                    enableAutomaticDownload: true
                ),
            automaticOnly: true
        );

    internal Task SelectProvider(string providerId) =>
        Change(
            cancellation =>
                Select(
                    options.FirstOrDefault(option => option.ProviderId == providerId)
                        ?? throw new ArgumentOutOfRangeException(nameof(providerId)),
                    cancellation
                ),
            automaticOnly: true
        );

    private async Task Select(
        ProviderOption option,
        CancellationToken cancellation,
        bool enableAutomaticDownload = false
    )
    {
        IProvider provider;
        lock (_gate)
        {
            if (enableAutomaticDownload && !Available(option))
                throw new VideoException(VideoFailure.ProviderUnavailable) { Provider = option };
            provider =
                option == Option && Provider is { } current ? current : option.Create(Key(option));
        }
        if (enableAutomaticDownload)
            await store
                .EnableAutomaticDownload(option.ProviderId, cancellation)
                .ConfigureAwait(false);
        else
            await store.SelectProvider(option.ProviderId, cancellation).ConfigureAwait(false);
        lock (_gate)
        {
            if (!ReferenceEquals(Provider, provider))
            {
                _failure = null;
                _recheck = null;
            }
            Option = option;
            Provider = provider;
            if (enableAutomaticDownload)
                IsAutomaticDownloadEnabled = true;
            Resume();
        }
    }

    /// <summary>Whether the person saved their own API key for <paramref name="providerId"/>.</summary>
    internal bool HasKey(string providerId)
    {
        lock (_gate)
            return _keys.ContainsKey(providerId);
    }

    /// <summary>
    /// Saves the person's own API key for <paramref name="providerId"/>, or
    /// removes it when <paramref name="key"/> is null so the provider uses its
    /// built-in key. A provider failure, such as a refused key, is cleared.
    /// </summary>
    internal Task SaveKey(string providerId, string? key) =>
        Change(
            async cancellation =>
            {
                await store.SaveKey(providerId, key, cancellation).ConfigureAwait(false);
                lock (_gate)
                {
                    if (key is null)
                        _keys.Remove(providerId);
                    else
                        _keys[providerId] = key;
                    if (Option is { } option && option.ProviderId == providerId)
                    {
                        Restart(option);
                        _recheck = new(DateTimeOffset.UtcNow.UtcTicks) { Recheck = true };
                    }
                }
            },
            automaticOnly: true
        );

    /// <summary>
    /// Runs <paramref name="replace"/> while no lookup reads the provider's
    /// downloaded data, such as a new IMDb data file replacing the old one.
    /// Clears local attempts so unresolved files can use the new dataset.
    /// </summary>
    internal Task Reload(
        ProviderOption option,
        Action replace,
        Func<bool>? enableAutomaticDownload = null
    ) =>
        Change(
            async cancellation =>
            {
                // Replacing a file can wait for a read in progress, so it never
                // runs on the calling window thread.
                await Task.Run(replace).ConfigureAwait(false);
                lock (_gate)
                    Restart(option);
                await store.ClearAttempts(option.ProviderId, cancellation).ConfigureAwait(false);
                if (enableAutomaticDownload?.Invoke() == true)
                    await Select(option, cancellation, enableAutomaticDownload: true)
                        .ConfigureAwait(false);
            },
            preserveBatch: true
        );

    // Callers hold _gate. The provider in use that starts again with a new
    // key or new data forgets the failures of the old one; another
    // provider's start leaves them.
    private void Restart(ProviderOption option)
    {
        if (Option != option)
            return;
        Provider = option.Create(Key(option));
        _failure = null;
        _recheck = null;
    }

    // Callers hold _gate, which guards the saved keys. A provider sends the
    // person's own key, else the one built into the release.
    private string Key(ProviderOption option) =>
        _keys.GetValueOrDefault(option.ProviderId) ?? option.BuiltInKey ?? string.Empty;

    // Callers hold _gate. A provider that needs an API key works only with
    // one, and one that answers from downloaded data only once it is installed.
    private bool Available(ProviderOption option) =>
        option.Dataset?.IsInstalled
        ?? (option.Access != Access.Key || !string.IsNullOrWhiteSpace(Key(option)));

    // Callers hold _gate. The saved provider serves its own option, so the
    // failure a lookup records belongs to the provider the person sees.
    private Context Bind(ProviderOption option, CancellationToken token)
    {
        var key = Key(option);
        return new(
            option,
            option == Option && Provider is { } saved ? saved : option.Create(key),
            _language,
            token
        )
        {
            Access = option.IsOnline
                ? new(option.ProviderId, RequestKey.Digest(key), string.Empty)
                : null,
        };
    }

    internal Task DisableAutomaticDownload() =>
        Change(
            async cancellation =>
            {
                await store.DisableAutomaticDownload(cancellation).ConfigureAwait(false);
                lock (_gate)
                {
                    IsAutomaticDownloadEnabled = false;
                    Resume();
                }
            },
            automaticOnly: true
        );

    internal Task Suspend(Func<Task> apply) =>
        Change(
            async cancellation =>
            {
                await apply().ConfigureAwait(false);
                Context? current;
                lock (_gate)
                    current = Option is { } option ? Bind(option, cancellation) : null;
                if (
                    current is { Access: not null }
                    && await AccessProtection(current, cancellation).ConfigureAwait(false) is null
                )
                    lock (_gate)
                        _failure = null;
            },
            preserveBatch: true
        );

    private async Task Change(
        Func<CancellationToken, Task> apply,
        bool preserveBatch = false,
        bool automaticOnly = false
    )
    {
        await _changes.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            Task work;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                _changingContext = !automaticOnly;
                if (automaticOnly)
                    CancelSweep();
                else
                    CancelContext(preserveBatch);
                work = _work;
            }
            if (automaticOnly)
            {
                await apply(_lifetime.Token).ConfigureAwait(false);
                return;
            }
            if (preserveBatch)
                await work.ConfigureAwait(false);
            await _access.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                await apply(_lifetime.Token).ConfigureAwait(false);
            }
            finally
            {
                _access.Release();
            }
        }
        finally
        {
            lock (_gate)
            {
                _changingContext = false;
                _changes.Release();
                // Reset can release a hold before its scheduled retry.
                if (preserveBatch && _batch is { } run)
                    for (var remaining = run.Queue.Count; remaining > 0; remaining--)
                        run.Queue.Enqueue(run.Queue.Dequeue() with { RetryAt = null });
                if (_batch is not null)
                    Wake();
                Refresh(_connected, _language);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelContext(bool preserveBatch = false)
    {
        _context.Cancel();
        _context.Dispose();
        _context = new();
        CancelSweep();
        if (!preserveBatch)
            EndBatch();
    }

    internal void Refresh(bool connected, string language)
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _again = true;
            if (_connected != connected || _language != language)
                CancelContext();
            _connected = connected;
            _language = language;
            if (
                !connected
                || !IsAutomaticDownloadEnabled
                || Option is not { } option
                || !Available(option)
                || _changes.CurrentCount == 0
            )
            {
                CancelSweep();
                return;
            }
            // A request cancels the sweep itself; the batch lets it finish its
            // file. Either only keeps it from starting here. A failure with no
            // retry time waits for Retry or a success, so scrolling repeats no
            // failing request; a sweep waits out a failure that has one.
            if (
                _interactive != 0
                || _batch is not null
                || _sweep is not null
                || _displayed.Count == 0
                || _failure is { RetryAt: null }
            )
                return;
            // CancelContext ends a waiting sweep on this thread, and the sweep's
            // end calls Refresh before the new context exists. A sweep started
            // then would end at once and call Refresh again, without end; the
            // caller of CancelContext starts the next sweep instead.
            if (_context.IsCancellationRequested)
                return;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token,
                _context.Token
            );
            _sweep = new(
                Bind(option, cancellation.Token) with
                {
                    Command = _recheck,
                },
                cancellation
            );
            Wake();
        }
    }

    /// <summary>
    /// Sets the entries on screen. The sweep identifies only these, as the
    /// Library contract's no-scan ruling requires.
    /// </summary>
    internal void SetDisplayed(IReadOnlySet<long> entries)
    {
        lock (_gate)
        {
            _displayed = entries;
            Refresh(_connected, _language);
        }
    }

    /// <summary>Clears a provider failure whose retry time has passed, so lookups start again.</summary>
    internal void Retry()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _cancelled.Clear();
            _recheck = new(DateTimeOffset.UtcNow.UtcTicks) { Recheck = true };
            Resume();
            Refresh(_connected, _language);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Resume()
    {
        if (Failure?.RetryAt > DateTimeOffset.UtcNow)
            return;
        _failure = null;
    }

    /// <summary>
    /// Searches <paramref name="source"/>, which may differ from the saved
    /// provider; the saved choice does not change. The results include the
    /// saved choices of the same title for automatic searches, which answer
    /// alone when the provider fails or is waiting.
    /// </summary>
    internal Task<VideoSearch> Search(
        string query,
        string fileName,
        ProviderOption source,
        CancellationToken cancellation,
        FetchScope scope = FetchScope.New,
        int? year = null,
        bool automatic = false
    ) =>
        Request<VideoSearch>(
            async context =>
            {
                var saved = await store
                    .Choices(context.Option.ProviderId, query, context.Token)
                    .ConfigureAwait(false);
                IReadOnlyList<VideoChoice> matching = year is { } requestedYear
                    ? saved.Where(choice => choice.Year == requestedYear).ToArray()
                    : saved;
                VideoSearch found;
                try
                {
                    found = await Execute(
                            context,
                            async current =>
                            {
                                var answer = await Search(current, query, year, automatic)
                                    .ConfigureAwait(false);
                                var release = FileName.Interpret(fileName) with
                                {
                                    Title = query,
                                    Year = year,
                                };
                                if (Matches(release, answer.Choices).Length != 1)
                                    await store
                                        .RejectSearch(
                                            SearchRequest(
                                                current,
                                                query,
                                                answer.Broadened ? null : year
                                            ).Key
                                        )
                                        .ConfigureAwait(false);
                                return answer;
                            }
                        )
                        .ConfigureAwait(false);
                }
                catch (ProviderException)
                    when (automatic && scope == FetchScope.New && matching.Count > 0)
                {
                    found = new(matching, false);
                }
                IReadOnlyList<VideoChoice> retained =
                    !automatic ? []
                    : found.Broadened ? saved
                    : matching;
                var choices = found.Choices.Concat(
                    retained.Where(choice =>
                        found.Choices.All(result => result.VideoId != choice.VideoId)
                    )
                );
                if (year is { } requested)
                    choices = choices.OrderBy(choice =>
                        choice.Year is { } candidate
                            ? Math.Abs(candidate - requested)
                            : int.MaxValue
                    );
                var ranked = choices.ToArray();
                return found with { Choices = ranked, Empty = ranked.Length == 0 && found.Empty };
            },
            cancellation,
            source,
            scope
        );

    /// <summary>
    /// Saves <paramref name="choice"/>, a result of searching <paramref name="source"/>.
    /// Throws a <see cref="VideoException"/> when the file changed or a newer decision won.
    /// </summary>
    internal Task Identify(
        Entry entry,
        VideoChoice choice,
        ProviderOption source,
        CancellationToken cancellation,
        FetchScope scope = FetchScope.New
    ) =>
        Request(
            context =>
                Execute(
                    context,
                    current =>
                        Collect(
                            current,
                            new VideoTarget(entry, current.Option.ProviderId, LookupKind.Explicit),
                            choice,
                            scope != FetchScope.New
                        )
                ),
            cancellation,
            source,
            scope
        );

    /// <summary>
    /// Identifies <paramref name="entry"/> with <paramref name="source"/>, reading it
    /// again when it has metadata. Returns false when no confident match exists.
    /// </summary>
    internal Task<bool> Lookup(
        Entry entry,
        ProviderOption source,
        CancellationToken cancellation,
        Action<WorkExecution> report
    ) =>
        Request(
            async context =>
                (
                    await Execute(
                            context,
                            current =>
                                Collect(
                                    current,
                                    new VideoTarget(
                                        entry,
                                        current.Option.ProviderId,
                                        LookupKind.Explicit
                                    ),
                                    null,
                                    entry.HasMetadata
                                ),
                            report
                        )
                        .ConfigureAwait(false)
                ).Outcome == IdentificationOutcome.Matched,
            cancellation,
            source
        );

    internal async Task<RequestBlock?> Protection(
        string query,
        string fileName,
        ProviderOption source,
        CancellationToken cancellation,
        int? year = null,
        bool automatic = false
    )
    {
        Context context;
        lock (_gate)
            context = Bind(source, cancellation);
        return await AccessProtection(context, cancellation).ConfigureAwait(false)
            ?? await SearchProtection(
                    context,
                    query,
                    FileName.Interpret(fileName) with
                    {
                        Title = query,
                        Year = year,
                    },
                    cancellation,
                    year,
                    automatic
                )
                .ConfigureAwait(false);
    }

    private async Task<RequestBlock?> SearchProtection(
        Context context,
        string query,
        Release release,
        CancellationToken cancellation,
        int? year = null,
        bool automatic = true
    )
    {
        var requests = Queries(context, query, year, automatic)
            .Select(query => SearchRequest(context, query.Text, query.Year))
            .ToArray();
        var protection = await store
            .Protection(requests, policy, cancellation)
            .ConfigureAwait(false);
        return SearchProtection(release, requests, protection);
    }

    private static RequestBlock? SearchProtection(
        Release release,
        IReadOnlyList<LookupRequest> requests,
        IReadOnlyDictionary<RequestKey, VideoProtection> protection
    )
    {
        for (var index = 0; index < requests.Count; index++)
        {
            var answer = protection[requests[index].Key];
            var saved = answer.Choices;
            if (saved is { Count: > 0 })
                return Matches(release, saved).Length == 1 ? null : new(RequestBlockKind.NoMatch);
            // A saved empty answer needs no new request before the fallback.
            var block = answer.Block;
            if (
                saved is { Count: 0 }
                && block?.Kind == RequestBlockKind.Missing
                && index < requests.Count - 1
            )
                continue;
            return block;
        }
        return null;
    }

    private Task<RequestBlock?> AccessProtection(Context context, CancellationToken cancellation) =>
        context.Access is { } access
            ? policy.Read(
                [access with { Operation = "search" }, access with { Operation = "detail" }],
                cancellation
            )
            : Task.FromResult<RequestBlock?>(null);

    internal async Task<IReadOnlyList<VideoFetch>> Plan(
        IReadOnlyCollection<Entry> entries,
        ProviderOption source,
        CancellationToken cancellation
    )
    {
        if (!source.IsOnline)
            return [];
        Context context;
        lock (_gate)
            context = Bind(source, cancellation);
        var candidates = await store
            .Fetchable(entries, source.ProviderId, cancellation)
            .ConfigureAwait(false);
        var wait = await AccessProtection(context, cancellation).ConfigureAwait(false);
        // A file without this provider's identification is searched by name.
        return candidates
            .Where(candidate =>
                candidate.Identified
                || CanMatch(source, FileName.Interpret(candidate.Entry.Name))
            )
            .Select(candidate => candidate with { Wait = wait })
            .ToArray();
    }

    /// <summary>
    /// Adds <paramref name="entries"/> to the batch, which identifies them one
    /// at a time with <paramref name="source"/>. Automatic lookups wait until
    /// the batch ends. A file already waiting is not added again.
    /// </summary>
    internal void Fetch(
        IReadOnlyCollection<Entry> entries,
        ProviderOption source,
        FetchScope scope = FetchScope.New
    )
    {
        if (!source.IsOnline)
            throw new VideoException(VideoFailure.ProviderUnavailable) { Provider = source };
        if (entries.Count == 0)
            return;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var run = _batch ??= new BatchRun(this);
            var command =
                scope == FetchScope.New ? null : new RetryCommand(DateTimeOffset.UtcNow.UtcTicks);
            foreach (var entry in entries)
                if (run.Entries.Add(entry.EntryId))
                {
                    run.Queue.Enqueue(new(entry, source, scope, command));
                    run.Total++;
                }
            ReportBatch(run, run.Work.Observation.Execution);
            // A sweep that has not begun its operation can be waiting for
            // start-up to settle, which holds the work loop, so it stops; the
            // batch's end starts it again. CancelAsync ends it on a worker
            // thread rather than on this thread, which holds _gate.
            if (_sweep is { Work: null, Cancellation.IsCancellationRequested: false } waiting)
                waiting.Signalling = waiting.Cancellation.CancelAsync();
            Wake();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EndBatch()
    {
        if (_batch is { } run)
            StopBatch(run);
    }

    private Task StopBatch(BatchRun run)
    {
        lock (_gate)
        {
            if (run.Work.TryCancel(out var signalling))
            {
                run.Queue.Clear();
                if (ReferenceEquals(_batch, run))
                    _batch = null;
                if (!run.Running)
                    Track(FinishBatch(run));
            }
            return signalling;
        }
    }

    private static void ReportBatch(BatchRun run, WorkExecution execution) =>
        run.Work.Report(
            run.Work.Observation with
            {
                Progress =
                    run.Done + run.Queue.Count > 0
                        ? new WorkProgress.Fraction(
                            run.Done,
                            run.Done + run.Queue.Count,
                            WorkUnit.Files
                        )
                        : new WorkProgress.Unknown(),
                Execution = execution,
            }
        );

    // Callers hold _gate, so a stop never finds the batch half ended.
    private Task FinishBatch(BatchRun run)
    {
        if (ReferenceEquals(_batch, run))
            _batch = null;
        var operation = run.Work.Operation;
        if (run.Work.Token.IsCancellationRequested)
            operation.Outcome = WorkOutcome.Cancelled;
        operation.Summarize("metadata_result", run.Found, run.Done, run.Total);
        return run.Work.DisposeAsync().AsTask();
    }

    // Callers hold _gate. Starts the work loop, or ends its wait for a hold so
    // it takes the new work first.
    private void Wake()
    {
        if (_working)
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        else if (!_closed)
        {
            _working = true;
            _work = Task.Run(Run);
        }
    }

    // Identifies one file at a time in the background. The batch takes each
    // file before the sweep takes its next, so the sweep finishes the file it
    // is reading and waits until the batch ends; a stopped sweep ends at once.
    private async Task Run()
    {
        while (true)
        {
            BatchRun? run;
            Sweep? sweep;
            lock (_gate)
            {
                run = _connected && _batch is { Queue.Count: > 0 } batch ? batch : null;
                // The context change that suspends the batch starts it again.
                if (run is not null && _changingContext)
                {
                    ReportBatch(run, new WorkExecution.Waiting());
                    run = null;
                }
                sweep =
                    run is null || _sweep is { Cancellation.IsCancellationRequested: true }
                        ? _sweep
                        : null;
                if (run is null && sweep is null)
                {
                    _working = false;
                    return;
                }
            }
            if (sweep is not null)
                await ServeSweep(sweep).ConfigureAwait(false);
            else if (run is not null)
                await ServeBatch(run).ConfigureAwait(false);
        }
    }

    // Identifies the batch's next file whose hold has ended, or waits for the
    // earliest hold when every file waits.
    private async Task ServeBatch(BatchRun run)
    {
        BatchItem item;
        DateTimeOffset? next = null;
        Context context;
        CancellationTokenSource request;
        lock (_gate)
        {
            if (run.Work.Token.IsCancellationRequested)
                return;
            run.Running = true;
            request = CancellationTokenSource.CreateLinkedTokenSource(
                run.Work.Token,
                _lifetime.Token,
                _context.Token
            );
            var now = DateTimeOffset.UtcNow;
            for (var remaining = run.Queue.Count; remaining > 0; remaining--)
            {
                item = run.Queue.Peek();
                if (item.RetryAt is not { } due || due <= now)
                {
                    next = null;
                    break;
                }
                next = next is null || due < next ? due : next;
                run.Queue.Enqueue(run.Queue.Dequeue());
            }
            item = run.Queue.Peek();
            context = Bind(item.Source, request.Token) with
            {
                Scope = item.Scope,
                Accepted = item.Accepted,
                Command = item.Command,
            };
            if (next is not null)
                ReportBatch(run, new WorkExecution.Waiting { RetryAt = next });
        }
        var ends = false;
        try
        {
            if (next is { } due)
            {
                await Wait(due, request.Token).ConfigureAwait(false);
                return;
            }
            var (outcome, block) = await Execute(
                    context,
                    current =>
                        Collect(
                            current,
                            new VideoTarget(
                                item.Entry,
                                current.Option.ProviderId,
                                item.Scope == FetchScope.All ? LookupKind.Refresh : LookupKind.Batch
                            ),
                            null,
                            item.Scope == FetchScope.All
                        ),
                    execution =>
                    {
                        lock (_gate)
                            ReportBatch(run, execution);
                    }
                )
                .ConfigureAwait(false);
            lock (_gate)
            {
                if (run.Work.Token.IsCancellationRequested)
                    return;
                run.Queue.Dequeue();
                run.Entries.Remove(item.Entry.EntryId);
                run.Done++;
                if (outcome == IdentificationOutcome.Matched)
                    run.Found++;
                ReportBatch(run, new WorkExecution.Running());
                if (block is { Kind: RequestBlockKind.Limit })
                {
                    run.Work.Operation.Error = new RequestBlockedException(block);
                    run.Limited++;
                }
            }
            Stored?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
            when (_changingContext && !run.Work.Token.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine(
                "Metadata batch lookup cancelled: provider context changed."
            );
        }
        catch (OperationCanceledException)
        {
            ends = true;
            System.Diagnostics.Debug.WriteLine("Metadata batch cancelled.");
        }
        catch (Exception error)
            when (error
                    is ProviderException { RetryAt: not null }
                        or RequestBlockedException { Block.RetryAt: not null }
            )
        {
            var retry = error is ProviderException provider
                ? provider.RetryAt
                : ((RequestBlockedException)error).Block.RetryAt;
            lock (_gate)
                if (!run.Work.Token.IsCancellationRequested)
                {
                    var failed = run.Queue.Dequeue();
                    run.Queue.Enqueue(
                        error is ProviderException
                            ? failed with
                            {
                                RetryAt = retry,
                                Command = null,
                            }
                            : failed with
                            {
                                RetryAt = retry,
                            }
                    );
                }
        }
        catch (Exception error)
        {
            ends = true;
            run.Work.Operation.Error = error;
            BatchFailed?.Invoke(this, error);
        }
        finally
        {
            request.Dispose();
            Task? finishing = null;
            lock (_gate)
            {
                run.Running = false;
                if (
                    !run.Work.IsEnded
                    && (ends || run.Work.Token.IsCancellationRequested || run.Queue.Count == 0)
                )
                {
                    if (run.Queue.Count == 0 && !run.Work.Token.IsCancellationRequested)
                        run.Work.Operation.Outcome = run.Work.Operation.Error is null
                            ? WorkOutcome.Completed
                            : WorkOutcome.Failed;
                    finishing = FinishBatch(run);
                }
            }
            if (finishing is not null)
            {
                await finishing.ConfigureAwait(false);
                Changed?.Invoke(this, EventArgs.Empty);
                if (run.Limited > 0)
                    BatchLimited?.Invoke(this, run.Limited);
                lock (_gate)
                    Refresh(_connected, _language);
            }
        }
    }

    // Waits until a hold ends or new work arrives. Each wait is bounded,
    // because a valid provider delay can exceed the timer's supported duration.
    private async Task Wait(DateTimeOffset retryAt, CancellationToken cancellation)
    {
        var delay = retryAt - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
            await _wake
                .WaitAsync(
                    delay < TimeSpan.FromDays(1) ? delay : TimeSpan.FromDays(1),
                    cancellation
                )
                .ConfigureAwait(false);
    }

    private sealed class BatchRun
    {
        internal Queue<BatchItem> Queue { get; } = [];
        internal HashSet<long> Entries { get; } = [];
        internal CancellableWork Work { get; }
        internal int Done;
        internal int Found;
        internal int Total;

        /// <summary>How many files the search limit left unidentified.</summary>
        internal int Limited;

        /// <summary>Whether the work loop is serving one of the batch's files.</summary>
        internal bool Running;

        internal BatchRun(VideoLookup owner)
        {
            Work = new(
                owner.Work,
                new(
                    new WorkDescription.Metadata(MetadataScope.Batch),
                    new WorkProgress.Unknown(),
                    new WorkExecution.Waiting()
                ),
                _ => owner.StopBatch(this)
            );
        }
    }

    private sealed record BatchItem(
        Entry Entry,
        ProviderOption Source,
        FetchScope Scope,
        RetryCommand? Command
    )
    {
        internal long? Accepted { get; } = Command?.Accepted;
        internal DateTimeOffset? RetryAt { get; init; }
    }

    private void Track(Task task)
    {
        _requests.Add(task);
        _ = task.ContinueWith(
            _ =>
            {
                lock (_gate)
                    _requests.Remove(task);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private Task<T> Request<T>(
        Func<Context, Task<T>> action,
        CancellationToken cancellation,
        ProviderOption? source = null,
        FetchScope scope = FetchScope.New
    )
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var option = source ?? Option;
            if (!_connected || option is not { IsOnline: true } || _changingContext)
                throw new VideoException(VideoFailure.LookupStopped);
            var request = CancellationTokenSource.CreateLinkedTokenSource(
                cancellation,
                _lifetime.Token,
                _context.Token
            );
            _interactive++;
            CancelSweep();
            var bound = Bind(option, request.Token);
            var command =
                scope == FetchScope.New
                && ReferenceEquals(bound.Provider, Provider)
                && _recheck is not null
                    ? _recheck
                    : new RetryCommand(DateTimeOffset.UtcNow.UtcTicks);
            var context = bound with
            {
                Scope = scope,
                Accepted = command.Accepted,
                Command = command,
            };
            var task = Perform();
            Track(task);
            return task;

            async Task<T> Perform()
            {
                try
                {
                    return await action(context).ConfigureAwait(false);
                }
                // A changed connection, provider, language or route cancels
                // the request, which the person sees instead of an empty answer.
                catch (OperationCanceledException)
                    when (!cancellation.IsCancellationRequested
                        && !_lifetime.IsCancellationRequested
                    )
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Interactive metadata lookup cancelled ({option.ProviderId}): lookup context changed."
                    );
                    throw new VideoException(VideoFailure.LookupStopped);
                }
                finally
                {
                    request.Dispose();
                    lock (_gate)
                    {
                        _interactive--;
                        Refresh(_connected, _language);
                    }
                }
            }
        }
    }

    private async Task<T> Execute<T>(
        Context context,
        Func<Context, Task<T>> action,
        Action<WorkExecution>? report = null
    )
    {
        report?.Invoke(new WorkExecution.Waiting());
        await _access.WaitAsync(context.Token).ConfigureAwait(false);
        try
        {
            context.Token.ThrowIfCancellationRequested();
            report?.Invoke(new WorkExecution.Running());
            bool available;
            lock (_gate)
                available = Available(context.Option);
            if (!available)
                throw new ProviderException(VideoFailure.ProviderUnavailable);
            var result = await action(context).ConfigureAwait(false);
            return result;
        }
        catch (VideoException error)
        {
            error.Provider ??= context.Option;
            if (error is ProviderException failure)
            {
                lock (_gate)
                    if (
                        !context.Token.IsCancellationRequested
                        && ReferenceEquals(Provider, context.Provider)
                    )
                        _failure = failure;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            throw;
        }
        catch (RequestBlockedException error)
        {
            if (
                error.Block.Kind
                is RequestBlockKind.Attention
                    or RequestBlockKind.RemoteWait
                    or RequestBlockKind.Allowance
                    or RequestBlockKind.Backoff
            )
            {
                lock (_gate)
                    if (ReferenceEquals(Provider, context.Provider))
                        _failure = new ProviderException(error.Block) { Provider = context.Option };
                Changed?.Invoke(this, EventArgs.Empty);
            }
            throw;
        }
        finally
        {
            _access.Release();
        }
    }

    private readonly HashSet<VideoTarget> _cancelled = [];

    // Callers hold _gate. Waking the work loop ends a stopped sweep at once,
    // even while the batch waits for a hold.
    private void CancelSweep()
    {
        if (_sweep is null)
            return;
        _sweep.Cancellation.Cancel();
        Wake();
    }

    private async Task<BackgroundWork.Operation?> PrepareSweep(Sweep sweep)
    {
        var context = sweep.Context;
        await settled.WaitAsync(context.Token).ConfigureAwait(false);
        IReadOnlySet<long> displayed;
        lock (_gate)
        {
            _again = false;
            displayed = _displayed;
        }
        var pending = await store
            .Pending(context.Option.ProviderId, displayed, context.Token)
            .ConfigureAwait(false);
        lock (_gate)
        {
            // A sweep stopped before this point never shows an operation.
            context.Token.ThrowIfCancellationRequested();
            sweep.Targets = pending.Where(target => !_cancelled.Contains(target)).ToList();
            if (sweep.Targets.Count == 0)
                return null;
            sweep.Work = new(
                Work,
                new(
                    new WorkDescription.Metadata(MetadataScope.Automatic),
                    new WorkProgress.Unknown(),
                    new WorkExecution.Waiting()
                ),
                work => StopSweep(sweep, work),
                sweep.Cancellation
            );
            ReportSweep(sweep, new WorkExecution.Waiting());
            return sweep.Work.Operation;
        }
    }

    // Identifies the sweep's next file, or waits for the hold that stopped it.
    private async Task ServeSweep(Sweep sweep)
    {
        var context = sweep.Context;
        var operation = sweep.Work?.Operation;
        var ended = false;
        try
        {
            context.Token.ThrowIfCancellationRequested();
            operation ??= await PrepareSweep(sweep).ConfigureAwait(false);
            if (operation is null)
            {
                ended = true;
                return;
            }
            if (sweep.RetryAt is { } due && due > DateTimeOffset.UtcNow)
            {
                lock (_gate)
                    ReportSweep(sweep, new WorkExecution.Waiting { RetryAt = due });
                await Wait(due, context.Token).ConfigureAwait(false);
                return;
            }
            sweep.RetryAt = null;
            VideoTarget? target;
            lock (_gate)
            {
                target = sweep.Targets[sweep.Done];
                // A row that scrolled away before its turn is identified when
                // it shows again.
                if (!_displayed.Contains(target.Entry.EntryId))
                {
                    sweep.Targets.RemoveAt(sweep.Done);
                    ReportSweep(sweep, new WorkExecution.Waiting());
                    target = null;
                }
            }
            if (target is not null)
            {
                try
                {
                    var (outcome, block) = await Execute(
                            context,
                            current => Collect(current, target, null, false),
                            execution =>
                            {
                                lock (_gate)
                                    ReportSweep(sweep, execution);
                            }
                        )
                        .ConfigureAwait(false);
                    lock (_gate)
                    {
                        sweep.Done++;
                        if (outcome == IdentificationOutcome.Matched)
                            sweep.Found++;
                        if (block is { Kind: RequestBlockKind.Limit })
                            operation.Error = new RequestBlockedException(block);
                        ReportSweep(sweep, new WorkExecution.Waiting());
                    }
                }
                catch (ProviderException error) when (error.RetryAt is not null)
                {
                    sweep.RetryAt = error.RetryAt;
                }
                catch (RequestBlockedException error) when (error.Block.RetryAt is not null)
                {
                    sweep.RetryAt = error.Block.RetryAt;
                }
            }
            if (sweep.Done == sweep.Targets.Count)
            {
                operation.Outcome = operation.Error is null
                    ? WorkOutcome.Completed
                    : WorkOutcome.Failed;
                // Automatic enrichment runs by itself; matches appear in the list.
                operation.ReportsSuccess = false;
                ended = true;
            }
        }
        catch (OperationCanceledException)
        {
            if (operation is not null)
                operation.Outcome = WorkOutcome.Cancelled;
            ended = true;
            System.Diagnostics.Debug.WriteLine(
                $"Automatic metadata lookup cancelled ({context.Option.ProviderId})."
            );
        }
        catch (Exception error) when (error is ProviderException or RequestBlockedException)
        {
            if (operation is not null)
                operation.Error = error;
            ended = true;
        }
        catch (Exception error)
        {
            if (operation is not null)
                operation.Error = error;
            ended = true;
            Failed?.Invoke(this, error);
        }
        finally
        {
            if (ended)
                await FinishSweep(sweep).ConfigureAwait(false);
        }
    }

    // Nothing is left when every target scrolled away before its turn.
    private static void ReportSweep(Sweep sweep, WorkExecution execution)
    {
        if (sweep.Work is { } work)
            work.Report(
                work.Observation with
                {
                    Progress =
                        sweep.Targets.Count > 0
                            ? new WorkProgress.Fraction(
                                sweep.Done,
                                sweep.Targets.Count,
                                WorkUnit.Files
                            )
                            : new WorkProgress.Unknown(),
                    Execution = execution,
                }
            );
    }

    private Task StopSweep(Sweep sweep, CancellableWork work)
    {
        lock (_gate)
        {
            if (work.TryCancel(out var signalling))
            {
                foreach (var target in sweep.Targets.Skip(sweep.Done))
                    _cancelled.Add(target);
                Wake();
            }
            return signalling;
        }
    }

    private async Task FinishSweep(Sweep sweep)
    {
        Task ending;
        lock (_gate)
        {
            if (ReferenceEquals(_sweep, sweep))
                _sweep = null;
            if (sweep.Work is { } work)
            {
                work.Operation.Summarize(
                    "metadata_result",
                    sweep.Found,
                    sweep.Done,
                    sweep.Targets.Count
                );
                ending = work.DisposeAsync().AsTask();
            }
            else
                ending = sweep.Signalling;
        }
        try
        {
            await ending.ConfigureAwait(false);
        }
        finally
        {
            if (sweep.Work is null)
                sweep.Cancellation.Dispose();
            lock (_gate)
                if (_again)
                    Refresh(_connected, _language);
        }
    }

    /// <summary>
    /// The background sweep, which looks up the pending files on screen
    /// automatically.
    /// </summary>
    private sealed class Sweep(Context context, CancellationTokenSource cancellation)
    {
        internal Context Context { get; } = context;

        /// <summary>
        /// The source of the context's token. Cancelling it directly is a stop
        /// the person did not ask for, which shows no Cancelling;
        /// <see cref="Work"/> takes it over once the sweep shows.
        /// </summary>
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal List<VideoTarget> Targets { get; set; } = [];

        /// <summary>The sweep's work, which shows only once it has files to identify.</summary>
        internal CancellableWork? Work { get; set; }

        /// <summary>
        /// The stop of a sweep that never showed; once it shows, only
        /// <see cref="Work"/> stops it asynchronously.
        /// </summary>
        internal Task Signalling { get; set; } = Task.CompletedTask;

        /// <summary>When the hold that stopped the next file ends.</summary>
        internal DateTimeOffset? RetryAt { get; set; }
        internal int Done;
        internal int Found;
    }

    public async ValueTask DisposeAsync()
    {
        Task[] requests;
        lock (_gate)
        {
            _closed = true;
            EndBatch();
            _lifetime.Cancel();
            _context.Cancel();
            CancelSweep();
            requests = [.. _requests, _work];
        }
        try
        {
            await Task.WhenAll(requests).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
        }
        await _changes.WaitAsync().ConfigureAwait(false);
        _changes.Release();
        _context.Dispose();
        _lifetime.Dispose();
    }

    private sealed record Context(
        ProviderOption Option,
        IProvider Provider,
        string Language,
        CancellationToken Token
    )
    {
        /// <summary>The protection that an online provider's requests with this key share.</summary>
        internal ProviderAccess? Access { get; init; }
        internal FetchScope Scope { get; init; }
        internal long? Accepted { get; init; }
        internal RetryCommand? Command { get; init; }

        internal FreshRequest? Fresh(RequestKey key) =>
            Command is { } command && (Scope != FetchScope.New || command.Recheck)
                ? new(command, key)
                : null;
    }
}

internal sealed record VideoFetch(Entry Entry, ProviderAnswer Answer, RequestBlock? Wait)
{
    // This provider, rather than another, holds the file's identification.
    internal bool Identified { get; init; }
}
