using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Library;

public sealed partial class Browser
{
    // A file without Information does not replace the person's section choice.
    private LibrarySection _section = LibrarySection.Information;
    private CancellableWork? _informationRequest;

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

    // The person's provider choice while its save runs, so a refresh does not
    // show the earlier choice before the save ends.
    private sealed record ProviderChoice(ProviderOption Option, bool AutomaticDownload);
    private ProviderChoice? _choice;
    private Task _choiceSave = Task.CompletedTask;
    private IReadOnlyList<string> _providerNames = [];

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
    internal ProviderOption? DataProvider =>
        Providers.FirstOrDefault(option => option.Dataset is not null);
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
    private ProviderOption? InformationProvider =>
        Selected?.Entry.CatalogId is { } catalog
            ? Providers.FirstOrDefault(option => option.ProviderId == catalog)
            : null;
    private bool CanRefresh =>
        CanLookup
        && InformationProvider is { IsOnline: true }
        && !IsFetching
        && Selection.Items.Count == 1
        && SupportsMetadata
        && Selected?.Entry.HasMetadata == true;

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

    internal Task EnableAutomaticDownload(string providerId) =>
        SaveProvider(FindProvider(providerId), true, videos => videos.EnableAutomaticDownload(providerId));

    internal Task SelectProvider(string providerId) =>
        SaveProvider(FindProvider(providerId), AutomaticDownload, videos => videos.SelectProvider(providerId));

    internal Task DisableAutomaticDownload() =>
        SaveProvider(ChosenProvider, false, videos => videos.DisableAutomaticDownload());

    private ProviderOption? FindProvider(string providerId) =>
        Providers.FirstOrDefault(option => option.ProviderId == providerId);

    internal bool HasKey(string providerId) => _videos?.HasKey(providerId) == true;

    /// <summary>The provider names in <see cref="Providers"/> order, for the Settings choice.</summary>
    public IReadOnlyList<string> ProviderNames
    {
        get
        {
            // A new list clears the choice's selection, so an equal list is kept.
            var names = Providers.Select(option => option.Name).ToArray();
            if (!_providerNames.SequenceEqual(names))
                _providerNames = names;
            return _providerNames;
        }
    }

    /// <summary>The chosen provider's index in <see cref="Providers"/>, or -1.</summary>
    public int ProviderIndex =>
        ChosenProvider is { } option ? Providers.ToList().IndexOf(option) : -1;

    /// <summary>
    /// Saves the provider at <paramref name="index"/> in <see cref="Providers"/>
    /// as the person's Settings choice, unless it is already chosen; a failure
    /// is reported.
    /// </summary>
    internal void ChooseProvider(int index)
    {
        if (!IsAvailable || index < 0 || index >= Providers.Count || index == ProviderIndex)
            return;
        _ = ReportFailure(SelectProvider(Providers[index].ProviderId));
    }

    /// <summary>Whether automatic download is chosen, including a choice that is still saving.</summary>
    public bool AutomaticDownload => _choice?.AutomaticDownload ?? IsAutomaticDownloadEnabled;

    internal ProviderOption? ChosenProvider => _choice?.Option ?? Option;
    public bool HasProvider => Option is not null;
    public bool UsesKey => Option is { Access: Access.Key };

    /// <summary>
    /// Saves automatic download for <see cref="ChosenProvider"/>. Turning it on
    /// needs the provider's data and, without a built-in key, the person's own
    /// key; when one is missing, nothing is saved and this returns what the
    /// person must supply, or <see cref="ProviderRequirement.None"/> when a data
    /// download cannot start now.
    /// </summary>
    internal ProviderRequirement ChooseAutomaticDownload(bool enabled)
    {
        if (!IsAvailable || enabled == AutomaticDownload || ChosenProvider is not { } option)
            return ProviderRequirement.None;
        var missing =
            !enabled ? ProviderRequirement.None
            : option is { Dataset.IsInstalled: false } ? ProviderRequirement.Data
            : option is { Access: Access.Key, HasBuiltInKey: false } && !HasKey(option.ProviderId)
                ? ProviderRequirement.Key
            : ProviderRequirement.None;
        if (missing != ProviderRequirement.None)
        {
            // The switch returns to the saved choice.
            Changed(nameof(AutomaticDownload));
            return missing == ProviderRequirement.Data && !CanDownload
                ? ProviderRequirement.None
                : missing;
        }
        _ = ReportFailure(
            enabled ? EnableAutomaticDownload(option.ProviderId) : DisableAutomaticDownload()
        );
        return ProviderRequirement.None;
    }

    // Settings has no dialog to show a failed save in.
    private async Task ReportFailure(Task save)
    {
        try
        {
            await save;
        }
        catch (Exception error)
        {
            ReportCommand(error);
        }
    }

    // Every provider change passes here. While it saves, Settings shows it,
    // and Detach waits for it, because closing VideoLookup cancels its changes.
    private Task SaveProvider(
        ProviderOption? option,
        bool automaticDownload,
        Func<VideoLookup, Task> save
    )
    {
        _automaticOnInstall = false;
        if (_videos is not { } videos)
            return Task.CompletedTask;
        var choice = option is null ? null : new ProviderChoice(option, automaticDownload);
        if (choice is not null)
        {
            _choice = choice;
            ChangedProviders();
        }
        var saving = save(videos);
        _choiceSave = Settle(choice, saving);
        return saving;
    }

