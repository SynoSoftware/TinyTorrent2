using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Library;

internal sealed class VideoLookup(Store store, Task settled, IReadOnlyList<ProviderOption> options)
    : IAsyncDisposable
{
    private readonly SemaphoreSlim _access = new(1);
    private readonly SemaphoreSlim _changes = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _requests = [];
    private readonly Dictionary<string, ProviderException> _failures = [];
    private readonly Dictionary<
        (string Catalog, string Query, string Language),
        IReadOnlyList<VideoChoice>
    > _searches = [];
    private readonly Queue<(Entry Entry, ProviderOption Source)> _batch = [];
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
    internal int LookupLimit { get; private set; } = Store.DefaultLookupLimit;
    internal IReadOnlyList<ProviderOption> Options => options;
    internal ProviderOption? Option { get; private set; }
    internal IProvider? Provider { get; private set; }
    internal ProviderException? Failure
    {
        get
        {
            lock (_gate)
                return Provider is { } provider
                    ? _failures.GetValueOrDefault(provider.CatalogId)
                    : null;
        }
    }
    internal bool IsAvailable => Provider?.IsAvailable == true;

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
            var limit = await store.LookupLimit(cancellation).ConfigureAwait(false);
            var option = selected is null
                ? options.FirstOrDefault()
                : options.FirstOrDefault(option => option.ProviderId == selected);
            var provider = option?.Create();
            if (enabled && provider is null)
                await store.DisableProvider(cancellation).ConfigureAwait(false);
            lock (_gate)
            {
                Option = option;
                Provider = provider;
                Enabled = enabled && provider is not null;
                LookupLimit = limit;
                _language = language;
            }
        });

    /// <summary>
    /// Saves <paramref name="providerId"/> as the provider and turns lookups on.
    /// An unavailable provider changes nothing and throws.
    /// </summary>
    internal Task Enable(string providerId) =>
        Change(async cancellation =>
        {
            var option =
                options.FirstOrDefault(option => option.ProviderId == providerId)
                ?? throw new ArgumentOutOfRangeException(nameof(providerId));
            var provider = option == Option && Provider is { } current ? current : option.Create();
            if (!provider.IsAvailable)
                throw new VideoException("library", "provider_unavailable");
            await store.EnableProvider(providerId, cancellation).ConfigureAwait(false);
            lock (_gate)
            {
                Option = option;
                Provider = provider;
                Enabled = true;
                Resume();
            }
        });

    internal Task Disable() =>
        Change(async cancellation =>
        {
            await store.DisableProvider(cancellation).ConfigureAwait(false);
            lock (_gate)
            {
                Enabled = false;
                Resume();
            }
        });

    /// <summary>Saves the search limit that Library and subtitles share; later searches use it.</summary>
    internal async Task SaveLookupLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await store.SaveLookupLimit(limit, _lifetime.Token).ConfigureAwait(false);
        LookupLimit = limit;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task Change(Func<CancellationToken, Task> apply)
    {
        await _changes.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                CancelContext();
            }
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
                Refresh(_connected, _language);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // A changed provider, switch, route, connection or language ends the
    // batch too, wherever its loop is.
    private void CancelContext()
    {
        _context.Cancel();
        _context.Dispose();
        _context = new();
        _background?.Cancel();
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
                || Provider
                    is not { Schedule: VideoSchedule.Background, IsAvailable: true } provider
                || _changes.CurrentCount == 0
                || Failure is { RetryAt: null }
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
            var context = new Context(provider, _language, cancellation.Token);
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
            Resume();
            Refresh(_connected, _language);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Resume()
    {
        if (Provider is not { } provider || Failure?.RetryAt > DateTimeOffset.UtcNow)
            return;
        _failures.Remove(provider.CatalogId);
    }

    /// <summary>
    /// Applies a new network route through <paramref name="configure"/>.
    /// Lookups on the old route are cancelled first, so their cancellation is
    /// not saved as a provider failure. Failures without a retry time are
    /// cleared, so lookups start again on the new route.
    /// </summary>
    internal void ChangeRoute(Action configure)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            CancelContext();
            configure();
            foreach (
                var (catalog, _) in _failures.Where(pair => pair.Value.RetryAt is null).ToArray()
            )
                _failures.Remove(catalog);
            Refresh(_connected, _language);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Searches <paramref name="source"/>, which may differ from the saved
    /// provider; the saved choice does not change. The results include the
    /// saved choices of the same title, which answer alone when the provider
    /// fails or is waiting.
    /// </summary>
    internal Task<IReadOnlyList<VideoChoice>> Search(
        string query,
        ProviderOption source,
        CancellationToken cancellation
    ) =>
        Request<IReadOnlyList<VideoChoice>>(
            async context =>
            {
                var saved = await store
                    .Choices(context.Provider.CatalogId, query, context.Token)
                    .ConfigureAwait(false);
                IReadOnlyList<VideoChoice> found;
                // The person searches again after an empty or failed answer, so
                // this search never reuses the session's earlier result.
                try
                {
                    found = await Execute(
                            context,
                            current => Search(current, query, reuse: false, limited: false)
                        )
                        .ConfigureAwait(false);
                }
                catch (ProviderException) when (saved.Count > 0)
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
            source
        );

    /// <summary>
    /// Saves <paramref name="choice"/>, a result of searching <paramref name="source"/>.
    /// Throws a <see cref="VideoException"/> when the file changed or a newer decision won.
    /// </summary>
    internal Task Identify(
        Entry entry,
        VideoChoice choice,
        ProviderOption source,
        CancellationToken cancellation
    ) =>
        Request(
            context =>
                Execute(
                    context,
                    current =>
                        Collect(
                            current,
                            new VideoTarget(entry, current.Provider.CatalogId, LookupKind.Explicit),
                            choice,
                            false
                        )
                ),
            cancellation,
            source
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
                            new VideoTarget(entry, current.Provider.CatalogId, LookupKind.Explicit),
                            null,
                            entry.HasMetadata
                        )
                ),
            cancellation
        );

    /// <summary>
    /// Adds <paramref name="entries"/> to the batch, which identifies them one
    /// at a time with <paramref name="source"/>. Automatic lookups wait until
    /// the batch ends. A file already waiting is not added again.
    /// </summary>
    internal void Fetch(IReadOnlyCollection<Entry> entries, ProviderOption source)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            foreach (var entry in entries)
                if (_batched.Add(entry.EntryId))
                    _batch.Enqueue((entry, source));
            // The background sweep finishes its current file, so a page read
            // already under way is not wasted, and then waits for the batch.
            if (!_batchRunning && _batch.Count > 0)
            {
                _batchRunning = true;
                _batchWork = Task.Run(RunBatch);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
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
            (Entry Entry, ProviderOption Source) item;
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
                context = new Context(
                    item.Source == Option && Provider is { } saved ? saved : item.Source.Create(),
                    _language,
                    request.Token
                );
            }
            bool identified;
            using (request)
            {
                try
                {
                    identified = await Execute(
                            context,
                            current =>
                                Collect(
                                    current,
                                    new VideoTarget(
                                        item.Entry,
                                        current.Provider.CatalogId,
                                        LookupKind.Batch
                                    ),
                                    null,
                                    false
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
                    if (!identified && context.Limited)
                        limited++;
                }
            Stored?.Invoke(this, EventArgs.Empty);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (limited > 0)
            BatchLimited?.Invoke(this, limited);
    }

    private Task<T> Request<T>(
        Func<Context, Task<T>> action,
        CancellationToken cancellation,
        ProviderOption? source = null
    )
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var provider = Provider;
            if (source is not null && (source != Option || provider is null))
                provider = source.Create();
            // Choosing a provider in Fetch metadata is the person's request,
            // which Off does not stop; Off stops only the downloads Library
            // starts itself.
            if (
                !_connected
                || provider is null
                || _changes.CurrentCount == 0
                || !Enabled && source is null
            )
                throw new VideoException("library", "lookup_stopped");
            var request = CancellationTokenSource.CreateLinkedTokenSource(
                cancellation,
                _lifetime.Token,
                _context.Token
            );
            _interactive++;
            _background?.Cancel();
            var context = new Context(provider, _language, request.Token);
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
                    throw new VideoException("library", "lookup_stopped");
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
            lock (_gate)
            {
                if (
                    _failures.GetValueOrDefault(context.Provider.CatalogId) is { } failure
                    && failure.RetryAt > DateTimeOffset.UtcNow
                )
                    throw failure;
            }
            if (!context.Provider.IsAvailable)
                throw new ProviderException("library", "provider_unavailable");
            var result = await action(context).ConfigureAwait(false);
            bool recovered;
            lock (_gate)
                recovered =
                    !context.Token.IsCancellationRequested
                    && ReferenceEquals(Provider, context.Provider)
                    && _failures.Remove(context.Provider.CatalogId);
            if (recovered)
                Changed?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (ProviderException error)
        {
            lock (_gate)
                if (
                    !context.Token.IsCancellationRequested
                    && ReferenceEquals(Provider, context.Provider)
                )
                    _failures[context.Provider.CatalogId] = error;
            Changed?.Invoke(this, EventArgs.Empty);
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
        var language = context.Provider.IsLocalized ? context.Language : null;
        // A refused explicit request says so, because its caller would
        // otherwise report that no match exists.
        var attempt = await store.Begin(target, language, context.Token).ConfigureAwait(false);
        if (attempt is null)
            return target.Kind == LookupKind.Explicit
                ? throw new VideoException("library", "file_changed")
                : false;
        VideoInformation? video = null;
        // Library remembers only a provider's own no-match answer; the
        // search limit and an unusable page end the lookup without one.
        var unanswered = false;
        context.Limited = false;
        bool accepted;
        try
        {
            refresh =
                refresh
                || target.Kind == LookupKind.Explicit
                    && choice is null
                    && attempt.Choice is not null;
            var request = new VideoRequest(
                FileName.Interpret(target.Entry.Name),
                context.Language,
                refresh
                    ? (_, _) => Task.FromResult<VideoRecord?>(null)
                    : (path, token) => store.Record(target.CatalogId, path, attempt.Language, token)
            );
            try
            {
                if (choice is null && attempt.Choice is null && !refresh)
                    video = await store
                        .Saved(target, context.Language, context.Token)
                        .ConfigureAwait(false);
                if (video is null)
                {
                    choice ??= attempt.Choice;
                    if (choice is not null)
                    {
                        if (choice.Kind == VideoKind.Series && request.Release.Episodes.Count == 0)
                            throw new VideoException("library", "episode_required");
                        video = await Describe(context, choice, request).ConfigureAwait(false);
                    }
                    else
                    {
                        var series = request.Release.Season is not null
                            ? await store
                                .Series(target, request.Release, context.Token)
                                .ConfigureAwait(false)
                            : null;
                        video = await Match(
                                context,
                                request,
                                series,
                                limited: target.Kind != LookupKind.Explicit
                            )
                            .ConfigureAwait(false);
                    }
                }
            }
            // Unusable information ends an automatic or batch lookup as a miss without
            // preventing the provider from serving other videos.
            catch (VideoException error)
                when (error is not ProviderException && target.Kind != LookupKind.Explicit)
            {
                unanswered = true;
            }
            var outcome =
                video is not null ? "complete"
                : unanswered || context.Limited ? "unanswered"
                : "no_match";
            accepted = await store
                .Complete(attempt, video, outcome, context.Token)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A provider that cannot serve any request has not answered for
            // this file, so the attempt leaves it eligible for a later lookup.
            var outcome =
                context.Token.IsCancellationRequested
                || error is OperationCanceledException or ProviderException
                    ? "cancelled"
                    : "failed";
            await store
                .Complete(attempt, null, outcome, CancellationToken.None)
                .ConfigureAwait(false);
            context.Token.ThrowIfCancellationRequested();
            throw;
        }
        if (!accepted)
            return target.Kind == LookupKind.Explicit
                ? throw new VideoException("library", "file_changed")
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
        VideoChoice? series,
        bool limited
    )
    {
        var release = request.Release;
        var kind = release.Season is null ? VideoKind.Movie : VideoKind.Series;
        // A season without an episode number, such as "Season 1", names no
        // episode a provider can describe.
        if (release.IsExtra || release.Title.Length == 0 || !context.Provider.Kinds.Contains(kind) ||
            kind == VideoKind.Series && release.Episodes.Count == 0)
            return null;
        if (series is not null)
            return await Describe(context, series, request).ConfigureAwait(false);
        VideoChoice[] Matches(IEnumerable<VideoChoice> choices) =>
            choices
                .Where(choice => choice.Kind == kind && Confidence(release, choice) > 0.8)
                .ToArray();
        // A saved choice replaces the search only with the release's year,
        // because the search could list a same-titled video no page has shown
        // yet. When the search finds nothing, a lone saved choice still answers.
        var saved = Matches(
            await store
                .Choices(context.Provider.CatalogId, release.Title, context.Token)
                .ConfigureAwait(false)
        );
        var dated = saved
            .Where(choice => release.Year is not null && choice.Year == release.Year)
            .ToArray();
        var matches =
            dated.Length == 1
                ? dated
                : Matches(
                    await Search(context, release.Title, reuse: true, limited).ConfigureAwait(false)
                );
        if (matches.Length == 0)
            matches = saved;
        if (matches.Length != 1)
            return null;
        var match = matches[0];
        var video = await Describe(context, match, request).ConfigureAwait(false);
        var year = kind == VideoKind.Movie ? video.Year ?? match.Year : match.Year ?? video.Year;
        return
            FileName.Normalize(video.Title) == FileName.Normalize(match.Title)
            && (release.Year is null || year == release.Year)
            ? video
            : null;
    }

    // Every provider result is saved as a choice, so a later lookup of the
    // same video can skip the provider's search or page. A search made once
    // this session is answered again without a request, so the files of one
    // movie search the provider once. A limited search finds nothing once the
    // provider has answered the same text as often as the limit allows.
    private async Task<IReadOnlyList<VideoChoice>> Search(
        Context context,
        string query,
        bool reuse,
        bool limited
    )
    {
        var key = (context.Provider.CatalogId, FileName.Normalize(query), context.Language);
        lock (_gate)
            if (reuse && _searches.TryGetValue(key, out var known))
                return known;
        if (
            limited
            && await store.Searches(key.CatalogId, query, context.Token).ConfigureAwait(false)
                >= LookupLimit
        )
        {
            context.Limited = true;
            return [];
        }
        var choices = await context
            .Provider.Search(query, context.Language, context.Token)
            .ConfigureAwait(false);
        await store
            .SaveSearch(key.CatalogId, query, choices, CancellationToken.None)
            .ConfigureAwait(false);
        context.Token.ThrowIfCancellationRequested();
        lock (_gate)
            _searches[key] = choices;
        return choices;
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
        await store
            .SaveResponse(
                video,
                context.Provider.IsLocalized ? context.Language : null,
                CancellationToken.None
            )
            .ConfigureAwait(false);
        context.Token.ThrowIfCancellationRequested();
        return video;
    }

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
                if (Failure is { RetryAt: { } retry })
                {
                    var delay = retry - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(
                                delay < TimeSpan.FromDays(1) ? delay : TimeSpan.FromDays(1),
                                context.Token
                            )
                            .ConfigureAwait(false);
                        continue;
                    }
                    // The wait is over, so Library stops showing it.
                    lock (_gate)
                        Resume();
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                var target = await store
                    .Next(context.Provider.CatalogId, context.Token)
                    .ConfigureAwait(false);
                if (target is null)
                    return;
                try
                {
                    await Execute(context, current => Collect(current, target, null, false))
                        .ConfigureAwait(false);
                }
                catch (ProviderException error) when (error.RetryAt is not null) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (ProviderException) { }
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

    private sealed record Context(IProvider Provider, string Language, CancellationToken Token)
    {
        // The search limit refused a search, so a miss may not be the provider's answer.
        internal bool Limited { get; set; }
    }
}

/// <summary>
/// Fetch metadata's progress: <see cref="Done"/> of <see cref="Total"/> files,
/// where <see cref="Total"/> includes the file in progress, from <see cref="Source"/>.
/// </summary>
internal sealed record VideoBatch(int Done, int Total, ProviderOption Source);
