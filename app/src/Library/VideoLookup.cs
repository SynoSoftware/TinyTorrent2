using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

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
    private readonly Queue<BatchItem> _batch = [];
    private readonly SemaphoreSlim _batchWake = new(0, 1);
    private readonly HashSet<long> _batched = [];
    private CancellationTokenSource _batchStop = new();
    private Task _batchWork = Task.CompletedTask;
    private int _batchDone;
    private bool _batchRunning;
    private CancellationTokenSource _context = new();
    private CancellationTokenSource? _background;
    private Task _work = Task.CompletedTask;
    private int _interactive;
    private string _language = "en";
    private bool _connected;
    private bool _closed;
    private bool _again;
    internal bool Enabled { get; private set; }
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
    internal bool IsAvailable
    {
        get
        {
            lock (_gate)
                return Option is { } option && Available(option);
        }
    }

    /// <summary>The files Fetch metadata is identifying, or null when it is idle.</summary>
    internal VideoBatch? Batch
    {
        get
        {
            lock (_gate)
                return _batch.TryPeek(out var item)
                    ? new VideoBatch(_batchDone, _batchDone + _batch.Count, item.Source)
                    : null;
        }
    }

    /// <summary>
    /// Raised when the provider, its failure or lookups being on changes, and when
    /// the batch grows, stops or ends. A file the batch finishes raises <see cref="Stored"/>.
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
            var enabled = await store.Enabled(cancellation).ConfigureAwait(false);
            await policy.Initialize(cancellation).ConfigureAwait(false);
            var keys = await store.Keys(cancellation).ConfigureAwait(false);
            var option = selected is null
                ? options.FirstOrDefault()
                : options.FirstOrDefault(option => option.ProviderId == selected);
            // A saved provider that this version lacks turns lookups off.
            if (enabled && option is null)
                await store.DisableProvider(cancellation).ConfigureAwait(false);
            lock (_gate)
            {
                _keys = keys;
                Option = option;
                Provider = option is null ? null : option.Create(Key(option));
                Enabled = enabled && option is not null;
                _language = language;
            }
        });

    /// <summary>
    /// Saves <paramref name="providerId"/> as the provider and turns lookups on.
    /// An unavailable provider changes nothing and throws.
    /// </summary>
    internal Task Enable(string providerId, bool preserveBatch = false) =>
        Change(cancellation => Select(
            options.FirstOrDefault(option => option.ProviderId == providerId)
                ?? throw new ArgumentOutOfRangeException(nameof(providerId)), cancellation), preserveBatch);

    private async Task Select(ProviderOption option, CancellationToken cancellation)
    {
        IProvider provider;
        lock (_gate)
        {
            if (!Available(option))
                throw new VideoException(VideoFailure.ProviderUnavailable) { Provider = option };
            provider = option == Option && Provider is { } current ? current : option.Create(Key(option));
        }
        await store.EnableProvider(option.ProviderId, cancellation).ConfigureAwait(false);
        lock (_gate)
        {
            if (!ReferenceEquals(Provider, provider))
            {
                _failure = null;
                _recheck = null;
            }
            Option = option;
            Provider = provider;
            Enabled = true;
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
        Change(async cancellation =>
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
        });

    /// <summary>
    /// Runs <paramref name="replace"/> while no lookup reads the provider's
    /// downloaded data, such as a new IMDb data file replacing the old one.
    /// Clears local attempts so unresolved files can use the new dataset.
    /// </summary>
    internal Task Reload(ProviderOption option, Action replace, Func<bool>? enable = null) =>
        Change(async cancellation =>
        {
            // Replacing a file can wait for a read in progress, so it never
            // runs on the calling window thread.
            await Task.Run(replace).ConfigureAwait(false);
            lock (_gate)
                Restart(option);
            await store.ClearAttempts(option.ProviderId, cancellation).ConfigureAwait(false);
            if (enable?.Invoke() == true)
                await Select(option, cancellation).ConfigureAwait(false);
        }, preserveBatch: true);

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
            Access = option.IsOnline ? new(option.ProviderId, RequestKey.Digest(key), string.Empty) : null,
        };
    }

    internal Task Disable(bool preserveBatch = false) =>
        Change(async cancellation =>
        {
            await store.DisableProvider(cancellation).ConfigureAwait(false);
            lock (_gate)
            {
                Enabled = false;
                Resume();
            }
        }, preserveBatch);

    internal Task Suspend(Func<Task> apply) => Change(async cancellation =>
    {
        await apply().ConfigureAwait(false);
        Context? current;
        lock (_gate)
            current = Option is { } option ? Bind(option, cancellation) : null;
        if (current is { Access: not null }
            && await AccessProtection(current, cancellation).ConfigureAwait(false) is null)
            lock (_gate)
                _failure = null;
    }, preserveBatch: true);

    private async Task Change(Func<CancellationToken, Task> apply, bool preserveBatch = false)
    {
        await _changes.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                CancelContext(preserveBatch);
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
            _changes.Release();
            lock (_gate)
            {
                // Reset can release a hold before its scheduled retry.
                if (preserveBatch)
                    for (var remaining = _batch.Count; remaining > 0; remaining--)
                        _batch.Enqueue(_batch.Dequeue() with { RetryAt = null });
                StartBatch();
                Refresh(_connected, _language);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // A changed provider, switch, route, connection or language ends the
    // batch too, wherever its loop is.
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
                || !Enabled
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
    /// saved choices of the same title, which answer alone when the provider
    /// fails or is waiting.
    /// </summary>
    internal Task<IReadOnlyList<VideoChoice>> Search(
        string query,
        string fileName,
        ProviderOption source,
        CancellationToken cancellation,
        FetchScope scope = FetchScope.New
    ) =>
        Request<IReadOnlyList<VideoChoice>>(
            async context =>
            {
                var saved = await store
                    .Choices(context.Option.ProviderId, query, context.Token)
                    .ConfigureAwait(false);
                IReadOnlyList<VideoChoice> found;
                try
                {
                    found = await Execute(
                            context,
                            async current =>
                            {
                                var choices = await Search(current, query).ConfigureAwait(false);
                                if (Matches(FileName.Interpret(fileName), choices).Length != 1)
                                    await store.RejectSearch(SearchRequest(current, query).Key).ConfigureAwait(false);
                                return choices;
                            }
                        )
                        .ConfigureAwait(false);
                }
                catch (ProviderException) when (scope == FetchScope.New && saved.Count > 0)
                {
                    return saved;
                }
                return
                [
                    .. found,
                    .. saved.Where(choice => found.All(result => result.VideoId != choice.VideoId)),
                ];
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
    /// Identifies <paramref name="entry"/> with the saved provider, reading it
    /// again when it has metadata. Returns false when no confident match exists.
    /// </summary>
    internal Task<bool> Lookup(Entry entry, CancellationToken cancellation) =>
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
                        )
                ),
            cancellation
        );

    internal async Task<RequestBlock?> Protection(string query, string fileName, ProviderOption source, CancellationToken cancellation)
    {
        Context context;
        lock (_gate)
            context = Bind(source, cancellation);
        return await AccessProtection(context, cancellation).ConfigureAwait(false)
            ?? await SearchProtection(SearchRequest(context, query), FileName.Interpret(fileName), cancellation).ConfigureAwait(false);
    }

    private async Task<RequestBlock?> SearchProtection(LookupRequest request, Release release, CancellationToken cancellation)
    {
        var saved = await store.Search(request.Key, cancellation).ConfigureAwait(false);
        if (saved is null || saved.Count == 0)
            return await policy.Read(request, cancellation).ConfigureAwait(false);
        return Matches(release, saved).Length == 1 ? null : new(RequestBlockKind.NoMatch);
    }

    private Task<RequestBlock?> AccessProtection(Context context, CancellationToken cancellation) =>
        context.Access is { } access
            ? policy.Read([access with { Operation = "search" }, access with { Operation = "detail" }], cancellation)
            : Task.FromResult<RequestBlock?>(null);

    internal async Task<IReadOnlyList<VideoFetch>> Plan(IReadOnlyCollection<Entry> entries, ProviderOption source, CancellationToken cancellation)
    {
        if (!source.IsOnline)
            return [];
        Context context;
        lock (_gate)
            context = Bind(source, cancellation);
        var candidates = await store.Fetchable(entries, source.ProviderId, cancellation).ConfigureAwait(false);
        var wait = await AccessProtection(context, cancellation).ConfigureAwait(false);
        var result = new List<VideoFetch>();
        foreach (var candidate in candidates)
        {
            var entry = candidate.Entry;
            var release = FileName.Interpret(entry.Name);
            if (!entry.HasMetadata && !CanMatch(source, release))
                continue;
            var request = SearchRequest(context, release.Title);
            var block = candidate.Block ?? await SearchProtection(request, release, cancellation).ConfigureAwait(false);
            result.Add(new(entry, block, wait));
        }
        return result;
    }

    /// <summary>
    /// Adds <paramref name="entries"/> to the batch, which identifies them one
    /// at a time with <paramref name="source"/>. Automatic lookups wait until
    /// the batch ends. A file already waiting is not added again.
    /// </summary>
    internal void Fetch(IReadOnlyCollection<Entry> entries, ProviderOption source, FetchScope scope = FetchScope.New)
    {
        if (!source.IsOnline)
            throw new VideoException(VideoFailure.ProviderUnavailable) { Provider = source };
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var command = scope == FetchScope.New ? null : new RetryCommand(DateTimeOffset.UtcNow.UtcTicks);
            foreach (var entry in entries)
                if (_batched.Add(entry.EntryId))
                    _batch.Enqueue(new(entry, source, scope, command));
            if (_batchWake.CurrentCount == 0)
                _batchWake.Release();
            // The background sweep finishes its current file, so a page read
            // already under way is not wasted, and then waits for the batch.
            StartBatch();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StartBatch()
    {
        if (!_closed && _connected && _changes.CurrentCount > 0 && !_batchRunning && _batch.Count > 0)
        {
            _batchRunning = true;
            _batchWork = Task.Run(RunBatch);
        }
    }

    /// <summary>Empties the batch and cancels its request in progress.</summary>
    internal void StopBatch()
    {
        lock (_gate)
            EndBatch();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EndBatch()
    {
        ClearBatch();
        _batchStop.Cancel();
        _batchStop.Dispose();
        _batchStop = new();
    }

    private void ClearBatch()
    {
        _batch.Clear();
        _batched.Clear();
        _batchDone = 0;
    }

    private async Task RunBatch()
    {
        var ended = false;
        var limited = 0;
        while (true)
        {
            BatchItem? item;
            DateTimeOffset? next = null;
            CancellationToken stop;
            CancellationTokenSource request;
            Context context;
            // The batch ends under the same lock that finds it empty, so a
            // Fetch either reaches this loop or starts the next one.
            lock (_gate)
            {
                if (
                    ended
                    || _closed
                    || !_connected
                    || _changes.CurrentCount == 0
                    || !_batch.TryPeek(out item)
                )
                {
                    if (_changes.CurrentCount != 0)
                        ClearBatch();
                    _batchRunning = false;
                    Refresh(_connected, _language);
                    break;
                }
                stop = _batchStop.Token;
                request = CancellationTokenSource.CreateLinkedTokenSource(
                    stop,
                    _lifetime.Token,
                    _context.Token
                );
                var now = DateTimeOffset.UtcNow;
                for (var remaining = _batch.Count; remaining > 0; remaining--)
                {
                    item = _batch.Peek();
                    if (item.RetryAt is not { } due || due <= now)
                    {
                        next = null;
                        break;
                    }
                    next = next is null || due < next ? due : next;
                    _batch.Enqueue(_batch.Dequeue());
                }
                context = Bind(item.Source, request.Token) with
                {
                    Scope = item.Scope,
                    Accepted = item.Accepted,
                    Command = item.Command,
                };
            }
            bool identified;
            using (request)
            {
                if (next is { } due)
                {
                    var delay = due - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        try
                        {
                            await _batchWake.WaitAsync(delay < TimeSpan.FromDays(1) ? delay : TimeSpan.FromDays(1), request.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { }
                    }
                    continue;
                }
                try
                {
                    identified = await Execute(
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
                                )
                        )
                        .ConfigureAwait(false);
                }
                // StopBatch has emptied the queue, which may since hold new
                // files; those start a new count.
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    if (limited > 0)
                        BatchLimited?.Invoke(this, limited);
                    limited = 0;
                    continue;
                }
                catch (OperationCanceledException) when (_changes.CurrentCount == 0)
                {
                    continue;
                }
                catch (Exception error) when (error is ProviderException { RetryAt: not null }
                    or RequestBlockedException { Block.RetryAt: not null })
                {
                    var retry = error is ProviderException providerError
                        ? providerError.RetryAt : ((RequestBlockedException)error).Block.RetryAt;
                    lock (_gate)
                        if (_batch.TryPeek(out var failed) && failed.Entry.EntryId == item.Entry.EntryId)
                        {
                            _batch.Dequeue();
                            _batch.Enqueue(error is ProviderException
                                ? failed with { RetryAt = retry, Command = null }
                                : failed with { RetryAt = retry });
                        }
                    continue;
                }
                // A file's own failure is a miss, so what reaches here is a
                // provider that cannot serve any file, or a changed provider,
                // route, connection or language, which cancels the batch.
                catch (Exception error)
                {
                    ended = true;
                    if (error is not OperationCanceledException)
                        BatchFailed?.Invoke(this, error);
                    continue;
                }
            }
            lock (_gate)
                if (!stop.IsCancellationRequested)
                {
                    _batch.Dequeue();
                    _batched.Remove(item.Entry.EntryId);
                    _batchDone++;
                    if (!identified && context.Block?.Kind == RequestBlockKind.Limit)
                        limited++;
                }
            Stored?.Invoke(this, EventArgs.Empty);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (limited > 0)
            BatchLimited?.Invoke(this, limited);
    }

    private sealed record BatchItem(Entry Entry, ProviderOption Source, FetchScope Scope, RetryCommand? Command)
    {
        internal long? Accepted { get; } = Command?.Accepted;
        internal DateTimeOffset? RetryAt { get; init; }
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
            // Choosing a provider in Fetch metadata is the person's request,
            // which Off does not stop; Off stops only the downloads Library
            // starts itself.
            if (
                !_connected
                || option is not { IsOnline: true }
                || _changes.CurrentCount == 0
                || !Enabled && source is null
            )
                throw new VideoException(VideoFailure.LookupStopped);
            var request = CancellationTokenSource.CreateLinkedTokenSource(
                cancellation,
                _lifetime.Token,
                _context.Token
            );
            _interactive++;
            _background?.Cancel();
            var bound = Bind(option, request.Token);
            var command = scope == FetchScope.New && ReferenceEquals(bound.Provider, Provider) && _recheck is not null
                ? _recheck : new RetryCommand(DateTimeOffset.UtcNow.UtcTicks);
            var context = bound with
            {
                Scope = scope,
                Accepted = command.Accepted,
                Command = command,
            };
            var task = Perform();
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

    private async Task<T> Execute<T>(Context context, Func<Context, Task<T>> action)
    {
        await _access.WaitAsync(context.Token).ConfigureAwait(false);
        try
        {
            context.Token.ThrowIfCancellationRequested();
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
            if (error.Block.Kind is RequestBlockKind.Attention or RequestBlockKind.RemoteWait
                or RequestBlockKind.Allowance or RequestBlockKind.Backoff)
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
            catch (RequestBlockedException error) when (target.Kind != LookupKind.Explicit
                && error.Block.Kind is RequestBlockKind.Missing or RequestBlockKind.Unusable or RequestBlockKind.Limit or RequestBlockKind.NoMatch)
            {
                context.Block = error.Block;
            }
            // An individual unusable record must not stop unrelated lookups.
            catch (VideoException error)
                when (error is not ProviderException && target.Kind != LookupKind.Explicit)
            {
                unanswered = true;
            }
            var outcome =
                video is not null ? "complete"
                : context.Block is { Kind: not RequestBlockKind.Missing } ? "blocked"
                : unanswered ? "unanswered"
                : "no_match";
            accepted = await store
                .Complete(attempt with { BlockedRequest = context.BlockedRequest }, video, outcome, context.Token)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A provider that cannot serve any request has not answered for
            // this file, so the attempt leaves it eligible for a later lookup.
            var outcome =
                context.Token.IsCancellationRequested
                || error is OperationCanceledException or ProviderException or RequestBlockedException
                    ? "cancelled"
                    : "failed";
            await store
                .Complete(attempt with { BlockedRequest = context.BlockedRequest }, null, outcome, CancellationToken.None)
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
        var matches = Matches(release, await Search(context, release.Title).ConfigureAwait(false));
        if (matches.Length != 1)
        {
            if (context.Option.IsOnline)
                await store.RejectSearch(SearchRequest(context, release.Title).Key).ConfigureAwait(false);
            return null;
        }
        var match = matches[0];
        var video = await Describe(context, match, request).ConfigureAwait(false);
        var year = kind == VideoKind.Movie ? video.Year ?? match.Year : match.Year ?? video.Year;
        if (FileName.Normalize(video.Title) == FileName.Normalize(match.Title)
            && (release.Year is null || year == release.Year))
            return video;
        if (context.Option.IsOnline)
            await store.RejectSearch(SearchRequest(context, release.Title).Key).ConfigureAwait(false);
        return null;
    }

    private async Task<IReadOnlyList<VideoChoice>> Search(
        Context context,
        string query
    )
    {
        query = context.Provider.Prepare(query);
        if (query.Length == 0)
            return [];
        if (context.Access is not { } access)
            return (await context.Provider.Search(new(query, context.Language, Offline), context.Token).ConfigureAwait(false)).Choices;
        var request = SearchRequest(context, query);
        var since = context.Scope == FetchScope.New ? 0 : context.Accepted ?? 0;
        if (await store.Search(request.Key, context.Token, since).ConfigureAwait(false) is { } saved)
            return saved;
        if (context.Scope == FetchScope.New || context.Command is null)
        {
            if (await policy.Read(request, context.Token).ConfigureAwait(false) is { } block)
                throw new RequestBlockedException(block);
        }
        VideoSearch answer;
        try
        {
            answer = await context.Provider
                .Search(new(query, context.Language, Get(context, "search", context.Fresh(request.Key))), context.Token)
                .ConfigureAwait(false);
        }
        catch (VideoException error) when (error is not ProviderException)
        {
            var hold = await policy.Fail(access with { Operation = "search" },
                new(RequestBlockKind.Backoff)).ConfigureAwait(false);
            throw new ProviderException(VideoFailure.InvalidResult, hold.RetryAt, error);
        }
        await store
            .SaveSearch(request, answer, CancellationToken.None)
            .ConfigureAwait(false);
        await policy.Succeed(access with { Operation = "search" }).ConfigureAwait(false);
        await Recovered(context, access).ConfigureAwait(false);
        context.Token.ThrowIfCancellationRequested();
        return answer.Choices;
    }

    private static LookupRequest SearchRequest(Context context, string query)
    {
        var option = context.Option;
        query = context.Provider.Prepare(query);
        return new(
            RequestKey.Create(option.ProviderId, "search", [query, option.IsLocalized ? context.Language : string.Empty]),
            RequestKey.Create(option.ProviderId, "search-count", [FileName.Normalize(query)])
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
                    new ProviderCall(message, policy, new(access with { Operation = operation })) { Fresh = fresh },
                    cancellation,
                    missingResource: operation == "detail",
                    refusal
                );

    // A provider that answers from downloaded data sends no request.
    private static readonly ProviderGet Offline = (_, _, _) => throw new InvalidOperationException();

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
        var request = new LookupRequest(RequestKey.Create(catalog, "detail", [path, language ?? string.Empty]));
        var since = context.Scope == FetchScope.New ? 0 : context.Command?.Accepted ?? 0;
        if (await store.Record(catalog, path, language, cancellation,
                refresh ? context.Accepted ?? long.MaxValue : 0).ConfigureAwait(false) is { } saved)
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
            var outcome = error is MissingVideoException ? RequestOutcome.Missing : RequestOutcome.Unusable;
            await policy.Record(request, outcome)
                .ConfigureAwait(false);
            context.BlockedRequest = request.Key;
            throw new RequestBlockedException(new(outcome == RequestOutcome.Missing ? RequestBlockKind.Missing : RequestBlockKind.Unusable));
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
        !release.IsExtra && release.Title.Length > 0
        && option.Kinds.Contains(release.Season is null ? VideoKind.Movie : VideoKind.Series)
        && (release.Season is null || release.Episodes.Count > 0);

    private static VideoChoice[] Matches(Release release, IEnumerable<VideoChoice> choices) =>
        choices.Where(choice => choice.Kind == (release.Season is null ? VideoKind.Movie : VideoKind.Series)
            && Confidence(release, choice) > 0.8).ToArray();

    private static double Confidence(Release release, VideoChoice choice)
    {
        if (release.Year is { } year && choice.Year is { } candidate && year != candidate)
            return 0;
        var title = FileName
            .Normalize(release.Title)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();
        var numbers = title.Where(word => word.All(char.IsDigit)).ToHashSet();
        double Similarity(string value)
        {
            var words = FileName
                .Normalize(value)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet();
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

    private async Task Run(Context context, CancellationTokenSource cancellation)
    {
        try
        {
            await settled.WaitAsync(context.Token).ConfigureAwait(false);
            while (true)
            {
                context.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    // A batch started since; it restarts this sweep when it ends.
                    if (_batchRunning)
                        return;
                    _again = false;
                }
                var target = await store
                    .Next(context.Option.ProviderId, context.Token)
                    .ConfigureAwait(false);
                if (target is null)
                    return;
                try
                {
                    await Execute(context, current => Collect(current, target, null, false))
                        .ConfigureAwait(false);
                }
                catch (ProviderException error) when (error.RetryAt is { } retry)
                {
                    var delay = retry - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay < TimeSpan.FromDays(1) ? delay : TimeSpan.FromDays(1), context.Token).ConfigureAwait(false);
                }
                catch (RequestBlockedException error) when (error.Block.RetryAt is { } retry)
                {
                    var delay = retry - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay < TimeSpan.FromDays(1) ? delay : TimeSpan.FromDays(1), context.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ProviderException) { }
        catch (RequestBlockedException) { }
        catch (Exception error)
        {
            Failed?.Invoke(this, error);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_background, cancellation))
                    _background = null;
                cancellation.Dispose();
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
        _batchStop.Dispose();
        _context.Dispose();
        _lifetime.Dispose();
    }

    private sealed record Context(ProviderOption Option, IProvider Provider, string Language, CancellationToken Token)
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
                ? new(command, key) : null;
    }
}

/// <summary>
/// Fetch metadata's progress: <see cref="Done"/> of <see cref="Total"/> files,
/// where <see cref="Total"/> includes the file in progress, from <see cref="Source"/>.
/// </summary>
internal sealed record VideoBatch(int Done, int Total, ProviderOption Source);

internal sealed record VideoFetch(Entry Entry, RequestBlock? Block, RequestBlock? Wait)
{
    internal bool Includes(FetchScope scope) => scope == FetchScope.All
        || !Entry.HasMetadata && (scope == FetchScope.Missing || Block?.CanForce != true);
}
