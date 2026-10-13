using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Library;

public sealed partial class Browser
{
    private InformationRequest? _informationRequest;
    // What the last Inspector lookup for the selected file left to show.
    private FetchOutcome? _outcome;
    private IProvider? _viewProvider;
    private bool _viewEnabled;
    private string? _viewWaiting;
    private DataDownload? _download;
    private bool _resettingProtection;

    private abstract record FetchOutcome
    {
        private FetchOutcome() { }
        internal sealed record NoMatch : FetchOutcome;
        internal sealed record Failed(VideoException Error) : FetchOutcome;
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
    private bool CanFetch =>
        CanLookup && Option is { IsOnline: true }
        && !IsFetching && Selected?.Entry.Kind == FileKind.Video;

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

    internal async Task Enable(string providerId)
    {
        if (_download is { } download)
            download.CanEnable = false;
        CancelInformation();
        if (_videos is null)
            return;
        await _videos.Enable(providerId);
    }

    internal bool HasKey(string providerId) => _videos?.HasKey(providerId) == true;

    /// <summary>Saves the person's own API key, as <see cref="VideoLookup.SaveKey"/> describes.</summary>
    internal async Task SaveKey(string providerId, string? key)
    {
        CancelInformation();
        if (_videos is null)
            return;
        await _videos.SaveKey(providerId, key);
    }

    internal BackgroundWork.Operation? DataWork => _download?.Operation;
    public bool HasDataWork => _download is not null;

    private sealed class DataDownload
    {
        private readonly object _gate = new();
        private WorkObservation _observation = new(
            new WorkDescription.Imdb(ImdbPhase.Downloading), new WorkProgress.Unknown(),
            new WorkExecution.Running());
        private Task _signalling = Task.CompletedTask;
        private bool _ended;
        internal BackgroundWork.Operation Operation { get; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Interrupted { get; private set; }
        internal volatile bool CanEnable;

        internal DataDownload(BackgroundWork work)
        {
            Operation = work.Begin(_observation, () => Stop(false));
        }

        internal void Report(Imdb.Dataset.Progress progress)
        {
            lock (_gate)
            {
                if (_ended || _observation.Execution is not WorkExecution.Running)
                    return;
                _observation = _observation with
                {
                    Description = new WorkDescription.Imdb(progress.Phase),
                    Progress = progress.Phase == ImdbPhase.Downloading
                        ? new WorkProgress.Amount(progress.Received, WorkUnit.Bytes)
                        : progress.Percent is { } percent
                            ? new WorkProgress.Fraction(percent, 100, WorkUnit.Percent, true)
                            : new WorkProgress.Unknown(),
                };
                Operation.Report(_observation);
            }
        }

        internal void Install(Action install)
        {
            lock (_gate)
            {
                Cancellation.Token.ThrowIfCancellationRequested();
                _observation = new(new WorkDescription.Imdb(ImdbPhase.Installing),
                    new WorkProgress.Unknown(), new WorkExecution.Finishing());
                Operation.Report(_observation);
            }
            install();
        }

        internal Task Stop(bool interrupted)
        {
            lock (_gate)
            {
                CanEnable = false;
                if (_ended || _observation.Execution is WorkExecution.Finishing)
                    return Finished.Task;
                Interrupted |= interrupted;
                if (_observation.Execution is not WorkExecution.Cancelling)
                {
                    _observation = _observation with { Execution = new WorkExecution.Cancelling() };
                    Operation.Report(_observation);
                    // CancelAsync sets the token immediately and runs callbacks off this gate.
                    _signalling = Cancellation.CancelAsync();
                }
                return Finished.Task;
            }
        }

        internal async Task Finish()
        {
            Task signalling;
            lock (_gate)
            {
                _ended = true;
                signalling = _signalling;
            }
            try
            {
                await signalling.ConfigureAwait(false);
            }
            finally
            {
                Cancellation.Dispose();
                Operation.Dispose();
                Finished.TrySetResult();
            }
        }
    }

    /// <summary>Whether a data download can start; a protection reset or a running download holds it back.</summary>
    internal bool CanDownload => !_resettingProtection && _download is null && DataProvider is not null;

