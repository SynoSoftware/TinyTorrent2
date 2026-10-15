using System.Text.Json;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml.Controls;
using SQLitePCL;
using Syno.TinyTorrent.Library;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using ImdbDataset = Syno.TinyTorrent.Library.Imdb.Dataset;
using ImdbProvider = Syno.TinyTorrent.Library.Imdb.Provider;
using MdbListProvider = Syno.TinyTorrent.Library.MdbList.Provider;
using OmdbProvider = Syno.TinyTorrent.Library.Omdb.Provider;
using SubtitleAcquisition = Syno.TinyTorrent.Subtitles.Acquisition;
using TmdbProvider = Syno.TinyTorrent.Library.Tmdb.Provider;
using WikidataProvider = Syno.TinyTorrent.Library.Wikidata.Provider;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private readonly object _featuresGate = new();
    private readonly SemaphoreSlim _featureAccess = new(1);
    private bool _refreshingFeatures;
    private bool _featuresPending;
    private Database? _database;
    private RequestPolicy? _requestPolicy;
    private TorrentSource? _fileSource;
    private readonly ProviderHttp _providerHttp = new();
    private HttpRoute? _providerRoute;
    private bool _applyingSettings;
    private Task? _subtitlesStarting;
    private Task? _settled;
    private bool _featuresClosed;
    private bool _resetting;
    private Exception? _featureFailure;
    private Exception? FeatureFailure =>
        _featureFailure ?? Library.FailureCause ?? Subtitles?.DatabaseFailure;
    private bool HasFeatureFailure => FeatureFailure is not null;
    private string FeatureFailureTitle =>
        HasIncompatibleDatabase || _featureFailure is not null || Library.HasFailure
            ? "library_unavailable"
            : "subtitles_unavailable";
    private bool HasIncompatibleDatabase => _database?.IsIncompatible == true;

    // The message bar names the cause. It offers the fix the app can make,
    // or Open folder beside Retry when the person fixes the file outside the
    // app.
    private Status FeatureStatus(Exception failure)
    {
        var title = Text.Get("notifications", FeatureFailureTitle);
        // An incompatible database fails both features, whichever reports
        // first, but a failed deletion of it says why it failed.
        if (HasIncompatibleDatabase)
            return new(
                title,
                failure is IOException or UnauthorizedAccessException
                    ? Text.Error(failure)
                    : Text.Get("errors", "database_incompatible"),
                InfoBarSeverity.Error,
                DeleteDatabase,
                Text.Get("file_action", "delete"),
                Syno.Lucide.Trash2,
                Text.Get("window", "delete_database_tip")
            );
        if (failure is not SqliteException { SqliteErrorCode: var code })
            return Retry(Text.Error(failure));
        if (code == raw.SQLITE_READONLY && _database?.IsReadOnlyFile == true)
            return new(
                title,
                Text.Format("window", "database_read_only", Database.FileName),
                InfoBarSeverity.Error,
                RepairDatabase,
                Text.Get("window", "repair_database"),
                Syno.Lucide.Wrench,
                Text.Get("window", "repair_database_tip")
            );
        var cause = code switch
        {
            raw.SQLITE_READONLY or raw.SQLITE_PERM or raw.SQLITE_CANTOPEN => "database_denied",
            raw.SQLITE_IOERR => "database_disk",
            raw.SQLITE_FULL => "database_full",
            raw.SQLITE_BUSY or raw.SQLITE_LOCKED => "database_in_use",
            _ => null,
        };
        // Any other code is a defect, and SQLite's own message is what a
        // report of it needs.
        return cause is null
            ? Retry(Text.Error(failure))
            : Retry(Text.Format("window", cause, Database.FileName), offersDataFolder: true);

        Status Retry(string body, bool offersDataFolder = false) =>
            new(
                title,
                body,
                InfoBarSeverity.Error,
                RetryFeatures,
                Text.Get("inspector", "retry"),
                Syno.Lucide.RotateCcw,
                Text.Get("window", "retry_features_tip"),
                offersDataFolder
            );
    }

    private async Task ClearDatabaseReadOnly()
    {
        if (_database is not { } database)
            return;
        try
        {
            database.ClearReadOnly();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ReportFeature(error);
            return;
        }
        await RecoverFeatures();
    }

    internal event EventHandler? DatabaseDeletionRequested;
    internal SubtitleAcquisition? Subtitles { get; private set; }
    internal Geolocation Geolocation { get; }

    internal Task<Statistics?> ReadStatistics() =>
        ReadDatabase(database => database.ReadStatistics());

    internal Task<long?> ReadDatabaseSize() =>
        ReadDatabase<long?>(async database => await database.ReadSize());

    // A read of a database that was replaced meanwhile returns nothing.
    private async Task<T?> ReadDatabase<T>(Func<Database, Task<T>> read)
    {
        if (_featuresClosed || !Library.IsAvailable || _database is not { } database)
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
                ReportFeature(error);
        }
        return default;
    }

    internal ICommand DownloadSubtitles { get; }
    internal event EventHandler<IReadOnlySet<long>>? SubtitlesRequested;
    private bool CanDownloadSubtitles =>
        Subtitles is { Ready: true }
        && Library.IsAvailable
        && Library.Selection.Items.Cast<LibraryRow>().Any(row => row.Entry.Kind == FileKind.Video);

    private void RefreshFeedback()
    {
        if (_closed || _featuresClosed)
            return;
        Changed(nameof(CurrentStatus));
        ((RelayCommand)RetryFeatures).Refresh();
        ((RelayCommand)DeleteDatabase).Refresh();
        ((RelayCommand)RepairDatabase).Refresh();
        ((RelayCommand)DownloadSubtitles).Refresh();
    }

    private void ReportFeature(Exception error)
    {
        if (_closed || _featuresClosed)
            return;
        _featureFailure = error;
        RefreshFeedback();
    }

