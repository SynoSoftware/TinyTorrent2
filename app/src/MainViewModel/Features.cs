using System.Text.Json;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml.Controls;
using SQLitePCL;
using Syno.TinyTorrent.Library;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using PublicProvider = Syno.TinyTorrent.Library.Public.Provider;
using SubtitleAcquisition = Syno.TinyTorrent.Subtitles.Acquisition;
using TmdbProvider = Syno.TinyTorrent.Library.Tmdb.Provider;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private readonly object _featuresGate = new();
    private readonly SemaphoreSlim _featureAccess = new(1);
    private Database? _database;
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

    internal async Task<Statistics?> ReadStatistics()
    {
        if (_featuresClosed || !Library.IsAvailable || _database is not { } database)
            return null;
        try
        {
            var statistics = await database.ReadStatistics();
            return ReferenceEquals(database, _database) ? statistics : null;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (ReferenceEquals(database, _database))
                ReportFeature(error);
        }
        return null;
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
            // Automatic lookups and subtitle searches wait, so starting the
            // window does not compete with them for the network.
            var settled = _settled ??= Task.Delay(TimeSpan.FromSeconds(30));
            var store = new Store(_database);
            var http = _providerHttp;
            var videos = new VideoLookup(
                store,
                settled,
                [
                    new ProviderOption(
                        "tmdb",
                        "TMDB",
                        "tmdb",
                        new("https://www.themoviedb.org/privacy-policy"),
                        new("https://www.themoviedb.org/api-terms-of-use"),
                        () => new TmdbProvider(http)
                    ),
                    .. PublicProvider.Options(http),
                ]
            );
            Library.Attach(store, videos, new FileFacts(_database));
            var subtitles = new SubtitleAcquisition(
                _database,
                _providerHttp,
                settled,
                () => videos.LookupLimit
            );
            Subtitles = subtitles;
            subtitles.Changed += (_, _) => _dispatcher.TryEnqueue(() =>
            {
                if (ReferenceEquals(Subtitles, subtitles))
                    RefreshFeedback();
            });
            subtitles.LanguagesChanged += (_, _) => _dispatcher.TryEnqueue(() =>
            {
                if (ReferenceEquals(Subtitles, subtitles))
                    Library.Refresh();
            });
            subtitles.FileFailed += (_, failure) => _dispatcher.TryEnqueue(() =>
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
                        _ = RefreshFeatures();
                });
            };
            source.Failed += (_, error) => _dispatcher.TryEnqueue(() =>
            {
                if (ReferenceEquals(_fileSource, source))
                    ReportFeature(error);
            });
            source.Observe(snapshot);
        }
    }

    private void ConfigureRoute()
    {
        if (_closed || _featuresClosed)
            return;
        var route = Settings.Proxy.RouteSettings(Settings.Adapter.ConfirmedText);
        if (_providerRoute != route)
        {
            Library.ChangeRoute(() => _providerHttp.Configure(route));
            _providerRoute = route;
            _ = Subtitles?.RouteChanged();
        }
    }

    private async Task RefreshFeatures()
    {
        var source = _fileSource;
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
                    _fileSource is { IsReady: true },
                    IsConnected && _fileSource is { IsCurrent: true }
                );
            }
            await observing;
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
            await Subtitles.Reconcile();
            lock (_featuresGate)
                if (!_closed && !_featuresClosed && _fileSource is { IsReady: true } && IsConnected)
                    _featureFailure = null;
            RefreshFeedback();
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
            DisconnectFeatures();
            await DetachFeatures();
            if (delete)
                await database.DeleteIncompatible();
            await database.DisposeAsync();
            _database = null;
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

    // Library saves the search limit and subtitles read it, so their Find
    // counts refresh with it.
    internal async Task SaveLookupLimit(int limit)
    {
        await Library.SaveLookupLimit(limit);
        Subtitles?.RefreshLookupLimit();
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
            await RefreshFeatures();
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
            await DetachFeatures();
            await Library.DisposeAsync();
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
