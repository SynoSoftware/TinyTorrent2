using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Library;

internal sealed class VideoLookup(Store store, IReadOnlyList<ProviderOption> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _access = new(1);
    private readonly SemaphoreSlim _changes = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _requests = [];
    private readonly Dictionary<string, ProviderException> _failures = [];
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
                return Provider is { } provider ? _failures.GetValueOrDefault(provider.CatalogId) : null;
        }
    }
    internal bool IsAvailable => Provider?.IsAvailable == true;
    internal event EventHandler? Changed;
    internal event EventHandler<Exception>? Failed;

    internal Task Initialize(string language) => Change(async cancellation =>
    {
        await store.Recover(cancellation).ConfigureAwait(false);
        var selected = await store.Provider(cancellation).ConfigureAwait(false);
        var enabled = await store.Enabled(cancellation).ConfigureAwait(false);
        var option = selected is null ? options.FirstOrDefault() : options.FirstOrDefault(option => option.ProviderId == selected);
        var provider = option is null ? null : await Create(option, cancellation).ConfigureAwait(false);
        if (enabled && provider is null)
            await store.SetEnabled(false, cancellation).ConfigureAwait(false);
        lock (_gate)
        {
            Option = option;
            Provider = provider;
            Enabled = enabled && provider is not null;
            _language = language;
        }
    });

    internal Task SetEnabled(bool enabled) => Change(async cancellation =>
    {
        if (enabled && !IsAvailable)
            throw new VideoException("library", "provider_unavailable");
        await store.SetEnabled(enabled, cancellation).ConfigureAwait(false);
        lock (_gate)
        {
            Enabled = enabled;
            Resume();
        }
    });

    internal Task Select(string providerId) => Change(async cancellation =>
    {
        var option = options.FirstOrDefault(option => option.ProviderId == providerId)
            ?? throw new ArgumentOutOfRangeException(nameof(providerId));
        var provider = await Create(option, cancellation).ConfigureAwait(false);
        await store.SetProvider(providerId, cancellation).ConfigureAwait(false);
        lock (_gate)
        {
            Option = option;
            Provider = provider;
            Resume();
        }
    });

    /// <summary>
    /// Saves new settings for <paramref name="edited"/> and replaces it. An editor
    /// left open while the person chose another provider saves nothing.
    /// </summary>
    internal Task Configure(IProvider edited, IReadOnlyDictionary<string, string> settings) => Change(async cancellation =>
    {
        if (!ReferenceEquals(Provider, edited) || Option is not { } option)
            throw new OperationCanceledException(cancellation);
        await store.SaveSettings(option.ProviderId, settings, cancellation).ConfigureAwait(false);
        lock (_gate)
        {
            Provider = option.Create(settings);
            Resume();
        }
    });

    private async Task<IProvider> Create(ProviderOption option, CancellationToken cancellation) =>
        option.Create(await store.Settings(option.ProviderId, cancellation).ConfigureAwait(false));

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
            try { await apply(_lifetime.Token).ConfigureAwait(false); }
            finally { _access.Release(); }
        }
        finally
        {
            _changes.Release();
            lock (_gate)
                Refresh(_connected, _language);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelContext()
    {
        _context.Cancel();
        _context.Dispose();
        _context = new();
        _background?.Cancel();
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
            if (!connected || !Enabled || Provider is not { Schedule: VideoSchedule.Background, IsAvailable: true } provider ||
                _interactive != 0 || _changes.CurrentCount == 0 || Failure is { RetryAt: null })
            {
                _background?.Cancel();
                return;
            }
            if (_background is null)
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _context.Token);
                var context = new Context(provider, _language, cancellation.Token);
                _background = cancellation;
                _work = Task.Run(() => Run(context, cancellation));
            }
        }
    }

    internal Task Retry() => Change(_ =>
    {
        lock (_gate)
            Resume();
        return Task.CompletedTask;
    });

    private void Resume()
    {
        if (Provider is not { } provider || Failure?.RetryAt > DateTimeOffset.UtcNow)
            return;
        _failures.Remove(provider.CatalogId);
    }

    internal Task<IReadOnlyList<VideoChoice>> Search(string query, CancellationToken cancellation) =>
        Request(context => context.Provider.Search(query, context.Language, context.Token), cancellation);

    internal Task<bool> Identify(Entry entry, VideoChoice choice, CancellationToken cancellation)
    {
        lock (_gate)
        {
            if (Provider?.CatalogId != choice.CatalogId)
                throw new VideoException("library", "provider_changed");
            return Request(context => Collect(context, new VideoTarget(entry, context.Provider.CatalogId, true), choice, false), cancellation);
        }
    }

    internal Task<bool> Lookup(Entry entry, bool manual, CancellationToken cancellation) =>
        Request(context => Collect(context, new VideoTarget(entry, context.Provider.CatalogId, manual),
            null, manual && entry.Type.Length > 0), cancellation);

    internal Task<bool> CanLookup(Entry entry, CancellationToken cancellation)
    {
        lock (_gate)
        {
            if (_closed || !Enabled || !_connected || Provider is not { Schedule: VideoSchedule.Selection } provider || Failure is not null)
                return Task.FromResult(false);
            return store.CanLookup(new VideoTarget(entry, provider.CatalogId, false), cancellation);
        }
    }

    private Task<T> Request<T>(Func<Context, Task<T>> action, CancellationToken cancellation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!Enabled || !_connected || Provider is not { } provider || _changes.CurrentCount == 0)
                throw new OperationCanceledException(cancellation);
            var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token, _context.Token);
            _interactive++;
            _background?.Cancel();
            var context = new Context(provider, _language, request.Token);
            var task = Perform();
            _requests.Add(task);
            _ = task.ContinueWith(_ =>
            {
                lock (_gate)
                    _requests.Remove(task);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;

            async Task<T> Perform()
            {
                try { return await Execute(context, action).ConfigureAwait(false); }
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
                if (Failure?.RetryAt > DateTimeOffset.UtcNow)
                    throw Failure;
            }
            if (!context.Provider.IsAvailable)
                throw new ProviderException("library", "provider_unavailable");
            var result = await action(context).ConfigureAwait(false);
            lock (_gate)
                if (!context.Token.IsCancellationRequested && ReferenceEquals(Provider, context.Provider))
                    _failures.Remove(context.Provider.CatalogId);
            return result;
        }
        catch (ProviderException error)
        {
            lock (_gate)
                if (!context.Token.IsCancellationRequested && ReferenceEquals(Provider, context.Provider))
                    _failures[context.Provider.CatalogId] = error;
            Changed?.Invoke(this, EventArgs.Empty);
            throw;
        }
        finally { _access.Release(); }
    }

    private async Task<bool> Collect(Context context, VideoTarget target, VideoChoice? choice, bool refresh)
    {
        var language = context.Provider.IsLocalized ? context.Language : null;
        var attempt = await store.Begin(target, language, context.Token).ConfigureAwait(false);
        if (attempt is null)
            return false;
        try
        {
            refresh = refresh || target.Manual && choice is null && attempt.Choice is not null;
            var request = new VideoRequest(FileName.Interpret(target.Entry.Name), context.Language, refresh
                ? (_, _) => Task.FromResult<VideoRecord?>(null)
                : (path, token) => store.Record(target.CatalogId, path, attempt.Language, token));
            VideoInformation? video = null;
            try
            {
                if (choice is null && attempt.Choice is null && !refresh)
                    video = await store.Saved(target, context.Language, context.Token).ConfigureAwait(false);
                if (video is null)
                {
                    choice ??= attempt.Choice;
                    if (choice is not null)
                    {
                        if (choice.Kind == VideoKind.Series && request.Release.Season is null)
                            throw new VideoException("library", "episode_required");
                        video = await context.Provider.Describe(choice, request, context.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        var series = request.Release.Season is not null
                            ? await store.Series(target, request.Release, context.Token).ConfigureAwait(false) : null;
                        video = await Match(context, request, series).ConfigureAwait(false);
                    }
                }
            }
            // Unusable information ends this automatic lookup as a miss without
            // preventing the provider from serving other videos.
            catch (VideoException error) when (error is not ProviderException && !target.Manual) { }
            var accepted = await store.Complete(attempt, video, video is null ? "no_match" : "complete", context.Token).ConfigureAwait(false);
            if (accepted)
                Changed?.Invoke(this, EventArgs.Empty);
            return video is not null && accepted;
        }
        catch (Exception error)
        {
            await store.Complete(attempt, null, context.Token.IsCancellationRequested || error is OperationCanceledException ? "cancelled" : "failed",
                CancellationToken.None).ConfigureAwait(false);
            context.Token.ThrowIfCancellationRequested();
            throw;
        }
    }

    /// <summary>
    /// Finds the provider's unambiguous match for a release, or null. A search
    /// result alone is weak evidence, so its description must agree with it.
    /// </summary>
    private static async Task<VideoInformation?> Match(Context context, VideoRequest request, VideoChoice? series)
    {
        var release = request.Release;
        var kind = release.Season is null ? VideoKind.Movie : VideoKind.Series;
        if (release.IsExtra || release.Title.Length == 0 || !context.Provider.Kinds.Contains(kind))
            return null;
        if (series is not null)
            return await context.Provider.Describe(series, request, context.Token).ConfigureAwait(false);
        var title = FileName.Normalize(release.Title);
        var matches = (await context.Provider.Search(release.Title, request.Language, context.Token).ConfigureAwait(false))
            .Where(choice => choice.Kind == kind &&
                (FileName.Normalize(choice.Title) == title || choice.Original is { } original && FileName.Normalize(original) == title) &&
                (release.Year is null || choice.Year is null || choice.Year == release.Year))
            .ToArray();
        if (matches.Length != 1)
            return null;
        var match = matches[0];
        var video = await context.Provider.Describe(match, request, context.Token).ConfigureAwait(false);
        return FileName.Normalize(video.Title) == FileName.Normalize(match.Title) &&
            (release.Year is null || match.Year is not null || video.Year == release.Year) ? video : null;
    }

    private async Task Run(Context context, CancellationTokenSource cancellation)
    {
        try
        {
            while (true)
            {
                context.Token.ThrowIfCancellationRequested();
                lock (_gate)
                    _again = false;
                if (Failure is { RetryAt: { } retry })
                {
                    var delay = retry - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay < TimeSpan.FromDays(1) ? delay : TimeSpan.FromDays(1), context.Token).ConfigureAwait(false);
                        continue;
                    }
                }
                var target = await store.Next(context.Provider.CatalogId, context.Token).ConfigureAwait(false);
                if (target is null)
                    return;
                try { await Execute(context, current => Collect(current, target, null, false)).ConfigureAwait(false); }
                catch (ProviderException error) when (error.RetryAt is not null) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (ProviderException) { }
        catch (Exception error) { Failed?.Invoke(this, error); }
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
            requests = [.. _requests, _work];
        }
        try { await Task.WhenAll(requests).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        await _changes.WaitAsync().ConfigureAwait(false);
        _changes.Release();
        _context.Dispose();
        _lifetime.Dispose();
    }

    private sealed record Context(IProvider Provider, string Language, CancellationToken Token);
}
