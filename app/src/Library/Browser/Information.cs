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

    public bool IsFetching { get; private set; }
    internal IReadOnlyList<ProviderOption> Providers => _videos?.Options ?? [];
    internal ProviderOption? Option => _videos?.Option;
    internal int LookupLimit => _videos?.LookupLimit ?? Store.DefaultLookupLimit;
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
        CanLookup && IsEnrichmentEnabled && !IsFetching && Selected?.Entry.Kind == FileKind.Video;

    private VideoBatch? Batch => _videos?.Batch;
    public bool HasBatch => Batch is not null;

    // The status counts the file in progress; the tooltip counts finished files.
    public string BatchStatus =>
        Batch is { } batch
            ? Text.Format("library", "batch_status", batch.Done + 1, batch.Total)
            : string.Empty;
    public string BatchTip =>
        Batch is { } batch
            ? Text.Format("library", "batch_tip", batch.Done, batch.Total, batch.Source.Label(Text))
            : string.Empty;

    /// <summary>
    /// Fetches metadata for the selection from <paramref name="source"/>: one
    /// file opens the Fetch metadata dialog; the files of a larger selection
    /// that have no metadata join the batch.
    /// </summary>
    internal void Fetch(ProviderOption source)
    {
        if (!FetchMetadata.CanExecute(null) || _videos is null)
            return;
        if (Selection.Items.Count == 1)
            FetchMetadataRequested?.Invoke(this, source);
        else
            _videos.Fetch(
                Selection
                    .Items.Cast<LibraryRow>()
                    .Select(row => row.Entry)
                    .Where(entry => entry.Kind == FileKind.Video && !entry.HasMetadata)
                    .ToArray(),
                source
            );
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
        CancelInformation();
        if (_videos is null)
            return;
        await _videos.Enable(providerId);
    }

    internal async Task SaveLookupLimit(int limit)
    {
        if (_videos is null)
            return;
        await _videos.SaveLookupLimit(limit);
    }

    internal async Task Disable()
    {
        CancelInformation();
        if (_videos is null)
            return;
        await _videos.Disable();
    }

    /// <summary>Applies a new network route through <paramref name="configure"/>, as <see cref="VideoLookup.ChangeRoute"/> describes.</summary>
    internal void ChangeRoute(Action configure)
    {
        if (_videos is null)
            configure();
        else
            _videos.ChangeRoute(configure);
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
    }

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
        Changed(nameof(LookupLimit));
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
