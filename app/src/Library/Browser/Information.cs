using System.Windows.Input;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Library;

public sealed partial class Browser
{
    private CancellationTokenSource? _informationRequest;
    private bool _inspectorVisible;
    private bool _noVideoMatch;
    private VideoException? _fetchFailure;
    private IProvider? _viewProvider;
    private bool _viewEnabled;
    private string? _viewWaiting;
    private DataDownload? _download;
    private bool _resettingProtection;

    public bool IsFetching { get; private set; }
    internal IReadOnlyList<ProviderOption> Providers => _videos?.Options ?? [];
    internal IReadOnlyList<ProviderOption> DownloadProviders =>
        Providers.Where(option => option.IsOnline).ToArray();

    /// <summary>The provider whose data the person downloads, or null before Library attaches.</summary>
    internal ProviderOption? DataProvider => Providers.FirstOrDefault(option => option.Dataset is not null);
    internal ProviderOption? Option => _videos?.Option;
    internal IProvider? Provider => _videos?.Provider;
    public string FetchMessage =>
        IsFetching ? Text.Get("library", "fetching")
        : _noVideoMatch ? Text.Get("library", "no_video_match")
        : string.Empty;
    public ICommand FetchDetails { get; private set; } = null!;

    // VideoLookup serves requests only once the source is ready, so the
    // commands that ask it wait for the same readiness. Fetch metadata names
    // its provider, so it works while downloading is Off; refreshing uses the
    // provider in Settings, so it needs downloading on.
    private bool CanLookup => IsAvailable && _ready;
    private bool CanFetch =>
        CanLookup && IsEnrichmentEnabled && Option is { IsOnline: true }
        && !IsFetching && Selected?.Entry.Kind == FileKind.Video;

    private VideoBatch? Batch => _videos?.Batch;
    public bool HasBatch => Batch is not null;

    // The status counts the file in progress; the tooltip counts finished files.
    public string BatchStatus =>
        Batch is { } batch
            ? Text.Format("library", "batch_status", batch.Done + 1, batch.Total)
            : string.Empty;
    public string BatchTip =>
        Batch is { } batch
            ? Text.Format("library", "batch_tip", batch.Done, batch.Total, batch.Source.Name)
            : string.Empty;

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

    internal void StopBatch() => _videos?.StopBatch();

    private void ConfigureInformation()
    {
        FetchDetails = new RelayCommand(
            async () =>
            {
                if (!CanFetch)
                    return;
                CancelInformation();
                await RequestInformation();
            },
            () => CanFetch
        );
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

    /// <summary>The progress of the running data download, or null when none runs.</summary>
    internal Imdb.Dataset.Progress? Downloading => _download?.Progress;

    // One running data download. A stop says whether the person asked for
    // it, because only a stop they did not ask for is reported.
    private sealed class DataDownload
    {
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Imdb.Dataset.Progress Progress { get; set; } = new(0, false);
        internal bool Interrupted { get; private set; }
        // A later choice revokes activation while replacement waits for lookups.
        internal volatile bool CanEnable;

        // The download's end deletes a build it did not install, so a caller
        // that stops it waits for the returned task before Library lets go of
        // anything.
        internal Task Stop(bool interrupted)
        {
            CanEnable = false;
            Interrupted |= interrupted;
            Cancellation.Cancel();
            return Finished.Task;
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
        var download = new DataDownload { CanEnable = enable };
        var cancellation = download.Cancellation.Token;
        _download = download;
        var installed = false;
        try
        {
            ShowDownload();
            var shown = 0L;
            await dataset.Build(
                progress =>
                {
                    // Progress arrives for each block of the file; the row shows
                    // whole megabytes, so it changes at most once for each.
                    var megabytes = progress.Received >> 20;
                    if (megabytes == shown && !progress.Importing)
                        return;
                    shown = megabytes;
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (!ReferenceEquals(_download, download))
                            return;
                        download.Progress = progress;
                        ShowDownload();
                    });
                },
                cancellation
            );
            cancellation.ThrowIfCancellationRequested();
            CancelInformation();
            await ReplaceData(videos, option, () =>
            {
                cancellation.ThrowIfCancellationRequested();
                dataset.Install();
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
                download.Cancellation.Dispose();
                try
                {
                    ShowDownload();
                }
                finally
                {
                    download.Finished.TrySetResult();
                }
            }
        }
        return installed;
    }

    /// <summary>Stops the running data download at the person's request, and completes when it has ended.</summary>
    internal Task CancelDownload() => _download?.Stop(interrupted: false) ?? Task.CompletedTask;

    /// <summary>
    /// Stops the running data download as one the person did not ask for,
    /// which reports it, and completes when the download has ended.
    /// </summary>
    internal Task InterruptDownload() => _download?.Stop(interrupted: true) ?? Task.CompletedTask;

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
    /// information off when the provider is the one in use. This reports a
    /// failure.
    /// </summary>
    internal async Task DeleteData()
    {
        if (_download is not null || DataProvider is not { Dataset: { } dataset } option || _videos is not { } videos)
            return;
        CancelInformation();
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
        Changed(nameof(Downloading));
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
        if (_inspectorVisible == visible)
            return;
        _inspectorVisible = visible;
        if (!visible)
            CancelInformation();
        else
            _ = ReadDetail();
        Changed(nameof(IsInspectorVisible));
    }

    internal bool IsInspectorVisible => _inspectorVisible;

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
                _noVideoMatch = false;
                _fetchFailure = null;
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
        ChangedBatch();
        RefreshCommands();
    }

    private void ChangedBatch()
    {
        Changed(nameof(HasBatch));
        Changed(nameof(BatchStatus));
        Changed(nameof(BatchTip));
    }

    private async Task RequestInformation()
    {
        if (_videos is not { } videos || Selected is not { } row)
            return;
        var entry = row.Entry;
        using var cancellation = new CancellationTokenSource();
        _informationRequest = cancellation;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (Option is { } source && await videos.Protection(FileName.Interpret(entry.Name).Title,
                    entry.Name, source, cancellation.Token) is not null)
            {
                Fetch(source);
                return;
            }
            IsFetching = true;
            _noVideoMatch = false;
            _fetchFailure = null;
            Changed(nameof(IsFetching));
            Changed(nameof(FetchMessage));
            Changed(nameof(Message));
            RefreshCommands();
            var found = await videos.Lookup(entry, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
                _noVideoMatch = !found;
        }
        catch (OperationCanceledException) { }
        catch (VideoException error)
        {
            if (!cancellation.IsCancellationRequested)
            {
                if (error is not ProviderException)
                    _fetchFailure = error;
                Changed(nameof(Message));
            }
        }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested && ReferenceEquals(videos, _videos))
                Report(error);
        }
        finally
        {
            if (ReferenceEquals(_informationRequest, cancellation))
            {
                _informationRequest = null;
                IsFetching = false;
                Changed(nameof(IsFetching));
                Changed(nameof(FetchMessage));
                RefreshCommands();
            }
        }
    }

    internal void CancelInformation()
    {
        if (_informationRequest is null)
            return;
        _informationRequest.Cancel();
        _informationRequest = null;
        IsFetching = false;
        Changed(nameof(IsFetching));
        Changed(nameof(FetchMessage));
        RefreshCommands();
    }
}
