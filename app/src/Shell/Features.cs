using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using SQLitePCL;
using Syno.TinyTorrent.Library;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Diagnostics;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shared.Network;
using Syno.TinyTorrent.Shared.Storage;
using ImdbDataset = Syno.TinyTorrent.Library.Imdb.Dataset;
using ImdbProvider = Syno.TinyTorrent.Library.Imdb.Provider;
using LibraryBrowser = Syno.TinyTorrent.Library.Browser;
using MdbListProvider = Syno.TinyTorrent.Library.MdbList.Provider;
using OmdbProvider = Syno.TinyTorrent.Library.Omdb.Provider;
using SubtitleAcquisition = Syno.TinyTorrent.Subtitles.Acquisition;
using TmdbProvider = Syno.TinyTorrent.Library.Tmdb.Provider;
using WikidataProvider = Syno.TinyTorrent.Library.Wikidata.Provider;

namespace Syno.TinyTorrent.Shell;

/// <summary>
/// Starts, watches, resets and closes the features that share the Library
/// database: Library lookup, subtitles and geolocation. Neither feature owns
/// the other's lifetime, so this host coordinates them.
/// </summary>
internal sealed class Features
{
    private readonly MainViewModel _owner;
    private readonly PipeClient _client;
    private readonly ProviderHttp _http;
    private readonly DispatcherQueue _dispatcher;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _access = new(1);
    private bool _refreshing;
    private bool _pending;
    private Database? _database;
    private Task<bool>? _opening;
    private RequestPolicy? _policy;
    private TorrentSource? _source;
    private HttpRoute? _route;
    private Task? _subtitlesStarting;
    private Task? _settled;
    private bool _resetting;
    private Exception? _failure;

    internal Features(
        MainViewModel owner,
        PipeClient client,
        ProviderHttp http,
        DispatcherQueue dispatcher
    )
    {
        _owner = owner;
        _client = client;
        _http = http;
        _dispatcher = dispatcher;
        Library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(Library.Failure) or "")
                Raise();
        };
    }

    /// <summary>
    /// Raised on the UI thread when <see cref="Failure"/>, <see cref="Subtitles"/>,
    /// <see cref="LookupLimit"/> or the subtitle state may have changed.
    /// </summary>
    internal event EventHandler? Changed;

    internal bool IsClosed { get; private set; }

    internal SubtitleAcquisition? Subtitles { get; private set; }

    internal int LookupLimit => _policy?.Limit ?? RequestPolicy.DefaultLimit;

    // Whether the saved proxy or adapter applies yet. The update check waits
    // for it too, so no request leaves before it does.
    internal bool HasRoute => _route is not null;

    internal FeatureFailure? Failure =>
        (_failure ?? Library.FailureCause ?? Subtitles?.DatabaseFailure) is { } cause
            ? new(
                cause,
                _failure is null && !Library.HasFailure && _database?.IsIncompatible != true,
                Condition(cause)
            )
            : null;

    private DatabaseCondition Condition(Exception cause) =>
        _database?.IsIncompatible == true ? DatabaseCondition.Incompatible
        : cause is SqliteException { SqliteErrorCode: raw.SQLITE_READONLY }
            && _database?.IsReadOnlyFile == true
            ? DatabaseCondition.ReadOnlyFile
        : DatabaseCondition.Other;

#if CAPTURE
    internal bool CanStart { get; set; } = true;