    /// <summary>
    /// Downloads <see cref="DataProvider"/>'s data and replaces the installed
    /// copy. Returns whether the new data is installed. Failure or cancellation
    /// before installation keeps the previous copy; a later failure leaves the
    /// replacement installed. A protection reset or route change that stops it is
    /// reported, because the person did not ask for it.
    /// With <paramref name="enable"/>, installation selects the provider unless
    /// the person accepts another choice before activation.
    /// </summary>
    internal async Task<bool> Download(bool enable = false)
    {
        if (!CanDownload || DataProvider is not { Dataset: { } dataset } option || _videos is not { } videos)
            return false;
        var download = new DataDownload(_work) { CanEnable = enable };
        var cancellation = download.Cancellation.Token;
        _download = download;
        var installed = false;
        try
        {
            ShowDownload();
            await dataset.Build(download.Report, cancellation);
            cancellation.ThrowIfCancellationRequested();
            CancelInformation();
            await ReplaceData(videos, option, () =>
            {
                download.Install(dataset.Install);
                installed = true;
            }, () => download.CanEnable);
        }
        // A stop can end the build with any error, such as the one a changed
        // network route causes.
        catch (Exception) when (download.Interrupted && !installed)
        {
            ReportCommand(new VideoException(VideoFailure.Interrupted) { Provider = option });
        }
        catch (Exception) when (cancellation.IsCancellationRequested && !installed) { }
        catch (Exception error)
        {
            if (error is VideoException failure)
                failure.Provider ??= option;
            ReportCommand(error);
        }
        finally
        {
            try
            {
                if (!installed)
                    await Task.Run(dataset.Discard);
            }
            finally
            {
                _download = null;
                await download.Finish();
                ShowDownload();
            }
        }
        return installed;
    }

    /// <summary>Stops the running data download at the person's request, and completes when it has ended.</summary>
    internal Task CancelDownload()
    {
        if (_download is not { } download)
            return Task.CompletedTask;
        var stopped = download.Stop(interrupted: false);
        ShowDownload();
        return stopped;
    }

    /// <summary>
    /// Stops the running data download as one the person did not ask for,
    /// which reports it, and completes when the download has ended.
    /// </summary>
    internal Task InterruptDownload()
    {
        if (_download is not { } download)
            return Task.CompletedTask;
        var stopped = download.Stop(interrupted: true);
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
    /// Deletes <see cref="DataProvider"/>'s downloaded data, and turns
    /// automatic downloading off when the provider is the one in use. This reports a
    /// failure.
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
            if (IsEnrichmentEnabled && Option == option)
                await videos.Disable(preserveBatch: true);
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
    private async Task ReplaceData(VideoLookup videos, ProviderOption option, Action replace, Func<bool>? enable = null)
    {
        try
        {
            await videos.Reload(option, replace, enable);
        }
        finally
        {
            Refresh();
            Changed(nameof(Providers));
        }
    }

    private void ShowDownload()
    {
        Changed(nameof(DataWork));
        Changed(nameof(HasDataWork));
        Changed(nameof(CanDownload));
    }

    internal async Task Disable()
    {
        if (_download is { } download)
            download.CanEnable = false;
        CancelInformation();
        if (_videos is null)
            return;
        await _videos.Disable();
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
        if (_viewProvider != Provider || _viewEnabled != IsEnrichmentEnabled)
        {
            if (_viewProvider != Provider)
            {
                CancelInformation();
                _viewProvider = Provider;
                _outcome = null;
            }
            _viewEnabled = IsEnrichmentEnabled;
            Changed(nameof(IsEnrichmentEnabled));
            Changed(nameof(Providers));
            Changed(nameof(Provider));
            Refresh();
        }
        if (_viewWaiting != Waiting)
        {
            _viewWaiting = Waiting;
            Changed(nameof(Waiting));
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
        if (_videos is not { } videos || Selected is not { } row)
            return;
        var entry = row.Entry;
        await using var request = new InformationRequest(_work);
        _informationRequest = request;
        _outcome = null;
        ShowInformation();
        try
        {
            if (Option is { } source && await videos.Protection(FileName.Interpret(entry.Name).Title,
                    entry.Name, source, request.Token) is not null)
            {
                request.Token.ThrowIfCancellationRequested();
                Fetch(source);
                return;
            }
            var found = await videos.Lookup(entry, request.Token, request.Report);
            request.Token.ThrowIfCancellationRequested();
            if (!found)
                _outcome = new FetchOutcome.NoMatch();
        }
        catch (Exception) when (request.Token.IsCancellationRequested) { }
        catch (OperationCanceledException) { }
        // The provider's own failure shows through VideoLookup.Failure.
        catch (ProviderException) { }
        catch (VideoException error)
        {
            _outcome = new FetchOutcome.Failed(error);
        }
        catch (Exception error)
        {
            if (ReferenceEquals(videos, _videos))
                Report(error);
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