    // VideoLookup saves changes in order, so the latest save ends last. The
    // caller of SaveProvider handles the failure.
    private async Task Settle(ProviderChoice? choice, Task save)
    {
        try
        {
            await save;
        }
        catch (Exception) { }
        finally
        {
            if (choice is not null && ReferenceEquals(_choice, choice))
            {
                _choice = null;
                ChangedProviders();
            }
        }
    }

    private void ChangedProviders()
    {
        Changed(nameof(Providers));
        // The names come first, because an index beyond the shown list throws.
        Changed(nameof(ProviderNames));
        Changed(nameof(ProviderIndex));
        Changed(nameof(AutomaticDownload));
        Changed(nameof(HasProvider));
        Changed(nameof(UsesKey));
    }

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
    public bool CanDownload =>
        !_resettingProtection && _download is null && DataProvider is not null;

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
        if (
            !CanDownload
            || DataProvider is not { Dataset: { } dataset } option
            || _videos is not { } videos
        )
            return false;
        var download = new DataDownload(_work, new WorkDescription.Imdb(ImdbPhase.Downloading));
        _download = download;
        _automaticOnInstall = enableAutomaticDownload;
        ShowDownload();
        try
        {
            return await download.Run(
                () =>
                    dataset.Build(
                        (phase, progress) =>
                            download.Report(new WorkDescription.Imdb(phase), progress),
                        download.Cancellation
                    ),
                () =>
                {
                    CancelInformation();
                    return ReplaceData(
                        videos,
                        option,
                        () =>
                            download.Install(
                                new WorkDescription.Imdb(ImdbPhase.Installing),
                                dataset.Install
                            ),
                        () => _automaticOnInstall
                    );
                },
                dataset.Discard,
                error =>
                {
                    if (error is VideoException failure)
                        failure.Provider ??= option;
                    ReportCommand(error);
                }
            );
        }
        finally
        {
            _download = null;
            ShowDownload();
        }
    }

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
        if (
            _download is not null
            || DataProvider is not { Dataset: { } dataset } option
            || _videos is not { } videos
        )
            return;
        CancelInformation();
        using var operation = _work.Begin(
            new(
                new WorkDescription.Maintenance(MaintenanceWork.DeleteDataset),
                new WorkProgress.Unknown(),
                new WorkExecution.Finishing()
            )
        );
        try
        {
            await ReplaceData(videos, option, dataset.Delete);
            operation.Outcome = WorkOutcome.Completed;
            // The Settings row shows that the data is gone.
            operation.ReportsSuccess = false;
        }
        catch (Exception error)
        {
            if (error is VideoException failure)
                failure.Provider ??= option;
            operation.Error = error;
            ReportCommand(error);
        }
    }

    // Every provider's movies show ratings from the data, not only IMDb's, so
    // the rows refresh, and Settings shows the data's new state.
    private async Task ReplaceData(
        VideoLookup videos,
        ProviderOption option,
        Action replace,
        Func<bool>? enableAutomaticDownload = null
    )
    {
        try
        {
            await videos.Reload(option, replace, enableAutomaticDownload);
        }
        finally
        {
            Refresh();
            ChangedProviders();
            Changed(nameof(IDataset.Installed));
        }
    }

    private void ShowDownload()
    {
        Changed(nameof(DataWork));
        Changed(nameof(HasDataWork));
        Changed(nameof(CanDownload));
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
            ChangedProviders();
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

    private static readonly WorkObservation InspectorLookup = new(
        new WorkDescription.Metadata(MetadataScope.Inspector),
        new WorkProgress.Unknown(),
        new WorkExecution.Running()
    );

    private async Task RequestInformation()
    {
        if (
            _videos is not { } videos
            || Selected is not { } row
            || InformationProvider is not { } source
        )
            return;
        var entry = row.Entry;
        await using var request = new CancellableWork(_work, InspectorLookup);
        _informationRequest = request;
        _outcome = null;
        ShowInformation();
        try
        {
            if (
                await videos.Protection(
                    FileName.Interpret(entry.Name).Title,
                    entry.Name,
                    source,
                    request.Token
                )
                is { } block
            )
            {
                request.Token.ThrowIfCancellationRequested();
                request.Operation.Error = new RequestBlockedException(block);
                Fetch(source);
                return;
            }
            var found = await videos.Lookup(
                entry,
                source,
                request.Token,
                execution => request.Report(InspectorLookup with { Execution = execution })
            );
            request.Token.ThrowIfCancellationRequested();
            if (!found)
                _outcome = new FetchOutcome.NoMatch();
            request.Operation.Outcome = WorkOutcome.Completed;
            // The details pane shows the information or that none matched.
            request.Operation.ReportsSuccess = false;
            request.Operation.Summarize("metadata_result", found ? 1 : 0, 1, 1);
        }
        catch (Exception error)
            when (request.Token.IsCancellationRequested || error is OperationCanceledException)
        {
            request.Operation.Outcome = WorkOutcome.Cancelled;
            System.Diagnostics.Debug.WriteLine(
                $"Library metadata refresh cancelled ({source.ProviderId})."
            );
        }
        catch (RequestBlockedException error)
        {
            request.Operation.Error = error;
            Fetch(source);
        }
        catch (VideoException error)
        {
            request.Operation.Error = error;
            _outcome = new FetchOutcome.Failed(error);
        }
        catch (Exception error)
        {
            request.Operation.Error = error;
            if (ReferenceEquals(videos, _videos))
                ReportCommand(error);
        }
        finally
        {
            // A lookup cancelled after its answer arrived still ends as cancelled.
            if (request.Token.IsCancellationRequested)
                request.Operation.Outcome = WorkOutcome.Cancelled;
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
        request.TryCancel(out _);
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
