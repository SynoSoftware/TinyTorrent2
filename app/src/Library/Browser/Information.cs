using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Library;

public sealed partial class Browser
{
    // A file without Information does not replace the person's section choice.
    private LibrarySection _section = LibrarySection.Information;
    private InformationRequest? _informationRequest;
    // What the last Inspector lookup for the selected file left to show.
    private FetchOutcome? _outcome;
    private IProvider? _viewProvider;
    private bool _automaticDownload;
    private string? _automaticWait;
    private DataDownload? _download;
    // Installation starts automatic downloads only while the person's
    // provider choice still applies.
    private bool _automaticOnInstall;
    private bool _resettingProtection;

    private abstract record FetchOutcome
    {
        private FetchOutcome() { }
        internal sealed record NoMatch : FetchOutcome;
        internal sealed record Failed(VideoException Error) : FetchOutcome;
    }

    internal bool CanShowInformation => Selected?.Entry.Kind is FileKind.Video or FileKind.Audio;
    internal bool SupportsMetadata => Selected?.Entry.Kind == FileKind.Video;
    internal LibrarySection Section
    {
        get => CanShowInformation ? _section : LibrarySection.File;
        set
        {
            if (_section == value)
                return;
            _section = value;
            if (Section == LibrarySection.File)
                CancelInformation();
            Changed(nameof(Section));
        }
    }

    public bool IsFetching => _informationRequest is not null;
    internal IReadOnlyList<ProviderOption> Providers => _videos?.Options ?? [];
    internal IReadOnlyList<ProviderOption> DownloadProviders =>
        Providers.Where(option => option.IsOnline).ToArray();

    /// <summary>The provider whose data the person downloads, or null before Library attaches.</summary>
    internal ProviderOption? DataProvider => Providers.FirstOrDefault(option => option.Dataset is not null);
    internal ProviderOption? Option => _videos?.Option;
    internal IProvider? Provider => _videos?.Provider;
    public string FetchMessage =>
        IsFetching ? Text.Get("library", "fetching")
        : _outcome is FetchOutcome.NoMatch ? Text.Get("library", "no_video_match")
        : string.Empty;

    // VideoLookup serves requests only once the source is ready, so the
    // commands that ask it wait for the same readiness. Explicit requests
    // remain available while automatic downloading is off.
    private bool CanLookup => IsAvailable && _readiness == LibraryReadiness.Ready;
    private ProviderOption? InformationProvider => Selected?.Entry.CatalogId is { } catalog
        ? Providers.FirstOrDefault(option => option.ProviderId == catalog) : null;
    private bool CanRefresh =>
        CanLookup && InformationProvider is { IsOnline: true }
        && !IsFetching && Selection.Items.Count == 1
        && SupportsMetadata && Selected?.Entry.HasMetadata == true;

    /// <summary>
    /// Fetches metadata for the selection from <paramref name="source"/>: one
    /// file opens the Fetch metadata dialog; a larger selection opens batch scopes.
    /// </summary>
    internal void Fetch(ProviderOption source)
    {
        if (!source.IsOnline || !FetchMetadata.CanExecute(null) || _videos is null)
            return;
        FetchMetadataRequested?.Invoke(this, source);
    }

    internal async Task EnableAutomaticDownload(string providerId)
    {
        _automaticOnInstall = false;
        if (_videos is null)
            return;
        await _videos.EnableAutomaticDownload(providerId);
    }

    internal async Task SelectProvider(string providerId)
    {
        _automaticOnInstall = false;
        if (_videos is null)
            return;
        await _videos.SelectProvider(providerId);
    }

    internal bool HasKey(string providerId) => _videos?.HasKey(providerId) == true;

    /// <summary>Saves the person's own API key, as <see cref="VideoLookup.SaveKey"/> describes.</summary>
    internal async Task SaveKey(string providerId, string? key)
    {
        if (_videos is null)
            return;
        await _videos.SaveKey(providerId, key);
    }

    internal BackgroundWork.Operation? DataWork => _download?.Operation;
    public bool HasDataWork => _download is not null;

    // Settings shows the IMDb data through the dataset contract.
    string IDataset.Section => Imdb.Provider.CatalogId;
    FileInfo? IDataset.Installed => DataProvider?.Dataset?.Inspect();
    bool IDataset.IsCurrent => DataProvider?.Dataset?.IsCurrent == true;
    Task<long>? IDataset.Count => DataProvider?.Dataset?.CountTitles();
    BackgroundWork.Operation? IDataset.DataWork => DataWork;
    Task<bool> IDataset.Download() => Download();
    Task IDataset.Delete() => DeleteData();