#endif

    private LibraryBrowser Library => _owner.Library;
    private Geolocation Geolocation => _owner.Geolocation;
    private BackgroundWork Work => _owner.Work;
    private bool IsStopped => _owner.IsClosed || IsClosed;

    internal void Report(Exception error)
    {
        if (IsStopped)
            return;
        _failure = error;
        Raise();
    }

    private void Raise()
    {
        if (!IsStopped)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    // Feature events arrive on worker threads. The UI thread drops one whose
    // instance a reset or close has replaced meanwhile.
    private void Post<T>(T instance, Func<T?> current, Action action)
        where T : class =>
        _dispatcher.TryEnqueue(() =>
        {
            if (ReferenceEquals(current(), instance))
                action();
        });

    internal void Observe(JsonElement snapshot)
    {
        if (_owner.IsClosing || IsStopped || _resetting)
            return;
#if CAPTURE
        if (!CanStart)
            return;
#endif
        if (_source is not null || _owner.DataDirectory is not { } directory)
            return;
        _database ??= new Database(directory);
        // Every feature queries the database as it starts, so they start
        // only after it opened: a file they cannot use then fails once,
        // and it is opened again only by Retry or Delete.
        _opening ??= Open(_database);
        if (_opening is not { IsCompletedSuccessfully: true, Result: true })
            return;
        var policy = _policy ??= new RequestPolicy(_database);
        // Automatic lookups and subtitle searches wait, so starting the
        // window does not compete with them for the network.
        var settled = _settled ??= Task.Delay(TimeSpan.FromSeconds(30));
        var imdb = new ImdbDataset(_http, directory, policy);
        var store = new Store(_database, imdb);
        // The list is the order Settings shows the providers in.
        var videos = new VideoLookup(
            store,
            settled,
            [
                TmdbProvider.Option,
                WikidataProvider.Option,
                OmdbProvider.Option,
                MdbListProvider.Option,
                ImdbProvider.Option(imdb),
            ],
            policy,
            _http
        )
        {
            Work = Work,
        };
        Library.Attach(store, videos, new FileFacts(_database, Work));
        Geolocation.Attach(directory, policy);
        var subtitles = new SubtitleAcquisition(_database, _http, settled, policy)
        {
            Work = Work,
        };
        Subtitles = subtitles;
        subtitles.Changed += (_, _) => Post(subtitles, () => Subtitles, Raise);
        subtitles.LanguagesChanged += (_, _) =>
            Post(subtitles, () => Subtitles, Library.StoredChanged);
        subtitles.FileFailed += (_, failure) =>
            Post(subtitles, () => Subtitles, () => _owner.Notifications.ShowSubtitleFailure(failure));
        Raise();
        _subtitlesStarting = subtitles.Initialize(_owner.Text.Language);
        var source = new TorrentSource(_client, new FileSources(_database));
        _source = source;
        source.Changed += (_, _) =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_source, source))
                    return;
                if (!source.IsReady)
                    Disconnect();
            }
            Post(source, () => _source, () => _ = RefreshSource());
        };
        source.Failed += (_, error) =>
            Post(
                source,
                () => _source,
                () =>
                {
                    Report(error);
                    if (error is SqliteException or DatabaseVersionException)
                        Library.Report(error);
                }
            );
        source.Published += (_, _) =>
            Post(
                source,
                () => _source,
                () =>
                {
                    Library.StoredChanged();
                    _ = RefreshSource();
                }
            );
        source.Observe(snapshot);
    }

    private async Task<bool> Open(Database database)
    {
        try
        {
            await database.Run(_ => true);
        }
        catch (Exception error)
        {
            if (ReferenceEquals(database, _database))
                Report(error);
            return false;
        }
        if (ReferenceEquals(database, _database))
            _owner.RequestSnapshot();
        return true;
    }

    // Applies the saved proxy or adapter to every feature request.
    internal async Task ConfigureRoute()
    {
        if (IsStopped)
            return;
        var settings = _owner.Settings;
        var route = settings.Proxy.RouteSettings(settings.Adapter.ConfirmedText ?? string.Empty);
        if (_route == route)
            return;
        _route = route;
        // The new route ends a running data download, which the person did
        // not ask for, so it is stopped as an interruption first.
        _ = Library.InterruptDownload();
        _ = Geolocation.InterruptDownload();
        _http.Configure(route);
        await _access.WaitAsync();
        try
        {
            if (IsStopped || _route != route)
                return;
            if (_database is not { } database || Subtitles is not { } subtitles)
                return;
            await Library.SuspendProtection(
                () =>
                    subtitles.Suspend(() =>
                        database.Run(connection =>
                        {
                            RequestPolicy.ClearBackoff(connection);
                            subtitles.ChangeRoute(connection);
                            return true;
                        })
                    ),
                includeDataset: true
            );
        }
        catch (Exception error)
        {
            if (_route == route)
                _route = null;
            Report(error);
        }
        finally
        {
            _access.Release();
        }
    }

    private async Task RefreshSource()
    {
        _pending = true;
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            while (_pending)
            {
                _pending = false;
                await Refresh();
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    // Brings the features up to date with the connection, the torrent files
    // and the interface language.
    internal async Task Refresh(bool wait = false)
    {
        var source = _source;
        Task reconciling = Task.CompletedTask;
        await _access.WaitAsync();
        try
        {
            Task observing;
            lock (_gate)
            {
                if (
                    _owner.IsClosing
                    || IsStopped
                    || _resetting
                    || !ReferenceEquals(source, _source)
                )
                    return;
                observing = Library.Observe(
                    !_owner.IsConnected || _source is not { IsCurrent: true }
                        ? LibraryReadiness.Offline
                    : _source.IsReady ? LibraryReadiness.Ready
                    : LibraryReadiness.Current
                );
            }
            await observing;
            Raise();
            lock (_gate)
                if (
                    _owner.IsClosing
                    || IsStopped
                    || _source is not { IsReady: true }
                    || !_owner.IsConnected
                    || Subtitles is null
                )
                    return;
            if (_subtitlesStarting is { } initializing)
                await initializing;
            lock (_gate)
            {
                if (
                    _owner.IsClosing
                    || IsStopped
                    || _source is not { IsReady: true }
                    || !_owner.IsConnected
                )
                    return;
                Subtitles.SetConnected(true);
            }
            await Subtitles.SetLanguage(_owner.Text.Language);
            reconciling = Reconcile(Subtitles);
        }
        catch (Exception error)
        {
            lock (_gate)
                Subtitles?.SetConnected(false);
            Report(error);
        }
        finally
        {
            _access.Release();
        }
        if (wait)
            await reconciling;
    }

    private async Task Reconcile(SubtitleAcquisition subtitles)
    {
        var failure = _failure;
        try
        {
            await subtitles.Reconcile();
            lock (_gate)
            {
                if (
                    _owner.IsClosing
                    || IsStopped
                    || _resetting
                    || !ReferenceEquals(subtitles, Subtitles)
                    || _source is not { IsReady: true }
                    || !_owner.IsConnected
                )
                    return;
                if (ReferenceEquals(_failure, failure))
                    _failure = null;
            }
            Raise();
        }
        catch (OperationCanceledException) when (subtitles.IsDisposed)
        {
            System.Diagnostics.Debug.WriteLine(
                "Subtitle reconciliation cancelled: worker is stopping."
            );
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (
                    _owner.IsClosing
                    || IsStopped
                    || _resetting
                    || !ReferenceEquals(subtitles, Subtitles)
                )
                    return;
                subtitles.SetConnected(false);
            }
            Report(error);
        }
    }

    // The message bar's Retry.
    internal async Task Retry()
    {
        var database = _database;
        if (IsStopped || _resetting)
            return;
        try
        {
            if (_database?.RequiresReopen == true)
            {
                await Reset(false);
                return;
            }
            if (_opening is { IsCompleted: true, Result: false })
                _opening = null;
            using var operation = Work.Begin(
                new(
                    new WorkDescription.Maintenance(MaintenanceWork.Recovery),
                    new WorkProgress.Unknown(),
                    new WorkExecution.Finishing()
                )
            );
            if (Library.HasFailure)
                await Library.Recover();
            else
                _owner.RequestSnapshot();
            await RetrySubtitles();
            operation.Outcome = Library.HasFailure ? WorkOutcome.Failed : WorkOutcome.Completed;
            // Recovery clears the message bar.
            operation.ReportsSuccess = false;
        }
        catch (Exception error)
        {
            if (ReferenceEquals(database, _database))
                Report(error);
        }
    }

    // The message bar's Repair: the database file is read-only.
    internal async Task Repair()
    {
        if (_database is not { } database)
            return;
        try
        {
            database.ClearReadOnly();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Report(error);
            return;
        }
        await Retry();
    }

    // Deletes a database that an earlier or later version created.
    internal async Task Delete()
    {
        if (IsStopped || _database?.IsIncompatible != true)
            return;
        await Reset(true);
    }

    private async Task Reset(bool delete)
    {
        var database = _database;
        await _access.WaitAsync();
        try
        {
            if (
                IsStopped
                || database is null
                || !ReferenceEquals(database, _database)
                || delete && !database.IsIncompatible
            )
                return;
            _resetting = true;
            using var operation = Work.Begin(
                new(
                    new WorkDescription.Maintenance(MaintenanceWork.Recovery),
                    new WorkProgress.Unknown(),
                    new WorkExecution.Finishing()
                )
            );
            Disconnect();
            await Detach();
            if (delete)
                await database.DeleteIncompatible();
            await database.DisposeAsync();
            _database = null;
            _opening = null;
            _policy = null;
            _failure = null;
            Raise();
            operation.Outcome = WorkOutcome.Completed;
            // Recovery clears the message bar.
            operation.ReportsSuccess = false;
        }
        catch (Exception error)
        {
            Report(error);
        }
        finally
        {
            _resetting = false;
            _access.Release();
        }
        if (!IsStopped)
            _owner.RequestSnapshot();
    }

    internal Task<Statistics?> ReadStatistics() => Read(database => database.ReadStatistics());

    internal Task<long?> ReadDatabaseSize() =>
        Read<long?>(async database => await database.ReadSize());

    // A read of a database that was replaced meanwhile returns nothing.
    private async Task<T?> Read<T>(Func<Database, Task<T>> read)
    {
        if (IsClosed || !Library.IsAvailable || _database is not { } database)
            return default;
        try
        {
            var result = await read(database);
            return ReferenceEquals(database, _database) ? result : default;
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Database summary read cancelled.");
        }
        catch (Exception error)
        {
            if (ReferenceEquals(database, _database))
                Report(error);
        }
        return default;
    }

    internal async Task SaveLookupLimit(int limit)
    {
        await _access.WaitAsync();
        try
        {
            // The Settings field keeps its input when nothing was saved.
            if (IsStopped || _policy is not { } policy)
                throw new IOException(_owner.Text.Get("notifications", "library_unavailable"));
            await policy.SaveLimit(limit, CancellationToken.None);
            Raise();
            Subtitles?.RefreshLookupLimit();
        }
        finally
        {
            _access.Release();
        }
    }

    /// <summary>
    /// Resets request protection for <paramref name="providerId"/>, or for
    /// every provider and supplier when it is null.
    /// </summary>
    internal async Task ResetProtection(string? providerId)
    {
        await _access.WaitAsync();
        try
        {
            if (IsStopped || _database is not { } database || Subtitles is not { } subtitles)
                return;
            using var operation = Work.Begin(
                new(
                    new WorkDescription.Maintenance(MaintenanceWork.ResetProtection),
                    new WorkProgress.Unknown(),
                    new WorkExecution.Finishing()
                )
            );
            // The reset clears what a running download writes, so the download
            // ends first, as the IMDb download does inside SuspendProtection.
            if (providerId is null)
                await Geolocation.InterruptDownload();
            await Library.SuspendProtection(
                () =>
                    subtitles.Suspend(() =>
                        database.Run(connection =>
                        {
                            Store.ResetProtection(connection, providerId);
                            subtitles.ResetProtection(connection, providerId);
                            RequestPolicy.Reset(connection, providerId);
                            return true;
                        })
                    ),
                providerId is null || providerId == Library.DataProvider?.ProviderId
            );
            operation.Outcome = WorkOutcome.Completed;
        }
        finally
        {
            _access.Release();
        }
    }

    internal async Task RetrySubtitles()
    {
        if (IsStopped || _resetting || Subtitles is not { } subtitles)
            return;
        if (_subtitlesStarting is { IsFaulted: true })
            _subtitlesStarting = subtitles.Initialize(_owner.Text.Language);
        if (_subtitlesStarting is { } starting)
            await starting;
        if (ReferenceEquals(subtitles, Subtitles))
            await Refresh(wait: true);
    }

    internal void Disconnect()
    {
        lock (_gate)
        {
            Library.Disconnect();
            Subtitles?.SetConnected(false);
        }
    }

    // Ends every feature and detaches the Library browser and geolocation,
    // which MainViewModel disposes, and then closes the database.
    internal async Task Close()
    {
        lock (_gate)
        {
            IsClosed = true;
            Disconnect();
        }
        await _access.WaitAsync();
        try
        {
            await Detach();
            if (_database is not null)
                await _database.DisposeAsync();
        }
        finally
        {
            _access.Release();
        }
    }

    // Runs after Close, so no feature writes its settings back. The window is
    // already closed to the person, so a failure goes to the error log rather
    // than leaving a window without its features. An incompatible database
    // holds no settings that apply.
    internal async Task ResetSettings()
    {
        if (_owner.DataDirectory is not { } directory)
            return;
        await using var database = new Database(directory);
        try
        {
            await database.ResetSettings();
        }
        catch (DatabaseVersionException) { }
        catch (Exception error)
        {
            ExceptionLog.Write(directory, error, "Reset all settings");
        }
    }

    private async Task Detach()
    {
        TorrentSource? source;
        SubtitleAcquisition? subtitles;
        Task? starting;
        lock (_gate)
        {
            source = _source;
            subtitles = Subtitles;
            starting = _subtitlesStarting;
            _source = null;
            Subtitles = null;
            _subtitlesStarting = null;
        }
        Raise();
        var libraryClosing = Library.Detach();
        await Geolocation.Detach();
        if (source is not null)
            await source.DisposeAsync();
        if (starting is not null)
        {
            try
            {
                await starting;
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine(error);
            }
        }
        await libraryClosing;
        if (subtitles is not null)
            await subtitles.DisposeAsync();
    }
}

/// <summary>Why the Library database features are unavailable.</summary>
/// <param name="SubtitlesOnly">Whether Library still works and only subtitles failed.</param>
internal sealed record FeatureFailure(
    Exception Cause,
    bool SubtitlesOnly,
    DatabaseCondition Database
);