#if CAPTURE
    internal bool CanStartFeatures { get; set; } = true;
#endif

    private void ObserveFiles(JsonElement snapshot)
    {
        if (_closed || _closing || _featuresClosed || _resetting)
            return;
#if CAPTURE
        if (!CanStartFeatures)
            return;
#endif
        if (_fileSource is null && DataDirectory is { } directory)
        {
            _database ??= new Database(directory);
            var policy = _requestPolicy ??= new RequestPolicy(_database);
            // Automatic lookups and subtitle searches wait, so starting the
            // window does not compete with them for the network.
            var settled = _settled ??= Task.Delay(TimeSpan.FromSeconds(30));
            var http = _providerHttp;
            var imdb = new ImdbDataset(http, directory, policy);
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
                http
            )
            {
                Work = Work,
            };
            Library.Attach(store, videos, new FileFacts(_database, Work));
            Geolocation.Attach(directory, policy);
            var subtitles = new SubtitleAcquisition(_database, _providerHttp, settled, policy)
            {
                Work = Work,
            };
            Subtitles = subtitles;
            subtitles.Changed += (_, _) =>
                _dispatcher.TryEnqueue(() =>
                {
                    if (ReferenceEquals(Subtitles, subtitles))
                    {
                        RefreshFeedback();
                    }
                });
            subtitles.LanguagesChanged += (_, _) =>
                _dispatcher.TryEnqueue(() =>
                {
                    if (ReferenceEquals(Subtitles, subtitles))
                        Library.StoredChanged();
                });
            subtitles.FileFailed += (_, failure) =>
                _dispatcher.TryEnqueue(() =>
                {
                    if (ReferenceEquals(Subtitles, subtitles))
                        ShowSubtitleFailure(failure);
                });
            Changed(nameof(Subtitles));
            _subtitlesStarting = Subtitles.Initialize(Text.Language);
            var sources = new FileSources(_database);
            var source = new TorrentSource(_client, sources);
            _fileSource = source;
            source.Changed += (_, _) =>
            {
                lock (_featuresGate)
                {
                    if (!ReferenceEquals(_fileSource, source))
                        return;
                    if (!source.IsReady)
                        DisconnectFeatures();
                }
                _dispatcher.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_fileSource, source))
                        _ = RefreshSource();
                });
            };
            source.Failed += (_, error) =>
                _dispatcher.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_fileSource, source))
                        ReportFeature(error);
                });
            source.Observe(snapshot);
        }
    }

    private async Task ConfigureRoute()
    {
        if (_closed || _featuresClosed)
            return;
        var route = Settings.Proxy.RouteSettings(Settings.Adapter.ConfirmedText);
        if (_providerRoute == route)
            return;
        _providerRoute = route;
        // The new route ends a running data download, which the person did
        // not ask for, so it is stopped as an interruption first.
        _ = Library.InterruptDownload();
        _ = Geolocation.InterruptDownload();
        _providerHttp.Configure(route);
        await _featureAccess.WaitAsync();
        try
        {
            if (_closed || _featuresClosed || _providerRoute != route)
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
            if (_providerRoute == route)
                _providerRoute = null;
            ReportFeature(error);
        }
        finally
        {
            _featureAccess.Release();
        }
    }

    private async Task RefreshSource()
    {
        _featuresPending = true;
        if (_refreshingFeatures)
            return;
        _refreshingFeatures = true;
        try
        {
            while (_featuresPending)
            {
                _featuresPending = false;
                await RefreshFeatures();
            }
        }
        finally
        {
            _refreshingFeatures = false;
        }
    }

    private async Task RefreshFeatures(bool wait = false)
    {
        var source = _fileSource;
        Task reconciling = Task.CompletedTask;
        await _featureAccess.WaitAsync();
        try
        {
            Task observing;
            lock (_featuresGate)
            {
                if (
                    _closed
                    || _closing
                    || _featuresClosed
                    || _resetting
                    || !ReferenceEquals(source, _fileSource)
                )
                    return;
                observing = Library.Observe(
                    !IsConnected || _fileSource is not { IsCurrent: true }
                        ? LibraryReadiness.Offline
                    : _fileSource.IsReady ? LibraryReadiness.Ready
                    : LibraryReadiness.Current
                );
            }
            await observing;
            Changed(nameof(LookupLimit));
            lock (_featuresGate)
                if (
                    _closed
                    || _closing
                    || _featuresClosed
                    || _fileSource is not { IsReady: true }
                    || !IsConnected
                    || Subtitles is null
                )
                    return;
            if (_subtitlesStarting is { } initializing)
                await initializing;
            lock (_featuresGate)
            {
                if (
                    _closed
                    || _closing
                    || _featuresClosed
                    || _fileSource is not { IsReady: true }
                    || !IsConnected
                )
                    return;
                Subtitles.SetConnected(true);
            }
            await Subtitles.SetLanguage(Text.Language);
            reconciling = ReconcileSubtitles(Subtitles);
        }
        catch (Exception error)
        {
            lock (_featuresGate)
                Subtitles?.SetConnected(false);
            ReportFeature(error);
        }
        finally
        {
            _featureAccess.Release();
        }
        if (wait)
            await reconciling;
    }

    private async Task ReconcileSubtitles(SubtitleAcquisition subtitles)
    {
        var failure = _featureFailure;
        try
        {
            await subtitles.Reconcile();
            lock (_featuresGate)
            {
                if (
                    _closed
                    || _closing
                    || _featuresClosed
                    || _resetting
                    || !ReferenceEquals(subtitles, Subtitles)
                    || _fileSource is not { IsReady: true }
                    || !IsConnected
                )
                    return;
                if (ReferenceEquals(_featureFailure, failure))
                    _featureFailure = null;
            }
            RefreshFeedback();
        }
        catch (OperationCanceledException) when (subtitles.IsDisposed)
        {
            System.Diagnostics.Debug.WriteLine(
                "Subtitle reconciliation cancelled: worker is stopping."
            );
        }
        catch (Exception error)
        {
            lock (_featuresGate)
            {
                if (
                    _closed
                    || _closing
                    || _featuresClosed
                    || _resetting
                    || !ReferenceEquals(subtitles, Subtitles)
                )
                    return;
                subtitles.SetConnected(false);
            }
            ReportFeature(error);
        }
    }

    private async Task RecoverFeatures()
    {
        var database = _database;
        if (_closed || _featuresClosed || _resetting)
            return;
        try
        {
            if (_database?.RequiresReopen == true)
            {
                await ResetFeatures(false);
                return;
            }
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
                RequestSnapshot();
            await RetrySubtitles();
        }
        catch (Exception error)
        {
            if (ReferenceEquals(database, _database))
                ReportFeature(error);
        }
    }

    internal async Task RecreateDatabase()
    {
        if (_closed || _featuresClosed || !HasIncompatibleDatabase)
            return;
        await ResetFeatures(true);
    }

    private async Task ResetFeatures(bool delete)
    {
        var database = _database;
        await _featureAccess.WaitAsync();
        try
        {
            if (
                _closed
                || _featuresClosed
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
            DisconnectFeatures();
            await DetachFeatures();
            if (delete)
                await database.DeleteIncompatible();
            await database.DisposeAsync();
            _database = null;
            _requestPolicy = null;
            _featureFailure = null;
            RefreshFeedback();
        }
        catch (Exception error)
        {
            ReportFeature(error);
        }
        finally
        {
            _resetting = false;
            _featureAccess.Release();
        }
        if (!_closed && !_featuresClosed)
            RequestSnapshot();
    }

    internal Task DownloadData(IDataset dataset) => AnnounceData(dataset, dataset.Download());

    /// <summary>Downloads the data of the provider the person chose, which the installed data selects.</summary>
    internal Task PrepareAutomaticDownload() =>
        AnnounceData(Library, Library.Download(enableAutomaticDownload: true));

    private async Task AnnounceData(IDataset dataset, Task<bool> download)
    {
        if (await download)
            Announce(Text.Get(dataset.Section, "ready"));
    }

    internal int LookupLimit => _requestPolicy?.Limit ?? RequestPolicy.DefaultLimit;

    internal async Task SaveLookupLimit(int limit)
    {
        await _featureAccess.WaitAsync();
        try
        {
            if (_closed || _featuresClosed || _requestPolicy is not { } policy)
                return;
            await policy.SaveLimit(limit, CancellationToken.None);
            Changed(nameof(LookupLimit));
            Subtitles?.RefreshLookupLimit();
        }
        finally
        {
            _featureAccess.Release();
        }
    }

    internal async Task ResetProtection(string? providerId = null)
    {
        await _featureAccess.WaitAsync();
        try
        {
            if (
                _closed
                || _featuresClosed
                || _database is not { } database
                || Subtitles is not { } subtitles
            )
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
        }
        finally
        {
            _featureAccess.Release();
        }
    }

    internal async Task RetrySubtitles()
    {
        if (_closed || _featuresClosed || _resetting || Subtitles is not { } subtitles)
            return;
        if (_subtitlesStarting is { IsFaulted: true })
            _subtitlesStarting = subtitles.Initialize(Text.Language);
        if (_subtitlesStarting is { } starting)
            await starting;
        if (ReferenceEquals(subtitles, Subtitles))
            await RefreshFeatures(wait: true);
    }

    internal void DisconnectFeatures()
    {
        lock (_featuresGate)
        {
            Library.Disconnect();
            Subtitles?.SetConnected(false);
        }
    }

    internal async Task CloseFeatures()
    {
        lock (_featuresGate)
        {
            _featuresClosed = true;
            _updateRequest?.Cancel();
            DisconnectFeatures();
        }
        await _featureAccess.WaitAsync();
        try
        {
            if (_updateCheck is { } update)
                await update;
            await DetachFeatures();
            await Library.DisposeAsync();
            Geolocation.Dispose();
            _providerHttp.Dispose();
            if (_database is not null)
                await _database.DisposeAsync();
        }
        finally
        {
            _featureAccess.Release();
        }
    }

    private async Task DetachFeatures()
    {
        TorrentSource? source;
        SubtitleAcquisition? subtitles;
        Task? starting;
        lock (_featuresGate)
        {
            source = _fileSource;
            subtitles = Subtitles;
            starting = _subtitlesStarting;
            _fileSource = null;
            Subtitles = null;
            _subtitlesStarting = null;
        }
        Changed(nameof(Subtitles));
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