    /// <summary>Whether a data download can start; a protection reset or a running download holds it back.</summary>
    public bool CanDownload => !_resettingProtection && _download is null && DataProvider is not null;

    /// <summary>
    /// Downloads <see cref="DataProvider"/>'s data and replaces the installed
    /// copy. Returns whether the new data is installed. Failure or cancellation
    /// before installation keeps the previous copy; a later failure leaves the
    /// replacement installed. A protection reset or route change that stops it is
    /// reported, because the person did not ask for it.
    /// With <paramref name="enableAutomaticDownload"/>, installation starts
    /// automatic downloads with the provider unless the person accepts another
    /// choice before activation.
    /// </summary>
    internal async Task<bool> Download(bool enableAutomaticDownload = false)
    {
        if (!CanDownload || DataProvider is not { Dataset: { } dataset } option || _videos is not { } videos)
            return false;
        var download = new DataDownload(_work, new WorkDescription.Imdb(ImdbPhase.Downloading));
        _download = download;
        _automaticOnInstall = enableAutomaticDownload;
        ShowDownload();
        try
        {
            return await download.Run(
                () => dataset.Build(
                    progress => download.Report(new WorkDescription.Imdb(progress.Phase), Progress(progress)),
                    download.Cancellation),
                () =>
                {
                    CancelInformation();
                    return ReplaceData(videos, option,
                        () => download.Install(new WorkDescription.Imdb(ImdbPhase.Installing), dataset.Install),
                        () => _automaticOnInstall);
                },
                dataset.Discard,
                error =>
                {
                    if (error is VideoException failure)
                        failure.Provider ??= option;
                    ReportCommand(error);
                });
        }
        finally
        {
            _download = null;
            ShowDownload();
        }
    }

    private static WorkProgress Progress(Imdb.Dataset.Progress progress) =>
        progress.Phase == ImdbPhase.Downloading ? new WorkProgress.Amount(progress.Received, WorkUnit.Bytes)
        : progress.Percent is { } percent ? new WorkProgress.Fraction(percent, 100, WorkUnit.Percent, true)
        : new WorkProgress.Unknown();

    /// <summary>Stops the running data download at the person's request, and completes when it has ended.</summary>
    internal Task CancelDownload() => StopDownload(interrupted: false);

    /// <summary>
    /// Stops the running data download as one the person did not ask for,
    /// which reports it, and completes when the download has ended.
    /// </summary>
    internal Task InterruptDownload() => StopDownload(interrupted: true);

    private Task StopDownload(bool interrupted)
    {
        if (_download is not { } download)
            return Task.CompletedTask;
        var stopped = download.Stop(interrupted);
        ShowDownload();
        return stopped;
    }

    internal async Task SuspendProtection(Func<Task> apply, bool includeDataset)
    {
        if (_resettingProtection || _videos is not { } videos)
            return;
        _resettingProtection = true;
        Changed(nameof(CanDownload));
        try
        {
            if (includeDataset)
                await InterruptDownload();
            await videos.Suspend(apply);
        }
        finally
        {
            _resettingProtection = false;
            Changed(nameof(CanDownload));
        }
    }

    /// <summary>
    /// Deletes <see cref="DataProvider"/>'s downloaded data without changing
    /// automatic download intent. This reports a failure.
    /// </summary>
    internal async Task DeleteData()
    {
        if (_download is not null || DataProvider is not { Dataset: { } dataset } option || _videos is not { } videos)
            return;
        CancelInformation();
        using var operation = _work.Begin(new(new WorkDescription.Maintenance(MaintenanceWork.DeleteDataset),
            new WorkProgress.Unknown(), new WorkExecution.Finishing()));
        try
        {
            await ReplaceData(videos, option, dataset.Delete);
        }
        catch (Exception error)
        {
            if (error is VideoException failure)
                failure.Provider ??= option;
            ReportCommand(error);
        }
    }

    // Every provider's movies show ratings from the data, not only IMDb's, so
    // the rows refresh, and Settings shows the data's new state.
    private async Task ReplaceData(VideoLookup videos, ProviderOption option, Action replace, Func<bool>? enableAutomaticDownload = null)
    {
        try
        {
            await videos.Reload(option, replace, enableAutomaticDownload);
        }
        finally
        {
            Refresh();
            Changed(nameof(Providers));
            Changed(nameof(IDataset.Installed));
        }
    }

    private void ShowDownload()
    {
        Changed(nameof(DataWork));
        Changed(nameof(HasDataWork));
        Changed(nameof(CanDownload));
    }

