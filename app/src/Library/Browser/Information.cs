using System.Windows.Input;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Library;

public sealed partial class Browser
{
    private CancellationTokenSource? _informationRequest;
    private bool _informationVisible;
    private bool _windowActive;
    private bool _detailLoaded;
    private bool _hasDialog;
    private bool _noVideoMatch;
    private VideoException? _fetchFailure;
    private IProvider? _viewProvider;

    public bool IsOnDemand => Provider?.Schedule is not (null or VideoSchedule.Background);
    public bool IsFetching { get; private set; }
    internal IReadOnlyList<ProviderOption> Providers => _videos?.Options ?? [];
    internal ProviderOption? Option => _videos?.Option;
    internal IProvider? Provider => _videos?.Provider;
    public int ProviderIndex
    {
        get
        {
            for (var index = 0; index < Providers.Count; index++)
                if (Providers[index] == Option)
                    return index;
            return -1;
        }
    }
    public bool CanConfigure => IsAvailable && Provider is IConfigurable;
    public string ProviderLabel => Text.Get("library", "source");
    public string FetchLabel => Text.Get("library", IsFetching ? "fetching" : _selected?.Entry.Type.Length > 0 ? "refresh" : "fetch");
    public string FetchTip => Text.Get("library", _selected?.Entry.Type.Length > 0 ? "refresh_tip" : "fetch_tip");
    public string FetchMessage => _noVideoMatch ? Text.Get("library", "no_video_match") : string.Empty;
    public ICommand FetchDetails { get; private set; } = null!;
    public ICommand ConfigureProvider { get; private set; } = null!;
    internal event EventHandler? ConfigureRequested;

    private bool CanFetch => IsAvailable && IsEnrichmentEnabled && IsOnDemand && !IsFetching && _selected?.Entry.Kind == FileKind.Video;

    private void ConfigureInformation()
    {
        FetchDetails = new RelayCommand(async () =>
        {
            if (!CanFetch)
                return;
            CancelInformation();
            await RequestInformation(false);
        }, () => CanFetch);
        ConfigureProvider = new RelayCommand(() =>
        {
            ConfigureRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }, () => CanConfigure);
    }

    internal async Task SelectProvider(string providerId)
    {
        CancelInformation();
        if (_videos is null)
            return;
        await _videos.Select(providerId);
        RefreshProviders();
    }

    internal async Task Configure(IProvider provider, IReadOnlyDictionary<string, string> settings)
    {
        CancelInformation();
        if (_videos is null)
            return;
        await _videos.Configure(provider, settings);
        RefreshProviders();
    }

    internal VideoEditor? CreateEditor() => Provider is IConfigurable provider
        ? provider.Edit(Text, settings => Configure(provider, settings)) : null;

    internal void WatchInformation(bool visible)
    {
        if (_informationVisible == visible)
            return;
        _informationVisible = visible;
        if (!visible)
            CancelInformation();
        else
            ScheduleInformation();
    }

    internal void ActivateInformation(bool active)
    {
        _windowActive = active;
        if (!active)
            CancelInformation();
        else
            ScheduleInformation();
    }

    internal void SetDialog(bool visible)
    {
        _hasDialog = visible;
        if (visible)
            CancelInformation();
        else
            ScheduleInformation();
    }

    private void RefreshProviders()
    {
        if (_viewProvider != Provider)
        {
            CancelInformation();
            _viewProvider = Provider;
            _noVideoMatch = false;
            _fetchFailure = null;
        }
        Changed(nameof(Providers));
        Changed(nameof(Provider));
        Changed(nameof(ProviderIndex));
        Changed(nameof(IsOnDemand));
        Changed(nameof(CanConfigure));
        Changed(nameof(FetchLabel));
        Changed(nameof(FetchTip));
        Changed(nameof(FetchMessage));
        Changed(nameof(Message));
        ScheduleInformation();
    }

    private void ScheduleInformation()
    {
        if (_informationRequest is not null || !CanFetch || !_visible || !IsOpen || !_informationVisible ||
            !_windowActive || _hasDialog || !_detailLoaded || _selected?.Entry.Type.Length != 0 ||
            Provider?.Schedule is not VideoSchedule.Selection || _videos?.Failure is not null)
            return;
        _ = RequestInformation(true);
    }

    private async Task RequestInformation(bool delayed)
    {
        if (_videos is null || _selected is not { } row || Provider is not { } provider)
            return;
        var entry = row.Entry;
        using var cancellation = new CancellationTokenSource();
        _informationRequest = cancellation;
        try
        {
            if (delayed)
            {
                if (provider.Schedule is not VideoSchedule.Selection selection)
                    return;
                await Task.Delay(selection.Delay, cancellation.Token);
                if (!await _videos.CanLookup(entry, cancellation.Token))
                    return;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            IsFetching = true;
            _noVideoMatch = false;
            _fetchFailure = null;
            Changed(nameof(IsFetching));
            Changed(nameof(FetchLabel));
            Changed(nameof(FetchMessage));
            Changed(nameof(Message));
            var found = await _videos.Lookup(entry, !delayed, cancellation.Token);
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
        catch (Exception error) { Report(error); }
        finally
        {
            if (ReferenceEquals(_informationRequest, cancellation))
            {
                _informationRequest = null;
                IsFetching = false;
                Changed(nameof(IsFetching));
                Changed(nameof(FetchLabel));
                Changed(nameof(FetchMessage));
            }
        }
    }

    private void CancelInformation()
    {
        if (_informationRequest is null)
            return;
        _informationRequest.Cancel();
        _informationRequest = null;
        IsFetching = false;
        Changed(nameof(IsFetching));
        Changed(nameof(FetchLabel));
    }
}
