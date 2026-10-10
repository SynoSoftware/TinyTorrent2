using System.Text.Json;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Library;
using SubtitleAcquisition = Syno.TinyTorrent.Subtitles.Acquisition;
using TmdbProvider = Syno.TinyTorrent.Library.Tmdb.Provider;
using PublicProvider = Syno.TinyTorrent.Library.Public.Provider;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private readonly object _featuresGate = new();
    private Database? _database;
    private TorrentSource? _fileSource;
    private readonly ProviderHttp _providerHttp = new();
    private HttpRoute? _providerRoute;
    private bool _applyingSettings;
    private Task? _subtitlesStarting;
    private bool _featuresClosed;
    internal SubtitleAcquisition? Subtitles { get; private set; }
#if CAPTURE
    internal bool CanStartFeatures { get; set; } = true;
#endif

    private void ObserveFiles(JsonElement snapshot)
    {
        if (_closed || _featuresClosed)
            return;
#if CAPTURE
        if (!CanStartFeatures)
            return;
#endif
        if (_database is null && DataDirectory is { } directory)
        {
            _database = new Database(directory, Store.Schema + SubtitleAcquisition.Schema,
                FileSources.Schema + FileFacts.Schema + Store.Projection);
            var store = new Store(_database);
            var http = _providerHttp;
            var videos = new VideoLookup(store,
            [
                new ProviderOption("tmdb", "tmdb", _ => new TmdbProvider(http)),
                new ProviderOption("public", "websites", values => PublicProvider.Create(http, values)),
            ]);
            Library.Attach(store, videos, new FileFacts(_database));
            Subtitles = new SubtitleAcquisition(_database, _providerHttp);
            Changed(nameof(Subtitles));
            _subtitlesStarting = Subtitles.Initialize(Text.Language);
            var sources = new FileSources(_database, Store.Cleanup + SubtitleAcquisition.Cleanup,
                Store.Reconcile + FileFacts.Reconcile, Store.Invalidate);
            _fileSource = new TorrentSource(_client, sources);
            _fileSource.Changed += (_, _) =>
            {
                lock (_featuresGate)
                    if (!_fileSource.IsReady)
                        DisconnectFeatures();
                _dispatcher.TryEnqueue(() => _ = RefreshFeatures());
            };
            _fileSource.Failed += (_, error) => _dispatcher.TryEnqueue(() => Report(error));
            _client.Disconnected += _ => DisconnectFeatures();
            _fileSource.Observe(snapshot);
        }
    }

    private void ConfigureRoute()
    {
        if (_closed || _featuresClosed)
            return;
        var route = Settings.Proxy.RouteSettings(Settings.Adapter.ConfirmedText);
        if (_providerRoute != route)
        {
            _providerHttp.Configure(route);
            _providerRoute = route;
            Subtitles?.RouteChanged();
        }
    }

    private async Task RefreshFeatures()
    {
        try
        {
            Task observing;
            lock (_featuresGate)
            {
                if (_closed || _featuresClosed)
                    return;
                observing = Library.Observe(_fileSource is { IsReady: true }, IsConnected);
            }
            await observing;
            lock (_featuresGate)
                if (_closed || _featuresClosed || _fileSource is not { IsReady: true } || !IsConnected || Subtitles is null)
                    return;
            if (_subtitlesStarting is { } initializing)
                await initializing;
            lock (_featuresGate)
            {
                if (_closed || _featuresClosed || _fileSource is not { IsReady: true } || !IsConnected)
                    return;
                Subtitles.SetConnected(true);
            }
            await Subtitles.SetLanguage(Text.Language);
            await Subtitles.Reconcile();
        }
        catch (Exception error)
        {
            lock (_featuresGate)
                Subtitles?.SetConnected(false);
            Report(error);
        }
    }

    internal async Task RetrySubtitles()
    {
        if (_closed || _featuresClosed || Subtitles is null)
            return;
        if (_subtitlesStarting is { IsFaulted: true })
            _subtitlesStarting = Subtitles.Initialize(Text.Language);
        if (_subtitlesStarting is { } starting)
            await starting;
        await RefreshFeatures();
    }

    private void DisconnectFeatures()
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
        var libraryClosing = Library.DisposeAsync();
        if (_subtitlesStarting is { } starting)
        {
            try { await starting; }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        }
        if (_fileSource is not null)
            await _fileSource.DisposeAsync();
        await libraryClosing;
        if (Subtitles is not null)
            await Subtitles.DisposeAsync();
        _providerHttp.Dispose();
        if (_database is not null)
            await _database.DisposeAsync();
    }
}