    internal async Task DisableAutomaticDownload()
    {
        _automaticOnInstall = false;
        if (_videos is null)
            return;
        await _videos.DisableAutomaticDownload();
    }

    internal void WatchInspector(bool visible)
    {
        if (IsInspectorVisible == visible)
            return;
        IsInspectorVisible = visible;
        if (!visible)
            CancelInformation();
        else
            _ = ReadDetail();
        Changed(nameof(IsInspectorVisible));
    }

    internal bool IsInspectorVisible { get; private set; }

    private void RefreshInformation()
    {
        if (_disposed)
            return;
        if (_viewProvider != Provider || _automaticDownload != IsAutomaticDownloadEnabled)
        {
            _viewProvider = Provider;
            _automaticDownload = IsAutomaticDownloadEnabled;
            Changed(nameof(IsAutomaticDownloadEnabled));
            Changed(nameof(Providers));
            Changed(nameof(Provider));
            Refresh();
        }
        if (_automaticWait != AutomaticWait)
        {
            _automaticWait = AutomaticWait;
            Changed(nameof(AutomaticWait));
        }
        Changed(nameof(FetchMessage));
        Changed(nameof(Status));
        Changed(nameof(Message));
        RefreshCommands();
    }

    private sealed class InformationRequest : IAsyncDisposable
    {
        private static readonly WorkObservation Started = new(
            new WorkDescription.Metadata(MetadataScope.Inspector),
            new WorkProgress.Unknown(), new WorkExecution.Running());
        private readonly CancellationTokenSource _cancellation = new();
        private readonly BackgroundWork.Operation _operation;
        private readonly object _gate = new();
        private Task _signalling = Task.CompletedTask;
        private bool _ended;

        internal InformationRequest(BackgroundWork work)
        {
            _operation = work.Begin(Started, Cancel);
        }

        internal CancellationToken Token => _cancellation.Token;

        internal void Report(WorkExecution execution)
        {
            lock (_gate)
                if (!_ended && !_cancellation.IsCancellationRequested)
                    _operation.Report(Started with { Execution = execution });
        }

        internal Task Cancel()
        {
            lock (_gate)
            {
                if (!_ended && !_cancellation.IsCancellationRequested)
                {
                    _operation.Report(Started with { Execution = new WorkExecution.Cancelling() });
                    _signalling = _cancellation.CancelAsync();
                }
                return _signalling;
            }
        }

        // CancelAsync runs the token's callbacks on the thread pool, so the
        // source is disposed only after they finish.
        public async ValueTask DisposeAsync()
        {
            Task signalling;
            lock (_gate)
            {
                _ended = true;
                signalling = _signalling;
            }
            try
            {
                await signalling;
            }
            finally
            {
                _operation.Dispose();
                _cancellation.Dispose();
            }
        }
    }

    private async Task RequestInformation()
    {
        if (_videos is not { } videos || Selected is not { } row || InformationProvider is not { } source)
            return;
        var entry = row.Entry;
        await using var request = new InformationRequest(_work);
        _informationRequest = request;
        _outcome = null;
        ShowInformation();
        try
        {
            if (await videos.Protection(FileName.Interpret(entry.Name).Title,
                    entry.Name, source, request.Token) is not null)
            {
                request.Token.ThrowIfCancellationRequested();
                Fetch(source);
                return;
            }
            var found = await videos.Lookup(entry, source, request.Token, request.Report);
            request.Token.ThrowIfCancellationRequested();
            if (!found)
                _outcome = new FetchOutcome.NoMatch();
        }
        catch (Exception error) when (request.Token.IsCancellationRequested || error is OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Library metadata refresh cancelled ({source.ProviderId}).");
        }
        catch (RequestBlockedException)
        {
            Fetch(source);
        }
        catch (VideoException error)
        {
            _outcome = new FetchOutcome.Failed(error);
        }
        catch (Exception error)
        {
            if (ReferenceEquals(videos, _videos))
                ReportCommand(error);
        }
        finally
        {
            if (ReferenceEquals(_informationRequest, request))
            {
                _informationRequest = null;
                ShowInformation();
            }
        }
    }

    internal void CancelInformation()
    {
        if (_informationRequest is not { } request)
            return;
        _ = request.Cancel();
        _informationRequest = null;
        ShowInformation();
    }

    private void ShowInformation()
    {
        Changed(nameof(IsFetching));
        Changed(nameof(FetchMessage));
        Changed(nameof(Message));
        RefreshCommands();
    }
}
