using System.Text.RegularExpressions;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Library;

internal sealed class VideoLookup(
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
    private readonly SemaphoreSlim _batchWake = new(0, 1);
    private Task _batchWork = Task.CompletedTask;
    private bool _batchRunning;
    private CancellationTokenSource _context = new();
    private CancellationTokenSource? _background;
    private Task _work = Task.CompletedTask;
    private int _interactive;
    private string _language = "en";
    private bool _connected;
    private bool _closed;
    private bool _again;
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
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                _changingContext = !automaticOnly;
                if (automaticOnly)
                    _background?.Cancel();
                else
                    CancelContext(preserveBatch);
            }
            if (automaticOnly)
            {
                await apply(_lifetime.Token).ConfigureAwait(false);
                return;
            }
            if (preserveBatch)
                await _batchWork.ConfigureAwait(false);
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
                StartBatch();
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
        _background?.Cancel();
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
                _background?.Cancel();
                return;
            }
            // A request cancels the sweep itself; the batch lets it finish its
            // file. Either only keeps it from starting here.
            if (_interactive != 0 || _batchRunning || _background is not null)
                return;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token,
                _context.Token
            );
            var context = Bind(option, cancellation.Token) with { Command = _recheck };
            _background = cancellation;
            _work = Task.Run(() => Run(context, cancellation));
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
            context =>
                Execute(
                    context,
                    current =>
                        Collect(
                            current,
                            new VideoTarget(entry, current.Option.ProviderId, LookupKind.Explicit),
                            null,
                            entry.HasMetadata
                        ),
                    report
                ),
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
                candidate.Entry.HasMetadata
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
            ReportBatch(run, run.Observation.Execution);
            if (_batchWake.CurrentCount == 0)
                _batchWake.Release();
            StartBatch();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StartBatch()
    {
        if (
            !_closed
            && _connected
            && !_changingContext
            && !_batchRunning
            && _batch is { Queue.Count: > 0 } run
        )
        {
            _batchRunning = run.Running = true;
            _batchWork = Task.Run(() => RunBatch(run));
        }
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
            if (!run.Ended && !run.Cancellation.IsCancellationRequested)
            {
                ReportBatch(run, new WorkExecution.Cancelling());
                run.Queue.Clear();
                run.Signalling = run.Cancellation.CancelAsync();
                if (ReferenceEquals(_batch, run))
                    _batch = null;
                if (!run.Running)
                    Track(FinishBatch(run));
            }
            return run.Signalling;
        }
    }

    private void ReportBatch(BatchRun run, WorkExecution execution)
    {
        if (run.Ended || run.Cancellation.IsCancellationRequested)
            return;
        run.Observation = run.Observation with
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
        };
        run.Operation.Report(run.Observation);
    }

    private async Task FinishBatch(BatchRun run)
    {
        Task signalling;
        lock (_gate)
        {
            run.Ended = true;
            signalling = run.Signalling;
            if (ReferenceEquals(_batch, run))
                _batch = null;
        }
        try
        {
            await signalling.ConfigureAwait(false);
        }
        finally
        {
            if (run.Cancellation.IsCancellationRequested)
                run.Operation.Outcome = WorkOutcome.Cancelled;
            run.Operation.Summarize("metadata_result", run.Found, run.Done, run.Total);
            run.Operation.Dispose();
            run.Cancellation.Dispose();
        }
    }

    private async Task RunBatch(BatchRun run)
    {
        var limited = 0;
        var suspended = false;
        try
        {
            while (true)
            {
                BatchItem item;
                DateTimeOffset? next = null;
                Context context;
                CancellationTokenSource request;
                lock (_gate)
                {
                    if (
                        _closed
                        || !_connected
                        || run.Cancellation.IsCancellationRequested
                        || run.Queue.Count == 0
                    )
                    {
                        run.Ended = true;
                        if (run.Queue.Count == 0 && !run.Cancellation.IsCancellationRequested)
                            run.Operation.Outcome = run.Operation.Error is null
                                ? WorkOutcome.Completed : WorkOutcome.Failed;
                        if (ReferenceEquals(_batch, run))
                            _batch = null;
                        break;
                    }
                    if (_changingContext)
                    {
                        suspended = true;
                        ReportBatch(run, new WorkExecution.Waiting());
                        break;
                    }
                    request = CancellationTokenSource.CreateLinkedTokenSource(
                        run.Cancellation.Token,
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
                using (request)
                {
                    try
                    {
                        if (next is { } due)
                        {
                            var delay = due - DateTimeOffset.UtcNow;
                            if (delay > TimeSpan.Zero)
                                await _batchWake
                                    .WaitAsync(
                                        delay < TimeSpan.FromDays(1) ? delay : TimeSpan.FromDays(1),
                                        request.Token
                                    )
                                    .ConfigureAwait(false);
                            continue;
                        }
                        var identified = await Execute(
                                context,
                                current =>
                                    Collect(
                                        current,
                                        new VideoTarget(
                                            item.Entry,
                                            current.Option.ProviderId,
                                            item.Scope == FetchScope.All
                                                ? LookupKind.Refresh
                                                : LookupKind.Batch
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
                            if (run.Cancellation.IsCancellationRequested)
                                break;
                            run.Queue.Dequeue();
                            run.Entries.Remove(item.Entry.EntryId);
                            run.Done++;
                            if (identified)
                                run.Found++;
                            ReportBatch(run, new WorkExecution.Running());
                            if (!identified && context.Block is { Kind: RequestBlockKind.Limit } block)
                            {
                                run.Operation.Error = new RequestBlockedException(block);
                                limited++;
                            }
                        }
                        Stored?.Invoke(this, EventArgs.Empty);
                    }
                    catch (OperationCanceledException)
                        when (_changingContext && !run.Cancellation.IsCancellationRequested)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "Metadata batch lookup cancelled: provider context changed."
                        );
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
                            if (!run.Cancellation.IsCancellationRequested)
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
                }
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Metadata batch cancelled.");
        }
        catch (Exception error)
        {
            run.Operation.Error = error;
            BatchFailed?.Invoke(this, error);
        }
        finally
        {
            if (!suspended)
                await FinishBatch(run).ConfigureAwait(false);
            lock (_gate)
            {
                run.Running = false;
                _batchRunning = false;
                if (suspended && run.Cancellation.IsCancellationRequested && !run.Ended)
                    Track(FinishBatch(run));
                StartBatch();
                Refresh(_connected, _language);
            }
            Changed?.Invoke(this, EventArgs.Empty);
            if (limited > 0)
                BatchLimited?.Invoke(this, limited);
        }
    }

    private sealed class BatchRun
    {
        internal Queue<BatchItem> Queue { get; } = [];
        internal HashSet<long> Entries { get; } = [];
        internal CancellationTokenSource Cancellation { get; } = new();
        internal BackgroundWork.Operation Operation { get; }
        internal WorkObservation Observation { get; set; } =
            new(
                new WorkDescription.Metadata(MetadataScope.Batch),
                new WorkProgress.Unknown(),
                new WorkExecution.Waiting()
            );
        internal Task Signalling { get; set; } = Task.CompletedTask;
        internal int Done;
        internal int Found;
        internal int Total;
        internal bool Running;
        internal bool Ended;

        internal BatchRun(VideoLookup owner)
        {
            Operation = owner.Work.Begin(Observation, () => owner.StopBatch(this));
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
            _background?.Cancel();
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

    private async Task<bool> Collect(
        Context context,
        VideoTarget target,
        VideoChoice? choice,
        bool refresh
    )
    {
        var language = context.Option.IsLocalized ? context.Language : null;
        // A refused explicit request says so, because its caller would
        // otherwise report that no match exists.
        var attempt = await store.Begin(target, language, context.Token).ConfigureAwait(false);
        if (attempt is null)
            return target.Kind == LookupKind.Explicit
                ? throw new VideoException(VideoFailure.FileChanged)
                : false;
        VideoInformation? video = null;
        var unanswered = false;
        context.Block = null;
        context.BlockedRequest = null;
        bool accepted;
        try
        {
            refresh =
                refresh
                || target.Kind == LookupKind.Explicit
                    && choice is null
                    && attempt.Choice is not null;
            var release = FileName.Interpret(target.Entry.Name);
            var request = new VideoRequest(
                release,
                context.Language,
                (path, download, token) => Read(context, path, download, refresh, token),
                Get(context, "detail", context.Fresh(SearchRequest(context, release.Title).Key))
            );
            try
            {
                if (context.Option.IsOnline && choice is null && attempt.Choice is null && !refresh)
                    video = await store
                        .Saved(target, context.Language, context.Token)
                        .ConfigureAwait(false);
                if (video is null)
                {
                    choice ??= attempt.Choice;
                    if (choice is not null)
                    {
                        if (choice.Kind == VideoKind.Series && request.Release.Episodes.Count == 0)
                            throw new VideoException(VideoFailure.EpisodeRequired);
                        video = await Describe(context, choice, request).ConfigureAwait(false);
                    }
                    else
                    {
                        var series = request.Release.Season is not null
                            ? await store
                                .Series(target, request.Release, context.Token)
                                .ConfigureAwait(false)
                            : null;
                        video = await Match(context, request, series).ConfigureAwait(false);
                    }
                }
            }
            catch (RequestBlockedException error)
                when (target.Kind != LookupKind.Explicit
                    && error.Block.Kind
                        is RequestBlockKind.Missing
                            or RequestBlockKind.Unusable
                            or RequestBlockKind.Limit
                            or RequestBlockKind.NoMatch
                )
            {
                context.Block = error.Block;
            }
            // An individual unusable record must not stop unrelated lookups.
            catch (VideoException error)
                when (error is not ProviderException && target.Kind != LookupKind.Explicit)
            {
                unanswered = true;
            }
            // Another title is no answer for the title the file already has.
            if (attempt.AddsTo is { } title && video?.ImdbId != title)
                video = null;
            var outcome =
                video is not null ? "complete"
                : context.Block is { Kind: not RequestBlockKind.Missing } ? "blocked"
                : unanswered ? "unanswered"
                : "no_match";
            accepted = await store
                .Complete(
                    attempt with
                    {
                        BlockedRequest = context.BlockedRequest,
                    },
                    video,
                    outcome,
                    context.Token
                )
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A provider that cannot serve any request has not answered for
            // this file, so the attempt leaves it eligible for a later lookup.
            var outcome =
                context.Token.IsCancellationRequested
                || error
                    is OperationCanceledException
                        or ProviderException
                        or RequestBlockedException
                    ? "cancelled"
                    : "failed";
            await store
                .Complete(
                    attempt with
                    {
                        BlockedRequest = context.BlockedRequest,
                    },
                    null,
                    outcome,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            context.Token.ThrowIfCancellationRequested();
            throw;
        }
        if (!accepted)
            return target.Kind == LookupKind.Explicit
                ? throw new VideoException(VideoFailure.FileChanged)
                : false;
        Stored?.Invoke(this, EventArgs.Empty);
        return video is not null;
    }

    /// <summary>
    /// Finds the provider's unambiguous match for a release, or null. A search
    /// result alone is weak evidence, so its description must agree with it.
    /// </summary>
    private async Task<VideoInformation?> Match(
        Context context,
        VideoRequest request,
        VideoChoice? series
    )
    {
        var release = request.Release;
        var kind = release.Season is null ? VideoKind.Movie : VideoKind.Series;
        if (!CanMatch(context.Option, release))
            return null;
        if (series is not null)
            return await Describe(context, series, request).ConfigureAwait(false);
        var matches = Matches(
            release,
            (await Search(context, release.Title).ConfigureAwait(false)).Choices
        );
        if (matches.Length != 1)
        {
            if (context.Option.IsOnline)
                await store
                    .RejectSearch(SearchRequest(context, release.Title).Key)
                    .ConfigureAwait(false);
            return null;
        }
        var match = matches[0];
        var video = await Describe(context, match, request).ConfigureAwait(false);
        var year = kind == VideoKind.Movie ? video.Year ?? match.Year : match.Year ?? video.Year;
        if (
            FileName.Normalize(video.Title) == FileName.Normalize(match.Title)
            && (release.Year is null || year == release.Year)
        )
            return video;
        if (context.Option.IsOnline)
            await store
                .RejectSearch(SearchRequest(context, release.Title).Key)
                .ConfigureAwait(false);
        return null;
    }

    private async Task<VideoSearch> Search(
        Context context,
        string query,
        int? year = null,
        bool automatic = true
    )
    {
        var queries = Queries(context, query, year, automatic).ToArray();
        for (var index = 0; index < queries.Length; index++)
        {
            context.Token.ThrowIfCancellationRequested();
            var answer = await SearchOne(context, queries[index].Text, queries[index].Year)
                .ConfigureAwait(false);
            if (!answer.Empty || index == queries.Length - 1)
                return answer with { Broadened = year is not null && queries[index].Year is null };
        }
        return new([], false);
    }

    private static IEnumerable<(string Text, int? Year)> Queries(
        Context context,
        string query,
        int? year = null,
        bool automatic = true
    )
    {
        query = context.Provider.Prepare(query);
        if (query.Length == 0)
            yield break;
        yield return (query, year);
        if (!automatic)
            yield break;
        if (year is not null)
            yield return (query, null);
        if (context.Access is null)
            yield break;
        var plain = context.Provider.Prepare(string.Join(' ', Words(query)));
        if (plain.Length > 0 && plain != query)
            yield return (plain, null);
    }

    private static string[] Words(string value) =>
        Regex.Split(value, @"[^\p{L}\p{N}]+").Where(word => word.Length > 0).ToArray();

    private async Task<VideoSearch> SearchOne(Context context, string query, int? year = null)
    {
        if (context.Access is not { } access)
            return await context
                .Provider.Search(
                    new(query, context.Language, Offline) { Year = year },
                    context.Token
                )
                .ConfigureAwait(false);
        var request = SearchRequest(context, query, year);
        var since = context.Scope == FetchScope.New ? 0 : context.Accepted ?? 0;
        if (
            await store.Search(request.Key, context.Token, since).ConfigureAwait(false) is { } saved
        )
            return new(
                saved,
                saved.Count == 0
                    && (await policy.Read(request, context.Token).ConfigureAwait(false))?.Kind
                        == RequestBlockKind.Missing
            );
        if (context.Scope == FetchScope.New || context.Command is null)
        {
            if (await policy.Read(request, context.Token).ConfigureAwait(false) is { } block)
                throw new RequestBlockedException(block);
        }
        VideoSearch answer;
        try
        {
            answer = await context
                .Provider.Search(
                    new(query, context.Language, Get(context, "search", context.Fresh(request.Key)))
                    {
                        Year = year,
                    },
                    context.Token
                )
                .ConfigureAwait(false);
        }
        catch (VideoException error) when (error is not ProviderException)
        {
            var hold = await policy
                .Fail(access with { Operation = "search" }, new(RequestBlockKind.Backoff))
                .ConfigureAwait(false);
            throw new ProviderException(VideoFailure.InvalidResult, hold.RetryAt, error);
        }
        await store.SaveSearch(request, answer, CancellationToken.None).ConfigureAwait(false);
        await policy.Succeed(access with { Operation = "search" }).ConfigureAwait(false);
        await Recovered(context, access).ConfigureAwait(false);
        context.Token.ThrowIfCancellationRequested();
        return answer;
    }

    private static LookupRequest SearchRequest(Context context, string query, int? year = null)
    {
        var option = context.Option;
        query = context.Provider.Prepare(query);
        var parts = new List<string>
        {
            query,
            option.IsLocalized ? context.Language : string.Empty,
        };
        if (year is { } requested)
            parts.Add(requested.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new(
            RequestKey.Create(option.ProviderId, "search", parts),
            RequestKey.Create(
                option.ProviderId,
                "search-count",
                [FileName.Normalize(string.Join(' ', Words(query)))]
            )
        );
    }

    // Sends an online provider's request with the protection of its key and
    // the operation Library runs; a 404 for a detail means the video is gone.
    private ProviderGet Get(Context context, string operation, FreshRequest? fresh) =>
        context.Access is not { } access
            ? Offline
            : (message, cancellation, refusal) =>
                ProviderJson.Get(
                    http,
                    new ProviderCall(message, policy, new(access with { Operation = operation }))
                    {
                        Fresh = fresh,
                    },
                    cancellation,
                    missingResource: operation == "detail",
                    refusal
                );

    // A provider that answers from downloaded data sends no request.
    private static readonly ProviderGet Offline = (_, _, _) =>
        throw new InvalidOperationException();

    private async Task<VideoRecord> Read(
        Context context,
        string path,
        Func<CancellationToken, Task<VideoRecord>> download,
        bool refresh,
        CancellationToken cancellation
    )
    {
        var catalog = context.Option.ProviderId;
        var language = context.Option.IsLocalized ? context.Language : null;
        var request = new LookupRequest(
            RequestKey.Create(catalog, "detail", [path, language ?? string.Empty])
        );
        var since = context.Scope == FetchScope.New ? 0 : context.Command?.Accepted ?? 0;
        if (
            await store
                .Record(
                    catalog,
                    path,
                    language,
                    cancellation,
                    refresh ? context.Accepted ?? long.MaxValue : 0
                )
                .ConfigureAwait(false) is
            { } saved
        )
            return saved;
        if (await policy.Read(request, cancellation, since).ConfigureAwait(false) is { } block)
        {
            context.BlockedRequest = request.Key;
            throw new RequestBlockedException(block);
        }
        VideoRecord record;
        try
        {
            record = await download(cancellation).ConfigureAwait(false);
        }
        catch (VideoException error) when (error is not ProviderException)
        {
            var outcome =
                error is MissingVideoException ? RequestOutcome.Missing : RequestOutcome.Unusable;
            await policy.Record(request, outcome).ConfigureAwait(false);
            context.BlockedRequest = request.Key;
            throw new RequestBlockedException(
                new(
                    outcome == RequestOutcome.Missing
                        ? RequestBlockKind.Missing
                        : RequestBlockKind.Unusable
                )
            );
        }
        await store.SaveRecord(request, record, language).ConfigureAwait(false);
        if (context.Access is { } access)
        {
            await policy.Succeed(access with { Operation = "detail" }).ConfigureAwait(false);
            await Recovered(context, access).ConfigureAwait(false);
        }
        return record;
    }

    private async Task Recovered(Context context, ProviderAccess access)
    {
        if (context.Command?.Recheck == true)
            await policy.Succeed(access).ConfigureAwait(false);
        lock (_gate)
            if (ReferenceEquals(Provider, context.Provider))
            {
                _failure = null;
                _recheck = null;
            }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<VideoInformation> Describe(
        Context context,
        VideoChoice choice,
        VideoRequest request
    )
    {
        var video = await context
            .Provider.Describe(choice, request, context.Token)
            .ConfigureAwait(false);
        await store.SaveChoices(video, CancellationToken.None).ConfigureAwait(false);
        context.Token.ThrowIfCancellationRequested();
        return video;
    }

    // A season without an episode number names no episode a provider can describe.
    private static bool CanMatch(ProviderOption option, Release release) =>
        !release.IsExtra
        && release.Title.Length > 0
        && option.Kinds.Contains(release.Season is null ? VideoKind.Movie : VideoKind.Series)
        && (release.Season is null || release.Episodes.Count > 0);

    private static VideoChoice[] Matches(Release release, IEnumerable<VideoChoice> choices) =>
        choices
            .Where(choice =>
                choice.Kind == (release.Season is null ? VideoKind.Movie : VideoKind.Series)
                && Confidence(release, choice) > 0.8
            )
            .ToArray();

    private static double Confidence(Release release, VideoChoice choice)
    {
        if (release.Year is { } year && choice.Year is { } candidate && year != candidate)
            return 0;
        var title = Words(FileName.Normalize(release.Title)).ToHashSet();
        var numbers = title.Where(word => word.All(char.IsDigit)).ToHashSet();
        double Similarity(string value)
        {
            var words = Words(FileName.Normalize(value)).ToHashSet();
            if (!numbers.SetEquals(words.Where(word => word.All(char.IsDigit))))
                return 0;
            return title.Count + words.Count == 0
                ? 0
                : 2.0 * title.Intersect(words).Count() / (title.Count + words.Count);
        }
        return Math.Max(
            Similarity(choice.Title),
            choice.Original is { } original ? Similarity(original) : 0
        );
    }

    private readonly HashSet<VideoTarget> _cancelled = [];

    private async Task Run(Context context, CancellationTokenSource cancellation)
    {
        BackgroundWork.Operation? operation = null;
        var signalling = Task.CompletedTask;
        var ended = false;
        var done = 0;
        var found = 0;
        var total = 0;
        var observation = new WorkObservation(
            new WorkDescription.Metadata(MetadataScope.Automatic),
            new WorkProgress.Unknown(),
            new WorkExecution.Waiting()
        );
        try
        {
            await settled.WaitAsync(context.Token).ConfigureAwait(false);
            lock (_gate)
                _again = false;
            var pending = await store
                .Pending(context.Option.ProviderId, context.Token)
                .ConfigureAwait(false);
            VideoTarget[] accepted;
            lock (_gate)
            {
                accepted = pending.Where(target => !_cancelled.Contains(target)).ToArray();
            }
            if (accepted.Length == 0)
                return;
            total = accepted.Length;
            lock (_gate)
            {
                operation = Work.Begin(
                    observation,
                    () =>
                    {
                        lock (_gate)
                        {
                            if (!ended && !cancellation.IsCancellationRequested)
                            {
                                foreach (var target in accepted.Skip(done))
                                    _cancelled.Add(target);
                                observation = observation with
                                {
                                    Execution = new WorkExecution.Cancelling(),
                                };
                                operation?.Report(observation);
                                signalling = cancellation.CancelAsync();
                            }
                            return signalling;
                        }
                    }
                );
            }
            foreach (var target in accepted)
            {
                while (true)
                {
                    context.Token.ThrowIfCancellationRequested();
                    Task? batch;
                    lock (_gate)
                    {
                        batch = _batchRunning ? _batchWork : null;
                        observation = observation with
                        {
                            Progress = new WorkProgress.Fraction(
                                done,
                                accepted.Length,
                                WorkUnit.Files
                            ),
                            Execution = new WorkExecution.Waiting(),
                        };
                        if (!cancellation.IsCancellationRequested)
                            operation.Report(observation);
                    }
                    if (batch is not null)
                    {
                        await batch.WaitAsync(context.Token).ConfigureAwait(false);
                        continue;
                    }
                    DateTimeOffset? retry;
                    try
                    {
                        var identified = await Execute(
                                context,
                                current => Collect(current, target, null, false),
                                execution =>
                                {
                                    lock (_gate)
                                    {
                                        if (ended || cancellation.IsCancellationRequested)
                                            return;
                                        observation = observation with { Execution = execution };
                                        operation.Report(observation);
                                    }
                                }
                            )
                            .ConfigureAwait(false);
                        lock (_gate)
                        {
                            done++;
                            if (identified)
                                found++;
                            else if (context.Block is { Kind: RequestBlockKind.Limit } block)
                                operation.Error = new RequestBlockedException(block);
                        }
                        break;
                    }
                    catch (ProviderException error) when (error.RetryAt is not null)
                    {
                        retry = error.RetryAt;
                    }
                    catch (RequestBlockedException error) when (error.Block.RetryAt is not null)
                    {
                        retry = error.Block.RetryAt;
                    }
                    lock (_gate)
                    {
                        if (!cancellation.IsCancellationRequested)
                        {
                            observation = observation with
                            {
                                Execution = new WorkExecution.Waiting { RetryAt = retry },
                            };
                            operation.Report(observation);
                        }
                    }
                    var delay = retry.Value - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(
                                delay < TimeSpan.FromDays(1) ? delay : TimeSpan.FromDays(1),
                                context.Token
                            )
                            .ConfigureAwait(false);
                }
            }
            operation.Outcome = operation.Error is null ? WorkOutcome.Completed : WorkOutcome.Failed;
        }
        catch (OperationCanceledException)
        {
            if (operation is not null)
                operation.Outcome = WorkOutcome.Cancelled;
            System.Diagnostics.Debug.WriteLine(
                $"Automatic metadata lookup cancelled ({context.Option.ProviderId})."
            );
        }
        catch (ProviderException error)
        {
            if (operation is not null)
                operation.Error = error;
        }
        catch (RequestBlockedException error)
        {
            if (operation is not null)
                operation.Error = error;
        }
        catch (Exception error)
        {
            if (operation is not null)
                operation.Error = error;
            Failed?.Invoke(this, error);
        }
        finally
        {
            lock (_gate)
            {
                ended = true;
                if (ReferenceEquals(_background, cancellation))
                    _background = null;
            }
            try
            {
                await signalling.ConfigureAwait(false);
            }
            finally
            {
                cancellation.Dispose();
                operation?.Summarize("metadata_result", found, done, total);
                operation?.Dispose();
                lock (_gate)
                    if (_again)
                        Refresh(_connected, _language);
            }
        }
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
            _background?.Cancel();
            requests = [.. _requests, _work, _batchWork];
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
        internal RequestBlock? Block { get; set; }
        internal RequestKey? BlockedRequest { get; set; }
        internal FetchScope Scope { get; init; }
        internal long? Accepted { get; init; }
        internal RetryCommand? Command { get; init; }

        internal FreshRequest? Fresh(RequestKey key) =>
            Command is { } command && (Scope != FetchScope.New || command.Recheck)
                ? new(command, key)
                : null;
    }
}

internal sealed record VideoFetch(Entry Entry, ProviderAnswer Answer, RequestBlock? Wait);
